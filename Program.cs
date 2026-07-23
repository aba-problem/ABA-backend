using System.Net;
using System.Text;
using abaproblem.Middleware;
using abaproblem.Repositories.Interfaces;
using abaproblem.Repositories.SqlServer;
using abaproblem.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
//  Carga .env para desarrollo local (`dotnet run`). En despliegue vía Docker las
//  variables ya llegan como variables de entorno reales del proceso (env_file/
//  environment: del orquestador) — esto es SOLO un fallback para local, y NUNCA
//  sobreescribe una variable que el sistema/Docker ya haya establecido.
// ─────────────────────────────────────────────────────────────────────────────
var envFilePath = Path.Combine(builder.Environment.ContentRootPath, ".env");
if (File.Exists(envFilePath))
{
    foreach (var linea in File.ReadAllLines(envFilePath))
    {
        var texto = linea.Trim();
        if (texto.Length == 0 || texto.StartsWith('#'))
            continue;

        var separador = texto.IndexOf('=');
        if (separador <= 0)
            continue;

        var clave = texto[..separador].Trim();
        var valor = texto[(separador + 1)..].Trim();

        if (Environment.GetEnvironmentVariable(clave) is null)
            Environment.SetEnvironmentVariable(clave, valor);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Módulo 5.2 — Hardening de Kestrel contra Slowloris y saturación de conexiones.
//  El backend NUNCA recibe tráfico directo de internet (5.3: siempre detrás de Nginx),
//  pero estos límites son la defensa de última línea del propio proceso .NET.
// ─────────────────────────────────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxConcurrentConnections = 100;
    options.Limits.MaxConcurrentUpgradedConnections = 100;
    options.Limits.MaxRequestBodySize = 1_048_576; // 1MB
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10); // clave contra Slowloris
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    options.Limits.MinRequestBodyDataRate =
        new MinDataRate(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(5));
});

// ─────────────────────────────────────────────────────────────────────────────
//  Configuración: mapea las variables planas del .env (JWT_KEY, DB_CONNECTION_STRING,
//  GOOGLE_CLIENT_ID, ...) a las claves jerárquicas que usa la app. Se añaden al final
//  para que tengan prioridad sobre appsettings.json (secretos nunca en el repo — Módulo 7).
// ─────────────────────────────────────────────────────────────────────────────
var mapaEnv = new Dictionary<string, string?>();
void MapEnv(string clave, string variable)
{
    var valor = Environment.GetEnvironmentVariable(variable);
    if (!string.IsNullOrWhiteSpace(valor)) mapaEnv[clave] = valor;
}
MapEnv("Jwt:Key", "JWT_KEY");
MapEnv("Jwt:Issuer", "JWT_ISSUER");
MapEnv("Jwt:Audience", "JWT_AUDIENCE");
MapEnv("ConnectionStrings:Default", "DB_CONNECTION_STRING");
MapEnv("Authentication:Google:ClientId", "GOOGLE_CLIENT_ID");
MapEnv("Authentication:Google:ClientSecret", "GOOGLE_CLIENT_SECRET");
MapEnv("Authentication:GitHub:ClientId", "GITHUB_CLIENT_ID");
MapEnv("Authentication:GitHub:ClientSecret", "GITHUB_CLIENT_SECRET");
MapEnv("Frontend:BaseUrl", "FRONTEND_BASE_URL");
MapEnv("ReverseProxy:TrustedNetwork", "REVERSE_PROXY_TRUSTED_NETWORK");
MapEnv("MySql:AdminConnectionString", "MYSQL_ADMIN_CONNECTION_STRING");
MapEnv("Captcha:TurnstileSiteKey", "TURNSTILE_SITE_KEY");
MapEnv("Captcha:TurnstileSecretKey", "TURNSTILE_SECRET_KEY");
builder.Configuration.AddInMemoryCollection(mapaEnv);

// ─────────────────────────────────────────────────────────────────────────────
//  Controllers + serialización estricta (control 5.7 / soporte a control 2.2):
//  rechaza propiedades JSON no declaradas en el DTO (previene mass-assignment / fuzzing).
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.UnmappedMemberHandling =
        System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
});

// Swagger solo en Development (control 0 / 5.5): nunca se registra en producción.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Módulo 6 — SizeLimit explícito: el cache de anti-fuerza-bruta (Módulo 1) y el de
// métricas públicas (Módulo 4) nunca crecen sin control. Toda entrada declara Size=1.
builder.Services.AddMemoryCache(options => options.SizeLimit = 2000);

// ─────────────────────────────────────────────────────────────────────────────
//  Antiforgery (control 1.2 — CSRF Double Submit): Angular lee la cookie XSRF-TOKEN
//  y reenvía el valor en el header X-CSRF-TOKEN en cada petición mutante.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__CSRF";
    options.Cookie.HttpOnly = true;                 // la cookie de validación es HttpOnly
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

// ─────────────────────────────────────────────────────────────────────────────
//  Autenticación: JWT (leído desde cookie HttpOnly) + cookie de correlación externa
//  + handlers OAuth de Google y GitHub. (Módulo 1)
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = AuthSchemes.Jwt;              // la API valida por JWT
        options.DefaultChallengeScheme = AuthSchemes.Jwt;
    })
    // 1) JWT Bearer — token tomado de la cookie HttpOnly, no del header Authorization.
    .AddJwtBearer(AuthSchemes.Jwt, options =>
    {
        options.RequireHttpsMetadata = true;
        options.SaveToken = false;
        options.MapInboundClaims = false;                    // conserva claims "sub"/"email"/"provider"/"name"
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]
                    ?? throw new InvalidOperationException("Jwt:Key no configurada."))),
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = "name",
            RoleClaimType = "role",
        };
        // Control 1.1 — el token viaja SOLO en cookie; aquí lo extraemos para validarlo.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var jwt = ctx.HttpContext.RequestServices.GetRequiredService<ICookieJwtService>();
                ctx.Token = jwt.LeerAccessTokenDesdeCookie(ctx.HttpContext);
                return Task.CompletedTask;
            }
        };
    })
    // 2) Cookie temporal de correlación para el flujo OAuth (state/PKCE del handler).
    //    SameSite=Lax es necesario aquí por la redirección top-level de OAuth (control 1.1).
    .AddCookie(AuthSchemes.External, options =>
    {
        options.Cookie.Name = "__External";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(5);     // ventana corta solo para completar el login
        options.SlidingExpiration = false;
    })
    // 3) Google OAuth — control 1.4: mapeamos email_verified y picture para validarlos luego.
    .AddGoogle(AuthSchemes.Google, options =>
    {
        options.ClientId = builder.Configuration["Authentication:Google:ClientId"] ?? "";
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? "";
        options.SignInScheme = AuthSchemes.External;
        options.CallbackPath = "/auth/google/callback";             // interno del handler; distinto del callback del controller
        options.UsePkce = true;
        options.SaveTokens = false;
        options.ClaimActions.MapJsonKey("email_verified", "email_verified");
        options.ClaimActions.MapJsonKey("picture", "picture");
    })
    // 4) GitHub OAuth — control 1.4: mapeamos avatar_url y pedimos scope user:email.
    .AddGitHub(AuthSchemes.GitHub, options =>
    {
        options.ClientId = builder.Configuration["Authentication:GitHub:ClientId"] ?? "";
        options.ClientSecret = builder.Configuration["Authentication:GitHub:ClientSecret"] ?? "";
        options.SignInScheme = AuthSchemes.External;
        options.CallbackPath = "/auth/github/callback";
        options.UsePkce = true;
        options.SaveTokens = false;
        options.Scope.Add("user:email");
        options.ClaimActions.MapJsonKey("avatar_url", "avatar_url");
    });

builder.Services.AddAuthorization();

// Rate limiting en capas (Módulo 5.1) + políticas dedicadas de los Módulos 1, 2, 3 y 4.
// Implementación completa en Services/RateLimitPolicies.cs.
builder.Services.AddSecurityRateLimiters();

// ─────────────────────────────────────────────────────────────────────────────
//  Módulo 5.3 — El backend vive detrás de Nginx (reverse proxy), nunca expuesto
//  directo a internet. Sin esto, TODOS los rate limiters por IP (auth/landing/global)
//  verían siempre la IP interna de Nginx en vez de la IP real del cliente.
//  Solo se confía en el header X-Forwarded-For si viene de la red interna de Docker
//  donde vive Nginx (control de spoofing: no se confía en cualquier origen).
// ─────────────────────────────────────────────────────────────────────────────
var redProxyConfiable = builder.Configuration["ReverseProxy:TrustedNetwork"] ?? "172.28.0.0/16";
var partesRed = redProxyConfiable.Split('/');
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
        IPAddress.Parse(partesRed[0]), int.Parse(partesRed[1])));
});

// ─────────────────────────────────────────────────────────────────────────────
//  CORS (anticipo del control 5.5): necesario para que el SPA envíe cookies HttpOnly.
//  Restrictivo: solo el origen exacto del frontend, con AllowCredentials.
// ─────────────────────────────────────────────────────────────────────────────
var frontendOrigin = builder.Configuration["Frontend:BaseUrl"] ?? "https://aba.andrescortes.dev";
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
        policy.WithOrigins(frontendOrigin)
              .AllowCredentials()                    // imprescindible para cookies HttpOnly
              .WithMethods("GET", "POST", "PUT", "DELETE")
              .WithHeaders("Content-Type", "X-CSRF-TOKEN"));
});

// ─────────────────────────────────────────────────────────────────────────────
//  Inyección de dependencias (patrón Repositorio + DIP).
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<ICookieJwtService, CookieJwtService>();
builder.Services.AddSingleton<ILoginAttemptTracker, LoginAttemptTracker>();
builder.Services.AddSingleton<ICacheService, CacheService>();
builder.Services.AddScoped<IUsuarioRepository, SqlServerUsuarioRepository>();
builder.Services.AddScoped<IIpWhitelistRepository, SqlServerIpWhitelistRepository>();
builder.Services.AddScoped<IProvisioningRepository, SqlServerProvisioningRepository>();
builder.Services.AddScoped<IDashboardRepository, SqlServerDashboardRepository>();
builder.Services.AddScoped<ILandingRepository, SqlServerLandingRepository>();

// Aprovisionamiento real contra el motor elegido (MySQL externo — fuera del presupuesto
// de RAM del Módulo 6 — o SQLServer en la MISMA instancia que ABA_Control, protegida por
// el logon trigger de sql/004 y el Resource Governor de sql/006).
builder.Services.AddScoped<IMySqlProvisioningService, MySqlProvisioningService>();
builder.Services.AddScoped<ISqlServerProvisioningService, SqlServerProvisioningService>();
builder.Services.AddScoped<IProvisioningOrchestrator, ProvisioningOrchestrator>();
builder.Services.AddScoped<IMySqlWhitelistSyncService, MySqlWhitelistSyncService>();
builder.Services.AddHostedService<MySqlQuotaEnforcementService>();

// GeoIP (control geográfico América/Latam, sql/003 + sql/004/005) — HttpClient con
// timeout corto: nunca debe demorar el login si el proveedor externo está lento/caído.
builder.Services.AddHttpClient<IGeoIpService, GeoIpService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});

// Captcha (control 1.3, segunda línea de defensa) — mismo patrón que GeoIpService:
// timeout corto, nunca debe demorar el login si Cloudflare está lento/caído.
builder.Services.AddHttpClient<ICaptchaService, TurnstileCaptchaService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();

// Swagger deshabilitado por completo en producción (control 5.5).
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts(); // Módulo 5.6 — HSTS solo tiene sentido fuera de Development
}

// ─────────────────────────────────────────────────────────────────────────────
//  Orden del pipeline (Módulo 5):
//   1) ExceptionHandling envuelve TODO — ninguna excepción no controlada filtra
//      detalles internos (control 0).
//   2) ForwardedHeaders ANTES que cualquier lectura de RemoteIpAddress, para que los
//      rate limiters por IP vean la IP real del cliente detrás de Nginx (5.3).
//   3) SecurityHeaders en cada respuesta (5.6).
//   4) RequestAudit envuelve auth/rate-limit/authorization/controllers para loguear
//      el resultado final (status code) y el usuarioId ya resuelto (5.8).
// ─────────────────────────────────────────────────────────────────────────────
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseForwardedHeaders();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseHttpsRedirection();
app.UseCors("frontend");
app.UseMiddleware<RequestAuditMiddleware>();
app.UseAuthentication();      // antes del rate limiter para poblar User en la política "provisioning"
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();

app.Run();
