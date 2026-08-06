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
/// Entregable 3 — Módulo DNS Autoservicio, endpoints de administración. Requiere el
/// claim de rol "Admin" en el JWT (ver CookieJwtService — se decide EXCLUSIVAMENTE por
/// Usuario.EsAdmin en ABA_Control, nunca por algo que el cliente controle). Los SPs
/// también revalidan EsAdmin como defensa en profundidad (ver sql/011).
/// </summary>
[ApiController]
[Route("admin/dns")]
[Authorize(Roles = "Admin")]
public sealed class AdminDnsController : ControllerBase
{
    private readonly IDnsRepository _repo;
    private readonly IDnsProviderService _proveedor;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<AdminDnsController> _logger;

    public AdminDnsController(IDnsRepository repo, IDnsProviderService proveedor, IAntiforgery antiforgery, ILogger<AdminDnsController> logger)
    {
        _repo = repo;
        _proveedor = proveedor;
        _antiforgery = antiforgery;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> ListarTodos(CancellationToken ct)
    {
        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        try
        {
            var registros = await _repo.ListarTodosAdminAsync(usuarioId, ct);
            return Ok(registros);
        }
        catch (SpBusinessException ex) when (ex.SpErrorNumber == 50045)
        {
            return Forbid(); // defensa en profundidad — no debería llegar aquí si el rol ya se validó
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> EliminarAdmin(int id, CancellationToken ct)
    {
        await _antiforgery.ValidateRequestAsync(HttpContext);

        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        try
        {
            var datos = await _repo.EliminarAdminAsync(usuarioId, id, IpCliente(), ct);
            if (datos is null)
                return NotFound();

            var borrado = await _proveedor.EliminarRegistroAsync(datos.Subdominio, ct);
            if (!borrado)
                _logger.LogError("Admin: no se pudo eliminar el registro DNS en el proveedor real, subdominio={Subdominio}", datos.Subdominio);

            _logger.LogInformation("Admin eliminó registro DNS adminId={AdminId} registroId={Id}", usuarioId, id);
            return NoContent();
        }
        catch (SpBusinessException ex) when (ex.SpErrorNumber == 50045)
        {
            return Forbid();
        }
    }

    private bool TryUsuarioId(out long usuarioId)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(sub, out usuarioId);
    }

    private string? IpCliente() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
