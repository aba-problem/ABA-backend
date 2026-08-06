namespace abaproblem.Services;

/// <summary>
/// Entregable 3 — Módulo DNS Autoservicio. Integración con el proveedor DNS real
/// (Cloudflare, ya configurado para andrescortes.dev). Mismo patrón que
/// IGeoIpService/ICaptchaService: HttpClient tipado con timeout corto — una llamada
/// externa lenta nunca debe colgar la request del usuario.
/// </summary>
public interface IDnsProviderService
{
    /// <summary>Crea el registro en el proveedor real. False si falla (nunca lanza para errores esperables del proveedor).</summary>
    Task<bool> CrearRegistroAsync(string subdominio, string tipoRegistro, string valor, CancellationToken ct = default);

    /// <summary>Elimina el registro en el proveedor real. Idempotente: true también si ya no existía.</summary>
    Task<bool> EliminarRegistroAsync(string subdominio, CancellationToken ct = default);
}
