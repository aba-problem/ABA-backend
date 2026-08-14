/* =========================================================================
   ABA - Fix: sp_CrearWorkspaceN8N y sp_CrearApiKey quedaron con
   QUOTED_IDENTIFIER OFF grabado permanentemente porque se crearon vía
   `sqlcmd` (que usa OFF por defecto), y ambos hacen INSERT contra una tabla
   con índice filtrado (UQ_WorkspaceN8N_UnoActivoPorUsuario / IX_ApiKey_Prefijo).

   SQL Server graba ANSI_NULLS/QUOTED_IDENTIFIER como metadata del objeto en
   el momento de CREATE PROCEDURE — la conexión ADO.NET de la app pone
   QUOTED_IDENTIFIER ON en su sesión, pero eso NO cambia el valor ya grabado
   en el procedimiento. Por eso el INSERT fallaba en producción con
   "INSERT failed because the following SET options have incorrect
   settings: 'QUOTED_IDENTIFIER'" aunque el índice ya estaba bien creado.

   Fix: recrear ambos procedimientos con SET QUOTED_IDENTIFIER ON activo
   ANTES del CREATE OR ALTER — se graba correctamente y persiste para
   siempre en el objeto, sin depender de quién lo llame después.
   Cuerpo de cada SP idéntico al original (009/010), sin cambios de lógica.
   ========================================================================= */

USE ABA_Control;
GO

SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE dbo.sp_CrearWorkspaceN8N
    @UsuarioId INT,
    @IpOrigen  VARCHAR(45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioId AND Activo = 1)
        THROW 50002, 'Usuario no existe o está inactivo.', 1;

    IF EXISTS (SELECT 1 FROM dbo.WorkspaceN8N WHERE UsuarioId = @UsuarioId AND Estado = 'ACTIVO')
        THROW 50020, 'Ya tienes un workspace de N8N activo.', 1;

    DECLARE @Sufijo VARCHAR(10) = LEFT(REPLACE(CONVERT(VARCHAR(36), NEWID()), '-', ''), 10);
    DECLARE @NombreWorkspace SYSNAME = CONCAT('ws_u', @UsuarioId, '_', @Sufijo);

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

    SET @PasswordPlano = STUFF(@PasswordPlano, 1, 1, SUBSTRING('ABCDEFGHJKLMNPQRSTUVWXYZ', (ABS(CHECKSUM(NEWID())) % 24) + 1, 1));
    SET @PasswordPlano = STUFF(@PasswordPlano, 2, 1, SUBSTRING('abcdefghijkmnpqrstuvwxyz', (ABS(CHECKSUM(NEWID())) % 24) + 1, 1));
    SET @PasswordPlano = STUFF(@PasswordPlano, 3, 1, SUBSTRING('23456789', (ABS(CHECKSUM(NEWID())) % 8) + 1, 1));
    SET @PasswordPlano = STUFF(@PasswordPlano, 4, 1, SUBSTRING('!@#$%^&*-_=+', (ABS(CHECKSUM(NEWID())) % 12) + 1, 1));

    DECLARE @PasswordCifrado VARBINARY(256);
    OPEN SYMMETRIC KEY SymKey_ABA_Credenciales DECRYPTION BY CERTIFICATE Cert_ABA_Credenciales;
    SET @PasswordCifrado = ENCRYPTBYKEY(KEY_GUID('SymKey_ABA_Credenciales'), @PasswordPlano);
    CLOSE SYMMETRIC KEY SymKey_ABA_Credenciales;

    DECLARE @WorkspaceId INT;

    BEGIN TRY
        BEGIN TRAN;

        INSERT INTO dbo.WorkspaceN8N (UsuarioId, NombreWorkspace, PasswordCifrado)
        VALUES (@UsuarioId, @NombreWorkspace, @PasswordCifrado);

        SET @WorkspaceId = SCOPE_IDENTITY();

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioId, 'WorkspaceN8N', @WorkspaceId, 'CREAR', @IpOrigen,
                (SELECT @NombreWorkspace AS nombreWorkspace FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT
        @WorkspaceId      AS Id,
        @NombreWorkspace  AS NombreWorkspace,
        @PasswordPlano    AS PasswordTemporal,
        10                AS LimiteWorkflows,
        500               AS LimiteEjecucionesMes;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_CrearApiKey
    @UsuarioId INT,
    @IpOrigen  VARCHAR(45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioId AND Activo = 1)
        THROW 50002, 'Usuario no existe o está inactivo.', 1;

    IF (SELECT COUNT(*) FROM dbo.ApiKey WHERE UsuarioId = @UsuarioId AND Activa = 1) >= 5
        THROW 50031, 'Se alcanzó el límite máximo de API keys activas.', 1;

    DECLARE @Charset VARCHAR(62) = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789';
    DECLARE @Longitud INT = 32;
    DECLARE @RandomBytes VARBINARY(100) = CRYPT_GEN_RANDOM(@Longitud);
    DECLARE @Aleatorio VARCHAR(40) = '';
    DECLARE @i INT = 1, @Byte INT;

    WHILE @i <= @Longitud
    BEGIN
        SET @Byte = CAST(SUBSTRING(@RandomBytes, @i, 1) AS INT);
        SET @Aleatorio = @Aleatorio + SUBSTRING(@Charset, (@Byte % LEN(@Charset)) + 1, 1);
        SET @i += 1;
    END

    DECLARE @Prefijo CHAR(8) = LEFT(@Aleatorio, 8);
    DECLARE @KeyCompleta VARCHAR(50) = CONCAT('sk_', @Aleatorio);
    DECLARE @KeyHash BINARY(32) = HASHBYTES('SHA2_256', @KeyCompleta);

    DECLARE @ApiKeyId INT;

    BEGIN TRY
        BEGIN TRAN;

        INSERT INTO dbo.ApiKey (UsuarioId, Prefijo, KeyHash)
        VALUES (@UsuarioId, @Prefijo, @KeyHash);

        SET @ApiKeyId = SCOPE_IDENTITY();

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioId, 'ApiKey', @ApiKeyId, 'CREAR', @IpOrigen,
                (SELECT @Prefijo AS prefijo FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT
        @ApiKeyId     AS Id,
        @Prefijo      AS Prefijo,
        @KeyCompleta  AS KeyCompleta,
        SYSUTCDATETIME() AS FechaCreacion;
END
GO
