using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace abaproblem.Controllers;

/// <summary>
/// Entregable 3 — Módulo IA como Servicio, gestión de API Keys. Estos endpoints de
/// GESTIÓN usan el esquema por defecto (cookie JWT + CSRF) — es la app web del usuario
/// administrando sus propias keys. El USO real de una key (/ai/completar) es un
/// controller separado con el esquema ApiKey, ver <see cref="AiController"/>.
/// </summary>
[ApiController]
[Route("apikeys")]
[Authorize]
public sealed class ApiKeysController : ControllerBase
{
    private readonly IApiKeyRepository _repo;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<ApiKeysController> _logger;

    public ApiKeysController(IApiKeyRepository repo, IAntiforgery antiforgery, ILogger<ApiKeysController> logger)
    {
        _repo = repo;
        _antiforgery = antiforgery;
        _logger = logger;
    }

    [HttpPost("crear")]
    [EnableRateLimiting("apikeys-crear")]
    public async Task<IActionResult> Crear([FromBody] CrearSinCuerpoRequest request, CancellationToken ct)
    {
        await _antiforgery.ValidateRequestAsync(HttpContext);

        if (request.PropiedadesDesconocidas is { Count: > 0 })
            return BadRequest(new { error = "El cuerpo contiene campos no permitidos." });

        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        try
        {
            var resultado = await _repo.CrearAsync(usuarioId, IpCliente(), ct);
            // Se loguea solo el prefijo — nunca la key completa (control 5.8).
            _logger.LogInformation("API key creada usuarioId={UsuarioId} apiKeyId={Id} prefijo={Prefijo}",
                usuarioId, resultado.Id, resultado.Prefijo);
            return Ok(resultado);
        }
        catch (SpBusinessException ex)
        {
            _logger.LogWarning("Creación de API key rechazada usuarioId={UsuarioId} err={Err}", usuarioId, ex.SpErrorNumber);
            var status = ex.SpErrorNumber == 50031 ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity;
            return StatusCode(status, new { error = MensajeSeguro(ex.SpErrorNumber) });
        }
    }

    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct)
    {
        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        var keys = await _repo.ListarAsync(usuarioId, ct);
        return Ok(keys);
    }

    [HttpPost("{id:int}/revocar")]
    public async Task<IActionResult> Revocar(int id, CancellationToken ct)
    {
        await _antiforgery.ValidateRequestAsync(HttpContext);

        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        var resultado = await _repo.RevocarAsync(usuarioId, id, IpCliente(), ct);
        return resultado is null ? NotFound() : NoContent();
    }

    [HttpGet("{id:int}/consumo")]
    public async Task<IActionResult> Consumo(int id, CancellationToken ct)
    {
        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        try
        {
            var consumo = await _repo.ObtenerConsumoAsync(usuarioId, id, ct);
            return Ok(consumo);
        }
        catch (SpBusinessException ex) when (ex.SpErrorNumber == 50011)
        {
            // Control 3.1 (BOLA): no existe o no es tuya → 404, nunca 403.
            return NotFound();
        }
    }

    private bool TryUsuarioId(out long usuarioId)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(sub, out usuarioId);
    }

    private string? IpCliente() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private static string MensajeSeguro(int spErrorNumber) => spErrorNumber switch
    {
        50002 => "Tu cuenta no está activa.",
        50031 => "Se alcanzó el límite máximo de API keys activas.",
        _ => "No se pudo completar la operación.",
    };
}
