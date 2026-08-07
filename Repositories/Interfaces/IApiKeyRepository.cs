using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>
/// Entregable 3 — Módulo IA como Servicio (API Keys). SOLO invoca SPs; la key completa
/// se genera dentro de sp_CrearApiKey y solo se devuelve una vez, nunca se guarda en claro.
/// </summary>
public interface IApiKeyRepository
{
    /// <summary>Invoca sp_CrearApiKey (50002 usuario inactivo, 50031 límite de keys activas).</summary>
    Task<ApiKeyCreadaDto> CrearAsync(long usuarioId, string? ipOrigen, CancellationToken ct = default);

    /// <summary>Invoca sp_ListarApiKeys — nunca incluye el hash ni la key completa.</summary>
    Task<IReadOnlyList<ApiKeyDto>> ListarAsync(long usuarioId, CancellationToken ct = default);

    /// <summary>
    /// Invoca sp_RevocarApiKey. Null si no existe o no pertenece al usuario (BOLA, 50011/50012
    /// → 404, nunca 403). True si se revocó (o ya estaba revocada, idempotente).
    /// </summary>
    Task<bool?> RevocarAsync(long usuarioId, int apiKeyId, string? ipOrigen, CancellationToken ct = default);

    /// <summary>
    /// Invoca sp_ObtenerApiKeyPorPrefijo — único punto de lectura para el hot path de
    /// autenticación. El backend compara el hash en tiempo constante, nunca el SP.
    /// </summary>
    Task<ApiKeyCandidataDto?> ObtenerPorPrefijoAsync(string prefijo, CancellationToken ct = default);

    /// <summary>Invoca sp_RegistrarUsoApiKey — auditoría de consumo, sin lógica adicional en C#.</summary>
    Task RegistrarUsoAsync(int apiKeyId, string endpoint, int? tokensEstimados, CancellationToken ct = default);

    /// <summary>Invoca sp_ObtenerConsumoApiKey (BOLA: valida que la key pertenezca al usuario).</summary>
    Task<IReadOnlyList<ApiKeyConsumoDiaDto>> ObtenerConsumoAsync(long usuarioId, int apiKeyId, CancellationToken ct = default);
}
