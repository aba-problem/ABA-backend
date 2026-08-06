using System.Text.Json;

namespace abaproblem.Services;

/// <summary>
/// Cloudflare Turnstile — sin límite de requests para uso normal, verificación
/// mayormente invisible (menos fricción que reCAPTCHA), y no depende de Google
/// (ya usado como proveedor OAuth; evita atar dos servicios al mismo tercero).
/// </summary>
public sealed class TurnstileCaptchaService : ICaptchaService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<TurnstileCaptchaService> _logger;

    public TurnstileCaptchaService(HttpClient http, IConfiguration config, ILogger<TurnstileCaptchaService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<bool> ValidarAsync(string token, string ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var secret = _config["Captcha:TurnstileSecretKey"];
            if (string.IsNullOrWhiteSpace(secret))
            {
                _logger.LogError("Captcha:TurnstileSecretKey no configurada — negando por fail-closed.");
                return false;
            }

            var contenido = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"] = secret,
                ["response"] = token,
                ["remoteip"] = ip,
            });

            var respuesta = await _http.PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify", contenido, ct);
            var json = await respuesta.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al validar captcha con Turnstile");
            return false;
        }
    }
}
