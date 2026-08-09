/* =========================================================================
   ABA - Plataforma de Hosting DB & Servicios para Desarrolladores
   Módulo 8 (continuación): listado de bases por célula socia.

   Motivo: hasta ahora una célula socia solo podía consultar una base si ya
   tenía el id (GET /partners/databases/{id}) — si lo perdía, no había forma
   de recuperarlo por API, tenía que escribirnos. Esta SP les da visibilidad
   completa de gestión propia (control 8: autoservicio, sin depender de
   soporte nuestro para saber qué bases tienen).

   Devuelve TODOS los estados (PENDIENTE/ACTIVA/ELIMINADA), no solo activas —
   mismo criterio de auditoría que el soft-delete: la célula debe poder ver
   su propio historial, no solo lo que sigue vivo.
   ========================================================================= */

USE ABA_Control;
GO

CREATE OR ALTER PROCEDURE dbo.sp_ListarBasesDatosSocio
    @CelulaSociaId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, NombreBD, UsuarioBD, Host, Puerto, Estado,
           EspacioMaximoMB, EspacioUtilizadoMB, FechaCreacion
    FROM dbo.BaseDeDatosSocio
    WHERE CelulaSociaId = @CelulaSociaId
    ORDER BY FechaCreacion DESC;
END
GO
