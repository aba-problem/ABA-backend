using System.Net.Http.Json;
using System.Text.Json;

namespace abaproblem.Services;

/// <summary>
/// Implementación real contra la API v4 de Cloudflare (DNS de coderhivex.com ya
/// gestionado ahí). El token de autorización se configura como header por defecto del
/// HttpClient tipado en Program.cs — nunca se concatena manualmente ni se loguea aquí.
///
/// "proxied = true" activa el proxy naranja de Cloudflare, que entrega TLS automático
/// sin necesitar Certbot/ACME propio (documentado como dependencia explícita en el
/// entregable: si el registro no debe pasar por el proxy — p.ej. un registro MX o un
/// servicio que no habla HTTP/HTTPS — este valor tendría que volverse configurable por
/// tipo de registro; fuera de alcance de este entregable, el caso de uso actual son
/// subdominios HTTP/HTTPS de servicios internos).
/// </summary>
public sealed class CloudflareDnsProviderService : IDnsProviderService
{
    private readonly HttpClient _http;
    private readonly string _zoneId;
    private readonly string _dominioBase;
    private readonly ILogger<CloudflareDnsProviderService> _logger;

    public CloudflareDnsProviderService(HttpClient http, IConfiguration config, ILogger<CloudflareDnsProviderService> logger)
    {
        _http = http;
        _zoneId = config["Dns:CloudflareZoneId"]
            ?? throw new InvalidOperationException("Dns:CloudflareZoneId no configurada.");
        _dominioBase = config["Dns:DominioBase"] ?? "aba.coderhivex.com";
        _logger = logger;
    }

    public async Task<bool> CrearRegistroAsync(string subdominio, string tipoRegistro, string valor, CancellationToken ct = default)
    {
        var nombreCompleto = $"{subdominio}.{_dominioBase}";
        var body = new
        {
            type = tipoRegistro,
            name = nombreCompleto,
            content = valor,
            ttl = 1,        // 1 = "automático" en Cloudflare
            proxied = true, // TLS automático vía proxy — ver nota de la clase
        };

        try
        {
            using var respuesta = await _http.PostAsJsonAsync($"client/v4/zones/{_zoneId}/dns_records", body, ct);
            if (!respuesta.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Cloudflare rechazó la creación del registro DNS {Subdominio} (status {Status})",
                    subdominio, (int)respuesta.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // Nunca debe tumbar la request del usuario por un tercero lento/caído — el
            // caller (DnsController) ya trata esto como "confirmar con Exitoso=false".
            _logger.LogError(ex, "Fallo de red creando registro DNS {Subdominio}", subdominio);
            return false;
        }
    }

    public async Task<bool> EliminarRegistroAsync(string subdominio, CancellationToken ct = default)
    {
        var nombreCompleto = $"{subdominio}.{_dominioBase}";
        try
        {
            using var busqueda = await _http.GetAsync(
                $"client/v4/zones/{_zoneId}/dns_records?name={Uri.EscapeDataString(nombreCompleto)}", ct);
            if (!busqueda.IsSuccessStatusCode)
            {
                _logger.LogError("Cloudflare rechazó la búsqueda del registro DNS {Subdominio} (status {Status})",
                    subdominio, (int)busqueda.StatusCode);
                return false;
            }

            await using var stream = await busqueda.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var resultados = json.RootElement.GetProperty("result");
            if (resultados.GetArrayLength() == 0)
                return true; // ya no existe en Cloudflare — idempotente, no es un fallo

            var recordId = resultados[0].GetProperty("id").GetString();
            using var borrado = await _http.DeleteAsync($"client/v4/zones/{_zoneId}/dns_records/{recordId}", ct);
            return borrado.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo de red eliminando registro DNS {Subdominio}", subdominio);
            return false;
        }
    }
}
