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
/// Entregable 3 — Módulo N8N (autoservicio de workspace). [Authorize] + rate limit
/// dedicado en creación. El backend solo dispara el SP; nombre y contraseña del
/// workspace se generan enteramente en sp_CrearWorkspaceN8N.
/// </summary>
[ApiController]
[Route("n8n")]
[Authorize]
public sealed class N8nController : ControllerBase
{
    private readonly IN8nWorkspaceRepository _repo;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<N8nController> _logger;

    public N8nController(IN8nWorkspaceRepository repo, IAntiforgery antiforgery, ILogger<N8nController> logger)
    {
        _repo = repo;
        _antiforgery = antiforgery;
        _logger = logger;
    }

    [HttpPost("crear")]
    [EnableRateLimiting("n8n-crear")]
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
            // Nunca se loguea la contraseña temporal (control 5.8) — solo el Id del workspace.
            _logger.LogInformation("Workspace N8N creado usuarioId={UsuarioId} workspaceId={Id}", usuarioId, resultado.Id);
            return Ok(resultado);
        }
        catch (SpBusinessException ex)
        {
            _logger.LogWarning("Creación de workspace N8N rechazada usuarioId={UsuarioId} err={Err}", usuarioId, ex.SpErrorNumber);
            var status = ex.SpErrorNumber == 50020 ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity;
            return StatusCode(status, new { error = MensajeSeguro(ex.SpErrorNumber) });
        }
    }

    [HttpGet("mi-workspace")]
    public async Task<IActionResult> MiWorkspace(CancellationToken ct)
    {
        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        var workspace = await _repo.ObtenerMiWorkspaceAsync(usuarioId, ct);
        return workspace is null ? NotFound() : Ok(workspace);
    }

    [HttpDelete("mi-workspace")]
    public async Task<IActionResult> Eliminar(CancellationToken ct)
    {
        await _antiforgery.ValidateRequestAsync(HttpContext);

        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        try
        {
            await _repo.EliminarAsync(usuarioId, IpCliente(), ct);
            return NoContent();
        }
        catch (SpBusinessException ex) when (ex.SpErrorNumber == 50021)
        {
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
        50020 => "Ya tienes un workspace de N8N activo.",
        _ => "No se pudo completar la operación.",
    };
}
