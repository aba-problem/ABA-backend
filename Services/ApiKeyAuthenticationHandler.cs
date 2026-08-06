using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using abaproblem.Repositories.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace abaproblem.Services;

/// <summary>
/// Entregable 3 — Módulo IA como Servicio. Esquema de autenticación separado del JWT de
/// cookies: lee el header "X-API-Key" (NUNCA acepta la key por query string — un query
/// param queda en logs de Nginx/proxies y en el historial del navegador).
///
/// Formato esperado: "sk_" + prefijo(8) + secreto(24). El prefijo localiza la fila
/// candidata sin escanear toda la tabla (sp_ObtenerApiKeyPorPrefijo, indexado); el hash
/// SHA-256 de la key COMPLETA se compara contra ApiKey.KeyHash con
/// <see cref="CryptographicOperations.FixedTimeEquals"/> — nunca == ni SequenceEqual,
/// que retornan en el primer byte distinto y filtran cuánto del hash coincidió (timing attack).
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string HeaderName = "X-API-Key";
    private const string Prefijo = "sk_";

    private readonly IApiKeyRepository _repo;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyRepository repo)
        : base(options, logger, encoder)
    {
        _repo = repo;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var valores) || valores.Count == 0)
            return AuthenticateResult.NoResult();

        var keyCompleta = valores.ToString();
        // "sk_" + prefijo(8) = mínimo 11 caracteres antes del secreto.
        if (string.IsNullOrWhiteSpace(keyCompleta) || !keyCompleta.StartsWith(Prefijo, StringComparison.Ordinal)
            || keyCompleta.Length < Prefijo.Length + 8)
        {
            return AuthenticateResult.Fail("API key con formato inválido.");
        }

        var prefijoCandidato = keyCompleta.Substring(Prefijo.Length, 8);

        var candidata = await _repo.ObtenerPorPrefijoAsync(prefijoCandidato, Context.RequestAborted);
        if (candidata is null || !candidata.Activa)
            return AuthenticateResult.Fail("API key inválida o revocada."); // mensaje genérico — no distingue "no existe" de "revocada"

        var hashRecibido = SHA256.HashData(Encoding.UTF8.GetBytes(keyCompleta));
        if (!CryptographicOperations.FixedTimeEquals(hashRecibido, candidata.KeyHash))
            return AuthenticateResult.Fail("API key inválida o revocada.");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, candidata.UsuarioId.ToString()),
            new Claim("apiKeyId", candidata.Id.ToString()),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
