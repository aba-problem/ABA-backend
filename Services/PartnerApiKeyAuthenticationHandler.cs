using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using abaproblem.Repositories.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace abaproblem.Services;

/// <summary>
/// Módulo 8 — Autenticación server-to-server para el endpoint de células socias
/// (Aba/INVESTIGACION-PROXY-MYSQL.md § 8). Lee `Authorization: Bearer &lt;api-key&gt;`, hashea
/// la key (SHA-256, mecánico — no es lógica de negocio) y le pregunta a ABA_Control, vía
/// ICelulaSociaRepository, si es válida. Nunca decide acá qué célula es ni cuál es su
/// prefijo: esos datos vienen del SP y se exponen como claims para que el controller los use.
///
/// Nombre separado de <see cref="ApiKeyAuthenticationHandler"/> (Entregable 3, Módulo IA) a
/// propósito: mismo concepto de "auth por API key", pero formatos y esquemas de autorización
/// completamente distintos — coexisten sin pisarse.
/// </summary>
public sealed class PartnerApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ICelulaSociaRepository _repo;

    public PartnerApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ICelulaSociaRepository repo)
        : base(options, logger, encoder)
    {
        _repo = repo;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
            return AuthenticateResult.NoResult();

        var value = header.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var apiKey = value["Bearer ".Length..].Trim();
        if (apiKey.Length == 0)
            return AuthenticateResult.Fail("API key vacía.");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));

        var celula = await _repo.ValidarApiKeyAsync(hash, Context.RequestAborted);
        if (celula is null)
            return AuthenticateResult.Fail("API key inválida.");

        var claims = new[]
        {
            new Claim("celulaId", celula.CelulaId.ToString()),
            new Claim("nombreCelula", celula.NombreCelula),
            new Claim("prefijo", celula.Prefijo),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
