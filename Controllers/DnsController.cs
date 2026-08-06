using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using abaproblem.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace abaproblem.Controllers;

/// <summary>
/// Entregable 3 — Módulo DNS Autoservicio (endpoints del usuario). Dos fases: el SP
/// reserva 'PENDIENTE', el backend llama al proveedor DNS real (Cloudflare) y confirma.
/// Endpoints admin en <see cref="AdminDnsController"/>.
/// </summary>
[ApiController]
[Route("dns")]
[Authorize]
public sealed class DnsController : ControllerBase
{
    private readonly IDnsRepository _repo;
    private readonly IDnsProviderService _proveedor;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<DnsController> _logger;

    public DnsController(IDnsRepository repo, IDnsProviderService proveedor, IAntiforgery antiforgery, ILogger<DnsController> logger)
    {
        _repo = repo;
        _proveedor = proveedor;
        _antiforgery = antiforgery;
        _logger = logger;
    }

    [HttpPost("crear")]
    [EnableRateLimiting("dns-crear")]
    public async Task<IActionResult> Crear([FromBody] DnsRegistroCrearRequest request, CancellationToken ct)
    {
        await _antiforgery.ValidateRequestAsync(HttpContext);

        if (request.PropiedadesDesconocidas is { Count: > 0 })
            return BadRequest(new { error = "El cuerpo contiene campos no permitidos." });

        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        DnsRegistroReservaDto reserva;
        try
        {
            reserva = await _repo.ValidarYCrearAsync(usuarioId, request.Subdominio, request.TipoRegistro, request.Valor, IpCliente(), ct);
        }
        catch (SpBusinessException ex)
        {
            _logger.LogWarning("Registro DNS rechazado por SP usuarioId={UsuarioId} err={Err}", usuarioId, ex.SpErrorNumber);
            var status = ex.SpErrorNumber == 50043 ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity;
            return StatusCode(status, new { error = MensajeSeguro(ex.SpErrorNumber) });
        }

        // Fase 2: el proveedor real. Si falla, la reserva se revierte a 'ELIMINADA' — nunca
        // queda 'ACTIVO' en ABA_Control sin existir de verdad en Cloudflare.
        var exitoso = await _proveedor.CrearRegistroAsync(reserva.Subdominio, reserva.TipoRegistro, reserva.Valor, ct);
        await _repo.ConfirmarAsync(reserva.Id, exitoso, IpCliente(), ct);

        if (!exitoso)
        {
            _logger.LogError("Fallo creando registro DNS en el proveedor real, registroId={Id} subdominio={Subdominio}",
                reserva.Id, reserva.Subdominio);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "No se pudo completar el registro DNS en este momento. Intenta de nuevo más tarde." });
        }

        _logger.LogInformation("Registro DNS creado usuarioId={UsuarioId} registroId={Id} subdominio={Subdominio}",
            usuarioId, reserva.Id, reserva.Subdominio);
        return Ok(reserva);
    }

    [HttpGet("mis-registros")]
    public async Task<IActionResult> MisRegistros(CancellationToken ct)
    {
        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        var registros = await _repo.ListarMisRegistrosAsync(usuarioId, ct);
        return Ok(registros);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Eliminar(int id, CancellationToken ct)
    {
        await _antiforgery.ValidateRequestAsync(HttpContext);

        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized(new { error = "Sesión inválida." });

        var datos = await _repo.EliminarAsync(usuarioId, id, IpCliente(), ct);
        if (datos is null)
            return NotFound(); // control 3.1 (BOLA): no existe o no es tuyo, nunca 403

        // Best-effort: ABA_Control ya quedó consistente (ELIMINADA) aunque esto falle;
        // un registro huérfano en Cloudflare no es un riesgo de seguridad, solo limpieza.
        var borrado = await _proveedor.EliminarRegistroAsync(datos.Subdominio, ct);
        if (!borrado)
            _logger.LogError("No se pudo eliminar el registro DNS en el proveedor real, subdominio={Subdominio}", datos.Subdominio);

        return NoContent();
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
        50041 => "Subdominio inválido.",
        50042 => "Tipo de registro no soportado.",
        50043 => "Ese subdominio ya está en uso.",
        50044 => "Se alcanzó el límite máximo de registros DNS.",
        _ => "No se pudo completar la operación.",
    };
}
