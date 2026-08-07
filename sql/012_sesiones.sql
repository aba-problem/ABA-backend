/* =========================================================================
   ABA - Vista "Registros de sesión" para el dashboard del usuario.
   Script aditivo — no modifica 001-011.

   No crea tabla nueva: dbo.Auditoria (001) ya registra cada login, registro
   y evento de whitelist de IP con FechaEvento/IpOrigen. Este SP solo expone
   una lectura filtrada y acotada (TOP 50) para que el usuario vea su propio
   historial de acceso — mismo principio BOLA que el resto del proyecto:
   siempre filtrado por @UsuarioId, nunca por un valor que el cliente elija.
   ========================================================================= */

CREATE OR ALTER PROCEDURE dbo.sp_ListarSesionesUsuario
    @UsuarioId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (50)
        Id, Entidad, Accion, IpOrigen, FechaEvento, Detalle
    FROM dbo.Auditoria
    WHERE UsuarioId = @UsuarioId
      AND Accion IN ('LOGIN', 'REGISTRO', 'IP_VALIDADA', 'IP_RECHAZADA')
    ORDER BY FechaEvento DESC;
END
GO
