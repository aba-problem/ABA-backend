using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>
/// Entregable 3 — Módulo N8N. Instancia N8N única y compartida (multi-tenancy lógico,
/// mismo argumento de presupuesto de RAM del Módulo 6 ya usado para el motor SQLServer
/// como alternativa a MySQL). El backend SOLO dispara SPs; nombre y contraseña del
/// workspace se generan enteramente dentro de sp_CrearWorkspaceN8N.
/// </summary>
public interface IN8nWorkspaceRepository
{
    /// <summary>
    /// Invoca sp_CrearWorkspaceN8N. Lanza <see cref="SpBusinessException"/> (50002 usuario
    /// inactivo, 50020 ya tiene un workspace activo) si el SP rechaza.
    /// </summary>
    Task<N8nWorkspaceCreadoDto> CrearAsync(long usuarioId, string? ipOrigen, CancellationToken ct = default);

    /// <summary>Invoca sp_ObtenerMiWorkspace. Null si el usuario no tiene workspace activo.</summary>
    Task<N8nWorkspaceDto?> ObtenerMiWorkspaceAsync(long usuarioId, CancellationToken ct = default);

    /// <summary>
    /// Invoca sp_EliminarWorkspace (soft delete). Lanza <see cref="SpBusinessException"/>
    /// (50021) si el usuario no tiene un workspace activo — el controller lo traduce a 404.
    /// </summary>
    Task EliminarAsync(long usuarioId, string? ipOrigen, CancellationToken ct = default);
}
