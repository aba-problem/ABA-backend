using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>
/// Módulo 3 — Dashboard (ABA_Control). El filtro de pertenencia (control BOLA) vive
/// DENTRO de sp_ObtenerCredencialesBaseDatos, nunca en este repositorio ni en el controller.
/// </summary>
public interface IDashboardRepository
{
    /// <summary>Invoca sp_ListarBasesDatosUsuario — info de conexión sin password.</summary>
    Task<IReadOnlyList<DashboardItemDto>> ListarAsync(long usuarioId, CancellationToken ct = default);

    /// <summary>
    /// Invoca sp_ObtenerCredencialesBaseDatos. Null si la base no existe (50011) o no
    /// pertenece al usuario (50012) — ambos casos se traducen a 404, nunca 403 (control 3.1).
    /// </summary>
    Task<CredencialDto?> ObtenerCredencialesAsync(long baseDeDatosId, long usuarioIdSolicitante, CancellationToken ct = default);
}
