/* =========================================================================
   ABA - Entregable 3, Módulo DNS Autoservicio
   Script aditivo — no modifica 001-010.

   Mismo patrón de dos fases que sp_AprovisionarBaseDatos /
   sp_ConfirmarAprovisionamiento: el SP RESERVA el registro en ABA_Control
   como 'PENDIENTE' (SQL Server no puede hacer la llamada HTTP al proveedor
   DNS real); el backend llama a IDnsProviderService y luego confirma. Así
   nunca queda un registro 'ACTIVO' en ABA_Control sin existir de verdad en
   el proveedor DNS, igual que nunca queda una BD 'ACTIVA' sin existir en
   el motor real.

   Subdominio siempre sanitizado con el mismo principio ya usado para
   nombres de BD en MySQL: regex estricta ANTES de tocar cualquier sistema
   externo, nunca el input crudo del cliente.

   Códigos de error de este módulo: 50040-50049.
   ========================================================================= */

ALTER TABLE dbo.Usuario ADD EsAdmin BIT NOT NULL DEFAULT 0;
GO

CREATE TABLE dbo.RegistroDns (
    Id            INT IDENTITY(1,1) PRIMARY KEY,
    UsuarioId     INT           NOT NULL,
    Subdominio    SYSNAME       NOT NULL,
    TipoRegistro  VARCHAR(10)   NOT NULL,
    Valor         VARCHAR(255)  NOT NULL,
    Estado        VARCHAR(20)   NOT NULL DEFAULT 'PENDIENTE',
    FechaCreacion DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_RegistroDns_Usuario FOREIGN KEY (UsuarioId) REFERENCES dbo.Usuario (Id),
    CONSTRAINT CK_RegistroDns_Tipo CHECK (TipoRegistro IN ('A', 'CNAME')),
    CONSTRAINT CK_RegistroDns_Estado CHECK (Estado IN ('PENDIENTE', 'ACTIVO', 'ELIMINADA'))
);
GO

-- Único subdominio activo/pendiente a la vez (colisión de nombre = rechazo, no sobreescritura).
CREATE UNIQUE INDEX UQ_RegistroDns_SubdominioVigente
    ON dbo.RegistroDns (Subdominio)
    WHERE Estado IN ('PENDIENTE', 'ACTIVO');
GO

ALTER TABLE dbo.Auditoria DROP CONSTRAINT IF EXISTS CK_Auditoria_Entidad;
GO
ALTER TABLE dbo.Auditoria ADD CONSTRAINT CK_Auditoria_Entidad
    CHECK (Entidad IN ('Usuario', 'BaseDeDatos', 'UsuarioIp', 'WorkspaceN8N', 'ApiKey', 'RegistroDns'));
GO

/* -------------------------------------------------------------------------
   sp_ValidarYCrearRegistroDns — Fase 1: valida forma, colisión y límite;
   reserva 'PENDIENTE'. El backend, si el SP no lanza error, llama al
   proveedor DNS real y después confirma con sp_ConfirmarRegistroDns.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ValidarYCrearRegistroDns
    @UsuarioId    INT,
    @Subdominio   VARCHAR(40),
    @TipoRegistro VARCHAR(10),
    @Valor        VARCHAR(255),
    @IpOrigen     VARCHAR(45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioId AND Activo = 1)
        THROW 50002, 'Usuario no existe o está inactivo.', 1;

    -- Regex ^[a-z0-9-]{1,40}$ — T-SQL no tiene regex nativo; PATINDEX con clase negada
    -- detecta cualquier carácter fuera del set permitido. Nunca se confía en que el
    -- backend ya validó esto — el SP es la última línea antes de tocar el proveedor real.
    IF @Subdominio IS NULL OR LEN(@Subdominio) = 0 OR LEN(@Subdominio) > 40
       OR PATINDEX('%[^a-z0-9-]%', @Subdominio) > 0
        THROW 50041, 'Subdominio inválido: solo minúsculas, dígitos y guiones, máx 40 caracteres.', 1;

    IF @TipoRegistro NOT IN ('A', 'CNAME')
        THROW 50042, 'Tipo de registro DNS no soportado.', 1;

    IF EXISTS (SELECT 1 FROM dbo.RegistroDns WHERE Subdominio = @Subdominio AND Estado IN ('PENDIENTE', 'ACTIVO'))
        THROW 50043, 'Ese subdominio ya está en uso.', 1;

    IF (SELECT COUNT(*) FROM dbo.RegistroDns WHERE UsuarioId = @UsuarioId AND Estado IN ('PENDIENTE', 'ACTIVO')) >= 5
        THROW 50044, 'Se alcanzó el límite máximo de registros DNS por usuario.', 1;

    DECLARE @RegistroId INT;

    BEGIN TRY
        BEGIN TRAN;

        INSERT INTO dbo.RegistroDns (UsuarioId, Subdominio, TipoRegistro, Valor, Estado)
        VALUES (@UsuarioId, @Subdominio, @TipoRegistro, @Valor, 'PENDIENTE');

        SET @RegistroId = SCOPE_IDENTITY();

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioId, 'RegistroDns', @RegistroId, 'CREAR_SOLICITADO', @IpOrigen,
                (SELECT @Subdominio AS subdominio, @TipoRegistro AS tipo FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT @RegistroId AS Id, @Subdominio AS Subdominio, @TipoRegistro AS TipoRegistro, @Valor AS Valor;
END
GO

/* -------------------------------------------------------------------------
   sp_ConfirmarRegistroDns — Fase 2, mismo contrato que
   sp_ConfirmarAprovisionamiento: @Exitoso=1 → ACTIVO, @Exitoso=0 → ELIMINADA
   (nunca queda 'ACTIVO' sin existir de verdad en el proveedor).
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ConfirmarRegistroDns
    @RegistroId INT,
    @Exitoso    BIT,
    @IpOrigen   VARCHAR(45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UsuarioId INT;
    SELECT @UsuarioId = UsuarioId FROM dbo.RegistroDns WHERE Id = @RegistroId AND Estado = 'PENDIENTE';

    IF @UsuarioId IS NULL
        THROW 50046, 'El registro DNS no existe o ya fue confirmado.', 1;

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.RegistroDns
        SET Estado = CASE WHEN @Exitoso = 1 THEN 'ACTIVO' ELSE 'ELIMINADA' END
        WHERE Id = @RegistroId;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen)
        VALUES (@UsuarioId, 'RegistroDns', @RegistroId,
                CASE WHEN @Exitoso = 1 THEN 'CREAR_OK' ELSE 'CREAR_FALLIDO' END, @IpOrigen);

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO

/* -------------------------------------------------------------------------
   sp_ListarMisRegistrosDns
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ListarMisRegistrosDns
    @UsuarioId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, Subdominio, TipoRegistro, Valor, Estado, FechaCreacion
    FROM dbo.RegistroDns
    WHERE UsuarioId = @UsuarioId AND Estado <> 'ELIMINADA'
    ORDER BY FechaCreacion DESC;
END
GO

/* -------------------------------------------------------------------------
   sp_EliminarRegistroDns — BOLA (control 3.1). Devuelve Subdominio/Tipo/Valor
   ANTES de marcar ELIMINADA para que el backend sepa qué borrar en el
   proveedor real después de este llamado.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_EliminarRegistroDns
    @UsuarioId  INT,
    @RegistroId INT,
    @IpOrigen   VARCHAR(45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UsuarioIdDueno INT, @Subdominio SYSNAME, @TipoRegistro VARCHAR(10), @Valor VARCHAR(255), @Estado VARCHAR(20);
    SELECT @UsuarioIdDueno = UsuarioId, @Subdominio = Subdominio, @TipoRegistro = TipoRegistro,
           @Valor = Valor, @Estado = Estado
    FROM dbo.RegistroDns WHERE Id = @RegistroId;

    IF @UsuarioIdDueno IS NULL OR @Estado = 'ELIMINADA'
        THROW 50011, 'El registro DNS no existe.', 1;

    IF @UsuarioIdDueno <> @UsuarioId
    BEGIN
        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, Detalle)
        VALUES (@UsuarioId, 'RegistroDns', @RegistroId, 'ACCESO_ELIMINAR_RECHAZADO',
                (SELECT @UsuarioIdDueno AS duenoReal FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));
        THROW 50012, 'No tienes permiso para eliminar este registro DNS.', 1;
    END

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.RegistroDns SET Estado = 'ELIMINADA' WHERE Id = @RegistroId;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen)
        VALUES (@UsuarioId, 'RegistroDns', @RegistroId, 'ELIMINAR', @IpOrigen);

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT @Subdominio AS Subdominio, @TipoRegistro AS TipoRegistro, @Valor AS Valor;
END
GO

/* -------------------------------------------------------------------------
   sp_ListarTodosRegistrosDns (admin) — defensa en profundidad: revalida
   EsAdmin DENTRO del SP aunque el controller ya exija [Authorize(Roles=
   "Admin")]. Un bug futuro en el mapeo de roles del JWT no basta por sí
   solo para filtrar datos de otros usuarios si esta capa también lo exige.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ListarTodosRegistrosDns
    @UsuarioIdSolicitante INT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioIdSolicitante AND EsAdmin = 1 AND Activo = 1)
        THROW 50045, 'No autorizado.', 1;

    SELECT r.Id, r.UsuarioId, u.Correo AS UsuarioCorreo, r.Subdominio, r.TipoRegistro, r.Valor,
           r.Estado, r.FechaCreacion
    FROM dbo.RegistroDns r
    INNER JOIN dbo.Usuario u ON u.Id = r.UsuarioId
    WHERE r.Estado <> 'ELIMINADA'
    ORDER BY r.FechaCreacion DESC;
END
GO

/* -------------------------------------------------------------------------
   sp_EliminarRegistroDnsAdmin — mismo criterio de defensa en profundidad.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_EliminarRegistroDnsAdmin
    @UsuarioIdSolicitante INT,
    @RegistroId           INT,
    @IpOrigen             VARCHAR(45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioIdSolicitante AND EsAdmin = 1 AND Activo = 1)
        THROW 50045, 'No autorizado.', 1;

    DECLARE @Subdominio SYSNAME, @TipoRegistro VARCHAR(10), @Valor VARCHAR(255), @Estado VARCHAR(20), @DuenoOriginal INT;
    SELECT @Subdominio = Subdominio, @TipoRegistro = TipoRegistro, @Valor = Valor,
           @Estado = Estado, @DuenoOriginal = UsuarioId
    FROM dbo.RegistroDns WHERE Id = @RegistroId;

    IF @Subdominio IS NULL OR @Estado = 'ELIMINADA'
        THROW 50011, 'El registro DNS no existe.', 1;

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.RegistroDns SET Estado = 'ELIMINADA' WHERE Id = @RegistroId;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioIdSolicitante, 'RegistroDns', @RegistroId, 'ELIMINAR_ADMIN', @IpOrigen,
                (SELECT @DuenoOriginal AS duenoOriginal FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT @Subdominio AS Subdominio, @TipoRegistro AS TipoRegistro, @Valor AS Valor;
END
GO

/* -------------------------------------------------------------------------
   Propaga EsAdmin (columna agregada arriba) a los dos puntos donde ya se
   arma el perfil del usuario, sin editar 002/008 en su archivo original —
   CREATE OR ALTER sobre el mismo objeto, mismo patrón ya usado por 007/008
   para extender comportamiento de forma aditiva. Cuerpo idéntico al
   original; único cambio real es sumar EsAdmin al SELECT final de cada uno.
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

    SELECT Id, Nombre, Correo, AvatarUrl, Proveedor, FechaCreacion, UltimoLogin, EsAdmin
    FROM dbo.Usuario
    WHERE Id = @UsuarioId;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_ObtenerPerfilUsuario
    @UsuarioId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        Id          AS UsuarioId,
        Nombre,
        Correo,
        AvatarUrl,
        Proveedor,
        FechaCreacion,
        UltimoLogin,
        EsAdmin
    FROM dbo.Usuario
    WHERE Id = @UsuarioId AND Activo = 1;
END
GO
