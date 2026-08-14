using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using abaproblem.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace abaproblem.Services;

/// <summary>
/// Implementación real contra la API de Snapshot. El HttpClient tipado (Program.cs) ya
/// trae BaseAddress y el header x-api-key con la key propia del backend — nunca se
/// concatena ni se loguea acá.
///
/// La API key es UNA sola, compartida por TODA la célula (no por usuario de ABA), con
/// límite del proveedor de 20 req/min EN TOTAL. El límite HTTP "n8n-crear"
/// (RateLimitPolicies.cs) está particionado POR USUARIO — protege contra que un usuario
/// individual haga ráfagas, pero no protege el presupuesto compartido si muchos usuarios
/// distintos aprovisionan a la vez. Por eso este cliente agrega un contador global en
/// memoria como segunda barrera, igual patrón que PolyServiceAiClient con PolyService.
/// </summary>
public sealed class SnapshotN8nClient : ISnapshotN8nClient
{
    private const int LimitePorMinutoSoft = 15; // por debajo del tope real del proveedor (20/min)

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly bool _configurado;
    private readonly ILogger<SnapshotN8nClient> _logger;
    private readonly object _contadorLock = new();

    public SnapshotN8nClient(HttpClient http, IMemoryCache cache, IConfiguration config, ILogger<SnapshotN8nClient> logger)
    {
        _http = http;
        _cache = cache;
        // Chequeo diferido a punto de uso, NUNCA en el constructor ni en el delegate de
        // Program.cs — construir este servicio nunca debe poder tumbar a quien lo pide
        // (mismo incidente real que con Mongo/PolyService: un throw acá tumbaba TODO
        // DashboardController/ProvisioningOrchestrator, no solo el motor que lo necesitaba).
        _configurado = !string.IsNullOrWhiteSpace(config["Snapshot:N8nApiKey"]);
        _logger = logger;
    }

    public async Task<SnapshotN8nProvisionResult> AprovisionarAsync(string externalUserRef, string email, CancellationToken ct = default)
    {
        if (!_configurado)
            throw new ProvisioningEngineException(
                "No se pudo aprovisionar el workspace de N8N en el motor destino.",
                new InvalidOperationException("Snapshot:N8nApiKey no configurada."));

        if (!ReservarCupoDelMinuto())
        {
            _logger.LogWarning("Snapshot: cupo por minuto (soft) agotado, no se llama al proveedor");
            throw new ServicioExternoSaturadoException("Snapshot");
        }

        var body = new { external_user_ref = externalUserRef, email };

        HttpResponseMessage respuesta;
        try
        {
            respuesta = await _http.PostAsJsonAsync("n8n/external/provision", body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Fallo de red llamando a Snapshot (N8N)");
            throw new ProvisioningEngineException("No se pudo aprovisionar el workspace de N8N en el motor destino.", ex);
        }

        using (respuesta)
        {
            if (respuesta.StatusCode == HttpStatusCode.Conflict)
            {
                _logger.LogWarning("Snapshot respondió 409 (cuenta ya existente) externalUserRef={Ref}", externalUserRef);
                throw new SnapshotCuentaExistenteException();
            }

            if (respuesta.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("Snapshot respondió 429 (límite del proveedor alcanzado)");
                throw new ServicioExternoSaturadoException("Snapshot");
            }

            if (!respuesta.IsSuccessStatusCode)
            {
                _logger.LogError("Snapshot respondió status {Status} aprovisionando N8N", (int)respuesta.StatusCode);
                throw new ProvisioningEngineException(
                    "No se pudo aprovisionar el workspace de N8N en el motor destino.",
                    new HttpRequestException($"Snapshot status {(int)respuesta.StatusCode}"));
            }

            await using var stream = await respuesta.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;

            var accountId = root.GetProperty("account_id").GetString()!;
            var status = root.GetProperty("status").GetString()!;
            var accessType = root.GetProperty("access_type").GetString()!;
            var credential = root.GetProperty("credential").GetString()!;

            _logger.LogInformation("Workspace N8N aprovisionado en Snapshot accountId={AccountId} status={Status}", accountId, status);

            return new SnapshotN8nProvisionResult(accountId, status, accessType, credential);
        }
    }

    /// <summary>
    /// Contador por minuto en memoria (clave = minuto UTC actual). IMemoryCache no
    /// incrementa atómicamente — se protege con un lock corto (una sola instancia de
    /// backend, no un contador distribuido entre réplicas).
    /// </summary>
    private bool ReservarCupoDelMinuto()
    {
        var clave = $"snapshot-n8n-uso-{DateTime.UtcNow:yyyy-MM-ddTHH:mm}";
        lock (_contadorLock)
        {
            var usoActual = _cache.TryGetValue(clave, out int valor) ? valor : 0;
            if (usoActual >= LimitePorMinutoSoft)
                return false;

            _cache.Set(clave, usoActual + 1, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2),
                Size = 1,
            });
            return true;
        }
    }
}
