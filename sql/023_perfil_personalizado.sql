/* =========================================================================
   ABA - Perfil editable (nombre + avatar).

   Problema real: sp_CrearUsuario corre en CADA login (Google/GitHub) y
   sobreescribe Nombre/AvatarUrl SIN CONDICIÓN con lo que diga el proveedor
   OAuth en ese momento (ver sql/002 y su extensión en sql/011/019). Si se
   agregara un simple "editar nombre" sobre esas mismas columnas, el cambio
   se perdería solo con volver a loguearse — no es un bug hipotético, es lo
   que pasaría literalmente la próxima vez que el usuario entre.

   Solución: dos columnas nuevas, NombrePersonalizado/AvatarUrlPersonalizado,
   que sp_CrearUsuario NUNCA toca. Todo lo que el usuario ve (perfil, login)
   usa COALESCE(Personalizado, valor de OAuth) — si el usuario nunca
   personalizó nada, ve exactamente lo que ya veía antes de este script.
   ========================================================================= */

USE ABA_Control;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Usuario') AND name = 'NombrePersonalizado'
)
BEGIN
    ALTER TABLE dbo.Usuario ADD NombrePersonalizado NVARCHAR(150) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Usuario') AND name = 'AvatarUrlPersonalizado'
)
BEGIN
    ALTER TABLE dbo.Usuario ADD AvatarUrlPersonalizado NVARCHAR(500) NULL;
END
GO

/* -------------------------------------------------------------------------
   sp_CrearUsuario (CREATE OR ALTER, cuerpo idéntico al de sql/019 salvo el
   SELECT final con COALESCE) — sigue sobreescribiendo Nombre/AvatarUrl "en
   crudo" en cada login (esos reflejan lo que dice el proveedor OAuth), pero
   ahora lo que se DEVUELVE respeta la personalización si existe.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_CrearUsuario
    @Nombre             NVARCHAR(150),
    @Correo             NVARCHAR(255),
    @AvatarUrl          NVARCHAR(500) = NULL,
    @Proveedor          VARCHAR(20),
    @ProveedorUsuarioId VARCHAR(100),
    @IpOrigen           VARCHAR(45)   = NULL,
    @UserAgent          NVARCHAR(500) = NULL
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
            (SELECT @Proveedor AS proveedor, @UserAgent AS userAgent FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
        );

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT
        Id,
        COALESCE(NombrePersonalizado, Nombre)        AS Nombre,
        Correo,
        COALESCE(AvatarUrlPersonalizado, AvatarUrl)  AS AvatarUrl,
        Proveedor,
        FechaCreacion,
        UltimoLogin,
        EsAdmin
    FROM dbo.Usuario
    WHERE Id = @UsuarioId;
END
GO

/* -------------------------------------------------------------------------
   sp_ObtenerPerfilUsuario (CREATE OR ALTER, mismo cambio de COALESCE).
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ObtenerPerfilUsuario
    @UsuarioId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        Id                                            AS UsuarioId,
        COALESCE(NombrePersonalizado, Nombre)         AS Nombre,
        Correo,
        COALESCE(AvatarUrlPersonalizado, AvatarUrl)   AS AvatarUrl,
        Proveedor,
        FechaCreacion,
        UltimoLogin,
        EsAdmin
    FROM dbo.Usuario
    WHERE Id = @UsuarioId AND Activo = 1;
END
GO

/* -------------------------------------------------------------------------
   sp_ActualizarPerfilUsuario — nuevo. Nombre siempre requerido (nunca hay
   un estado "sin nombre" en la UI); AvatarUrl es opcional — mandarlo vacío
   o NULL vuelve a mostrar el avatar real de Google/GitHub (borra la
   personalización en vez de dejar un campo vacío roto).
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ActualizarPerfilUsuario
    @UsuarioId  INT,
    @Nombre     NVARCHAR(150),
    @AvatarUrl  NVARCHAR(500) = NULL,
    @IpOrigen   VARCHAR(45)   = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioId AND Activo = 1)
        THROW 50002, 'Usuario no existe o está inactivo.', 1;

    SET @Nombre = NULLIF(LTRIM(RTRIM(@Nombre)), '');
    IF @Nombre IS NULL
        THROW 50040, 'El nombre no puede estar vacío.', 1;

    SET @AvatarUrl = NULLIF(LTRIM(RTRIM(@AvatarUrl)), '');
    IF @AvatarUrl IS NOT NULL AND @AvatarUrl NOT LIKE 'http://%' AND @AvatarUrl NOT LIKE 'https://%'
        THROW 50041, 'La URL del avatar debe empezar con http:// o https://.', 1;

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.Usuario
        SET NombrePersonalizado    = @Nombre,
            AvatarUrlPersonalizado = @AvatarUrl
        WHERE Id = @UsuarioId;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen)
        VALUES (@UsuarioId, 'Usuario', @UsuarioId, 'PERFIL_ACTUALIZADO', @IpOrigen);

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT
        Id                                            AS UsuarioId,
        COALESCE(NombrePersonalizado, Nombre)         AS Nombre,
        Correo,
        COALESCE(AvatarUrlPersonalizado, AvatarUrl)   AS AvatarUrl,
        Proveedor,
        FechaCreacion,
        UltimoLogin,
        EsAdmin
    FROM dbo.Usuario
    WHERE Id = @UsuarioId;
END
GO
