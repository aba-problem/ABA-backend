using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace abaproblem.Controllers;

/// <summary>
/// Módulo 8 (continuación) — alta/gestión de células socias desde el panel, reemplazando el
/// proceso 100% manual documentado en Aba/ALTA-CELULA-SOCIA.md. Requiere el claim de rol
/// "Admin" en el JWT (ver CookieJwtService — se decide EXCLUSIVAMENTE por Usuario.EsAdmin en
/// ABA_Control, nunca por algo que el cliente controle). Los SPs también revalidan EsAdmin
/// como defensa en profundidad (sql/018), mismo patrón que AdminDnsController.
///
/// No confundir con PartnersProvisioningController (/partners/databases): ese lo consume la
/// PROPIA célula con su API key para aprovisionar SUS bases; este lo consume un admin de ABA
/// para gestionar el REGISTRO de células — nunca la misma identidad ni el mismo propósito.
/// </summary>
[ApiController]
[Route("admin/celulas-socias")]
[Authorize(Roles = "Admin")]
public sealed class AdminCelulasSociasController : ControllerBase
{
    private readonly ICelulasSociasAdminRepository _repo;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<AdminCelulasSociasController> _logger;

    public AdminCelulasSociasController(ICelulasSociasAdminRepository repo, IAntiforgery antiforgery, ILogger<AdminCelulasSociasController> logger)
    {
        _repo = repo;
        _antiforgery = antiforgery;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct)
    {
        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        try
        {
            var celulas = await _repo.ListarAsync(usuarioId, ct);
            return Ok(celulas);
        }
        catch (SpBusinessException ex) when (ex.SpErrorNumber == 50107)
        {
            return Forbid(); // defensa en profundidad — no debería llegar aquí si el rol ya se validó
        }
    }

    [HttpPost]
    public async Task<IActionResult> Alta([FromBody] AltaCelulaSociaRequest body, CancellationToken ct)
    {
        await _antiforgery.ValidateRequestAsync(HttpContext);

        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        try
        {
            var creada = await _repo.AltaAsync(usuarioId, body.NombreCelula.Trim(), body.Prefijo.Trim(), ct);
            _logger.LogInformation("Admin dio de alta célula socia adminId={AdminId} celulaId={CelulaId}", usuarioId, creada.Id);
            return StatusCode(StatusCodes.Status201Created, creada);
        }
        catch (SpBusinessException ex) when (ex.SpErrorNumber == 50107)
        {
            return Forbid();
        }
        catch (SpBusinessException ex)
        {
            // 50001 prefijo inválido, 50002 nombre/prefijo duplicado
            return UnprocessableEntity(new { error = ex.Message });
        }
    }

    [HttpPatch("{id:int}/estado")]
    public async Task<IActionResult> CambiarEstado(int id, [FromBody] CambiarEstadoCelulaSociaRequest body, CancellationToken ct)
    {
        await _antiforgery.ValidateRequestAsync(HttpContext);

        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        try
        {
            var actualizada = await _repo.CambiarEstadoAsync(usuarioId, id, body.Activo, ct);
            _logger.LogInformation("Admin cambió estado de célula socia adminId={AdminId} celulaId={CelulaId} activo={Activo}",
                usuarioId, id, body.Activo);
            return Ok(actualizada);
        }
        catch (SpBusinessException ex) when (ex.SpErrorNumber == 50107)
        {
            return Forbid();
        }
        catch (SpBusinessException ex) when (ex.SpErrorNumber == 50108)
        {
            return NotFound();
        }
    }

    [HttpPost("{id:int}/rotar-key")]
    public async Task<IActionResult> RotarApiKey(int id, CancellationToken ct)
    {
        await _antiforgery.ValidateRequestAsync(HttpContext);

        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        try
        {
            var rotada = await _repo.RotarApiKeyAsync(usuarioId, id, ct);
            _logger.LogInformation("Admin rotó API key de célula socia adminId={AdminId} celulaId={CelulaId}", usuarioId, id);
            return Ok(rotada);
        }
        catch (SpBusinessException ex) when (ex.SpErrorNumber == 50107)
        {
            return Forbid();
        }
        catch (SpBusinessException ex) when (ex.SpErrorNumber == 50108)
        {
            return NotFound();
        }
    }

    private bool TryUsuarioId(out long usuarioId)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(sub, out usuarioId);
    }
}
