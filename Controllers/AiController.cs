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
/// Integración real con PolyService IA (https://ia.polyrepo.andrescortes.dev). La API
/// key del proveedor es un secreto de infraestructura del backend (POLYSERVICE_AI_KEY),
/// nunca se expone al usuario final.
/// </summary>
[ApiController]
[Route("ai")]
[Authorize(AuthenticationSchemes = AuthSchemes.ApiKey)]
public sealed class AiController : ControllerBase
{
    private readonly IApiKeyRepository _repo;
    private readonly IPolyServiceAiClient _polyService;
    private readonly ILogger<AiController> _logger;

    public AiController(IApiKeyRepository repo, IPolyServiceAiClient polyService, ILogger<AiController> logger)
    {
        _repo = repo;
        _polyService = polyService;
        _logger = logger;
    }

    [HttpPost("completar")]
    [EnableRateLimiting("ai-service")]
    public async Task<IActionResult> Completar([FromBody] AiCompletarRequest request, CancellationToken ct)
    {
        if (request.PropiedadesDesconocidas is { Count: > 0 })
            return BadRequest(new { error = "El cuerpo contiene campos no permitidos." });

        var apiKeyId = int.Parse(User.FindFirst("apiKeyId")!.Value);

        try
        {
            var resultado = await _polyService.CompletarAsync(request.Prompt, request.MaxTokens ?? 256, ct);

            // Auditoría de consumo con el uso REAL que devolvió el proveedor (no una estimación).
            await _repo.RegistrarUsoAsync(apiKeyId, "/ai/completar",
                tokensEstimados: resultado.TokensPrompt + resultado.TokensCompletion, ct);

            _logger.LogInformation("Llamada a /ai/completar OK apiKeyId={ApiKeyId} tokens={Tokens}",
                apiKeyId, resultado.TokensPrompt + resultado.TokensCompletion);

            return Ok(new { respuesta = resultado.Contenido });
        }
        catch (ServicioExternoSaturadoException)
        {
            _logger.LogWarning("PolyService saturado, /ai/completar rechazado apiKeyId={ApiKeyId}", apiKeyId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "IA_NO_DISPONIBLE",
                mensaje = "El servicio de IA alcanzó su límite temporal. Intenta en unos minutos.",
            });
        }
        catch (ProvisioningEngineException ex)
        {
            _logger.LogError(ex, "Fallo llamando a PolyService apiKeyId={ApiKeyId}", apiKeyId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "IA_NO_DISPONIBLE",
                mensaje = "El servicio de IA no está disponible en este momento. Intenta de nuevo más tarde.",
            });
        }
    }
}
