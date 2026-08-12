using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using abaproblem.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace abaproblem.Controllers;

/// <summary>
/// Historial de acceso del usuario autenticado (login, registro, validación/rechazo/
/// revocación de IP en la whitelist) — lectura de dbo.Auditoria ya existente, sin tabla
/// nueva. La revocación de IP sí escribe (UsuarioIp.Activo=0) — control BOLA: siempre
/// sobre el propio usuario del token.
/// </summary>
[ApiController]
[Route("sesiones")]
[Authorize]
public sealed class SesionesController : ControllerBase
{
    private readonly ISesionRepository _repo;
    private readonly IMySqlWhitelistSyncService _mysqlWhitelistSync;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<SesionesController> _logger;

    public SesionesController(
        ISesionRepository repo,
        IMySqlWhitelistSyncService mysqlWhitelistSync,
        IAntiforgery antiforgery,
        ILogger<SesionesController> logger)
    {
        _repo = repo;
        _mysqlWhitelistSync = mysqlWhitelistSync;
        _antiforgery = antiforgery;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] int pagina = 1, [FromQuery] int tamanoPagina = 20, CancellationToken ct = default)
    {
        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        var resultado = await _repo.ListarAsync(usuarioId, pagina, tamanoPagina, ct);
        return Ok(resultado);
    }

    /// <summary>
    /// "No fui yo, bloquear" — desactiva esa IP puntual y sincroniza de inmediato el
    /// espejo de whitelist en MySQL para que el bloqueo aplique también ahí, no solo
    /// en el bookkeeping de SQL Server.
    /// </summary>
    [HttpPost("ips/revocar")]
    public async Task<IActionResult> RevocarIp([FromBody] RevocarIpRequest request, CancellationToken ct)
    {
        await _antiforgery.ValidateRequestAsync(HttpContext);

        if (request.PropiedadesDesconocidas is { Count: > 0 })
            return BadRequest(new { error = "El cuerpo contiene campos no permitidos." });

        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        var ipSolicitud = HttpContext.Connection.RemoteIpAddress?.ToString();

        bool encontrada;
        try
        {
            encontrada = await _repo.RevocarIpAsync(usuarioId, request.DireccionIp, ipSolicitud, ct);
        }
        catch (SpBusinessException ex)
        {
            _logger.LogWarning("Revocación de IP rechazada usuarioId={UsuarioId} err={Err}", usuarioId, ex.SpErrorNumber);
            return StatusCode(StatusCodes.Status422UnprocessableEntity, new { error = "No se pudo completar la operación." });
        }

        if (!encontrada)
            return NotFound();

        try
        {
            await _mysqlWhitelistSync.SincronizarAsync(usuarioId, ct);
        }
        catch (Exception syncEx)
        {
            // La IP ya quedó revocada en ABA_Control (fuente de verdad); si el espejo de
            // MySQL falla en sincronizar ahora, el próximo login/aprovisionamiento lo
            // reconcilia — nunca debe convertir esto en un error para el usuario.
            _logger.LogError(syncEx, "IP revocada pero falló la sincronización de whitelist MySQL usuarioId={UsuarioId}", usuarioId);
        }

        _logger.LogInformation("IP revocada por el usuario usuarioId={UsuarioId}", usuarioId);
        return NoContent();
    }

    private bool TryUsuarioId(out long usuarioId)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(sub, out usuarioId);
    }
}
