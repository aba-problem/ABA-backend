using Microsoft.Extensions.Caching.Memory;

namespace abaproblem.Services;

public interface ILoginAttemptTracker
{
    /// <summary>True si la IP está temporalmente bloqueada. Devuelve también segundos restantes de backoff.</summary>
    bool EstaBloqueada(string ip, out int retryAfterSegundos);

    /// <summary>Registra un intento fallido. Devuelve el Retry-After sugerido (backoff exponencial).</summary>
    int RegistrarFallo(string ip);

    /// <summary>Limpia el historial de la IP tras un login exitoso.</summary>
    void RegistrarExito(string ip);

    /// <summary>
    /// True si esa IP ya acumuló suficientes fallos (control 1.3, segunda línea de defensa)
    /// como para exigir CAPTCHA antes de reintentar — previo al bloqueo total por tiempo.
    /// </summary>
    bool RequiereCaptcha(string ip);
}

/// <summary>
/// Control 1.3 — Defensa contra fuerza bruta / fuzzing en el flujo de login.
///
/// - Bloqueo temporal (NO permanente) de la IP tras 15 fallos en 1 hora.
/// - Backoff exponencial en Retry-After a medida que se acumulan fallos.
/// - Gestionado 100% en memoria (IMemoryCache) para NO gastar ciclos del motor SQL en esto.
///
/// Complementa (no reemplaza) al rate limiter "auth" de Program.cs (5 intentos/IP/5min).
/// El rate limiter frena ráfagas; este tracker penaliza acumulación sostenida de fallos.
/// </summary>
public sealed class LoginAttemptTracker : ILoginAttemptTracker
{
    private readonly IMemoryCache _cache;

    private const int UmbralBloqueo = 15;                              // fallos que disparan bloqueo
    private const int UmbralCaptcha = 3;                                // fallos que exigen captcha (previo al bloqueo)
    private static readonly TimeSpan VentanaConteo = TimeSpan.FromHours(1);
    private static readonly TimeSpan DuracionBloqueo = TimeSpan.FromMinutes(15);
    private const int RetryAfterBaseSegundos = 2;                       // base del backoff exponencial
    private const int RetryAfterMaxSegundos = 300;                      // techo del backoff (5 min)

    public LoginAttemptTracker(IMemoryCache cache) => _cache = cache;

    private static string ClaveFallos(string ip) => $"login:fails:{ip}";
    private static string ClaveBloqueo(string ip) => $"login:block:{ip}";

    public bool EstaBloqueada(string ip, out int retryAfterSegundos)
    {
        if (_cache.TryGetValue(ClaveBloqueo(ip), out DateTimeOffset expira))
        {
            var restante = (int)Math.Ceiling((expira - DateTimeOffset.UtcNow).TotalSeconds);
            retryAfterSegundos = Math.Max(restante, 1);
            return true;
        }
        retryAfterSegundos = 0;
        return false;
    }

    public int RegistrarFallo(string ip)
    {
        // Módulo 6: el IMemoryCache raíz tiene SizeLimit → toda entrada debe declarar Size.
        var fallos = _cache.GetOrCreate(ClaveFallos(ip), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = VentanaConteo;
            entry.Size = 1;
            return 0;
        });

        fallos++;
        // Reescribe conservando la ventana deslizante de conteo.
        _cache.Set(ClaveFallos(ip), fallos, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = VentanaConteo,
            Size = 1,
        });

        if (fallos >= UmbralBloqueo)
        {
            var expira = DateTimeOffset.UtcNow.Add(DuracionBloqueo);
            _cache.Set(ClaveBloqueo(ip), expira, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = DuracionBloqueo,
                Size = 1,
            });
            return (int)DuracionBloqueo.TotalSeconds;
        }

        // Backoff exponencial: 2, 4, 8, 16 ... con techo de 300s.
        var backoff = RetryAfterBaseSegundos * (int)Math.Pow(2, Math.Min(fallos - 1, 8));
        return Math.Min(backoff, RetryAfterMaxSegundos);
    }

    public void RegistrarExito(string ip)
    {
        _cache.Remove(ClaveFallos(ip));
        _cache.Remove(ClaveBloqueo(ip));
    }

    public bool RequiereCaptcha(string ip)
    {
        var fallos = _cache.Get<int?>(ClaveFallos(ip)) ?? 0;
        return fallos >= UmbralCaptcha;
    }
}
