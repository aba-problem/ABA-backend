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
    private readonly IProvisioningRepository _provisioningRepo;
    private readonly IMongoProvisioningService _mongo;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(
        IDashboardRepository repo,
        IProvisioningRepository provisioningRepo,
        IMongoProvisioningService mongo,
        IAntiforgery antiforgery,
        ILogger<DashboardController> logger)
    {
        _repo = repo;
        _provisioningRepo = provisioningRepo;
        _mongo = mongo;
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
    /// Rotación de credencial para motores donde la password la genera un proveedor
    /// externo (hoy: MongoDB). Control 3.1/3.2: mismo chequeo BOLA y mismo rate limit
    /// estricto que /credencial (5/hora por usuario) — sigue siendo re-exposición de
    /// secreto. Control 1.2: endpoint mutante con cookies → exige CSRF token.
    /// </summary>
    [HttpPost("bases/{id:long}/rotar-credencial")]
    [EnableRateLimiting("credenciales")]
    public async Task<IActionResult> RotarCredencial(long id, CancellationToken ct)
    {
        await _antiforgery.ValidateRequestAsync(HttpContext);

        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized();

        // Reutiliza sp_ObtenerCredencialesBaseDatos (mismo chequeo BOLA de siempre) solo
        // para confirmar dueño + motor + obtener el MongoExternalId — la password que trae
        // NO se usa ni se reenvía, se descarta.
        var actual = await _repo.ObtenerCredencialesAsync(id, usuarioId, ct);
        if (actual is null)
            return NotFound();

        if (!string.Equals(actual.Motor, "MongoDB", StringComparison.OrdinalIgnoreCase) || actual.MongoExternalId is null)
            return BadRequest(new { error = "La rotación de credencial solo está disponible para bases MongoDB." });

        MongoCredencialRotadaResult credencialNueva;
        try
        {
            credencialNueva = await _mongo.RotarCredencialAsync(actual.MongoExternalId, ct);
        }
        catch (ServicioExternoSaturadoException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "El proveedor de MongoDB alcanzó su límite temporal. Intenta en unos minutos.",
            });
        }
        catch (ProvisioningEngineException ex)
        {
            _logger.LogError(ex, "Fallo rotando credencial Mongo usuarioId={UsuarioId} baseId={BaseId}", usuarioId, id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "No se pudo rotar la credencial en este momento. Intenta de nuevo más tarde.",
            });
        }

        var persistido = await _provisioningRepo.RotarCredencialExternaAsync(
            id, usuarioId, credencialNueva.Username, credencialNueva.Password, ipOrigen: null, ct);
        if (!persistido)
        {
            // La rotó el proveedor pero ABA_Control ya no la reconoce como del usuario (carrera
            // rarísima: la base se desactivó entre el chequeo de arriba y este punto). No se
            // pierde el secreto: sigue siendo válida en el proveedor, solo no queda accesible acá.
            _logger.LogError("Credencial Mongo rotada en el proveedor pero no se pudo persistir usuarioId={UsuarioId} baseId={BaseId}", usuarioId, id);
            return NotFound();
        }

        _logger.LogInformation("Credencial rotada usuarioId={UsuarioId} baseId={BaseId}", usuarioId, id);
        // Se entrega UNA sola vez, igual que /credencial. El proveedor regenera también el
        // usuario (no solo la password) — el frontend debe reemplazar ambos en su estado local.
        return Ok(new { usuario = credencialNueva.Username, password = credencialNueva.Password });
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

    /// <summary>Retorna el perfil del usuario autenticado.</summary>
    [HttpGet("perfil")]
    [EnableRateLimiting("sliding")]
    public async Task<IActionResult> Perfil(CancellationToken ct)
    {
        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized();

        var perfil = await _repo.ObtenerPerfilAsync(usuarioId, ct);
        if (perfil is null)
            return NotFound();

        return Ok(perfil);
    }

    /// <summary>
    /// Actualiza nombre/avatar del usuario autenticado. No pisa lo que venga de
    /// Google/GitHub — persiste en columnas separadas que sp_CrearUsuario nunca toca,
    /// así el cambio sobrevive al próximo login (ver sql/023_perfil_personalizado.sql).
    /// Control 1.2: endpoint mutante con cookies → exige CSRF token.
    /// </summary>
    [HttpPut("perfil")]
    public async Task<IActionResult> ActualizarPerfil([FromBody] ActualizarPerfilRequest request, CancellationToken ct)
    {
        await _antiforgery.ValidateRequestAsync(HttpContext);

        if (request.PropiedadesDesconocidas is { Count: > 0 })
            return BadRequest(new { error = "El cuerpo contiene campos no permitidos." });

        if (!TryUsuarioId(out var usuarioId))
            return Unauthorized();

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

        try
        {
            var perfil = await _repo.ActualizarPerfilAsync(usuarioId, request.Nombre, request.AvatarUrl, ip, ct);
            _logger.LogInformation("Perfil actualizado usuarioId={UsuarioId}", usuarioId);
            return Ok(perfil);
        }
        catch (SpBusinessException ex) when (ex.SpErrorNumber is 50040 or 50041)
        {
            return BadRequest(new { error = MensajeValidacionPerfil(ex.SpErrorNumber) });
        }
    }

    private static string MensajeValidacionPerfil(int spErrorNumber) => spErrorNumber switch
    {
        50040 => "El nombre no puede estar vacío.",
        50041 => "La URL del avatar debe empezar con http:// o https://.",
        _ => "No se pudo actualizar el perfil.",
    };

    private bool TryUsuarioId(out long usuarioId)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(sub, out usuarioId);
    }
}
