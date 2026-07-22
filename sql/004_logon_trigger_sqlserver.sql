/* =========================================================================
   ABA - Plataforma de Hosting DB & Servicios para Desarrolladores
   Logon Trigger a nivel de servidor (SQL Server)
   Se ejecuta en el mismo motor SQL Server que ya hospeda ABA_Control, así
   que puede consultar las tablas de whitelist directamente sin necesidad
   de un Linked Server.

   ⚠️  ADVERTENCIA OPERATIVA — LEE ANTES DE EJECUTAR ⚠️
   Un logon trigger de servidor corre para absolutamente TODAS las
   conexiones a esta instancia, incluida la de "sa". Un bug aquí puede
   dejar a todo el equipo sin poder entrar por SSMS/DBeaver.
   Vía de escape si eso pasa: conéctate por la Dedicated Admin Connection
   (DAC), que IGNORA los logon triggers:
       sqlcmd -S 127.0.0.1 -A -U sa -P "<password>"
   y desde ahí: DISABLE TRIGGER trg_ABA_ValidarConexionSQLServer ON ALL SERVER;
   (el DAC solo escucha en localhost del propio VPS, así que esto se hace
   con una sesión SSH directa al servidor, no por el túnel de DBeaver).

   GOTCHA IMPORTANTE (confirmado probando en vivo):
   Un logon trigger corre con los PERMISOS DEL LOGIN QUE SE ESTÁ CONECTANDO,
   no con los del creador del trigger. Un login de estudiante recién creado
   no tiene ningún permiso sobre ABA_Control, así que sin "EXECUTE AS" la
   consulta a BaseDeDatos/UsuarioIp falla por permisos (no por lógica de
   negocio) y el login se rechaza SIEMPRE, incluso si la IP sí está en la
   whitelist. Por eso el trigger corre impersonando a un login de servicio
   dedicado (aba_trigger_svc) con permisos mínimos de solo lectura sobre
   las tablas que necesita, en vez de usar la identidad del que se conecta.
   ========================================================================= */

USE master;
GO

-- Login de servicio dedicado para que el trigger pueda leer ABA_Control
-- sin depender de los permisos del login que se está conectando, y sin
-- usar 'sa' (mínimo privilegio: solo lectura + insertar en Auditoria).
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'aba_trigger_svc')
BEGIN
    DECLARE @TriggerSvcPassword NVARCHAR(100) = CONVERT(NVARCHAR(100), NEWID()) + CONVERT(NVARCHAR(100), NEWID());
    EXEC('CREATE LOGIN aba_trigger_svc WITH PASSWORD = ''' + @TriggerSvcPassword + ''', CHECK_POLICY = ON;');
END
GO

USE ABA_Control;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'aba_trigger_svc')
    CREATE USER aba_trigger_svc FOR LOGIN aba_trigger_svc;
GO

GRANT SELECT ON dbo.BaseDeDatos   TO aba_trigger_svc;
GRANT SELECT ON dbo.MotorBaseDatos TO aba_trigger_svc;
GRANT SELECT ON dbo.UsuarioIp     TO aba_trigger_svc;
GRANT INSERT ON dbo.Auditoria     TO aba_trigger_svc;
GO

USE master;
GO

CREATE OR ALTER TRIGGER trg_ABA_ValidarConexionSQLServer
ON ALL SERVER
WITH EXECUTE AS 'aba_trigger_svc'
FOR LOGON
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @LoginName SYSNAME = ORIGINAL_LOGIN();

    -- Los administradores (sa, logins del equipo) nunca se bloquean.
    -- Esto también protege al futuro login de servicio del backend, siempre
    -- que se le otorgue el rol sysadmin (o se agregue aquí explícitamente).
    IF IS_SRVROLEMEMBER('sysadmin', @LoginName) = 1
        RETURN;

    -- Solo interesa validar logins que correspondan a una BD aprovisionada
    -- para un estudiante en el motor 'SQLServer'.
    DECLARE @UsuarioId INT, @BaseDeDatosId INT;

    SELECT TOP 1 @UsuarioId = bd.UsuarioId, @BaseDeDatosId = bd.Id
    FROM ABA_Control.dbo.BaseDeDatos bd
    INNER JOIN ABA_Control.dbo.MotorBaseDatos m ON m.Id = bd.MotorId
    WHERE bd.UsuarioBD = @LoginName
      AND m.Nombre = 'SQLServer'
      AND bd.Estado = 'ACTIVA';

    IF @UsuarioId IS NULL
        RETURN;   -- login ajeno a nuestro sistema de aprovisionamiento, no se toca

    DECLARE @ClientIp VARCHAR(48) =
        CONVERT(VARCHAR(48), EVENTDATA().value('(/EVENT_INSTANCE/ClientHost)[1]', 'NVARCHAR(100)'));

    IF NOT EXISTS (
        SELECT 1
        FROM ABA_Control.dbo.UsuarioIp ip
        WHERE ip.UsuarioId = @UsuarioId
          AND ip.DireccionIp = @ClientIp
          AND ip.Activo = 1
    )
    BEGIN
        INSERT INTO ABA_Control.dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioId, 'BaseDeDatos', @BaseDeDatosId, 'CONEXION_RECHAZADA', @ClientIp,
                (SELECT @LoginName AS loginBD FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        ROLLBACK;   -- corta la conexión: el cliente recibe error de login
    END
END;
GO
