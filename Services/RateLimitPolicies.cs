using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace abaproblem.Services;

/// <summary>
/// Módulo 5.1 — Rate limiting en capas (multi-estrategia), centralizado aquí para que
/// Program.cs solo cablee el pipeline. Cada política dedicada corresponde a un control
/// específico de otro módulo; se documenta en el comentario de cada una.
/// </summary>
public static class RateLimitPolicies
{
    public static IServiceCollection AddSecurityRateLimiters(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // Estrategia de lectura (Módulo 5.1) — dashboard usa esta; landing tiene la suya propia.
            options.AddSlidingWindowLimiter("sliding", opt =>
            {
                opt.PermitLimit = 10;
                opt.Window = TimeSpan.FromMinutes(1);
                opt.SegmentsPerWindow = 4;
                opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                opt.QueueLimit = 2;
            });

            // Control 1.3 — política "auth" dedicada y agresiva: 5 intentos por IP cada 5 minutos.
            options.AddPolicy("auth", context =>
            {
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(5),
                    QueueLimit = 0,
                });
            });

            // Control 2.2 — política "provisioning" por usuario: Token Bucket, 1 base cada 10 min.
            options.AddPolicy("provisioning", context =>
            {
                var userId = IdentificarPartición(context);
                return RateLimitPartition.GetTokenBucketLimiter(userId, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 1,                          // ráfaga inicial de 1
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(10), // recarga lenta
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });

            // Control 3.2 — política "credenciales" por usuario: 5 consultas de contraseña por hora.
            options.AddPolicy("credenciales", context =>
            {
                var userId = IdentificarPartición(context);
                return RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                });
            });

            // Control 4.1 — política "landing" por IP: Sliding Window agresivo (endpoint público sin auth).
            options.AddPolicy("landing", context =>
            {
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetSlidingWindowLimiter(ip, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 4,
                    QueueLimit = 0,
                });
            });

            // Red de seguridad global (Módulo 5.1) — se encadenan DOS defensas para toda request:
            //   1) Fixed Window por IP: red general de tráfico (60 req/min).
            //   2) Concurrency Limiter GLOBAL (una sola partición, no por IP): máximo de peticiones
            //      procesándose simultáneamente (20) — defensa MÁS DIRECTA contra saturación de
            //      threads/memoria del backend en la VPS de 4GB ante un pico de tráfico.
            // CreateChained exige que AMBOS limiters permitan la request; si cualquiera rechaza, 429.
            var fixedWindowPorIp = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
            var concurrencyGlobal = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetConcurrencyLimiter("global", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = 20,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                }));
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(fixedWindowPorIp, concurrencyGlobal);

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                if (!context.HttpContext.Response.Headers.ContainsKey("Retry-After"))
                    context.HttpContext.Response.Headers["Retry-After"] = "60";
                await context.HttpContext.Response.WriteAsync(
                    "Demasiadas peticiones. Intenta de nuevo más tarde.", token);
            };
        });

        return services;
    }

    /// <summary>Partición por usuarioId (claim "sub") si está autenticado; si no, por IP.</summary>
    private static string IdentificarPartición(HttpContext context)
        => context.User.FindFirst("sub")?.Value
           ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
           ?? context.Connection.RemoteIpAddress?.ToString()
           ?? "anon";
}
