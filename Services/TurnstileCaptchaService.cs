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

        var secret = _config["Captcha:TurnstileSecretKey"]
            ?? throw new InvalidOperationException("Captcha:TurnstileSecretKey no configurada.");

        var contenido = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["secret"] = secret,
            ["response"] = token,
            ["remoteip"] = ip,
        });

        try
        {
            var respuesta = await _http.PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify", contenido, ct);
            var json = await respuesta.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean();
        }
        catch (Exception ex)
        {
            // Fail-closed: si Cloudflare no responde, NO dejamos pasar el login sin captcha
            // cuando el captcha era obligatorio para esa IP. Mejor negar que dejar un hueco.
            _logger.LogWarning(ex, "Fallo al validar captcha con Turnstile");
            return false;
        }
    }
}
