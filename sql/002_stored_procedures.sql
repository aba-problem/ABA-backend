/* =========================================================================
   ABA - Plataforma de Hosting DB & Servicios para Desarrolladores
   Stored Procedures: Usuario y aprovisionamiento de BaseDeDatos
   Motor: Microsoft SQL Server (ABA_Control)

   Aprovisionamiento en dos fases:
   SQL Server no puede ejecutar CREATE DATABASE/CREATE USER de forma nativa
   sobre un motor MySQL (son servidores físicamente distintos). Por eso:
     1) sp_AprovisionarBaseDatos decide TODO lo que es negocio (nombre único,
        usuario, contraseña segura, límites) y deja la fila en 'PENDIENTE'.
     2) El backend (tonto) toma esos datos y ejecuta el DDL real contra el
        motor correspondiente -- no decide nada, solo ejecuta.
     3) El backend llama a sp_ConfirmarAprovisionamiento para marcar el
        resultado ('ACTIVA' o 'ELIMINADA' si falló la creación física).
   ========================================================================= */

USE ABA_Control;
GO

/* -------------------------------------------------------------------------
   sp_CrearUsuario
   Upsert de Usuario en cada login OAuth (Google/GitHub). Si ya existe la
   cuenta para ese proveedor, solo actualiza datos de perfil y UltimoLogin;
   si no existe, la crea. Bloquea el login si la cuenta fue desactivada
   (soft-delete) -- ver sp_DesactivarUsuario.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_CrearUsuario
    @Nombre             NVARCHAR(150),
    @Correo             NVARCHAR(255),
    @AvatarUrl          NVARCHAR(500) = NULL,
    @Proveedor          VARCHAR(20),
    @ProveedorUsuarioId VARCHAR(100),
    @IpOrigen           VARCHAR(45)   = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Proveedor NOT IN ('GOOGLE', 'GITHUB')
        THROW 50001, 'Proveedor de autenticación no soportado.', 1;

    DECLARE @UsuarioId INT;
    DECLARE @EsNuevo BIT = 0;

    BEGIN TRY
        BEGIN TRAN;

        SELECT @UsuarioId = Id
        FROM dbo.Usuario WITH (UPDLOCK, HOLDLOCK)
        WHERE Proveedor = @Proveedor AND ProveedorUsuarioId = @ProveedorUsuarioId;

        IF @UsuarioId IS NULL
        BEGIN
            INSERT INTO dbo.Usuario (Nombre, Correo, AvatarUrl, Proveedor, ProveedorUsuarioId, UltimoLogin)
            VALUES (@Nombre, @Correo, @AvatarUrl, @Proveedor, @ProveedorUsuarioId, SYSUTCDATETIME());

            SET @UsuarioId = SCOPE_IDENTITY();
            SET @EsNuevo = 1;
        END
        ELSE
        BEGIN
            IF EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioId AND Activo = 0)
                THROW 50005, 'Esta cuenta fue desactivada. Contacta al administrador.', 1;

            UPDATE dbo.Usuario
            SET Nombre      = @Nombre,
                Correo      = @Correo,
                AvatarUrl   = @AvatarUrl,
                UltimoLogin = SYSUTCDATETIME()
            WHERE Id = @UsuarioId;
        END

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (
            @UsuarioId, 'Usuario', @UsuarioId,
            CASE WHEN @EsNuevo = 1 THEN 'REGISTRO' ELSE 'LOGIN' END,
            @IpOrigen,
            (SELECT @Proveedor AS proveedor FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
        );

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT Id, Nombre, Correo, AvatarUrl, Proveedor, FechaCreacion, UltimoLogin
    FROM dbo.Usuario
    WHERE Id = @UsuarioId;
END
GO

/* -------------------------------------------------------------------------
   sp_DesactivarUsuario
   Camino explícito para dar de baja una cuenta (a diferencia del trigger
   trg_Usuario_SoftDelete, que solo actúa como red de seguridad ante un
   DELETE crudo). Además de desactivar al usuario, desactiva sus IPs
   whitelisteadas y pausa sus BDs activas -- no las borra, solo deja de
   permitirles tráfico nuevo.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_DesactivarUsuario
    @UsuarioId INT,
    @Motivo    NVARCHAR(255) = NULL,
    @IpOrigen  VARCHAR(45)   = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioId)
        THROW 50006, 'Usuario no encontrado.', 1;

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.Usuario SET Activo = 0 WHERE Id = @UsuarioId AND Activo = 1;

        UPDATE dbo.UsuarioIp SET Activo = 0 WHERE UsuarioId = @UsuarioId AND Activo = 1;

        UPDATE dbo.BaseDeDatos SET Estado = 'PAUSADA' WHERE UsuarioId = @UsuarioId AND Estado = 'ACTIVA';

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioId, 'Usuario', @UsuarioId, 'DESACTIVAR', @IpOrigen,
                (SELECT @Motivo AS motivo FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO

/* -------------------------------------------------------------------------
   sp_AprovisionarBaseDatos
   Fase 1 del aprovisionamiento (ver cabecera del archivo). Genera nombre
   de BD, usuario y contraseña aleatorios, cifra la contraseña y deja la
   fila en 'PENDIENTE'. Devuelve la contraseña en texto plano UNA sola vez
   -- es la única ocasión en que el backend la ve sin cifrar, para poder
   crear el login real en el motor.
   Nota técnica: la generación aleatoria va dentro del SP (no en una
   función) porque SQL Server no permite NEWID()/CRYPT_GEN_RANDOM() dentro
   de funciones definidas por el usuario (error 443, "side-effecting
   operator").
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_AprovisionarBaseDatos
    @UsuarioId   INT,
    @NombreMotor VARCHAR(30),
    @IpOrigen    VARCHAR(45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioId AND Activo = 1)
        THROW 50002, 'Usuario no existe o está inactivo.', 1;

    DECLARE @MotorId TINYINT, @Host VARCHAR(255), @Puerto INT;

    SELECT @MotorId = Id, @Host = HostDefault, @Puerto = PuertoDefault
    FROM dbo.MotorBaseDatos
    WHERE Nombre = @NombreMotor AND Activo = 1;

    IF @MotorId IS NULL
        THROW 50003, 'Motor de base de datos no soportado o inactivo.', 1;

    -- límite de BDs por usuario, para evitar abuso del servicio gratuito
    IF (SELECT COUNT(*) FROM dbo.BaseDeDatos WHERE UsuarioId = @UsuarioId AND Estado IN ('ACTIVA', 'PENDIENTE')) >= 5
        THROW 50004, 'Se alcanzó el límite máximo de bases de datos por usuario.', 1;

    DECLARE @Sufijo VARCHAR(10) = LEFT(REPLACE(CONVERT(VARCHAR(36), NEWID()), '-', ''), 10);
    DECLARE @NombreBD VARCHAR(63) = CONCAT('aba_u', @UsuarioId, '_', @Sufijo);
    DECLARE @UsuarioBD VARCHAR(32) = CONCAT('u', @UsuarioId, '_', LEFT(@Sufijo, 8));

    DECLARE @Longitud INT = 20;
    DECLARE @Charset VARCHAR(94) = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#$%^&*-_=+';
    DECLARE @RandomBytes VARBINARY(100) = CRYPT_GEN_RANDOM(@Longitud);
    DECLARE @PasswordPlano VARCHAR(50) = '';
    DECLARE @i INT = 1, @Byte INT;

    WHILE @i <= @Longitud
    BEGIN
        SET @Byte = CAST(SUBSTRING(@RandomBytes, @i, 1) AS INT);
        SET @PasswordPlano = @PasswordPlano + SUBSTRING(@Charset, (@Byte % LEN(@Charset)) + 1, 1);
        SET @i += 1;
    END

    -- Garantiza al menos una mayúscula, una minúscula, un dígito y un símbolo
    -- (algunos motores/políticas de password lo exigen para aceptar el login)
    SET @PasswordPlano = STUFF(@PasswordPlano, 1, 1, SUBSTRING('ABCDEFGHJKLMNPQRSTUVWXYZ', (ABS(CHECKSUM(NEWID())) % 24) + 1, 1));
    SET @PasswordPlano = STUFF(@PasswordPlano, 2, 1, SUBSTRING('abcdefghijkmnpqrstuvwxyz', (ABS(CHECKSUM(NEWID())) % 24) + 1, 1));
    SET @PasswordPlano = STUFF(@PasswordPlano, 3, 1, SUBSTRING('23456789', (ABS(CHECKSUM(NEWID())) % 8) + 1, 1));
    SET @PasswordPlano = STUFF(@PasswordPlano, 4, 1, SUBSTRING('!@#$%^&*-_=+', (ABS(CHECKSUM(NEWID())) % 12) + 1, 1));

    DECLARE @PasswordCifrado VARBINARY(256);

    OPEN SYMMETRIC KEY SymKey_ABA_Credenciales DECRYPTION BY CERTIFICATE Cert_ABA_Credenciales;
    SET @PasswordCifrado = ENCRYPTBYKEY(KEY_GUID('SymKey_ABA_Credenciales'), @PasswordPlano);
    CLOSE SYMMETRIC KEY SymKey_ABA_Credenciales;

    DECLARE @BaseDeDatosId INT;

    BEGIN TRY
        BEGIN TRAN;

        INSERT INTO dbo.BaseDeDatos
            (UsuarioId, MotorId, NombreBD, UsuarioBD, PasswordCifrado, Host, Puerto, Estado)
        VALUES
            (@UsuarioId, @MotorId, @NombreBD, @UsuarioBD, @PasswordCifrado, @Host, @Puerto, 'PENDIENTE');

        SET @BaseDeDatosId = SCOPE_IDENTITY();

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioId, 'BaseDeDatos', @BaseDeDatosId, 'APROVISIONAR_SOLICITADO', @IpOrigen,
                (SELECT @NombreBD AS nombreBD, @NombreMotor AS motor FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT
        @BaseDeDatosId AS BaseDeDatosId,
        @NombreBD      AS NombreBD,
        @UsuarioBD     AS UsuarioBD,
        @PasswordPlano AS PasswordTemporal,   -- única vez en texto plano
        @Host          AS Host,
        @Puerto        AS Puerto,
        @NombreMotor   AS Motor;
END
GO

/* -------------------------------------------------------------------------
   sp_ConfirmarAprovisionamiento
   Fase 2. El backend llama esto después de intentar crear la BD/usuario
   real en el motor. @Exitoso=1 pasa la fila a 'ACTIVA'; @Exitoso=0 la
   pasa a 'ELIMINADA' (nunca se borra la fila, para no romper Auditoria).
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ConfirmarAprovisionamiento
    @BaseDeDatosId INT,
    @Exitoso       BIT,
    @IpOrigen      VARCHAR(45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UsuarioId INT;

    SELECT @UsuarioId = UsuarioId
    FROM dbo.BaseDeDatos
    WHERE Id = @BaseDeDatosId AND Estado = 'PENDIENTE';

    IF @UsuarioId IS NULL
        THROW 50007, 'La base de datos no existe o ya fue confirmada.', 1;

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.BaseDeDatos
        SET Estado          = CASE WHEN @Exitoso = 1 THEN 'ACTIVA' ELSE 'ELIMINADA' END,
            UltimaActividad = SYSUTCDATETIME()
        WHERE Id = @BaseDeDatosId;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen)
        VALUES (@UsuarioId, 'BaseDeDatos', @BaseDeDatosId,
                CASE WHEN @Exitoso = 1 THEN 'APROVISIONAR_OK' ELSE 'APROVISIONAR_FALLIDO' END,
                @IpOrigen);

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO

/* -------------------------------------------------------------------------
   sp_EliminarBaseDatos
   Camino explícito de baja de una BD (solicitada por el usuario, o por un
   job de limpieza por TTL/espacio). Soft-delete: la fila queda en
   'ELIMINADA' para no romper Auditoria. El backend, después de llamar a
   este SP, debe borrar la BD física y el login en el motor correspondiente.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_EliminarBaseDatos
    @BaseDeDatosId INT,
    @Motivo        NVARCHAR(255) = NULL,
    @IpOrigen      VARCHAR(45)   = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UsuarioId INT;

    SELECT @UsuarioId = UsuarioId
    FROM dbo.BaseDeDatos
    WHERE Id = @BaseDeDatosId AND Estado <> 'ELIMINADA';

    IF @UsuarioId IS NULL
        THROW 50008, 'La base de datos no existe o ya estaba eliminada.', 1;

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.BaseDeDatos SET Estado = 'ELIMINADA' WHERE Id = @BaseDeDatosId;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioId, 'BaseDeDatos', @BaseDeDatosId, 'ELIMINAR', @IpOrigen,
                (SELECT @Motivo AS motivo FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO

/* -------------------------------------------------------------------------
   sp_ObtenerCredencialesBaseDatos
   Vuelve a mostrar la contraseña (desencriptada) para el dashboard --
   sp_AprovisionarBaseDatos solo la entrega en texto plano UNA vez, al
   momento de crear la BD. La validación de "solo el dueño puede verla" se
   hace aquí, dentro de SQL Server, en vez de confiar en que el backend
   filtre bien -- así un bug o un IDOR en la API no expone credenciales
   ajenas: aunque el backend pase un BaseDeDatosId de otro usuario, el SP
   rechaza la consulta si @UsuarioIdSolicitante no es el dueño real.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ObtenerCredencialesBaseDatos
    @BaseDeDatosId        INT,
    @UsuarioIdSolicitante INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @UsuarioIdDueno INT;

    SELECT @UsuarioIdDueno = UsuarioId
    FROM dbo.BaseDeDatos
    WHERE Id = @BaseDeDatosId;

    IF @UsuarioIdDueno IS NULL
        THROW 50011, 'La base de datos no existe.', 1;

    IF @UsuarioIdDueno <> @UsuarioIdSolicitante
    BEGIN
        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, Detalle)
        VALUES (@UsuarioIdSolicitante, 'BaseDeDatos', @BaseDeDatosId, 'ACCESO_CREDENCIALES_RECHAZADO',
                (SELECT @UsuarioIdDueno AS duenoReal FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        THROW 50012, 'No tienes permiso para ver las credenciales de esta base de datos.', 1;
    END

    OPEN SYMMETRIC KEY SymKey_ABA_Credenciales DECRYPTION BY CERTIFICATE Cert_ABA_Credenciales;

    SELECT
        bd.Id, bd.NombreBD, bd.UsuarioBD,
        CONVERT(VARCHAR(50), DECRYPTBYKEY(bd.PasswordCifrado)) AS Password,
        bd.Host, bd.Puerto, m.Nombre AS Motor, bd.Estado,
        bd.FechaCreacion, bd.UltimaActividad,
        bd.EspacioMaximoMB, bd.EspacioUtilizadoMB
    FROM dbo.BaseDeDatos bd
    INNER JOIN dbo.MotorBaseDatos m ON m.Id = bd.MotorId
    WHERE bd.Id = @BaseDeDatosId;

    CLOSE SYMMETRIC KEY SymKey_ABA_Credenciales;
END
GO
