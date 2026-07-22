using System.Net;
using System.Text.Json;

namespace abaproblem.Services;

/// <summary>
/// Implementación vía API HTTP pública (ip-api.com, free tier: sin API key,
/// ~45 req/min por IP saliente). Solo se llama una vez por login (no es hot-path),
/// así que el costo de una llamada externa es aceptable dentro del presupuesto
/// de recursos del Módulo 6.
///
/// Nota de producción: el free tier de ip-api.com no ofrece SLA ni HTTPS. Si el
/// volumen de logins crece o se necesita mayor fiabilidad, migrar a una base de
/// datos local (MaxMind GeoLite2-Country.mmdb + librería MaxMind.GeoIP2) evita la
/// dependencia de red por completo — el contrato <see cref="IGeoIpService"/> no
/// cambiaría, solo esta implementación.
/// </summary>
public sealed class GeoIpService : IGeoIpService
{
    private readonly HttpClient _http;
    private readonly ILogger<GeoIpService> _logger;

    public GeoIpService(HttpClient http, ILogger<GeoIpService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<string?> ResolverPaisIsoAsync(string direccionIp, CancellationToken ct = default)
    {
        // IPs privadas/loopback (dev local, health checks) nunca son geo-localizables.
        if (!IPAddress.TryParse(direccionIp, out var ip) || EsPrivadaOLoopback(ip))
            return null;

        try
        {
            using var respuesta = await _http.GetAsync(
                $"http://ip-api.com/json/{Uri.EscapeDataString(direccionIp)}?fields=status,countryCode", ct);

            if (!respuesta.IsSuccessStatusCode)
                return null;

            await using var stream = await respuesta.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var estado = json.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (estado != "success")
                return null;

            return json.RootElement.TryGetProperty("countryCode", out var c) ? c.GetString() : null;
        }
        catch (Exception ex)
        {
            // Nunca debe romper el login: si el proveedor geo-IP falla, se registra
            // y el llamador decide (típicamente: omitir el whitelisting de esta IP).
            _logger.LogWarning(ex, "No se pudo resolver el país para la IP {Ip}", direccionIp);
            return null;
        }
    }

    private static bool EsPrivadaOLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }
        return false;
    }
}
