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
/// Entregable 3 — Módulo N8N (autoservicio de workspace), aprovisionado por Snapshot
/// (proveedor externo real, https://api.snapshot.andrescortes.dev). [Authorize] + rate
/// limit dedicado en creación. El backend llama a Snapshot y persiste el resultado —
/// nunca genera el account_id ni el enlace de invitación, esos vienen del proveedor.
/// </summary>
[ApiController]
[Route("n8n")]
[Authorize]
public sealed class N8nController : ControllerBase
{
    private readonly IN8nWorkspaceRepository _repo;
    private readonly IDashboardRepository _dashboardRepo;
    private readonly ISnapshotN8nClient _snapshot;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<N8nController> _logger;

    public N8nController(
        IN8nWorkspaceRepository repo,
        IDashboardRepository dashboardRepo,
        ISnapshotN8nClient snapshot,
        IAntiforgery antiforgery,
        ILogger<N8nController> logger)
    {
        _repo = repo;
        _dashboardRepo = dashboardRepo;
        _snapshot = snapshot;
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

        // Control BOLA: el correo SIEMPRE sale del perfil del usuario autenticado, nunca del
        // body — es la identidad con la que Snapshot entrega el enlace de invitación.
        var perfil = await _dashboardRepo.ObtenerPerfilAsync(usuarioId, ct);
        if (perfil is null)
            return Unauthorized(new { error = "Sesión inválida." });

        // Chequeo local previo: evita gastar una llamada real a Snapshot (y su cupo
        // compartido) para el caso común de "ya tenés un workspace en ABA_Control". No
        // elimina la ventana de carrera por completo, pero cubre el caso normal sin tocar
        // al proveedor externo.
        var existente = await _repo.ObtenerMiWorkspaceAsync(usuarioId, ct);
        if (existente is not null)
            return StatusCode(StatusCodes.Status409Conflict, new { error = MensajeSeguro(50020) });

        try
        {
            var aprovisionado = await _snapshot.AprovisionarAsync(usuarioId.ToString(), perfil.Correo, ct);

            N8nWorkspaceCreadoDto guardado;
            try
            {
                guardado = await _repo.RegistrarExternoAsync(
                    usuarioId, aprovisionado.AccountId, perfil.Correo, aprovisionado.Credential, IpCliente(), ct);
            }
            catch (SpBusinessException ex)
            {
                // Snapshot YA creó la cuenta real en su lado en este mismo request, pero
                // ABA_Control rechazó el registro local (p. ej. una carrera con otra solicitud
                // simultánea). No hay endpoint de borrado en la API de Snapshot para deshacerlo
                // — queda una cuenta huérfana ahí. Se loguea como ERROR real para investigar a
                // mano, nunca se le miente al usuario diciendo que salió bien.
                _logger.LogError(ex,
                    "Snapshot aprovisionó (accountId={AccountId}) pero ABA_Control rechazó el registro — posible cuenta huérfana en Snapshot. usuarioId={UsuarioId} err={Err}",
                    aprovisionado.AccountId, usuarioId, ex.SpErrorNumber);
                var status = ex.SpErrorNumber == 50020 ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity;
                return StatusCode(status, new { error = MensajeSeguro(ex.SpErrorNumber) });
            }

            // Nunca se loguea el enlace de invitación (control 5.8) — solo el Id del workspace.
            _logger.LogInformation("Workspace N8N creado (Snapshot) usuarioId={UsuarioId} workspaceId={Id}", usuarioId, guardado.Id);
            return Ok(guardado);
        }
        catch (SnapshotCuentaExistenteException)
        {
            _logger.LogWarning("Snapshot indica cuenta N8N ya existente usuarioId={UsuarioId}", usuarioId);
            return StatusCode(StatusCodes.Status409Conflict, new
            {
                error = "Ya existe una cuenta de N8N asociada a tu correo en el proveedor externo. Si perdiste el enlace de invitación, contactá soporte.",
            });
        }
        catch (ServicioExternoSaturadoException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "El proveedor de N8N alcanzó su límite temporal. Intenta en unos minutos.",
            });
        }
        catch (ProvisioningEngineException ex)
        {
            _logger.LogError(ex, "Fallo aprovisionando N8N (Snapshot) usuarioId={UsuarioId}", usuarioId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "No se pudo crear el workspace en este momento. Intenta de nuevo más tarde.",
            });
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

    /// <summary>
    /// Soft-delete LOCAL únicamente — Snapshot no expone ningún endpoint de borrado o
    /// deprovisioning. La cuenta real sigue existiendo del lado del proveedor; si el
    /// usuario intenta crear un workspace nuevo después de esto, es esperable que Snapshot
    /// responda 409 (ver <see cref="SnapshotCuentaExistenteException"/>). El frontend debe
    /// dejar esto explícito, nunca dar a entender que la cuenta externa se eliminó.
    /// </summary>
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
