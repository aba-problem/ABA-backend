using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using abaproblem.Repositories.Interfaces;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace abaproblem.Controllers;

/// <summary>
/// Módulo 3 — Dashboard, Detalle y Desactivación de Bases (ABA_Control).
/// Todos los endpoints son [Authorize] y filtran exclusivamente por el usuarioId del JWT.
/// Los endpoints de solo lectura no requieren CSRF; los mutantes (desactivar) sí.
/// </summary>
[ApiController]
[Route("dashboard")]
[Authorize]
public sealed class DashboardController : ControllerBase
{
    private readonly IDashboardRepository _repo;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(IDashboardRepository repo, IAntiforgery antiforgery, ILogger<DashboardController> logger)
    {
        _repo = repo;
        _antiforgery = antiforgery;
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
    /// Control 3.1 (BOLA): retorna UNA base por ID, SIN password. La validación
    /// de dueño ocurre dentro de sp_ObtenerBaseDatosPorId — si el ID no pertenece
    /// al usuario, respondemos 404 (nunca 403) para no confirmar existencia.
    /// </summary>
    [HttpGet("bases/{id:long}")]
    [EnableRateLimiting("sliding")] // Módulo 5.1: lectura → Sliding Window
    public async Task<IActionResult> ObtenerPorId(long id, CancellationToken ct)
    {
        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized();

        var baseDatos = await _repo.ObtenerPorIdAsync(id, usuarioId, ct);
        if (baseDatos is null)
            return NotFound(); // no existe o no pertenece al usuario (control 3.1)

        return Ok(baseDatos);
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

    /// <summary>
    /// Desactiva (soft-delete) una base de datos. Estado → 'ELIMINADA'.
    /// Control 1.2: endpoint mutante → requiere CSRF token.
    /// Control BOLA: sp_DesactivarBaseDatos valida que la base pertenezca al usuario.
    /// El DELETE real es interceptado por trg_BaseDeDatos_SoftDelete que convierte
    /// a UPDATE + registro en Auditoria (Accion='DESACTIVAR').
    /// </summary>
    [HttpDelete("bases/{id:long}")]
    public async Task<IActionResult> Desactivar(long id, CancellationToken ct)
    {
        await _antiforgery.ValidateRequestAsync(HttpContext);

        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized();

        var resultado = await _repo.DesactivarAsync(id, usuarioId, ct);
        if (resultado is null)
            return NotFound(); // no existe o no pertenece al usuario (control 3.1)

        _logger.LogInformation("Base desactivada usuarioId={UsuarioId} baseId={BaseId}", usuarioId, id);
        return NoContent();
    }

    private bool TryUsuarioId(out long usuarioId)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(sub, out usuarioId);
    }
}
