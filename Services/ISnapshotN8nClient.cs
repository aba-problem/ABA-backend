using abaproblem.Contracts;

namespace abaproblem.Services;

/// <summary>
/// Cliente de la API de aprovisionamiento externo de N8N (Snapshot,
/// https://api.snapshot.andrescortes.dev). Backend-only: la API key vive solo en la
/// configuración del servidor, nunca llega al usuario final ni al frontend.
/// </summary>
public interface ISnapshotN8nClient
{
    /// <summary>
    /// Invoca POST /n8n/external/provision. Lanza <see cref="SnapshotCuentaExistenteException"/>
    /// en 409, <see cref="ServicioExternoSaturadoException"/> en 429, o
    /// <see cref="ProvisioningEngineException"/> para cualquier otro fallo.
    /// </summary>
    Task<SnapshotN8nProvisionResult> AprovisionarAsync(string externalUserRef, string email, CancellationToken ct = default);
}
