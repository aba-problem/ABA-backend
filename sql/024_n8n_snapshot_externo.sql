/* =========================================================================
   ABA - N8N pasa a usar el proveedor externo real (Snapshot). Hasta ahora
   sp_CrearWorkspaceN8N generaba un nombre y una contraseña ENTERAMENTE
   dentro de SQL Server — no existía ninguna instancia N8N real detrás. La
   API de Snapshot (https://api.snapshot.andrescortes.dev) SÍ aprovisiona
   una cuenta real, pero con un modelo de credencial distinto: no admite
   fijar contraseña por API, así que devuelve un ENLACE DE INVITACIÓN de un
   solo uso — el usuario final lo abre y define su propia contraseña ahí.

   Por eso PasswordCifrado pasa a ser NULLable (las cuentas nuevas no la
   usan más) y se agregan AccountIdExterno/CredencialUrl. NombreWorkspace
   se sigue usando, pero ahora guarda el correo del usuario (la identidad
   real en Snapshot), no un nombre sintético — el UNIQUE existente sobre esa
   columna sigue siendo válido (un correo no debería repetirse tampoco).

   IMPORTANTE — asimetría real de la API de Snapshot: no existe ningún
   endpoint de borrado/deprovisioning documentado. sp_EliminarWorkspace
   (sql/009) sigue siendo válido para "olvidar" el registro LOCAL, pero NO
   borra la cuenta del lado de Snapshot — si el usuario la crea de nuevo,
   es esperable que Snapshot responda 409 (ya existe). El backend documenta
   esto explícitamente en el mensaje de error correspondiente, nunca lo
   oculta.
   ========================================================================= */

USE ABA_Control;
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.WorkspaceN8N') AND name = 'PasswordCifrado' AND is_nullable = 0
)
BEGIN
    ALTER TABLE dbo.WorkspaceN8N ALTER COLUMN PasswordCifrado VARBINARY(256) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.WorkspaceN8N') AND name = 'AccountIdExterno'
)
BEGIN
    ALTER TABLE dbo.WorkspaceN8N ADD AccountIdExterno VARCHAR(64) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.WorkspaceN8N') AND name = 'CredencialUrl'
)
BEGIN
    ALTER TABLE dbo.WorkspaceN8N ADD CredencialUrl NVARCHAR(1000) NULL;
END
GO

SET QUOTED_IDENTIFIER ON;
GO

/* -------------------------------------------------------------------------
   sp_RegistrarWorkspaceN8NExterno
   Reemplaza a sp_CrearWorkspaceN8N para el flujo real: el backend YA llamó
   a la API de Snapshot (POST /n8n/external/provision) antes de invocar este
   SP — acá solo se persiste el resultado. @AccountIdExterno/@CredencialUrl
   vienen del proveedor, @Email siempre del perfil del usuario autenticado
   (nunca del body — control BOLA, igual que el resto del proyecto).
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_RegistrarWorkspaceN8NExterno
    @UsuarioId        INT,
    @AccountIdExterno VARCHAR(64),
    @Email            NVARCHAR(255),
    @CredencialUrl    NVARCHAR(1000),
    @IpOrigen         VARCHAR(45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioId AND Activo = 1)
        THROW 50002, 'Usuario no existe o está inactivo.', 1;

    IF EXISTS (SELECT 1 FROM dbo.WorkspaceN8N WHERE UsuarioId = @UsuarioId AND Estado = 'ACTIVO')
        THROW 50020, 'Ya tienes un workspace de N8N activo.', 1;

    DECLARE @WorkspaceId INT;

    BEGIN TRY
        BEGIN TRAN;

        INSERT INTO dbo.WorkspaceN8N (UsuarioId, NombreWorkspace, PasswordCifrado, AccountIdExterno, CredencialUrl)
        VALUES (@UsuarioId, @Email, NULL, @AccountIdExterno, @CredencialUrl);

        SET @WorkspaceId = SCOPE_IDENTITY();

        -- Detalle SIN el enlace de invitación (control 5.8 — es una credencial de acceso
        -- de un solo uso, se trata igual que una password: nunca se loguea).
        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioId, 'WorkspaceN8N', @WorkspaceId, 'CREAR', @IpOrigen,
                (SELECT @AccountIdExterno AS accountIdExterno FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT
        @WorkspaceId      AS Id,
        @Email            AS NombreWorkspace,
        @CredencialUrl    AS CredencialUrl,
        10                AS LimiteWorkflows,
        500               AS LimiteEjecucionesMes;
END
GO
