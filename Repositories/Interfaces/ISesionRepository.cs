using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>Historial de acceso (login/registro/whitelist de IP) del usuario autenticado.</summary>
public interface ISesionRepository
{
    /// <summary>Invoca sp_ListarSesionesUsuario (paginado) — siempre filtrado por el usuario del JWT.</summary>
    Task<SesionesPaginadasDto> ListarAsync(long usuarioId, int pagina, int tamanoPagina, CancellationToken ct = default);

    /// <summary>
    /// Invoca sp_RevocarIpUsuario — "No fui yo, bloquear" desde Registros de sesión.
    /// False si esa IP no existe o no está activa para este usuario (404, nunca 403).
    /// </summary>
    Task<bool> RevocarIpAsync(long usuarioId, string direccionIp, string? ipOrigenSolicitud, CancellationToken ct = default);
}
