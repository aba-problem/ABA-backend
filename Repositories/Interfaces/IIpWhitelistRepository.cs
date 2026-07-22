using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>
/// Módulo 1/3 — Whitelist automática de IPs (control geográfico América/Latam).
/// El backend NO decide qué países están permitidos (eso es la FK UsuarioIp→PaisPermitido,
/// control 1.4 del documento de base de datos); solo transporta IP+país resuelto al SP.
/// </summary>
public interface IIpWhitelistRepository
{
    /// <summary>
    /// Invoca sp_RegistrarIpUsuario. Lanza <see cref="SpBusinessException"/> (50010) si el
    /// país no está en dbo.PaisPermitido — el SP ya auditó el rechazo antes de lanzar.
    /// </summary>
    Task<IpRegistroDto> RegistrarAsync(long usuarioId, string direccionIp, string paisIso, CancellationToken ct = default);

    /// <summary>Logins MySQL (UsuarioBD) de las bases activas del usuario — para sincronizar la whitelist espejo.</summary>
    Task<IReadOnlyList<string>> ListarUsuarioBDMySqlAsync(long usuarioId, CancellationToken ct = default);

    /// <summary>Conjunto completo de IPs activas del usuario — para reconciliar la whitelist espejo en MySQL.</summary>
    Task<IReadOnlyList<string>> ListarIpsActivasAsync(long usuarioId, CancellationToken ct = default);
}
