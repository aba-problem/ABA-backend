/* =========================================================================
   ABA - Integración PolyService IA + MongoDB Provisioning API.

   PolyService IA (servicio externo de completions) no toca el esquema:
   usa el ApiKey/rate-limit/auditoría de consumo que sql/010 ya creó para
   /ai/completar (sp_RegistrarUsoApiKey). No hay nada nuevo que crear aquí
   para ese lado.

   MongoDB Provisioning API SÍ es un motor nuevo dentro del mismo patrón de
   dos fases que MySQL/SQLServer (sql/002: sp_AprovisionarBaseDatos reserva,
   el backend crea en el motor real, sp_ConfirmarAprovisionamiento confirma).
   Diferencia clave: para MySQL/SQLServer, SQL Server genera el nombre/usuario/
   password y el backend los usa tal cual para crear el login real. Para
   MongoDB, el PROVEEDOR EXTERNO genera su propio usuario/password/id al
   crear la base — el backend no los conoce hasta después de llamar a la API
   externa. Por eso hace falta una confirmación "externa" que reciba esos
   valores reales y los persista (cifrando la password DENTRO de SQL Server,
   igual que en cualquier otro flujo — el backend nunca cifra en C#, sólo
   pasa el texto plano como parámetro dentro de la misma llamada).
   ========================================================================= */

USE ABA_Control;
GO

SET QUOTED_IDENTIFIER ON;
GO

/* -------------------------------------------------------------------------
   Motor nuevo. HostDefault/PuertoDefault quedan como referencia informativa
   (el proveedor es multi-tenant sobre un mismo cluster) — sp_ConfirmarAprovisionamientoExterno
   sobreescribe Host/Puerto con los valores reales que devuelve el proveedor
   por si en el futuro el proveedor asigna hosts distintos por base.
   ------------------------------------------------------------------------- */
-- 'mongo.szapatar.dev' es la URL de la API de aprovisionamiento (BaseAddress del
-- HttpClient en Program.cs) — el host de CONEXIÓN real a la base es un subdominio
-- distinto, documentado en Aba/external_services/mongo_contract.md: "Host de
-- conexión a MongoDB: connection.szapatar.dev". HostDefault existía con el valor
-- de la API por error; se corrige acá para quien ya corrió esta migración.
IF NOT EXISTS (SELECT 1 FROM dbo.MotorBaseDatos WHERE Nombre = 'MongoDB')
BEGIN
    INSERT INTO dbo.MotorBaseDatos (Nombre, HostDefault, PuertoDefault, Activo)
    VALUES ('MongoDB', 'connection.szapatar.dev', 27017, 1);
END
ELSE
BEGIN
    UPDATE dbo.MotorBaseDatos
    SET HostDefault = 'connection.szapatar.dev'
    WHERE Nombre = 'MongoDB' AND HostDefault = 'mongo.szapatar.dev';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.BaseDeDatos') AND name = 'MongoExternalId'
)
BEGIN
    ALTER TABLE dbo.BaseDeDatos ADD MongoExternalId VARCHAR(100) NULL;
END
GO

/* -------------------------------------------------------------------------
   sp_ConfirmarAprovisionamientoExterno
   Variante de sp_ConfirmarAprovisionamiento (sql/002) para motores donde el
   NOMBRE DE USUARIO/PASSWORD REALES los decide el proveedor externo, no
   SQL Server. @Exitoso=1 sobreescribe UsuarioBD/PasswordCifrado/Host/Puerto/
   MongoExternalId con los valores reales devueltos por el proveedor y pasa
   a 'ACTIVA'. @Exitoso=0 se comporta igual que sp_ConfirmarAprovisionamiento
   (pasa a 'ELIMINADA', sin tocar las columnas de credencial).
   La password llega en texto plano como parámetro y se cifra AQUÍ DENTRO
   (mismo patrón ENCRYPTBYKEY/SymKey_ABA_Credenciales que sp_AprovisionarBaseDatos)
   — el backend nunca cifra ni persiste la password en texto plano.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ConfirmarAprovisionamientoExterno
    @BaseDeDatosId INT,
    @Exitoso       BIT,
    @UsuarioBdReal VARCHAR(100) = NULL,
    @PasswordPlano VARCHAR(200) = NULL,
    @Host          VARCHAR(255) = NULL,
    @Puerto        INT          = NULL,
    @ExternalId    VARCHAR(100) = NULL,
    @IpOrigen      VARCHAR(45)  = NULL
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

    IF @Exitoso = 1 AND (@UsuarioBdReal IS NULL OR @PasswordPlano IS NULL OR @ExternalId IS NULL)
        THROW 50013, 'Confirmación externa exitosa requiere usuario, password y externalId reales.', 1;

    DECLARE @PasswordCifrado VARBINARY(256);

    IF @Exitoso = 1
    BEGIN
        OPEN SYMMETRIC KEY SymKey_ABA_Credenciales DECRYPTION BY CERTIFICATE Cert_ABA_Credenciales;
        SET @PasswordCifrado = ENCRYPTBYKEY(KEY_GUID('SymKey_ABA_Credenciales'), @PasswordPlano);
        CLOSE SYMMETRIC KEY SymKey_ABA_Credenciales;
    END

    BEGIN TRY
        BEGIN TRAN;

        IF @Exitoso = 1
        BEGIN
            UPDATE dbo.BaseDeDatos
            SET Estado           = 'ACTIVA',
                UsuarioBD        = @UsuarioBdReal,
                PasswordCifrado  = @PasswordCifrado,
                Host             = COALESCE(@Host, Host),
                Puerto           = COALESCE(@Puerto, Puerto),
                MongoExternalId  = @ExternalId,
                UltimaActividad  = SYSUTCDATETIME()
            WHERE Id = @BaseDeDatosId;
        END
        ELSE
        BEGIN
            UPDATE dbo.BaseDeDatos
            SET Estado          = 'ELIMINADA',
                UltimaActividad = SYSUTCDATETIME()
            WHERE Id = @BaseDeDatosId;
        END

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
   sp_RotarCredencialExterna
   Persiste una password NUEVA para una base cuyo motor genera credenciales
   externamente (hoy: MongoDB). El backend ya llamó al endpoint de reset del
   proveedor y trae la password nueva en texto plano — este SP solo la cifra
   y la guarda, con el mismo control BOLA que sp_ObtenerCredencialesBaseDatos
   (si @UsuarioIdSolicitante no es el dueño real, 50012 y nunca se revela si
   el recurso existe o no vía el mensaje de error).
   ------------------------------------------------------------------------- */
/* -------------------------------------------------------------------------
   sp_ObtenerCredencialesBaseDatos (CREATE OR ALTER, cuerpo idéntico al de
   sql/002 salvo el nuevo campo MongoExternalId en el SELECT) — lo necesita
   el endpoint POST /dashboard/bases/{id}/rotar-credencial para saber, tras
   el mismo chequeo BOLA de siempre, el id externo con el que llamar a la
   Mongo Provisioning API.
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
        bd.EspacioMaximoMB, bd.EspacioUtilizadoMB,
        bd.MongoExternalId
    FROM dbo.BaseDeDatos bd
    INNER JOIN dbo.MotorBaseDatos m ON m.Id = bd.MotorId
    WHERE bd.Id = @BaseDeDatosId;

    CLOSE SYMMETRIC KEY SymKey_ABA_Credenciales;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_RotarCredencialExterna
    @BaseDeDatosId        INT,
    @UsuarioIdSolicitante INT,
    @UsuarioBdNuevo       VARCHAR(100),
    @PasswordPlanoNuevo   VARCHAR(200),
    @IpOrigen             VARCHAR(45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UsuarioIdDueno INT;

    SELECT @UsuarioIdDueno = UsuarioId
    FROM dbo.BaseDeDatos
    WHERE Id = @BaseDeDatosId AND Estado = 'ACTIVA';

    IF @UsuarioIdDueno IS NULL
        THROW 50011, 'La base de datos no existe.', 1;

    IF @UsuarioIdDueno <> @UsuarioIdSolicitante
    BEGIN
        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, Detalle)
        VALUES (@UsuarioIdSolicitante, 'BaseDeDatos', @BaseDeDatosId, 'ACCESO_CREDENCIALES_RECHAZADO',
                (SELECT @UsuarioIdDueno AS duenoReal FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        THROW 50012, 'No tienes permiso para rotar la credencial de esta base de datos.', 1;
    END

    DECLARE @PasswordCifrado VARBINARY(256);

    OPEN SYMMETRIC KEY SymKey_ABA_Credenciales DECRYPTION BY CERTIFICATE Cert_ABA_Credenciales;
    SET @PasswordCifrado = ENCRYPTBYKEY(KEY_GUID('SymKey_ABA_Credenciales'), @PasswordPlanoNuevo);
    CLOSE SYMMETRIC KEY SymKey_ABA_Credenciales;

    BEGIN TRY
        BEGIN TRAN;

        -- El proveedor (Mongo Provisioning API) regenera USUARIO Y password en el reset,
        -- no solo la password — hay que sobreescribir UsuarioBD también o queda desincronizado.
        UPDATE dbo.BaseDeDatos
        SET UsuarioBD        = @UsuarioBdNuevo,
            PasswordCifrado  = @PasswordCifrado,
            UltimaActividad  = SYSUTCDATETIME()
        WHERE Id = @BaseDeDatosId;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen)
        VALUES (@UsuarioIdSolicitante, 'BaseDeDatos', @BaseDeDatosId, 'CREDENCIAL_ROTADA', @IpOrigen);

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO
