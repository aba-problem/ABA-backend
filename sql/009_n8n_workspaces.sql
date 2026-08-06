/* =========================================================================
   ABA - Entregable 3, Módulo N8N (autoservicio de workspace)
   Script aditivo — no modifica 001-008. Sigue el mismo patrón que
   sp_AprovisionarBaseDatos: el backend NUNCA decide nombre ni contraseña,
   todo se genera y valida dentro de los SPs (Regla de Oro del proyecto).

   Decisión de arquitectura: UNA sola instancia N8N compartida, con
   workspaces separados internamente (multi-tenancy lógico, no un
   contenedor por usuario) — mismo argumento de presupuesto de RAM
   (Módulo 6) ya usado para justificar SQL Server como motor alternativo
   de bases de datos en vez de una instancia dedicada por tenant.

   Códigos de error de este módulo: 50020-50029 (rango propio, no choca
   con 50001-50012 ya usados en 002/003).
   ========================================================================= */

CREATE TABLE dbo.WorkspaceN8N (
    Id                   INT IDENTITY(1,1) PRIMARY KEY,
    UsuarioId            INT           NOT NULL,
    NombreWorkspace      SYSNAME       NOT NULL,
    PasswordCifrado      VARBINARY(256) NOT NULL,   -- misma SymKey_ABA_Credenciales de 001 — nunca texto plano
    LimiteWorkflows      INT           NOT NULL DEFAULT 10,
    LimiteEjecucionesMes INT           NOT NULL DEFAULT 500,
    Estado               VARCHAR(20)   NOT NULL DEFAULT 'ACTIVO',
    FechaCreacion        DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_WorkspaceN8N_Usuario FOREIGN KEY (UsuarioId) REFERENCES dbo.Usuario (Id),
    CONSTRAINT UQ_WorkspaceN8N_Nombre UNIQUE (NombreWorkspace),
    CONSTRAINT CK_WorkspaceN8N_Estado CHECK (Estado IN ('ACTIVO', 'ELIMINADO'))
);
GO

-- Un solo workspace ACTIVO por usuario a la vez (GET /n8n/mi-workspace es singular).
CREATE UNIQUE INDEX UQ_WorkspaceN8N_UnoActivoPorUsuario
    ON dbo.WorkspaceN8N (UsuarioId)
    WHERE Estado = 'ACTIVO';
GO

/* Extiende el catálogo de entidades auditables (control 5.8) sin tocar 001. */
ALTER TABLE dbo.Auditoria DROP CONSTRAINT IF EXISTS CK_Auditoria_Entidad;
GO
ALTER TABLE dbo.Auditoria ADD CONSTRAINT CK_Auditoria_Entidad
    CHECK (Entidad IN ('Usuario', 'BaseDeDatos', 'UsuarioIp', 'WorkspaceN8N'));
GO

/* -------------------------------------------------------------------------
   sp_CrearWorkspaceN8N
   El backend solo pasa @UsuarioId (del claim JWT, jamás del body — BOLA) y
   @IpOrigen. Nombre y contraseña se generan aquí, nunca a partir de input
   del cliente (cierra la puerta a colisión/enumeración de nombres, igual
   que sp_AprovisionarBaseDatos).
   ------------------------------------------------------------------------- */
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

    -- CSPRNG — mismo generador que sp_AprovisionarBaseDatos, nunca NEWID()/RAND() para el secreto.
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

        -- Detalle SIN el password (control 5.8 — nunca loguear el secreto, ni en Auditoria).
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
        @PasswordPlano    AS PasswordTemporal,   -- única vez en texto plano — el backend no la loguea
        10                AS LimiteWorkflows,
        500               AS LimiteEjecucionesMes;
END
GO

/* -------------------------------------------------------------------------
   sp_ObtenerMiWorkspace
   Sin password (control: el secreto solo se muestra una vez, al crear).
   Filtra siempre por @UsuarioId del token — nunca por un Id que el
   cliente envíe (BOLA, control 3.1). Sin filas = el llamador lo traduce
   a "no tiene workspace" (204/404), no es un error de negocio.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ObtenerMiWorkspace
    @UsuarioId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, NombreWorkspace, LimiteWorkflows, LimiteEjecucionesMes, Estado, FechaCreacion
    FROM dbo.WorkspaceN8N
    WHERE UsuarioId = @UsuarioId AND Estado = 'ACTIVO';
END
GO

/* -------------------------------------------------------------------------
   sp_EliminarWorkspace
   Soft delete del workspace del usuario autenticado. Nunca recibe un
   WorkspaceId del cliente — opera siempre sobre "el" workspace activo del
   @UsuarioId del token, cerrando cualquier vector BOLA por diseño.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_EliminarWorkspace
    @UsuarioId INT,
    @IpOrigen  VARCHAR(45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @WorkspaceId INT;
    SELECT @WorkspaceId = Id FROM dbo.WorkspaceN8N WHERE UsuarioId = @UsuarioId AND Estado = 'ACTIVO';

    IF @WorkspaceId IS NULL
        THROW 50021, 'No tienes un workspace de N8N activo.', 1;

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.WorkspaceN8N SET Estado = 'ELIMINADO' WHERE Id = @WorkspaceId;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen)
        VALUES (@UsuarioId, 'WorkspaceN8N', @WorkspaceId, 'ELIMINAR', @IpOrigen);

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO
