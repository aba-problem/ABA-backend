/* =========================================================================
   ABA - Plataforma de Hosting DB & Servicios para Desarrolladores
   Resource Governor + límite de conexiones concurrentes por usuario
   Motor: Microsoft SQL Server

   Requisito de Actividad.md / Entrega2.md: "Conexiones Concurrentes:
   Restricción estricta de usuarios concurrentes por base de datos
   aprovisionada para evitar el agotamiento del pool de conexiones del
   servidor principal."

   Dos mecanismos complementarios, no redundantes:
     1) Resource Governor: clasifica CADA conexión entrante en un grupo de
        carga de trabajo. El equipo/servicio admin va sin restricciones;
        cualquier otro login (las BDs aprovisionadas a estudiantes) cae en
        un grupo compartido con tope de CPU/memoria/peticiones. Protege al
        servidor de que UNA BD acapare recursos de TODAS las demás.
     2) Extensión del logon trigger de 004: cuenta las sesiones activas del
        login que se está conectando y rechaza si supera el límite. Esto sí
        es un tope INDIVIDUAL por usuario, distinto al grupo compartido de
        arriba (que es un techo colectivo, no por login).
   ========================================================================= */

USE master;
GO

/* -------------------------------------------------------------------------
   Función clasificadora de Resource Governor
   Debe vivir en master (requisito de SQL Server) y ejecuta ANTES de que la
   sesión tenga contexto de ninguna otra base de datos -- por eso solo puede
   usar funciones de sistema de alcance de servidor (ORIGINAL_LOGIN,
   IS_SRVROLEMEMBER), no consultar ABA_Control. Como los únicos logins que
   existen en esta instancia son: logins sysadmin del equipo, el login de
   servicio del trigger (aba_trigger_svc) y los logins de BDs aprovisionadas
   a estudiantes, no hace falta una consulta más compleja que esta.
   ------------------------------------------------------------------------- */
CREATE OR ALTER FUNCTION dbo.fn_ABA_ClasificadorRG()
RETURNS SYSNAME
AS
BEGIN
    DECLARE @grupo SYSNAME;
    DECLARE @login SYSNAME = ORIGINAL_LOGIN();

    IF IS_SRVROLEMEMBER('sysadmin', @login) = 1 OR @login = 'aba_trigger_svc'
        SET @grupo = 'ABA_Admin';
    ELSE
        SET @grupo = 'ABA_Estudiantes';

    RETURN @grupo;
END
GO

/* -------------------------------------------------------------------------
   Grupo ABA_Admin: sin restricciones adicionales -- el equipo y el login
   de servicio del trigger nunca deben verse afectados por estos topes.
   ------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.resource_governor_workload_groups WHERE name = 'ABA_Admin')
    CREATE WORKLOAD GROUP ABA_Admin
        USING [default];
GO

/* -------------------------------------------------------------------------
   Grupo ABA_Estudiantes: aquí caen todos los logins de BDs aprovisionadas.
   Los topes son deliberadamente conservadores porque el plan gratuito es
   de 20MB por BD -- no se esperan consultas pesadas, y cualquier consulta
   que sí lo sea probablemente es un intento de abuso, no un caso legítimo.

   GROUP_MAX_REQUESTS = 20 es un techo COMPARTIDO entre TODOS los logins de
   estudiantes conectados simultáneamente a la instancia (no es 20 por
   login individual) -- el tope individual por login lo da el logon trigger
   más abajo, no esto. Ajustar este número según cuántas BDs concurrentes
   se esperen en producción real.
   ------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.resource_governor_workload_groups WHERE name = 'ABA_Estudiantes')
    CREATE WORKLOAD GROUP ABA_Estudiantes
        WITH (
            REQUEST_MAX_CPU_TIME_SEC = 30,
            REQUEST_MAX_MEMORY_GRANT_PERCENT = 10,
            REQUEST_MEMORY_GRANT_TIMEOUT_SEC = 10,
            MAX_DOP = 1,
            GROUP_MAX_REQUESTS = 20
        )
        USING [default];
GO

ALTER RESOURCE GOVERNOR WITH (CLASSIFIER_FUNCTION = dbo.fn_ABA_ClasificadorRG);
GO

ALTER RESOURCE GOVERNOR RECONFIGURE;
GO

/* -------------------------------------------------------------------------
   Extensión del logon trigger: límite de conexiones concurrentes POR LOGIN
   Se redefine el mismo trigger de 004_logon_trigger_sqlserver.sql agregando
   el conteo de sesiones activas. El resto de la lógica (whitelist de IP)
   queda exactamente igual.
   ------------------------------------------------------------------------- */
CREATE OR ALTER TRIGGER trg_ABA_ValidarConexionSQLServer
ON ALL SERVER
WITH EXECUTE AS 'aba_trigger_svc'
FOR LOGON
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @LoginName SYSNAME = ORIGINAL_LOGIN();

    IF IS_SRVROLEMEMBER('sysadmin', @LoginName) = 1
        RETURN;

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

    -- Paso 1 (igual que antes): whitelist de IP
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
                (SELECT @LoginName AS loginBD, 'ip_no_autorizada' AS motivo FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        ROLLBACK;
        RETURN;
    END

    -- Paso 2 (nuevo): límite de conexiones concurrentes por login individual.
    -- sys.dm_exec_sessions ya incluye la sesión que se está estableciendo,
    -- así que el umbral de 3 permite hasta 3 conexiones simultáneas reales.
    DECLARE @ConexionesActivas INT;

    SELECT @ConexionesActivas = COUNT(*)
    FROM sys.dm_exec_sessions
    WHERE login_name = @LoginName AND is_user_process = 1;

    IF @ConexionesActivas > 3
    BEGIN
        INSERT INTO ABA_Control.dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioId, 'BaseDeDatos', @BaseDeDatosId, 'CONEXION_RECHAZADA', @ClientIp,
                (SELECT @LoginName AS loginBD, 'limite_conexiones_concurrentes' AS motivo, @ConexionesActivas AS activas FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        ROLLBACK;
    END
END;
GO
