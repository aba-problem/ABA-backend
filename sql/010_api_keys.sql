/* =========================================================================
   ABA - Entregable 3, Módulo IA como Servicio (API Keys)
   Script aditivo — no modifica 001-009.

   La key completa NUNCA se guarda en texto plano ni se puede volver a
   consultar tras la creación (a diferencia de las contraseñas de BD, que
   se cifran de forma reversible porque el dashboard las vuelve a mostrar —
   una API key no necesita ese caso de uso, así que aquí se aplica el
   estándar más estricto posible: solo hash, irreversible).

   Formato de la key entregada al cliente: 'sk_' + Prefijo(8) + Secreto(24).
   El Prefijo se guarda en claro (identifica la fila sin escanear toda la
   tabla ni sin exponer nada explotable); KeyHash es SHA-256 de la key
   COMPLETA (con prefijo incluido) — es literalmente lo que el backend
   recibe en el header X-API-Key y compara en tiempo constante.

   Códigos de error de este módulo: 50030-50039.
   ========================================================================= */

CREATE TABLE dbo.ApiKey (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    UsuarioId       INT           NOT NULL,
    Prefijo         CHAR(8)       NOT NULL,
    KeyHash         BINARY(32)    NOT NULL,   -- SHA-256 de la key completa, jamás la key en claro
    Activa          BIT           NOT NULL DEFAULT 1,
    FechaCreacion   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    FechaRevocacion DATETIME2     NULL,
    UltimoUso       DATETIME2     NULL,

    CONSTRAINT FK_ApiKey_Usuario FOREIGN KEY (UsuarioId) REFERENCES dbo.Usuario (Id)
);
GO

-- Búsqueda de la candidata en la autenticación (hot path de /ai/completar) por prefijo.
CREATE INDEX IX_ApiKey_Prefijo ON dbo.ApiKey (Prefijo) WHERE Activa = 1;
GO

CREATE TABLE dbo.ApiKeyUso (
    Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    ApiKeyId        INT           NOT NULL,
    Endpoint        VARCHAR(200)  NOT NULL,
    TokensEstimados INT           NULL,
    Timestamp       DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_ApiKeyUso_ApiKey FOREIGN KEY (ApiKeyId) REFERENCES dbo.ApiKey (Id)
);
GO

CREATE INDEX IX_ApiKeyUso_ApiKeyId_Timestamp ON dbo.ApiKeyUso (ApiKeyId, Timestamp);
GO

ALTER TABLE dbo.Auditoria DROP CONSTRAINT IF EXISTS CK_Auditoria_Entidad;
GO
ALTER TABLE dbo.Auditoria ADD CONSTRAINT CK_Auditoria_Entidad
    CHECK (Entidad IN ('Usuario', 'BaseDeDatos', 'UsuarioIp', 'WorkspaceN8N', 'ApiKey'));
GO

/* -------------------------------------------------------------------------
   sp_CrearApiKey
   Límite de 5 keys ACTIVAS por usuario (protección contra abuso/scraping
   de cuota, mismo espíritu que el límite de 5 bases de datos). El backend
   solo pasa @UsuarioId (del claim JWT) — nunca decide el valor de la key.
   ------------------------------------------------------------------------- */
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

    -- CSPRNG (CRYPT_GEN_RANDOM) — alfabeto alfanumérico sin símbolos: la key viaja en un
    -- header HTTP y en clientes CLI/SDK de terceros, evitar caracteres que puedan requerir
    -- escapado o generar problemas de copiado/pegado.
    DECLARE @Charset VARCHAR(62) = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789';
    DECLARE @Longitud INT = 32; -- 8 prefijo + 24 secreto
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
    DECLARE @KeyCompleta VARCHAR(50) = CONCAT('sk_', @Aleatorio); -- 'sk_' + prefijo(8) + secreto(24)
    DECLARE @KeyHash BINARY(32) = HASHBYTES('SHA2_256', @KeyCompleta);

    DECLARE @ApiKeyId INT;

    BEGIN TRY
        BEGIN TRAN;

        INSERT INTO dbo.ApiKey (UsuarioId, Prefijo, KeyHash)
        VALUES (@UsuarioId, @Prefijo, @KeyHash);

        SET @ApiKeyId = SCOPE_IDENTITY();

        -- Detalle SIN el hash ni la key (control 5.8) — solo el prefijo, que es lo que el
        -- usuario ve en su propio listado y no sirve para autenticar por sí solo.
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
        @KeyCompleta  AS KeyCompleta,  -- única vez en texto plano
        SYSUTCDATETIME() AS FechaCreacion;
END
GO

/* -------------------------------------------------------------------------
   sp_ListarApiKeys — nunca devuelve KeyHash ni la key completa.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ListarApiKeys
    @UsuarioId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, Prefijo, Activa, FechaCreacion, FechaRevocacion, UltimoUso
    FROM dbo.ApiKey
    WHERE UsuarioId = @UsuarioId
    ORDER BY FechaCreacion DESC;
END
GO

/* -------------------------------------------------------------------------
   sp_RevocarApiKey — BOLA (control 3.1): valida dueño dentro del SP.
   50011 = no existe, 50012 = no es el dueño — mismos códigos que Dashboard,
   ambos se traducen a 404 en el backend, nunca 403 (no confirmar existencia).
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_RevocarApiKey
    @UsuarioId INT,
    @ApiKeyId  INT,
    @IpOrigen  VARCHAR(45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UsuarioIdDueno INT, @YaRevocada BIT;
    SELECT @UsuarioIdDueno = UsuarioId, @YaRevocada = ~Activa
    FROM dbo.ApiKey WHERE Id = @ApiKeyId;

    IF @UsuarioIdDueno IS NULL
        THROW 50011, 'La API key no existe.', 1;

    IF @UsuarioIdDueno <> @UsuarioId
    BEGIN
        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, Detalle)
        VALUES (@UsuarioId, 'ApiKey', @ApiKeyId, 'ACCESO_REVOCAR_RECHAZADO',
                (SELECT @UsuarioIdDueno AS duenoReal FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));
        THROW 50012, 'No tienes permiso para revocar esta API key.', 1;
    END

    IF @YaRevocada = 1
        RETURN; -- no-op idempotente, no es un error

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.ApiKey SET Activa = 0, FechaRevocacion = SYSUTCDATETIME() WHERE Id = @ApiKeyId;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen)
        VALUES (@UsuarioId, 'ApiKey', @ApiKeyId, 'REVOCAR', @IpOrigen);

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO

/* -------------------------------------------------------------------------
   sp_ObtenerApiKeyPorPrefijo
   Único punto de lectura para el ApiKeyAuthenticationHandler (hot path).
   Devuelve la fila candidata (con su hash) para que el backend compare en
   tiempo constante — el SP NUNCA compara el secreto, eso requeriría que
   el hash viajara/computara en SQL en cada request de IA, y además la
   comparación de tiempo constante es responsabilidad de la capa que recibe
   el secreto directamente (CryptographicOperations.FixedTimeEquals en C#).
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ObtenerApiKeyPorPrefijo
    @Prefijo CHAR(8)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, UsuarioId, KeyHash, Activa
    FROM dbo.ApiKey
    WHERE Prefijo = @Prefijo AND Activa = 1;
END
GO

/* -------------------------------------------------------------------------
   sp_RegistrarUsoApiKey
   Llamado por el backend tras cada autenticación exitosa vía ApiKey scheme.
   Alimenta el reporte de cuotas sin lógica adicional de negocio en C#.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_RegistrarUsoApiKey
    @ApiKeyId        INT,
    @Endpoint        VARCHAR(200),
    @TokensEstimados INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.ApiKeyUso (ApiKeyId, Endpoint, TokensEstimados)
    VALUES (@ApiKeyId, @Endpoint, @TokensEstimados);

    UPDATE dbo.ApiKey SET UltimoUso = SYSUTCDATETIME() WHERE Id = @ApiKeyId;
END
GO

/* -------------------------------------------------------------------------
   sp_ObtenerConsumoApiKey — agrega ApiKeyUso por key, BOLA validado (el
   usuario solo puede pedir el consumo de SUS PROPIAS keys).
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ObtenerConsumoApiKey
    @UsuarioId INT,
    @ApiKeyId  INT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.ApiKey WHERE Id = @ApiKeyId AND UsuarioId = @UsuarioId)
        THROW 50011, 'La API key no existe.', 1; -- incluye "no es tuya", mismo 404 sin distinguir

    SELECT
        CAST(Timestamp AS DATE) AS Dia,
        COUNT(*)                AS Llamadas,
        SUM(ISNULL(TokensEstimados, 0)) AS TokensTotales
    FROM dbo.ApiKeyUso
    WHERE ApiKeyId = @ApiKeyId AND Timestamp >= DATEADD(DAY, -30, SYSUTCDATETIME())
    GROUP BY CAST(Timestamp AS DATE)
    ORDER BY Dia DESC;
END
GO
