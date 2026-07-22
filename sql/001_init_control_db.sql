/* =========================================================================
   ABA - Plataforma de Hosting DB & Servicios para Desarrolladores
   Script de creación: Base de datos de CONTROL (metadata)
   Motor: Microsoft SQL Server
   Contiene el modelo Usuario 1:N UsuarioIp, Usuario 1:N BaseDeDatos.
   No confundir con las BDs individuales que se aprovisionan a cada
   estudiante (esas viven en los motores MySQL/SQLServer del VPS).



    1. Confirma el túnel realmente escucha en tu máquina. Debe verse el comando corriendo sin errores:
    ssh -L 14330:127.0.0.1:1433 root@178.105.217.45
    1. Prueba en otra terminal (Windows PowerShell):
    Test-NetConnection -ComputerName localhost -Port 14330
    1. Si TcpTestSucceeded es False, el problema es el túnel/el contenedor, no DBeaver.
    2. En DBeaver, datos de la conexión:
    - Host: localhost
    - Port: 14330 (el puerto local que elegiste, no 1433)
    - Database: ABA_Control (solo si ya corriste el script 001_init_control_db.sql; si no, deja master para probar primero)
    - Authentication: SQL Server Authentication
    - Username: sa
    - Password: la de MSSQL_SA_PASSWORD en INFRAESTRUCTURA.md
    3. Pestaña SSL (la causa #1 de fallos con SQL Server en contenedor): el certificado del contenedor es autofirmado, así que por defecto el driver rechaza la conexión con un error tipo PKIX path building failed o SSL handshake failed. Solución: en la pestaña SSL de la conexión, marca "Trust server certificate" (o en Driver Properties agrega trustServerCertificate=true / encrypt=false según la versión del driver que tengas instalado).
    4. Dale a Test Connection después de marcar eso.



   ========================================================================= */

CREATE DATABASE ABA_Control;
GO

USE ABA_Control;
GO

/* -------------------------------------------------------------------------
   Cifrado de credenciales
   Las contraseñas de las BDs aprovisionadas se cifran en reposo (AES-256)
   porque deben poder mostrarse de nuevo al usuario en el dashboard
   ("Información de conexión"), por lo que un hash unidireccional no sirve.
   ------------------------------------------------------------------------- */
CREATE MASTER KEY ENCRYPTION BY PASSWORD = '<REEMPLAZAR_POR_CLAVE_FUERTE_Y_GUARDAR_EN_SECRETS>';
GO

CREATE CERTIFICATE Cert_ABA_Credenciales
    WITH SUBJECT = 'Cifrado de credenciales de bases de datos aprovisionadas';
GO

CREATE SYMMETRIC KEY SymKey_ABA_Credenciales
    WITH ALGORITHM = AES_256
    ENCRYPTION BY CERTIFICATE Cert_ABA_Credenciales;
GO

/* -------------------------------------------------------------------------
   Catálogo: Motores de Base de Datos soportados
   Normaliza el "motor" de cada BD aprovisionada y sus valores por defecto
   de host/puerto, en lugar de repetir strings sueltos.
   ------------------------------------------------------------------------- */
CREATE TABLE dbo.MotorBaseDatos (
    Id            TINYINT      IDENTITY(1,1) PRIMARY KEY,
    Nombre        VARCHAR(30)  NOT NULL UNIQUE,      -- 'MySQL', 'SQLServer'
    PuertoDefault INT          NOT NULL,
    HostDefault   VARCHAR(255) NOT NULL,
    Activo        BIT          NOT NULL DEFAULT 1
);
GO

INSERT INTO dbo.MotorBaseDatos (Nombre, PuertoDefault, HostDefault) VALUES
    ('MySQL',     3306, 'db.aba.andrescortes.dev'),
    ('SQLServer', 1433, 'db.aba.andrescortes.dev');
GO

/* -------------------------------------------------------------------------
   Usuario
   Se autentica vía OAuth (Google / GitHub). La combinación
   (Proveedor, ProveedorUsuarioId) evita duplicar cuentas del mismo
   proveedor, tal como exige Entrega #2.
   ------------------------------------------------------------------------- */
CREATE TABLE dbo.Usuario (
    Id                 INT IDENTITY(1,1) PRIMARY KEY,
    Nombre             NVARCHAR(150) NOT NULL,
    Correo             NVARCHAR(255) NOT NULL,
    AvatarUrl          NVARCHAR(500) NULL,
    Proveedor          VARCHAR(20)   NOT NULL,   -- 'GOOGLE' | 'GITHUB'
    ProveedorUsuarioId VARCHAR(100)  NOT NULL,   -- id/sub devuelto por el proveedor OAuth
    FechaCreacion      DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    UltimoLogin        DATETIME2     NULL,
    Activo             BIT           NOT NULL DEFAULT 1,

    CONSTRAINT CK_Usuario_Proveedor CHECK (Proveedor IN ('GOOGLE', 'GITHUB')),
    CONSTRAINT UQ_Usuario_Proveedor_ProveedorId UNIQUE (Proveedor, ProveedorUsuarioId)
);
GO

CREATE INDEX IX_Usuario_Correo ON dbo.Usuario (Correo);
GO

/* -------------------------------------------------------------------------
   Catálogo: Países permitidos (América y Latam)
   La restricción geográfica se aplica a nivel de BD: al referenciar esta
   tabla desde UsuarioIp.PaisIso, cualquier intento de whitelistear un país
   fuera de la región falla por integridad referencial (FK), sin necesidad
   de un IF en el backend. Ajusta la lista según la política final.
   ------------------------------------------------------------------------- */
CREATE TABLE dbo.PaisPermitido (
    PaisIso CHAR(2)     NOT NULL PRIMARY KEY,  -- ISO-3166-1 alpha-2
    Nombre  VARCHAR(60) NOT NULL,
    Activo  BIT         NOT NULL DEFAULT 1
);
GO

INSERT INTO dbo.PaisPermitido (PaisIso, Nombre) VALUES
    -- Norteamérica
    ('US', 'Estados Unidos'), ('CA', 'Canadá'), ('MX', 'México'),
    -- Centroamérica
    ('GT', 'Guatemala'), ('BZ', 'Belice'), ('HN', 'Honduras'), ('SV', 'El Salvador'),
    ('NI', 'Nicaragua'), ('CR', 'Costa Rica'), ('PA', 'Panamá'),
    -- Caribe
    ('CU', 'Cuba'), ('DO', 'República Dominicana'), ('HT', 'Haití'), ('JM', 'Jamaica'),
    ('TT', 'Trinidad y Tobago'), ('BS', 'Bahamas'), ('BB', 'Barbados'),
    -- Sudamérica
    ('CO', 'Colombia'), ('VE', 'Venezuela'), ('EC', 'Ecuador'), ('PE', 'Perú'),
    ('BO', 'Bolivia'), ('PY', 'Paraguay'), ('UY', 'Uruguay'), ('AR', 'Argentina'),
    ('CL', 'Chile'), ('BR', 'Brasil'), ('GY', 'Guyana'), ('SR', 'Surinam');
GO

/* -------------------------------------------------------------------------
   UsuarioIp  (Usuario 1:N UsuarioIp)
   Lista blanca de IPs para conectarse a los puertos de los contenedores
   de BD. El usuario final NUNCA gestiona esto manualmente: el sistema
   registra/valida su IP automáticamente en cada login (Origen = 'AUTO'),
   consultando el gestor solo requiere que su IP actual esté aquí. Un
   registro 'MANUAL' queda reservado para intervención directa del equipo.
   ------------------------------------------------------------------------- */
CREATE TABLE dbo.UsuarioIp (
    Id                INT           IDENTITY(1,1) PRIMARY KEY,
    UsuarioId         INT           NOT NULL,
    DireccionIp       VARCHAR(45)   NOT NULL,   -- soporta IPv4 e IPv6
    PaisIso           CHAR(2)       NOT NULL,   -- debe existir en PaisPermitido
    Origen            VARCHAR(10)   NOT NULL DEFAULT 'AUTO',  -- 'AUTO' | 'MANUAL'
    Alias             NVARCHAR(100) NULL,       -- solo aplica a registros MANUAL
    Activo            BIT           NOT NULL DEFAULT 1,
    FechaRegistro     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    FechaVerificacion DATETIME2     NULL,       -- último geo-check exitoso

    CONSTRAINT FK_UsuarioIp_Usuario FOREIGN KEY (UsuarioId)
        REFERENCES dbo.Usuario (Id),   -- sin CASCADE: Usuario nunca se borra físicamente (ver trg_Usuario_SoftDelete)
    CONSTRAINT FK_UsuarioIp_PaisPermitido FOREIGN KEY (PaisIso)
        REFERENCES dbo.PaisPermitido (PaisIso),
    CONSTRAINT CK_UsuarioIp_Origen CHECK (Origen IN ('AUTO', 'MANUAL')),
    CONSTRAINT UQ_UsuarioIp_Usuario_Ip UNIQUE (UsuarioId, DireccionIp)
);
GO

CREATE INDEX IX_UsuarioIp_DireccionIp ON dbo.UsuarioIp (DireccionIp);
GO

/* -------------------------------------------------------------------------
   BaseDeDatos  (Usuario 1:N BaseDeDatos)
   Cada fila = una BD aprovisionada en algún motor, con su propio usuario
   y contraseña exclusivos (ningún usuario ajeno puede acceder a otra BD
   del mismo contenedor). El nombre de BD y el usuario de BD deben ser
   únicos por motor porque son objetos reales dentro de ese motor.
   ------------------------------------------------------------------------- */
CREATE TABLE dbo.BaseDeDatos (
    Id                 INT            IDENTITY(1,1) PRIMARY KEY,
    UsuarioId          INT            NOT NULL,
    MotorId            TINYINT        NOT NULL,
    NombreBD           VARCHAR(63)    NOT NULL,          -- nombre real de la BD en el motor
    UsuarioBD          VARCHAR(32)    NOT NULL,          -- login creado exclusivamente para esta BD
    PasswordCifrado    VARBINARY(256) NOT NULL,          -- cifrado con SymKey_ABA_Credenciales
    Host               VARCHAR(255)   NOT NULL,
    Puerto             INT            NOT NULL,
    Estado             VARCHAR(20)    NOT NULL DEFAULT 'ACTIVA',
    EspacioMaximoMB    SMALLINT       NOT NULL DEFAULT 20,
    EspacioUtilizadoMB DECIMAL(10,2)  NOT NULL DEFAULT 0,
    FechaCreacion      DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    UltimaActividad    DATETIME2      NULL,
    FechaExpiracion    DATETIME2      NULL,              -- soporte a política de TTL

    CONSTRAINT FK_BaseDeDatos_Usuario FOREIGN KEY (UsuarioId)
        REFERENCES dbo.Usuario (Id),   -- sin CASCADE: Usuario nunca se borra físicamente (ver trg_Usuario_SoftDelete)
    CONSTRAINT FK_BaseDeDatos_Motor FOREIGN KEY (MotorId)
        REFERENCES dbo.MotorBaseDatos (Id),
    CONSTRAINT CK_BaseDeDatos_Estado CHECK (Estado IN ('PENDIENTE', 'ACTIVA', 'PAUSADA', 'ELIMINADA')),
    CONSTRAINT UQ_BaseDeDatos_Motor_Nombre UNIQUE (MotorId, NombreBD),
    CONSTRAINT UQ_BaseDeDatos_Motor_UsuarioBD UNIQUE (MotorId, UsuarioBD)
);
GO

CREATE INDEX IX_BaseDeDatos_UsuarioId ON dbo.BaseDeDatos (UsuarioId);
GO

/* -------------------------------------------------------------------------
   Auditoria
   Registro de eventos relevantes (login, aprovisionamiento, cambios de
   estado, validaciones/rechazos de IP, etc.). Se llena desde los SPs y/o
   triggers de la propia BD -- nunca desde lógica del backend -- para que
   quede garantizado que todo evento de negocio queda trazado.
   UsuarioId puede quedar NULL solo cuando el evento lo origina un job del
   sistema sin usuario asociado (ej. limpieza por TTL). Usuario, UsuarioIp
   y BaseDeDatos nunca se borran físicamente (ver triggers de soft-delete
   más abajo), así que EntidadId siempre resuelve a una fila real.
   ------------------------------------------------------------------------- */
CREATE TABLE dbo.Auditoria (
    Id          BIGINT        IDENTITY(1,1) PRIMARY KEY,
    UsuarioId   INT           NULL,
    Entidad     VARCHAR(30)   NOT NULL,   -- 'Usuario' | 'BaseDeDatos' | 'UsuarioIp'
    EntidadId   INT           NULL,       -- PK de la fila afectada
    Accion      VARCHAR(30)   NOT NULL,   -- 'LOGIN', 'CREAR', 'PAUSAR', 'DESACTIVAR', 'IP_VALIDADA', 'IP_RECHAZADA', ...
    IpOrigen    VARCHAR(45)   NULL,       -- IP desde la que se ejecutó la acción
    Detalle     NVARCHAR(MAX) NULL,       -- JSON con datos adicionales del evento
    FechaEvento DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_Auditoria_Usuario FOREIGN KEY (UsuarioId)
        REFERENCES dbo.Usuario (Id),   -- sin SET NULL: ya no hace falta, Usuario nunca desaparece
    CONSTRAINT CK_Auditoria_Entidad CHECK (Entidad IN ('Usuario', 'BaseDeDatos', 'UsuarioIp'))
);
GO

CREATE INDEX IX_Auditoria_UsuarioId ON dbo.Auditoria (UsuarioId);
GO

CREATE INDEX IX_Auditoria_Entidad_EntidadId ON dbo.Auditoria (Entidad, EntidadId);
GO

CREATE INDEX IX_Auditoria_FechaEvento ON dbo.Auditoria (FechaEvento);
GO

/* -------------------------------------------------------------------------
   Protección contra DELETE físico (soft-delete obligatorio)
   Usuario, UsuarioIp y BaseDeDatos son referenciadas por
   Auditoria.EntidadId. Si alguna fila se borrara de verdad, el historial
   de auditoría quedaría apuntando a un registro inexistente. Estos
   triggers interceptan cualquier DELETE crudo y lo convierten en
   soft-delete (Activo = 0 / Estado = 'ELIMINADA'), dejando además el
   evento registrado en Auditoria. Así la regla "nunca borrar de verdad"
   la impone la propia base de datos, no una convención del backend.
   ------------------------------------------------------------------------- */
CREATE OR ALTER TRIGGER dbo.trg_Usuario_SoftDelete
ON dbo.Usuario
INSTEAD OF DELETE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE u
    SET Activo = 0
    FROM dbo.Usuario u
    INNER JOIN deleted d ON d.Id = u.Id
    WHERE u.Activo = 1;

    INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, Detalle)
    SELECT Id, 'Usuario', Id, 'DESACTIVAR', 'Soft-delete automático (DELETE interceptado por trigger)'
    FROM deleted;
END
GO

CREATE OR ALTER TRIGGER dbo.trg_UsuarioIp_SoftDelete
ON dbo.UsuarioIp
INSTEAD OF DELETE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE i
    SET Activo = 0
    FROM dbo.UsuarioIp i
    INNER JOIN deleted d ON d.Id = i.Id
    WHERE i.Activo = 1;

    INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, Detalle)
    SELECT UsuarioId, 'UsuarioIp', Id, 'DESACTIVAR', 'Soft-delete automático (DELETE interceptado por trigger)'
    FROM deleted;
END
GO

CREATE OR ALTER TRIGGER dbo.trg_BaseDeDatos_SoftDelete
ON dbo.BaseDeDatos
INSTEAD OF DELETE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE b
    SET Estado = 'ELIMINADA'
    FROM dbo.BaseDeDatos b
    INNER JOIN deleted d ON d.Id = b.Id
    WHERE b.Estado <> 'ELIMINADA';

    INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, Detalle)
    SELECT UsuarioId, 'BaseDeDatos', Id, 'DESACTIVAR', 'Soft-delete automático (DELETE interceptado por trigger)'
    FROM deleted
    WHERE Estado <> 'ELIMINADA';
END
GO
