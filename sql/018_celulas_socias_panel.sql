/* =========================================================================
   ABA - Plataforma de Hosting DB & Servicios para Desarrolladores
   Modulo 8 (continuacion): alta de celulas socias desde el panel de admin
   en vez de a mano por sqlcmd (ver Aba/ALTA-CELULA-SOCIA.md paragrafo Pendiente
   - "Evaluar si este alta manual eventualmente necesita un endpoint admin
   protegido, AdminDnsController como precedente"). Mismo patron que ese
   controller: [Authorize(Roles="Admin")] en el backend + revalidacion de
   EsAdmin DENTRO de cada SP como defensa en profundidad (sql/011).

   La API key ahora la genera este SP (mismo mecanismo CRYPT_GEN_RANDOM que ya
   se usa para contrasenas de MySQL) en vez de que el admin corra 'openssl rand'
   a mano y pegue el hash - se entrega en texto plano UNA sola vez en la
   respuesta, igual que passwordTemporal. sp_AltaCelulaSocia (013) se deja tal
   cual para el flujo manual documentado, por si el panel no esta disponible.
   ========================================================================= */

USE ABA_Control;
GO

-- Auditoria de altas/bajas/rotaciones de celulas socias desde el panel,
-- distinguible de 'BaseDeDatosSocio' (mismo criterio que 013 al agregar esa).
ALTER TABLE dbo.Auditoria DROP CONSTRAINT CK_Auditoria_Entidad;
GO
ALTER TABLE dbo.Auditoria ADD CONSTRAINT CK_Auditoria_Entidad
    CHECK (Entidad IN ('Usuario', 'BaseDeDatos', 'UsuarioIp', 'BaseDeDatosSocio', 'CelulaSocia'));
GO

/* -------------------------------------------------------------------------
   sp_AltaCelulaSociaAutoKey
   Equivalente a sp_AltaCelulaSocia (013) pero generando la key acá adentro
   en vez de recibirla ya hasheada. 24 bytes de CRYPT_GEN_RANDOM en hex crudo
   (48 caracteres) - mismo tamano que el 'openssl rand -hex 24' manual que
   reemplaza. No pasa por el charset "tipeable" de las contrasenas de MySQL
   porque una API key nunca se tipea a mano, solo se copia una vez.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_AltaCelulaSociaAutoKey
    @UsuarioIdSolicitante INT,
    @NombreCelula         VARCHAR(50),
    @Prefijo              VARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioIdSolicitante AND EsAdmin = 1 AND Activo = 1)
        THROW 50107, 'No autorizado.', 1;

    IF @Prefijo LIKE '%[^a-z0-9_]%' OR LEN(@Prefijo) NOT BETWEEN 2 AND 20
        THROW 50001, 'Prefijo invalido: solo minusculas, digitos y guion bajo, entre 2 y 20 caracteres.', 1;

    IF EXISTS (SELECT 1 FROM dbo.CelulasSocias WHERE NombreCelula = @NombreCelula OR Prefijo = @Prefijo)
        THROW 50002, 'Ya existe una celula socia con ese nombre o prefijo.', 1;

    DECLARE @ApiKeyBytes VARBINARY(24) = CRYPT_GEN_RANDOM(24);
    DECLARE @ApiKeyPlano VARCHAR(48) = LOWER(CONVERT(VARCHAR(48), @ApiKeyBytes, 2));
    DECLARE @ApiKeyHash  VARBINARY(64) = HASHBYTES('SHA2_256', @ApiKeyPlano);

    DECLARE @CelulaId INT;

    BEGIN TRY
        BEGIN TRAN;

        INSERT INTO dbo.CelulasSocias (NombreCelula, Prefijo, ApiKeyHash)
        VALUES (@NombreCelula, @Prefijo, @ApiKeyHash);

        SET @CelulaId = SCOPE_IDENTITY();

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, Detalle)
        VALUES (@UsuarioIdSolicitante, 'CelulaSocia', @CelulaId, 'ALTA_DESDE_PANEL',
                (SELECT @NombreCelula AS nombreCelula, @Prefijo AS prefijo FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT Id, NombreCelula, Prefijo, Activo, FechaCreacion, @ApiKeyPlano AS ApiKeyPlano
    FROM dbo.CelulasSocias
    WHERE Id = @CelulaId;
END
GO

/* -------------------------------------------------------------------------
   sp_ListarCelulasSocias (admin) - nunca devuelve ApiKeyHash, ni falta que
   hace: no hay forma de mostrar una key ya generada, solo rotarla.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ListarCelulasSocias
    @UsuarioIdSolicitante INT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioIdSolicitante AND EsAdmin = 1 AND Activo = 1)
        THROW 50107, 'No autorizado.', 1;

    SELECT Id, NombreCelula, Prefijo, Activo, FechaCreacion
    FROM dbo.CelulasSocias
    ORDER BY FechaCreacion DESC;
END
GO

/* -------------------------------------------------------------------------
   sp_CambiarEstadoCelulaSocia - reemplaza el UPDATE manual de
   ALTA-CELULA-SOCIA.md paragrafo 7 (dar de baja). Nunca se borra la fila,
   mismo criterio que el resto de la plataforma.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_CambiarEstadoCelulaSocia
    @UsuarioIdSolicitante INT,
    @CelulaSociaId        INT,
    @Activo                BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioIdSolicitante AND EsAdmin = 1 AND Activo = 1)
        THROW 50107, 'No autorizado.', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.CelulasSocias WHERE Id = @CelulaSociaId)
        THROW 50108, 'La celula socia no existe.', 1;

    UPDATE dbo.CelulasSocias SET Activo = @Activo WHERE Id = @CelulaSociaId;

    INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion)
    VALUES (@UsuarioIdSolicitante, 'CelulaSocia', @CelulaSociaId,
            CASE WHEN @Activo = 1 THEN 'REACTIVAR' ELSE 'DESACTIVAR' END);

    SELECT Id, NombreCelula, Prefijo, Activo, FechaCreacion
    FROM dbo.CelulasSocias
    WHERE Id = @CelulaSociaId;
END
GO

/* -------------------------------------------------------------------------
   sp_RotarApiKeyCelulaSocia - reemplaza el UPDATE manual de
   ALTA-CELULA-SOCIA.md paragrafo 8 (key comprometida). Cierra el pendiente
   anotado ahi mismo ("Convertir el UPDATE manual del paso 8 en un SP propio").
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_RotarApiKeyCelulaSocia
    @UsuarioIdSolicitante INT,
    @CelulaSociaId        INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioIdSolicitante AND EsAdmin = 1 AND Activo = 1)
        THROW 50107, 'No autorizado.', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.CelulasSocias WHERE Id = @CelulaSociaId)
        THROW 50108, 'La celula socia no existe.', 1;

    DECLARE @ApiKeyBytes VARBINARY(24) = CRYPT_GEN_RANDOM(24);
    DECLARE @ApiKeyPlano VARCHAR(48) = LOWER(CONVERT(VARCHAR(48), @ApiKeyBytes, 2));
    DECLARE @ApiKeyHash  VARBINARY(64) = HASHBYTES('SHA2_256', @ApiKeyPlano);

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.CelulasSocias SET ApiKeyHash = @ApiKeyHash WHERE Id = @CelulaSociaId;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion)
        VALUES (@UsuarioIdSolicitante, 'CelulaSocia', @CelulaSociaId, 'ROTAR_API_KEY');

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT Id, NombreCelula, Prefijo, Activo, FechaCreacion, @ApiKeyPlano AS ApiKeyPlano
    FROM dbo.CelulasSocias
    WHERE Id = @CelulaSociaId;
END
GO
