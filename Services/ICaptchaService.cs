namespace abaproblem.Services;

/// <summary>
/// Control 1.3 (cierre de auditoría) — Segunda línea de defensa contra fuerza bruta,
/// exigida condicionalmente por <see cref="ILoginAttemptTracker.RequiereCaptcha"/> tras
/// varios fallos consecutivos desde la misma IP. El backoff exponencial ya existente
/// frena la velocidad; el captcha frena a un script paciente que respeta el backoff.
/// </summary>
public interface ICaptchaService
{
    Task<bool> ValidarAsync(string token, string ip, CancellationToken ct);
}
