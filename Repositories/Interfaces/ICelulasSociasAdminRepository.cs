using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>
/// Alta/gestion de celulas socias desde el panel de admin (sql/018_celulas_socias_panel.sql).
/// Distinto de IPartnersProvisioningRepository (ese es el aprovisionamiento de BASES que hace
/// la propia celula con su API key; este es la gestion de la CELULA en si, que solo un admin
/// de ABA puede hacer). El backend solo dispara SPs — la generacion de la API key y el
/// hasheo viven en SQL, no acá.
/// </summary>
public interface ICelulasSociasAdminRepository
{
    /// <summary>Invoca sp_AltaCelulaSociaAutoKey. Lanza SpBusinessException si no autorizado, prefijo invalido, o duplicado.</summary>
    Task<CelulaSociaCreadaDto> AltaAsync(long usuarioIdSolicitante, string nombreCelula, string prefijo, CancellationToken ct = default);

    /// <summary>Invoca sp_ListarCelulasSocias. Lanza SpBusinessException si no autorizado.</summary>
    Task<IReadOnlyList<CelulaSociaResumenDto>> ListarAsync(long usuarioIdSolicitante, CancellationToken ct = default);

    /// <summary>Invoca sp_CambiarEstadoCelulaSocia. Lanza SpBusinessException si no autorizado o no existe.</summary>
    Task<CelulaSociaResumenDto> CambiarEstadoAsync(long usuarioIdSolicitante, int celulaSociaId, bool activo, CancellationToken ct = default);

    /// <summary>Invoca sp_RotarApiKeyCelulaSocia. Lanza SpBusinessException si no autorizado o no existe.</summary>
    Task<CelulaSociaCreadaDto> RotarApiKeyAsync(long usuarioIdSolicitante, int celulaSociaId, CancellationToken ct = default);
}
