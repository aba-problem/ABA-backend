using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using abaproblem.Repositories.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace abaproblem.Controllers;

/// <summary>
/// Módulo 3 — Dashboard y Consulta de Credenciales (ABA_Control). Todos los endpoints
/// son de solo lectura, [Authorize], y filtran exclusivamente por el usuarioId del JWT.
/// </summary>
[ApiController]
[Route("dashboard")]
[Authorize]
public sealed class DashboardController : ControllerBase
{
    private readonly IDashboardRepository _repo;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(IDashboardRepository repo, ILogger<DashboardController> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>Lista las bases del usuario autenticado: conexión, estado, espacio, fechas.</summary>
    [HttpGet("bases")]
    [EnableRateLimiting("sliding")] // Módulo 5.1: lectura → Sliding Window
    public async Task<IActionResult> Listar(CancellationToken ct)
    {
        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized();

        var lista = await _repo.ListarAsync(usuarioId, ct);
        return Ok(lista);
    }

    /// <summary>
    /// Control 3.2 — re-exposición de credencial. Rate limit propio y estricto
    /// (política "credenciales": 5 consultas/hora por usuario). La validación de dueño
    /// (control 3.1 BOLA) ocurre DENTRO de sp_ObtenerCredencialesBaseDatos — si @id no
    /// pertenece al usuario, aquí respondemos 404 (nunca 403) para no confirmar
    /// la existencia del recurso a un atacante.
    /// </summary>
    [HttpGet("bases/{id:long}/credencial")]
    [EnableRateLimiting("credenciales")]
    public async Task<IActionResult> Credencial(long id, CancellationToken ct)
    {
        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized();

        var credencial = await _repo.ObtenerCredencialesAsync(id, usuarioId, ct);
        if (credencial is null)
            return NotFound(); // no existe o no pertenece al usuario (control 3.1)

        _logger.LogInformation("Credencial consultada usuarioId={UsuarioId} baseId={BaseId}", usuarioId, id);
        return Ok(credencial); // nunca se loguea la contraseña en sí (control 5.8)
    }

    private bool TryUsuarioId(out long usuarioId)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(sub, out usuarioId);
    }
}
