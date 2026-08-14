using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>
/// Entregable 3 — Módulo N8N, aprovisionado por Snapshot (proveedor externo real,
/// https://api.snapshot.andrescortes.dev). El backend llama a Snapshot (ver
/// <see cref="ISnapshotN8nClient"/>) y SOLO dispara SPs para persistir el resultado —
/// nunca decide el account_id ni el enlace de invitación, esos vienen del proveedor.
/// </summary>
public interface IN8nWorkspaceRepository
{
    /// <summary>
    /// Invoca sp_RegistrarWorkspaceN8NExterno con los valores YA devueltos por Snapshot.
    /// Lanza <see cref="SpBusinessException"/> (50002 usuario inactivo, 50020 ya tiene un
    /// workspace activo EN ABA_Control) si el SP rechaza.
    /// </summary>
    Task<N8nWorkspaceCreadoDto> RegistrarExternoAsync(
        long usuarioId, string accountIdExterno, string email, string credencialUrl, string? ipOrigen, CancellationToken ct = default);

    /// <summary>Invoca sp_ObtenerMiWorkspace. Null si el usuario no tiene workspace activo.</summary>
    Task<N8nWorkspaceDto?> ObtenerMiWorkspaceAsync(long usuarioId, CancellationToken ct = default);

    /// <summary>
    /// Invoca sp_EliminarWorkspace (soft delete). Lanza <see cref="SpBusinessException"/>
    /// (50021) si el usuario no tiene un workspace activo — el controller lo traduce a 404.
    /// </summary>
    Task EliminarAsync(long usuarioId, string? ipOrigen, CancellationToken ct = default);
}
