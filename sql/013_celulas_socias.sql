/* =========================================================================
   ABA - Plataforma de Hosting DB & Servicios para Desarrolladores
   Módulo 8: Aprovisionamiento para células socias (API server-to-server)

   Contexto completo: Aba/INVESTIGACION-PROXY-MYSQL.md § 8. Cada célula socia
   del grupo (hoy 11, incluyendo la propia) recibe una API key (alta manual,
   entregada fuera de banda) que su BACKEND — nunca su frontend — usa para
   pedir el aprovisionamiento de bases MySQL en nuestra plataforma, igual que
   nosotros mismos consumimos SQL Server como servicio externo.

   Regla de oro de siempre: el backend NUNCA decide el prefijo ni valida la
   API key en C# — solo hashea la key recibida (SHA-256, mecánico, no es
   lógica de negocio) y se la pasa a sp_ValidarApiKeyCelula. Quién es la
   célula, si está activa, y cuál es su prefijo lo decide únicamente esta
   base de datos.
   ========================================================================= */

USE ABA_Control;
GO

CREATE TABLE dbo.CelulasSocias (
    Id            INT IDENTITY PRIMARY KEY,
    NombreCelula  VARCHAR(50)   NOT NULL UNIQUE,
    -- Prefijo lo fija ABA al dar de alta, nunca la célula — es lo que fuerza el
    -- aislamiento de nombres real (ver § 8.5 de la investigación). Mismo alfabeto
    -- que MySqlProvisioningService.IdentificadorValido ([a-zA-Z0-9_]), en minúscula
    -- por convención, y corto porque se concatena con el sufijo del backend dentro
    -- del límite de 64 caracteres de un identificador de MySQL.
    Prefijo       VARCHAR(20)   NOT NULL UNIQUE
                  CHECK (Prefijo NOT LIKE '%[^a-z0-9_]%' AND LEN(Prefijo) BETWEEN 2 AND 20),
    -- Hash SHA-256 (32 bytes) de la API key real — la key en texto plano se entrega
    -- una sola vez fuera de banda al dar de alta y nunca se guarda acá, mismo
    -- criterio que las contraseñas de MySQL que ya se entregan a los estudiantes.
    ApiKeyHash    VARBINARY(64) NOT NULL UNIQUE,
    Activo        BIT NOT NULL DEFAULT 1,
    FechaCreacion DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- Alta manual (hoy 11 células, no hay flujo self-service — ver § 12 de la
-- investigación). Se corre a mano contra ABA_Control con el hash ya calculado
-- fuera de banda (ver Aba/INFRAESTRUCTURA.md § 9 para el procedimiento).
CREATE OR ALTER PROCEDURE dbo.sp_AltaCelulaSocia
    @NombreCelula VARCHAR(50),
    @Prefijo      VARCHAR(20),
    @ApiKeyHash   VARBINARY(64)
AS
BEGIN
    SET NOCOUNT ON;

    -- Re-validación defensiva: el CHECK de la tabla ya cubre esto, pero un SP de
    -- alta nunca debe asumir que el único camino de entrada es válido por sí solo.
    IF @Prefijo LIKE '%[^a-z0-9_]%' OR LEN(@Prefijo) NOT BETWEEN 2 AND 20
    BEGIN
        THROW 50001, 'Prefijo invalido: solo minusculas, digitos y guion bajo, entre 2 y 20 caracteres.', 1;
    END

    IF EXISTS (SELECT 1 FROM dbo.CelulasSocias WHERE NombreCelula = @NombreCelula OR Prefijo = @Prefijo)
    BEGIN
        THROW 50002, 'Ya existe una celula socia con ese nombre o prefijo.', 1;
    END

    INSERT INTO dbo.CelulasSocias (NombreCelula, Prefijo, ApiKeyHash)
    VALUES (@NombreCelula, @Prefijo, @ApiKeyHash);

    SELECT Id, NombreCelula, Prefijo, Activo, FechaCreacion
    FROM dbo.CelulasSocias
    WHERE Id = SCOPE_IDENTITY();
END
GO

-- Invocado por ApiKeyAuthenticationHandler en CADA request autenticado con API
-- key. Devuelve un solo row si la key es válida y la célula está activa; result
-- set vacío en cualquier otro caso (key inexistente, revocada, o desactivada) —
-- el handler nunca distingue el motivo exacto en la respuesta HTTP (evita dar
-- pistas a quien esté probando keys al azar).
CREATE OR ALTER PROCEDURE dbo.sp_ValidarApiKeyCelula
    @ApiKeyHash VARBINARY(64)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id AS CelulaId, NombreCelula, Prefijo
    FROM dbo.CelulasSocias
    WHERE ApiKeyHash = @ApiKeyHash AND Activo = 1;
END
GO

/* =========================================================================
   Tabla SEPARADA de dbo.BaseDeDatos (decisión deliberada, 2026-08-06): la
   investigación original proponía reusar la misma tabla/máquina de estados
   con UsuarioId nullable + CelulaSociaId, pero dbo.BaseDeDatos ya tiene 6 SPs
   en producción real (sp_ConfirmarAprovisionamiento, sp_EliminarBaseDatos,
   sp_ObtenerBaseDatosPorId, sp_ObtenerCredencialesBaseDatos,
   sp_DesactivarBaseDatos, sp_ActualizarEspacioUsado) que usan
   "IF @UsuarioId IS NULL THROW 'no existe'" como ÚNICA forma de detectar
   fila inexistente — con UsuarioId legítimamente NULL para una fila de
   célula socia, ese patrón se rompe (falso "no existe") para estudiantes
   reales. Sin suite de tests en este repo, tocar esos 6 SPs para arreglarlo
   es más riesgo del que vale la pena correr. Costo aceptado de esta
   decisión: MySqlQuotaEnforcementService (y cualquier vigilancia automática
   futura) NO cubre estas filas hasta que se extienda aparte — pendiente,
   no bloqueante mientras no haya bases de células socias todavía.
   ========================================================================= */

-- Igual que Auditoria.Entidad='BaseDeDatos' para estudiantes, pero distinguible.
ALTER TABLE dbo.Auditoria DROP CONSTRAINT CK_Auditoria_Entidad;
GO
ALTER TABLE dbo.Auditoria ADD CONSTRAINT CK_Auditoria_Entidad
    CHECK (Entidad IN ('Usuario', 'BaseDeDatos', 'UsuarioIp', 'BaseDeDatosSocio'));
GO

CREATE TABLE dbo.BaseDeDatosSocio (
    Id                 INT            IDENTITY(1,1) PRIMARY KEY,
    CelulaSociaId      INT            NOT NULL,
    MotorId            TINYINT        NOT NULL,
    NombreBD           VARCHAR(63)    NOT NULL,
    UsuarioBD          VARCHAR(32)    NOT NULL,
    PasswordCifrado    VARBINARY(256) NOT NULL,
    Host               VARCHAR(255)   NOT NULL,
    Puerto             INT            NOT NULL,
    Estado             VARCHAR(20)    NOT NULL DEFAULT 'PENDIENTE',
    EspacioMaximoMB    SMALLINT       NOT NULL DEFAULT 20,
    EspacioUtilizadoMB DECIMAL(10,2)  NOT NULL DEFAULT 0,
    FechaCreacion      DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    UltimaActividad    DATETIME2      NULL,

    CONSTRAINT FK_BaseDeDatosSocio_Celula FOREIGN KEY (CelulaSociaId)
        REFERENCES dbo.CelulasSocias (Id),
    CONSTRAINT FK_BaseDeDatosSocio_Motor FOREIGN KEY (MotorId)
        REFERENCES dbo.MotorBaseDatos (Id),
    CONSTRAINT CK_BaseDeDatosSocio_Estado CHECK (Estado IN ('PENDIENTE', 'ACTIVA', 'ELIMINADA')),
    CONSTRAINT UQ_BaseDeDatosSocio_Motor_Nombre UNIQUE (MotorId, NombreBD),
    CONSTRAINT UQ_BaseDeDatosSocio_Motor_UsuarioBD UNIQUE (MotorId, UsuarioBD)
);
GO

CREATE INDEX IX_BaseDeDatosSocio_CelulaSociaId ON dbo.BaseDeDatosSocio (CelulaSociaId);
GO

/* -------------------------------------------------------------------------
   sp_AprovisionarBaseDatosSocio — equivalente a sp_AprovisionarBaseDatos
   (mismo mecanismo de contraseña/cifrado, copiado deliberadamente en vez de
   compartido: CRYPT_GEN_RANDOM no se puede envolver en una función, ver
   comentario de sp_AprovisionarBaseDatos en 002_stored_procedures.sql).
   Diferencia clave: el nombre se arma <prefijo>_<sufijo> — el prefijo lo
   fuerza esta tabla (CelulasSocias.Prefijo), la célula nunca lo propone.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_AprovisionarBaseDatosSocio
    @CelulaSociaId INT,
    @NombreMotor   VARCHAR(30)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Prefijo VARCHAR(20);
    SELECT @Prefijo = Prefijo FROM dbo.CelulasSocias WHERE Id = @CelulaSociaId AND Activo = 1;
    IF @Prefijo IS NULL
        THROW 50101, 'Celula socia no existe o esta inactiva.', 1;

    -- MySQL únicamente (ver Aba/INVESTIGACION-PROXY-MYSQL.md § 11) — no se ofrece SQLServer
    -- para células socias, a diferencia del flujo de estudiantes.
    DECLARE @MotorId TINYINT, @Host VARCHAR(255), @Puerto INT;
    SELECT @MotorId = Id, @Host = HostDefault, @Puerto = PuertoDefault
    FROM dbo.MotorBaseDatos
    WHERE Nombre = @NombreMotor AND Nombre = 'MySQL' AND Activo = 1;

    IF @MotorId IS NULL
        THROW 50102, 'Motor de base de datos no soportado para celulas socias (solo MySQL).', 1;

    -- Tope de abuso por célula — generoso a propósito (no hay límite definido en la
    -- investigación original; valor provisional, ajustar según uso real observado).
    IF (SELECT COUNT(*) FROM dbo.BaseDeDatosSocio WHERE CelulaSociaId = @CelulaSociaId AND Estado IN ('ACTIVA', 'PENDIENTE')) >= 500
        THROW 50103, 'Se alcanzo el limite de bases de datos activas para esta celula.', 1;

    DECLARE @Sufijo VARCHAR(10) = LEFT(REPLACE(CONVERT(VARCHAR(36), NEWID()), '-', ''), 10);
    DECLARE @NombreBD VARCHAR(63) = CONCAT(@Prefijo, '_', @Sufijo);
    -- Prefijo <= 20 + '_' + 8 <= 29 caracteres: bajo el límite de 32 de un usuario MySQL.
    DECLARE @UsuarioBD VARCHAR(32) = CONCAT(@Prefijo, '_', LEFT(@Sufijo, 8));

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

    DECLARE @BaseDeDatosSocioId INT;

    BEGIN TRY
        BEGIN TRAN;

        INSERT INTO dbo.BaseDeDatosSocio
            (CelulaSociaId, MotorId, NombreBD, UsuarioBD, PasswordCifrado, Host, Puerto, Estado)
        VALUES
            (@CelulaSociaId, @MotorId, @NombreBD, @UsuarioBD, @PasswordCifrado, @Host, @Puerto, 'PENDIENTE');

        SET @BaseDeDatosSocioId = SCOPE_IDENTITY();

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, Detalle)
        VALUES (NULL, 'BaseDeDatosSocio', @BaseDeDatosSocioId, 'APROVISIONAR_SOLICITADO',
                (SELECT @CelulaSociaId AS celulaSociaId, @NombreBD AS nombreBD FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT
        @BaseDeDatosSocioId AS BaseDeDatosId,
        @NombreBD           AS NombreBD,
        @UsuarioBD          AS UsuarioBD,
        @PasswordPlano      AS PasswordTemporal,
        @Host               AS Host,
        @Puerto             AS Puerto,
        @NombreMotor        AS Motor;
END
GO

-- Fase 2, igual que sp_ConfirmarAprovisionamiento pero con chequeo de existencia
-- independiente de cualquier columna nullable (no hereda el bug de sentinel de
-- la tabla de estudiantes, ver comentario de cabecera de esta sección).
CREATE OR ALTER PROCEDURE dbo.sp_ConfirmarAprovisionamientoSocio
    @BaseDeDatosSocioId INT,
    @Exitoso             BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.BaseDeDatosSocio WHERE Id = @BaseDeDatosSocioId AND Estado = 'PENDIENTE')
        THROW 50104, 'La base de datos no existe o ya fue confirmada.', 1;

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.BaseDeDatosSocio
        SET Estado          = CASE WHEN @Exitoso = 1 THEN 'ACTIVA' ELSE 'ELIMINADA' END,
            UltimaActividad = SYSUTCDATETIME()
        WHERE Id = @BaseDeDatosSocioId;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion)
        VALUES (NULL, 'BaseDeDatosSocio', @BaseDeDatosSocioId,
                CASE WHEN @Exitoso = 1 THEN 'APROVISIONAR_OK' ELSE 'APROVISIONAR_FALLIDO' END);

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO

-- Control BOLA equivalente al de sp_ObtenerBaseDatosPorId: una célula solo puede
-- ver sus propias bases. Se usa tanto para el GET de consulta como, antes de
-- desaprovisionar, para resolver NombreBD/UsuarioBD reales que necesita el motor.
CREATE OR ALTER PROCEDURE dbo.sp_ObtenerBaseDatosSocioPorId
    @BaseDeDatosSocioId INT,
    @CelulaSociaId       INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, NombreBD, UsuarioBD, Host, Puerto, Estado,
           EspacioMaximoMB, EspacioUtilizadoMB, FechaCreacion
    FROM dbo.BaseDeDatosSocio
    WHERE Id = @BaseDeDatosSocioId AND CelulaSociaId = @CelulaSociaId;
END
GO

-- Soft-delete, mismo criterio que sp_DesactivarBaseDatos (nunca se borra la fila).
-- El backend, ANTES de llamar esto, ya usó sp_ObtenerBaseDatosSocioPorId para
-- confirmar pertenencia y obtener NombreBD/UsuarioBD para el DROP real en MySQL.
CREATE OR ALTER PROCEDURE dbo.sp_DesactivarBaseDatosSocio
    @BaseDeDatosSocioId INT,
    @CelulaSociaId       INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (
        SELECT 1 FROM dbo.BaseDeDatosSocio
        WHERE Id = @BaseDeDatosSocioId AND CelulaSociaId = @CelulaSociaId AND Estado <> 'ELIMINADA'
    )
        THROW 50105, 'La base de datos no existe, no pertenece a esta celula, o ya fue eliminada.', 1;

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.BaseDeDatosSocio
        SET Estado = 'ELIMINADA', UltimaActividad = SYSUTCDATETIME()
        WHERE Id = @BaseDeDatosSocioId;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion)
        VALUES (NULL, 'BaseDeDatosSocio', @BaseDeDatosSocioId, 'DESACTIVAR');

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO
