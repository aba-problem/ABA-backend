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

            // Control 2.2 — política "provisioning" por usuario: Token Bucket, 1 base cada 2 min.
            options.AddPolicy("provisioning", context =>
            {
                var userId = IdentificarPartición(context);
                return RateLimitPartition.GetTokenBucketLimiter(userId, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 1,                          // ráfaga inicial de 1
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(2),
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

            // Módulo 8 — política "partners" por célula socia: Token Bucket, ráfaga de 10 y
            // recarga de 1 cada 2 min. Subido de 5 a 10 (2026-08-10) al agregar el chequeo de
            // cuota de espacio (sql/017_espacio_socio.sql): con ráfaga 5, una célula que
            // consultaba espacio antes de escribir se quedaba sin cupo para crear/listar/
            // eliminar bases. Ajustar de nuevo según el volumen real que generen las 11
            // células una vez en uso.
            options.AddPolicy("partners", context =>
            {
                var celulaId = context.User.FindFirst("celulaId")?.Value
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "anon";
                return RateLimitPartition.GetTokenBucketLimiter(celulaId, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 10,
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(2),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });

            // Entregable 3, Módulo N8N — "n8n-crear" por usuario: mismo Token Bucket que
            // "provisioning" (1 workspace cada 10 min), mismo argumento: crear infraestructura
            // real (aunque sea lógica, no un contenedor) nunca debe permitir ráfagas.
            options.AddPolicy("n8n-crear", context =>
            {
                var userId = IdentificarPartición(context);
                return RateLimitPartition.GetTokenBucketLimiter(userId, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 1,
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(10),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });

            // Entregable 3, Módulo IA — "apikeys-crear" por usuario: Fixed Window, más
            // permisivo que provisioning/n8n (no aprovisiona infraestructura real), pero
            // igual acotado para frenar el "farming" de keys.
            options.AddPolicy("apikeys-crear", context =>
            {
                var userId = IdentificarPartición(context);
                return RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                });
            });

            // Entregable 3, Módulo IA — "ai-service": PolyService IA usa UNA sola API key
            // compartida por TODO ABA (no una por usuario), con un límite del proveedor de
            // 10 req/min + 100/día EN TOTAL. Por eso esta partición usa una clave CONSTANTE
            // (no por ApiKeyId/IP) — el techo tiene que ser sobre el consumo agregado de
            // todos los usuarios, no por usuario, o cada usuario podría agotar el presupuesto
            // compartido por su cuenta. 8/min queda deliberadamente por debajo del límite real
            // del proveedor (10/min) para dejar margen antes de que PolyService devuelva 429.
            // El eje de 100/día se refuerza además con un contador soft en PolyServiceAiClient.
            options.AddPolicy("ai-service", context =>
                RateLimitPartition.GetTokenBucketLimiter("polyservice-global", key => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 8,
                    TokensPerPeriod = 8,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

            // Entregable 3, Módulo DNS — "dns-crear" por usuario: Token Bucket, cada
            // registro toca un proveedor externo real (Cloudflare) — ráfagas controladas.
            options.AddPolicy("dns-crear", context =>
            {
                var userId = IdentificarPartición(context);
                return RateLimitPartition.GetTokenBucketLimiter(userId, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 1,
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(5),
                    QueueLimit = 0,
                    AutoReplenishment = true,
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
            //   1) Fixed Window por IP: red general de tráfico.
            //   2) Concurrency Limiter GLOBAL (una sola partición, no por IP): máximo de peticiones
            //      procesándose simultáneamente — defensa MÁS DIRECTA contra saturación de
            //      threads/memoria del backend en la VPS de 4GB ante un pico de tráfico.
            // CreateChained exige que AMBOS limiters permitan la request; si cualquiera rechaza, 429.
            //
            // 2026-08-11: subidos los umbrales — con 60/min + 20 concurrentes SIN cola (QueueLimit=0,
            // rechazo inmediato), un solo usuario creando una BD (que tarda varios segundos en el
            // aprovisionamiento de dos fases) y probando otro módulo (N8N/DNS/ApiKeys) en la misma
            // ventana ya alcanzaba el techo GLOBAL — aunque cada política por-feature
            // (provisioning/n8n-crear/apikeys-crear/dns-crear) es independiente y NUNCA comparte
            // balde entre sí, esta red global sí es compartida por toda la sesión y estaba
            // bloqueando "crear otro servicio" sin que fuera abuso real. Se sube el techo y se le
            // da una cola corta (en vez de rechazo instantáneo) para absorber ráfagas legítimas.
            var fixedWindowPorIp = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 180,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
            var concurrencyGlobal = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetConcurrencyLimiter("global", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = 60,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 15, // absorbe ráfagas cortas en vez de rechazar la request 61 al instante
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
