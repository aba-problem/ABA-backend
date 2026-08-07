using abaproblem.Contracts;
using abaproblem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace abaproblem.Controllers;

/// <summary>
/// Módulo 8 — Aprovisionamiento server-to-server para células socias
/// (Aba/INVESTIGACION-PROXY-MYSQL.md § 8). Autenticación por API key
/// ([Authorize(AuthenticationSchemes = AuthSchemes.ApiKey)]), nunca JWT/cookie — el
/// llamador es el BACKEND de la célula, nunca su frontend ni un navegador.
///
/// Sin CSRF: el patrón Double Submit Cookie protege contra un navegador engañado para
/// mandar una request con las cookies de sesión de la víctima — no aplica acá, no hay
/// cookies ni sesión de navegador involucradas en ningún punto de este flujo.
///
/// `ipsPermitidas` (whitelist de IP opcional, § 9 de la investigación) queda deliberadamente
/// SIN implementar todavía: el mecanismo de whitelist se retiró del lado de MySQL (ver
/// Aba/INFRAESTRUCTURA.md § 4.3) y su reemplazo en ProxySQL (§ 6.7) nunca se construyó — no
/// hay nada real a lo que enchufar ese campo hoy.
/// </summary>
[ApiController]
[Route("partners/databases")]
[Authorize(AuthenticationSchemes = AuthSchemes.ApiKey)]
[EnableRateLimiting("partners")]
public sealed class PartnersProvisioningController : ControllerBase
{
    private readonly IPartnersProvisioningOrchestrator _orchestrator;
    private readonly ILogger<PartnersProvisioningController> _logger;

    public PartnersProvisioningController(
        IPartnersProvisioningOrchestrator orchestrator, ILogger<PartnersProvisioningController> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>Aprovisiona una base MySQL nueva para la célula autenticada. Sin body.</summary>
    [HttpPost]
    public async Task<IActionResult> Crear(CancellationToken ct)
    {
        if (!TryCelulaSociaId(out var celulaSociaId))
            return Unauthorized(new { error = "API key inválida." });

        try
        {
            var resultado = await _orchestrator.AprovisionarAsync(celulaSociaId, ct);

            _logger.LogInformation("Aprovisionamiento socia OK celulaSociaId={CelulaSociaId} baseId={BaseId}",
                celulaSociaId, resultado.BaseDeDatosId);

            // Mismo contrato que ya usan los estudiantes (§ 8.4 de la investigación) — no se
            // inventa un formato nuevo. La contraseña se entrega UNA SOLA VEZ aquí.
            return StatusCode(StatusCodes.Status201Created, resultado);
        }
        catch (SpBusinessException ex)
        {
            _logger.LogWarning("Aprovisionamiento socia rechazado por SP celulaSociaId={CelulaSociaId} err={Err}",
                celulaSociaId, ex.SpErrorNumber);

            var status = ex.SpErrorNumber == 50103
                ? StatusCodes.Status409Conflict // límite de bases activas para la célula
                : StatusCodes.Status422UnprocessableEntity;

            return StatusCode(status, new { error = MensajeSeguro(ex.SpErrorNumber, NombreCelula()) });
        }
        catch (ProvisioningEngineException ex)
        {
            _logger.LogError(ex, "Fallo de motor aprovisionando (socia) celulaSociaId={CelulaSociaId}", celulaSociaId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "No se pudo completar el aprovisionamiento en este momento. Intenta de nuevo más tarde.",
            });
        }
    }

    /// <summary>Consulta estado y espacio usado de una base de la célula autenticada.</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Obtener(long id, CancellationToken ct)
    {
        if (!TryCelulaSociaId(out var celulaSociaId))
            return Unauthorized(new { error = "API key inválida." });

        var resultado = await _orchestrator.ObtenerAsync(id, celulaSociaId, ct);
        if (resultado is null)
            return NotFound(); // no existe o no pertenece a esta célula (control BOLA)

        return Ok(resultado);
    }

    /// <summary>
    /// Desaprovisiona de verdad (DROP DATABASE + DROP USER en MySQL) — a diferencia del
    /// soft-delete que usan los estudiantes, pensado para cuando un usuario final de la
    /// célula se da de baja.
    /// </summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Eliminar(long id, CancellationToken ct)
    {
        if (!TryCelulaSociaId(out var celulaSociaId))
            return Unauthorized(new { error = "API key inválida." });

        try
        {
            await _orchestrator.DesaprovisionarAsync(id, celulaSociaId, ct);
            _logger.LogInformation("Base socia eliminada celulaSociaId={CelulaSociaId} baseId={BaseId}", celulaSociaId, id);
            return NoContent();
        }
        catch (SpBusinessException)
        {
            return NotFound(); // no existe, no pertenece a esta célula, o ya fue eliminada
        }
    }

    private bool TryCelulaSociaId(out int celulaSociaId)
    {
        var claim = User.FindFirst("celulaId")?.Value;
        return int.TryParse(claim, out celulaSociaId);
    }

    /// <summary>
    /// Nombre real de la célula ya autenticada (claim puesto por ApiKeyAuthenticationHandler)
    /// — a partir de este punto la identidad ya está confirmada, así que usar el nombre en los
    /// mensajes de error no filtra nada que el llamador no supiera ya de sí mismo.
    /// </summary>
    private string NombreCelula() => User.FindFirst("nombreCelula")?.Value ?? "tu célula";

    private static string MensajeSeguro(int spErrorNumber, string nombreCelula) => spErrorNumber switch
    {
        50101 => $"La célula '{nombreCelula}' no fue encontrada o está inactiva.",
        50102 => "Motor de base de datos no soportado.",
        50103 => $"La célula '{nombreCelula}' alcanzó el límite de bases de datos activas.",
        _ => "No se pudo completar el aprovisionamiento.",
    };
}
