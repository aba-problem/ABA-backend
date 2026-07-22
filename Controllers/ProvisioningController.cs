using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using abaproblem.Contracts;
using abaproblem.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace abaproblem.Controllers;

/// <summary>
/// Módulo 2 — Aprovisionamiento automático de base de datos.
/// [Authorize] + rate limit "provisioning" (Token Bucket: 1 base cada 10 min por usuario).
/// El backend solo dispara el SP y el servicio MySQL; nombres, tamaños, límites y
/// auditoría de negocio viven en SQL Server (el orquestador solo coordina ambos sistemas).
/// </summary>
[ApiController]
[Route("provisioning")]
[Authorize]
public sealed class ProvisioningController : ControllerBase
{
    private readonly IProvisioningOrchestrator _orchestrator;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<ProvisioningController> _logger;

    public ProvisioningController(
        IProvisioningOrchestrator orchestrator,
        IAntiforgery antiforgery,
        ILogger<ProvisioningController> logger)
    {
        _orchestrator = orchestrator;
        _antiforgery = antiforgery;
        _logger = logger;
    }

    /// <summary>
    /// Crea una nueva base de datos para el usuario autenticado.
    /// Control 2.2: el rate limit "provisioning" (Token Bucket) permite 1 ráfaga y recarga lenta.
    /// Control BOLA: el usuarioId se toma del claim JWT, jamás del body.
    /// </summary>
    [HttpPost("crear")]
    [EnableRateLimiting("provisioning")]
    public async Task<IActionResult> Crear([FromBody] ProvisioningRequest request, CancellationToken ct)
    {
        // Control 1.2 — endpoint mutante con cookies → exige CSRF token.
        await _antiforgery.ValidateRequestAsync(HttpContext);

        // Control 2.2 — rechaza cualquier campo desconocido en el body (mass-assignment / fuzzing).
        // Refuerza a UnmappedMemberHandling.Disallow por si el DTO evoluciona.
        if (request.PropiedadesDesconocidas is { Count: > 0 })
            return BadRequest(new { error = "El cuerpo contiene campos no permitidos." });

        // Control BOLA — usuarioId SIEMPRE desde el token, nunca del body.
        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

        try
        {
            var resultado = await _orchestrator.AprovisionarAsync(usuarioId, request.NombreMotor, ip, ct);

            // Control 2.1 — la contraseña se devuelve UNA SOLA VEZ aquí; NO se loguea.
            _logger.LogInformation("Aprovisionamiento OK usuarioId={UsuarioId} baseId={BaseId} motor={Motor}",
                usuarioId, resultado.BaseDeDatosId, resultado.Motor);

            return Ok(resultado);
        }
        catch (SpBusinessException ex)
        {
            // El SP decidió rechazar (límite de bases, motor inexistente, etc.). Mapeamos a
            // un status sin exponer detalle interno de la BD.
            _logger.LogWarning("Aprovisionamiento rechazado por SP usuarioId={UsuarioId} err={Err}",
                usuarioId, ex.SpErrorNumber);

            // 50004 = límite de bases por usuario excedido (sp_AprovisionarBaseDatos) → 409 Conflict.
            var status = ex.SpErrorNumber == 50004
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status422UnprocessableEntity;

            return StatusCode(status, new { error = MensajeSeguro(ex.SpErrorNumber) });
        }
        catch (ProvisioningEngineException ex)
        {
            // La reserva en ABA_Control ya se revirtió a 'ELIMINADA' dentro del orquestador
            // (sp_ConfirmarAprovisionamiento @Exitoso=0); aquí solo se traduce a un status
            // semánticamente correcto (no 500): el motor destino falló, no el cliente.
            _logger.LogError(ex, "Fallo de motor aprovisionando usuarioId={UsuarioId}", usuarioId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "No se pudo completar el aprovisionamiento en este momento. Intenta de nuevo más tarde.",
            });
        }
    }

    private bool TryUsuarioId(out long usuarioId)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(sub, out usuarioId);
    }

    /// <summary>Mensajes de negocio permitidos; nunca se filtra estructura interna de la BD.</summary>
    private static string MensajeSeguro(int spErrorNumber) => spErrorNumber switch
    {
        50002 => "Tu cuenta no está activa.",
        50003 => "Motor de base de datos no soportado.",
        50004 => "Has alcanzado el número máximo de bases de datos permitidas.",
        _ => "No se pudo completar el aprovisionamiento.",
    };
}
