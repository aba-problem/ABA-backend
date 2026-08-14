using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using abaproblem.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace abaproblem.Services;

/// <summary>
/// Implementación real contra PolyService IA. El HttpClient tipado (registrado en
/// Program.cs) ya trae BaseAddress y el header Authorization con la API key propia
/// del backend — nunca se concatena ni se loguea aquí.
///
/// PolyService limita la key compartida a 10 req/min + 100/día EN TOTAL (no por
/// usuario de ABA). El límite HTTP "ai-service" (RateLimitPolicies.cs) ya protege el
/// eje de req/min con un tope global por debajo de 10; este cliente agrega un
/// contador diario en memoria como segunda barrera — soft (se resetea si el
/// contenedor reinicia), pensado para frenar el consumo antes de gastar el
/// presupuesto real del proveedor, no como sustituto de él.
/// </summary>
public sealed class PolyServiceAiClient : IPolyServiceAiClient
{
    private const string Modelo = "llama-8b-nvidia";
    private const int LimiteDiarioSoft = 90; // por debajo del tope real del proveedor (100/día)

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PolyServiceAiClient> _logger;
    private readonly object _contadorLock = new();

    public PolyServiceAiClient(HttpClient http, IMemoryCache cache, ILogger<PolyServiceAiClient> logger)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
    }

    public async Task<PolyServiceChatResult> CompletarAsync(string prompt, int maxTokens, CancellationToken ct = default)
    {
        var maxTokensClamped = Math.Clamp(maxTokens, 1, 1024);

        if (!ReservarCupoDiario())
        {
            _logger.LogWarning("PolyService: cupo diario soft agotado, no se llama al proveedor");
            throw new ServicioExternoSaturadoException("PolyService");
        }

        var body = new
        {
            model = Modelo,
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = maxTokensClamped,
            stream = false,
        };

        HttpResponseMessage respuesta;
        try
        {
            respuesta = await _http.PostAsJsonAsync("v1/chat/completions", body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Fallo de red llamando a PolyService IA");
            throw new ProvisioningEngineException("No se pudo contactar al servicio de IA.", ex);
        }

        using (respuesta)
        {
            if (respuesta.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("PolyService respondió 429 (límite del proveedor alcanzado)");
                throw new ServicioExternoSaturadoException("PolyService");
            }

            if (!respuesta.IsSuccessStatusCode)
            {
                _logger.LogError("PolyService respondió status {Status}", (int)respuesta.StatusCode);
                throw new ProvisioningEngineException(
                    "El servicio de IA no pudo procesar la solicitud.",
                    new HttpRequestException($"PolyService status {(int)respuesta.StatusCode}"));
            }

            await using var stream = await respuesta.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;

            var contenido = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

            var tokensPrompt = 0;
            var tokensCompletion = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt)) tokensPrompt = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var ct2)) tokensCompletion = ct2.GetInt32();
            }

            return new PolyServiceChatResult(contenido, tokensPrompt, tokensCompletion);
        }
    }

    /// <summary>
    /// Contador diario en memoria (clave = fecha UTC actual, expira a medianoche UTC).
    /// IMemoryCache no incrementa atómicamente — se protege con un lock corto, ya que
    /// esto es una sola instancia de backend (no un contador distribuido entre réplicas).
    /// </summary>
    private bool ReservarCupoDiario()
    {
        var clave = $"polyservice-uso-diario-{DateTime.UtcNow:yyyy-MM-dd}";
        lock (_contadorLock)
        {
            var usoActual = _cache.TryGetValue(clave, out int valor) ? valor : 0;
            if (usoActual >= LimiteDiarioSoft)
                return false;

            var medianocheUtc = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1), TimeSpan.Zero);
            _cache.Set(clave, usoActual + 1, new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = medianocheUtc,
                Size = 1,
            });
            return true;
        }
    }
}
