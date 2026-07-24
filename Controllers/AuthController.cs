using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using abaproblem.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace abaproblem.Controllers;

/// <summary>
/// Módulo 1 — Autenticación OAuth2 (Google + GitHub).
/// El JWT se entrega SOLO como cookie HttpOnly (control 1.1); jamás en el body.
/// </summary>
[ApiController]
[Route("auth")]
public sealed class AuthController : ControllerBase
{
    // Control 1.3 — mensaje genérico único: nunca revela "usuario no existe" vs "token inválido".
    private const string ErrorGenerico = "No se pudo completar la autenticación";

    private readonly IUsuarioRepository _usuarios;
    private readonly IIpWhitelistRepository _ipWhitelist;
    private readonly IGeoIpService _geoIp;
    private readonly IMySqlWhitelistSyncService _mysqlWhitelistSync;
    private readonly ICookieJwtService _jwt;
    private readonly ILoginAttemptTracker _tracker;
    private readonly ICaptchaService _captcha;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<AuthController> _logger;
    private readonly string _frontendBaseUrl;

    public AuthController(
        IUsuarioRepository usuarios,
        IIpWhitelistRepository ipWhitelist,
        IGeoIpService geoIp,
        IMySqlWhitelistSyncService mysqlWhitelistSync,
        ICookieJwtService jwt,
        ILoginAttemptTracker tracker,
        ICaptchaService captcha,
        IAntiforgery antiforgery,
        IConfiguration config,
        ILogger<AuthController> logger)
    {
        _usuarios = usuarios;
        _ipWhitelist = ipWhitelist;
        _geoIp = geoIp;
        _mysqlWhitelistSync = mysqlWhitelistSync;
        _jwt = jwt;
        _tracker = tracker;
        _captcha = captcha;
        _antiforgery = antiforgery;
        _logger = logger;
        _frontendBaseUrl = (config["Frontend:BaseUrl"] ?? "https://aba.andrescortes.dev").TrimEnd('/');
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  LOGIN (challenge) — inicia el flujo OAuth. El framework genera y valida el
    //  parámetro `state` (cookie de correlación) → control 1.3 CSRF del redirect.
    //  Control 1.3 (cierre de auditoría) — segunda línea de defensa: si esa IP ya
    //  acumuló >= 3 fallos, exige captcha ANTES de gastar el flujo OAuth completo.
    //  El bloqueo total por tiempo también se chequea aquí (no solo en el callback),
    //  para no ni siquiera redirigir a Google/GitHub si la IP ya está bloqueada.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet("google/login")]
    public Task<IActionResult> GoogleLogin([FromQuery] string? captchaToken, CancellationToken ct)
        => RetarProveedorAsync(AuthSchemes.Google, "google", captchaToken, ct);

    [HttpGet("github/login")]
    public Task<IActionResult> GitHubLogin([FromQuery] string? captchaToken, CancellationToken ct)
        => RetarProveedorAsync(AuthSchemes.GitHub, "github", captchaToken, ct);

    private async Task<IActionResult> RetarProveedorAsync(string scheme, string proveedor, string? captchaToken, CancellationToken ct)
    {
        var ip = IpCliente();

        if (_tracker.EstaBloqueada(ip, out var retryAfter))
        {
            Response.Headers["Retry-After"] = retryAfter.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ErrorGenerico });
        }

        if (_tracker.RequiereCaptcha(ip))
        {
            var captchaValido = await _captcha.ValidarAsync(captchaToken ?? string.Empty, ip, ct);
            if (!captchaValido)
                return BadRequest(new { error = "CAPTCHA_REQUERIDO", mensaje = "Verificación adicional requerida." });
        }

        var props = new AuthenticationProperties
        {
            // Tras validar el code/state, el handler de OAuth (CallbackPath en Program.cs,
            // igual a /auth/{proveedor}/callback — lo que está registrado en Google/GitHub)
            // redirige aquí para el post-procesamiento. Esta ruta DEBE ser distinta de
            // CallbackPath: si coincidieran, el handler interceptaría también este redirect
            // (sin code/state en la URL) y tronaría con "oauth state was missing or invalid".
            RedirectUri = Url.Action(nameof(Callback), "Auth", new { proveedor })!,
            AllowRefresh = false,
        };
        return Challenge(props, scheme);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  POST-PROCESAMIENTO — punto de mayor riesgo: rate limit dedicado + anti-fuerza-bruta.
    //  Control 1.3: 5 intentos/IP/5min (rate limiter "auth") + bloqueo temporal 15/1h.
    //
    //  IMPORTANTE: esta ruta ("procesar") es distinta de CallbackPath en Program.cs
    //  ("/auth/{proveedor}/callback", que es lo registrado en Google/GitHub). El handler
    //  de OAuth intercepta CallbackPath, valida code+state, y redirige aquí. Si esta ruta
    //  fuera la MISMA que CallbackPath, el handler interceptaría también este redirect
    //  (sin code/state) y fallaría con "oauth state was missing or invalid" — no cambiar
    //  esto sin también separar CallbackPath en Program.cs.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet("{proveedor:regex(^(google|github)$)}/procesar")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Callback(string proveedor, CancellationToken ct)
    {
        var ip = IpCliente();

        // Bloqueo temporal en memoria (no toca SQL Server) — control 1.3.
        if (_tracker.EstaBloqueada(ip, out var retryAfter))
        {
            Response.Headers["Retry-After"] = retryAfter.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ErrorGenerico });
        }

        // El handler OAuth ya validó code+state y firmó en la cookie "External".
        var externo = await HttpContext.AuthenticateAsync(AuthSchemes.External);
        if (!externo.Succeeded || externo.Principal is null)
            return FalloAutenticacion(ip, "principal externo ausente");

        // Control 1.4 — validar el payload de OAuth ANTES de tocar el SP.
        if (!TryConstruirInfo(proveedor, externo.Principal, out var info, out var motivo))
            return FalloAutenticacion(ip, motivo);

        // Validación de DataAnnotations sobre el DTO (longitudes, formato email/url).
        var ctxVal = new ValidationContext(info);
        var errores = new List<ValidationResult>();
        if (!Validator.TryValidateObject(info, ctxVal, errores, validateAllProperties: true))
            return FalloAutenticacion(ip, "payload OAuth inválido: " + string.Join("; ", errores.Select(e => e.ErrorMessage)));

        UsuarioDto usuario;
        try
        {
            // Upsert dentro del SP (nunca en el backend) — sp_CrearUsuario (ABA_Control).
            usuario = await _usuarios.ObtenerOCrearAsync(info, ip, ct);
        }
        catch (SpBusinessException ex)
        {
            return FalloAutenticacion(ip, $"SP rechazó login: {ex.SpErrorNumber}");
        }

        // Éxito: limpiar contador de fallos, emitir cookies HttpOnly, cerrar cookie externa.
        _tracker.RegistrarExito(ip);
        _jwt.EmitirCookies(HttpContext, usuario);
        await HttpContext.SignOutAsync(AuthSchemes.External);

        _logger.LogInformation("Login OK proveedor={Proveedor} usuarioId={UsuarioId} ip={Ip}",
            info.Proveedor, usuario.UsuarioId, ip); // nunca se loguea el JWT (control 5.8)

        // Whitelist de IP (sql/003) + espejo MySQL (sql/005): NUNCA debe hacer fallar el
        // login — ya se emitió el JWT. Si el país está fuera de América/Latam, el SP ya
        // audita el rechazo (IP_RECHAZADA) y el usuario simplemente no podrá CONECTARSE a
        // su BD (lo bloquea el logon trigger), aunque sí pueda usar la plataforma web.
        await RegistrarWhitelistSinFallarLoginAsync(usuario.UsuarioId, ip, ct);

        // Redirige al frontend; el JWT ya viaja en cookie, no en la URL ni en el body.
        return Redirect($"{_frontendBaseUrl}/auth/success");
    }

    private async Task RegistrarWhitelistSinFallarLoginAsync(long usuarioId, string ip, CancellationToken ct)
    {
        try
        {
            var paisIso = await _geoIp.ResolverPaisIsoAsync(ip, ct);
            if (paisIso is null)
            {
                _logger.LogWarning("No se pudo resolver país para ip={Ip} usuarioId={UsuarioId}; whitelist omitida", ip, usuarioId);
                return;
            }

            await _ipWhitelist.RegistrarAsync(usuarioId, ip, paisIso, ct);
            await _mysqlWhitelistSync.SincronizarAsync(usuarioId, ct);
        }
        catch (SpBusinessException ex)
        {
            // 50010: país fuera de América/Latam — ya auditado como IP_RECHAZADA en el SP.
            _logger.LogWarning("Whitelist de IP rechazada usuarioId={UsuarioId} ip={Ip} err={Err}", usuarioId, ip, ex.SpErrorNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo registrando whitelist de IP usuarioId={UsuarioId} ip={Ip}", usuarioId, ip);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  REFRESH — rota el par access/refresh. Mutante → requiere CSRF token.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        await ValidarCsrf();

        // El refresh token es autocontenido (lleva el perfil mínimo). No golpea a SQL Server.
        if (!_jwt.ValidarRefreshToken(HttpContext, out var usuario) || usuario is null)
        {
            _jwt.LimpiarCookies(HttpContext);
            return Unauthorized(new { error = ErrorGenerico });
        }

        _jwt.EmitirCookies(HttpContext, usuario); // rotación: nuevo jti en cada uso
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  LOGOUT — mutante → requiere CSRF token. Borra las cookies de sesión.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await ValidarCsrf();
        _jwt.LimpiarCookies(HttpContext);
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  CSRF — entrega el token anti-falsificación (cookie NO HttpOnly) para que
    //  Angular lo lea y lo reenvíe en el header X-CSRF-TOKEN (control 1.2).
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet("csrf")]
    public IActionResult Csrf()
    {
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        // El request token se expone en cookie legible por JS (patrón double-submit); el
        // token de validación viaja en cookie HttpOnly gestionada por el framework.
        // SameSite=None es OBLIGATORIO aquí: el frontend (aba.andrescortes.dev) y la API
        // (api.aba.andrescortes.dev) son subdominios distintos = "cross-site" para el
        // navegador. Con Strict o Lax, el navegador DESCARTA la cookie en cualquier
        // petición cross-origin, sin importar credentials:'include'. El par __CSRF
        // (HttpOnly) también necesita None por la misma razón.
        Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,               // el frontend DEBE poder leerla (Double Submit Cookie)
            Secure = true,                  // solo sobre HTTPS
            SameSite = SameSiteMode.None,   // OBLIGATORIO para cross-origin (subdominios distintos)
            Path = "/",
        });
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task ValidarCsrf()
    {
        // Control 1.2 — valida que el header X-CSRF-TOKEN coincida con la cookie de sesión.
        // Lanza AntiforgeryValidationException (→ 400 vía middleware) si no coincide.
        await _antiforgery.ValidateRequestAsync(HttpContext);
    }

    private IActionResult FalloAutenticacion(string ip, string motivoInterno)
    {
        var retryAfter = _tracker.RegistrarFallo(ip);
        Response.Headers["Retry-After"] = retryAfter.ToString();

        // Se loguea el motivo REAL solo del lado servidor; al cliente, mensaje genérico.
        _logger.LogWarning("Fallo de autenticación ip={Ip} motivo={Motivo}", ip, motivoInterno);
        return Redirect($"{_frontendBaseUrl}/auth/error?reason=auth_failed");
    }

    private string IpCliente() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Control 1.4 — Construye y valida el payload de OAuth. Nunca confía ciegamente en los claims.
    /// Google: exige email_verified = true. GitHub: exige email presente (verificado por GitHub).
    /// Proveedor se normaliza en MAYÚSCULAS ('GOOGLE'/'GITHUB') — coincide con
    /// CK_Usuario_Proveedor de sql/001_init_control_db.sql.
    /// </summary>
    private static bool TryConstruirInfo(string proveedor, ClaimsPrincipal principal, out ExternalLoginInfo info, out string motivo)
    {
        info = default!;
        var correo = principal.FindFirstValue(ClaimTypes.Email);
        var nombre = principal.FindFirstValue(ClaimTypes.Name)
                     ?? principal.FindFirstValue(ClaimTypes.GivenName)
                     ?? correo;
        // Id estable de la cuenta en el proveedor ("sub" en Google, "id" en GitHub) — ambos
        // handlers de ASP.NET Core lo mapean por defecto a ClaimTypes.NameIdentifier.
        var proveedorUsuarioId = principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(correo))
        {
            motivo = "email ausente en claims";
            return false;
        }
        if (string.IsNullOrWhiteSpace(proveedorUsuarioId))
        {
            motivo = "id de proveedor ausente en claims";
            return false;
        }

        string? avatar;
        bool verificado;

        if (proveedor.Equals("google", StringComparison.OrdinalIgnoreCase))
        {
            avatar = principal.FindFirstValue("picture");
            verificado = bool.TryParse(principal.FindFirstValue("email_verified"), out var v) && v;
            if (!verificado)
            {
                motivo = "email_verified != true (Google)";
                return false;
            }
        }
        else // github
        {
            avatar = principal.FindFirstValue("avatar_url");
            verificado = true; // GitHub solo expone emails verificados de su API de usuario
        }

        info = new ExternalLoginInfo
        {
            Proveedor = proveedor.ToUpperInvariant(),
            ProveedorUsuarioId = proveedorUsuarioId.Trim(),
            Correo = correo.Trim(),
            Nombre = (nombre ?? correo).Trim(),
            AvatarUrl = string.IsNullOrWhiteSpace(avatar) ? null : avatar.Trim(),
            EmailVerificado = verificado,
        };
        motivo = string.Empty;
        return true;
    }
}
