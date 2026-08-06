using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using abaproblem.Repositories.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace abaproblem.Controllers;

/// <summary>
/// Historial de acceso del usuario autenticado (login, registro, validación/rechazo
/// de IP en la whitelist) — lectura de dbo.Auditoria ya existente, sin tabla nueva.
/// </summary>
[ApiController]
[Route("sesiones")]
[Authorize]
public sealed class SesionesController : ControllerBase
{
    private readonly ISesionRepository _repo;

    public SesionesController(ISesionRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct)
    {
        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        var sesiones = await _repo.ListarAsync(usuarioId, ct);
        return Ok(sesiones);
    }

    private bool TryUsuarioId(out long usuarioId)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(sub, out usuarioId);
    }
}
