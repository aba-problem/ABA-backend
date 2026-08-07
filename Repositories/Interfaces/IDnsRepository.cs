using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>
/// Entregable 3 — Módulo DNS Autoservicio. Dos fases, mismo patrón que aprovisionamiento
/// de bases de datos: SQL Server reserva 'PENDIENTE' (no puede llamar HTTP al proveedor
/// DNS real); el backend llama a IDnsProviderService y confirma con ConfirmarAsync.
/// </summary>
public interface IDnsRepository
{
    /// <summary>
    /// Invoca sp_ValidarYCrearRegistroDns (50002 usuario inactivo, 50041 subdominio
    /// inválido, 50042 tipo no soportado, 50043 colisión, 50044 límite excedido).
    /// </summary>
    Task<DnsRegistroReservaDto> ValidarYCrearAsync(long usuarioId, string subdominio, string tipoRegistro, string valor, string? ipOrigen, CancellationToken ct = default);

    /// <summary>Invoca sp_ConfirmarRegistroDns. @exitoso=true → ACTIVO; false → ELIMINADA.</summary>
    Task ConfirmarAsync(int registroId, bool exitoso, string? ipOrigen, CancellationToken ct = default);

    /// <summary>Invoca sp_ListarMisRegistrosDns.</summary>
    Task<IReadOnlyList<DnsRegistroDto>> ListarMisRegistrosAsync(long usuarioId, CancellationToken ct = default);

    /// <summary>
    /// Invoca sp_EliminarRegistroDns (BOLA: 50011/50012 → null → 404). Si no es null,
    /// el backend debe borrar ese registro en el proveedor DNS real a continuación.
    /// </summary>
    Task<DnsRegistroReservaDto?> EliminarAsync(long usuarioId, int registroId, string? ipOrigen, CancellationToken ct = default);

    /// <summary>Invoca sp_ListarTodosRegistrosDns — requiere que @usuarioIdSolicitante sea Admin.</summary>
    Task<IReadOnlyList<DnsRegistroDto>> ListarTodosAdminAsync(long usuarioIdSolicitante, CancellationToken ct = default);

    /// <summary>Invoca sp_EliminarRegistroDnsAdmin — requiere que @usuarioIdSolicitante sea Admin.</summary>
    Task<DnsRegistroReservaDto?> EliminarAdminAsync(long usuarioIdSolicitante, int registroId, string? ipOrigen, CancellationToken ct = default);
}
