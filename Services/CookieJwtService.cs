using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using abaproblem.Contracts;
using Microsoft.IdentityModel.Tokens;

namespace abaproblem.Services;

public interface ICookieJwtService
{
    /// <summary>Emite access + refresh como cookies HttpOnly. Nunca devuelve el token en el body.</summary>
    void EmitirCookies(HttpContext ctx, UsuarioDto usuario);

    /// <summary>Extrae el access token desde la cookie (lo usa JwtBearer.OnMessageReceived).</summary>
    string? LeerAccessTokenDesdeCookie(HttpContext ctx);

    /// <summary>Valida el refresh token de cookie y devuelve el usuario (autocontenido) si es válido.</summary>
    bool ValidarRefreshToken(HttpContext ctx, out UsuarioDto? usuario);

    /// <summary>Borra todas las cookies de sesión (logout).</summary>
    void LimpiarCookies(HttpContext ctx);
}

/// <summary>
/// Módulo 1 — Control 1.1: el JWT se entrega EXCLUSIVAMENTE como cookie HttpOnly, Secure,
/// SameSite=Strict, Path=/, expiración corta + refresh token HttpOnly con rotación en cada uso.
/// El backend NUNCA devuelve el JWT en el body JSON. El frontend nunca manipula el token.
///
/// SameSite=Strict es válido aquí porque el frontend (aba.andrescortes.dev) y la API
/// (api.aba.andrescortes.dev) comparten sitio registrable. El único punto donde el flujo
/// OAuth necesita Lax es la cookie de correlación del handler, gestionada por el framework.
/// </summary>
public sealed class CookieJwtService : ICookieJwtService
{
    public const string AccessCookie = "access_token";
    public const string RefreshCookie = "refresh_token";

    private const string RefreshTokenTypeClaim = "token_type";
    private const string RefreshTokenTypeValue = "refresh";

    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _accessMinutes;
    private readonly int _refreshDays;
    private readonly bool _secureCookies;
    // MapInboundClaims=false: conserva "sub"/"email" sin remapear a URIs de ClaimTypes.
    private readonly JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };

    public CookieJwtService(IConfiguration config, IWebHostEnvironment env)
    {
        var jwtKey = config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < 32)
            throw new InvalidOperationException("Jwt:Key ausente o demasiado corta (mínimo 32 bytes).");

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        _issuer = config["Jwt:Issuer"] ?? "abaproblem-api";
        _audience = config["Jwt:Audience"] ?? "abaproblem-clients";
        _accessMinutes = config.GetValue("Jwt:AccessMinutes", 45);   // 30-60 min según control 1.1
        _refreshDays = config.GetValue("Jwt:RefreshDays", 7);
        // Secure siempre en producción; en Development se relaja solo si no hay HTTPS local.
        _secureCookies = !env.IsDevelopment() || config.GetValue("Cookies:Secure", true);
    }

    public void EmitirCookies(HttpContext ctx, UsuarioDto usuario)
    {
        var accessExp = DateTime.UtcNow.AddMinutes(_accessMinutes);
        var refreshExp = DateTime.UtcNow.AddDays(_refreshDays);

        var access = ConstruirToken(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, usuario.UsuarioId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, usuario.Correo),
            new Claim("name", usuario.Nombre),
            new Claim("provider", usuario.Proveedor),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        }, accessExp);

        // Rotación: cada refresh token lleva un jti nuevo. Emitir uno nuevo en cada /refresh
        // invalida (por reemplazo) el anterior en poder del cliente. Lleva el perfil mínimo
        // para que /refresh sea autocontenido y no golpee a SQL Server en cada renovación.
        var refresh = ConstruirToken(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, usuario.UsuarioId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, usuario.Correo),
            new Claim("name", usuario.Nombre),
            new Claim("provider", usuario.Proveedor),
            new Claim("fecha_creacion", usuario.FechaCreacion.ToString("o")),
            new Claim(RefreshTokenTypeClaim, RefreshTokenTypeValue),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        }, refreshExp);

        ctx.Response.Cookies.Append(AccessCookie, access, OpcionesCookie(accessExp, "/"));
        // El refresh solo viaja al endpoint que lo consume → Path acotado reduce exposición.
        ctx.Response.Cookies.Append(RefreshCookie, refresh, OpcionesCookie(refreshExp, "/auth"));
    }

    public string? LeerAccessTokenDesdeCookie(HttpContext ctx)
        => ctx.Request.Cookies.TryGetValue(AccessCookie, out var t) && !string.IsNullOrWhiteSpace(t) ? t : null;

    public bool ValidarRefreshToken(HttpContext ctx, out UsuarioDto? usuario)
    {
        usuario = null;
        if (!ctx.Request.Cookies.TryGetValue(RefreshCookie, out var token) || string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var principal = _handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _issuer,
                ValidAudience = _audience,
                IssuerSigningKey = _key,
                ClockSkew = TimeSpan.FromMinutes(1),
            }, out _);

            if (principal.FindFirst(RefreshTokenTypeClaim)?.Value != RefreshTokenTypeValue)
                return false; // un access token no puede usarse como refresh

            var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (!long.TryParse(sub, out var usuarioId))
                return false;

            var fechaCreacionClaim = principal.FindFirst("fecha_creacion")?.Value;
            var fechaCreacion = DateTime.TryParse(fechaCreacionClaim,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var fc) ? fc : DateTime.UtcNow;

            usuario = new UsuarioDto
            {
                UsuarioId = usuarioId,
                Correo = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value ?? string.Empty,
                Nombre = principal.FindFirst("name")?.Value ?? string.Empty,
                Proveedor = principal.FindFirst("provider")?.Value ?? string.Empty,
                FechaCreacion = fechaCreacion,
            };
            return true;
        }
        catch
        {
            return false; // firma inválida, expirado, manipulado → refresh rechazado
        }
    }

    public void LimpiarCookies(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(AccessCookie, OpcionesCookie(DateTime.UtcNow, "/"));
        ctx.Response.Cookies.Delete(RefreshCookie, OpcionesCookie(DateTime.UtcNow, "/auth"));
    }

    private string ConstruirToken(IEnumerable<Claim> claims, DateTime expira)
    {
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expira,
            signingCredentials: creds);
        return _handler.WriteToken(token);
    }

    private CookieOptions OpcionesCookie(DateTimeOffset expira, string path) => new()
    {
        HttpOnly = true,                        // JS del frontend NO puede leerla → mitiga robo vía XSS
        Secure = _secureCookies,                // solo sobre HTTPS
        SameSite = SameSiteMode.Strict,         // mitiga CSRF / cross-site
        Path = path,
        Expires = expira,
        IsEssential = true,
    };
}
