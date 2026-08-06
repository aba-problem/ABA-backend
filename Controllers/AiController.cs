using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using abaproblem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace abaproblem.Controllers;

/// <summary>
/// Entregable 3 — Módulo IA como Servicio, uso real. Protegido por el esquema ApiKey
/// (header X-API-Key), NUNCA por las cookies del usuario — es para consumo externo
/// (scripts, CI, otros backends), no para el navegador del usuario.
///
/// Alcance de este entregable: andamiaje completo de auth/rate-limit/auditoría de
/// consumo. La integración con un proveedor de IA real queda documentada como pendiente,
/// no bloqueante — swap trivial dentro de este mismo método cuando se decida el proveedor.
/// </summary>
[ApiController]
[Route("ai")]
[Authorize(AuthenticationSchemes = AuthSchemes.ApiKey)]
public sealed class AiController : ControllerBase
{
    private readonly IApiKeyRepository _repo;
    private readonly ILogger<AiController> _logger;

    public AiController(IApiKeyRepository repo, ILogger<AiController> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    [HttpPost("completar")]
    [EnableRateLimiting("ai-service")]
    public async Task<IActionResult> Completar([FromBody] AiCompletarRequest request, CancellationToken ct)
    {
        if (request.PropiedadesDesconocidas is { Count: > 0 })
            return BadRequest(new { error = "El cuerpo contiene campos no permitidos." });

        var apiKeyId = int.Parse(User.FindFirst("apiKeyId")!.Value);

        // Auditoría de consumo sin lógica adicional en C# — sp_RegistrarUsoApiKey hace el insert
        // y actualiza UltimoUso. TokensEstimados en null: no hay proveedor real todavía que lo mida.
        await _repo.RegistrarUsoAsync(apiKeyId, "/ai/completar", tokensEstimados: null, ct);

        _logger.LogInformation("Llamada a /ai/completar apiKeyId={ApiKeyId}", apiKeyId);

        return Ok(new
        {
            mensaje = "Andamiaje de IA como Servicio verificado: autenticación por API key, " +
                      "rate limit particionado y auditoría de consumo operativos. Integración " +
                      "con un proveedor de IA real pendiente de definir (fuera del alcance actual).",
        });
    }
}
