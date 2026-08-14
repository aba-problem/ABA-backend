using abaproblem.Contracts;

namespace abaproblem.Services;

/// <summary>
/// Cliente del proveedor externo de completions PolyService IA
/// (https://ia.polyrepo.andrescortes.dev). Backend-only: la API key del
/// proveedor vive solo en la configuración del servidor, nunca llega al
/// usuario final ni al frontend.
/// </summary>
public interface IPolyServiceAiClient
{
    Task<PolyServiceChatResult> CompletarAsync(string prompt, int maxTokens, CancellationToken ct = default);
}
