/* =========================================================================
   ABA - Fix: sp_EliminarWorkspace (N8N) y sp_RevocarApiKey quedaron con
   QUOTED_IDENTIFIER OFF grabado permanentemente, igual causa que
   sql/020_fix_quoted_identifier.sql (creados vía `sqlcmd`, que usa OFF por
   defecto) — pero sql/020 solo recreó las PROCEDURES DE CREACIÓN
   (sp_CrearWorkspaceN8N/sp_CrearApiKey). Estas dos hacen UPDATE contra las
   MISMAS tablas con índice filtrado (WorkspaceN8N/ApiKey) y se quedaron
   sin arreglar — confirmado en logs de producción:
     "UPDATE failed because the following SET options have incorrect
      settings: 'QUOTED_IDENTIFIER'"
   en DELETE /n8n/mi-workspace y POST /apikeys/{id}/revocar.

   Mismo fix: SET QUOTED_IDENTIFIER ON; antes del CREATE OR ALTER, cuerpo
   idéntico al original (009/010), sin cambios de lógica.
   ========================================================================= */

USE ABA_Control;
GO

SET QUOTED_IDENTIFIER ON;
GO

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
