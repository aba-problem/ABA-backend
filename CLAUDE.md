## 0. CONTEXTO Y RESTRICCIONES NO NEGOCIABLES
 
Actúas como arquitecto de software senior especializado en seguridad de APIs (OWASP API Security Top 10) y sistemas de bajo consumo de recursos. Vas a generar un backend en **.NET Web API (C#)** que cumple estrictamente:
 
- **RAM total disponible: 4GB**, compartida entre SQL Server, el backend, y el proxy reverso. El backend debe operar cómodo con un presupuesto máximo de **300-400MB en reposo**, sin fugas de memoria ni pools de conexión sobredimensionados.
- **El backend no contiene lógica de negocio.** Toda regla de validación, cálculo, aprovisionamiento y auditoría vive en Stored Procedures, Views y Functions de SQL Server. El backend solo: recibe petición → valida forma/autenticación/límites → invoca SP → retorna respuesta estructurada.
- **"Tonto pero no débil":** la ausencia de lógica de negocio NO significa ausencia de seguridad perimetral. El backend es la primera línea de defensa contra tráfico malicioso antes de que llegue a SQL Server.
- No expongas nunca en el código, logs, respuestas de error, ni Swagger de producción: connection strings, claves JWT, contraseñas generadas, ni estructura interna de la base de datos.
- No generes código con `AddOpenApi()` nativo Y Swashbuckle simultáneamente (conflicto de ensamblados ya conocido). Usa únicamente Swashbuckle, solo activo en `Development`.
  
 
## MÓDULO 1 — Autenticación OAuth2 (Google + GitHub)
 
### Requerimientos funcionales
- Registro e inicio de sesión vía Google y GitHub.
- No duplicar usuarios: si el email/proveedor ya existe, reutiliza el registro (`sp_ObtenerOCrearUsuario`, lógica de "upsert" dentro del SP, nunca en el backend).
- Guardar: nombre, correo, avatar, proveedor, fecha de creación, último login.
### Requerimientos de seguridad específicos para este módulo
 
**1.1 — Tokens en cookies HttpOnly, no en localStorage/sessionStorage**
- El JWT emitido tras el login exitoso debe entregarse **exclusivamente** como cookie con las flags:
  - `HttpOnly` (JavaScript del frontend no puede leerla → mitiga robo de token vía XSS/keylogging de JS malicioso).
  - `Secure` (solo se transmite sobre HTTPS).
  - `SameSite=Strict` (mitiga CSRF y ataques de sitios cruzados; usa `Lax` únicamente si el flujo OAuth de redirección lo requiere).
  - `Path=/` con expiración corta (30-60 min) + mecanismo de refresh token igualmente HttpOnly, con rotación en cada uso.
- El backend nunca debe devolver el JWT en el body de la respuesta JSON. Angular nunca debe manipular el token directamente — el navegador lo adjunta automáticamente en cada petición.
**1.2 — Protección CSRF (obligatoria al usar cookies)**
- Implementa el patrón **Double Submit Cookie** o `Antiforgery` nativo de ASP.NET Core: un token anti-falsificación adicional, no-HttpOnly, que Angular debe leer y reenviar en un header custom (`X-CSRF-TOKEN`) en cada petición mutante (POST/PUT/DELETE). El backend valida que el valor del header coincida con el de la cookie de sesión.
**1.3 — Fuerza bruta / fuzzing sobre el flujo de login**
- Rate limiter dedicado y agresivo en los endpoints `/auth/google/callback` y `/auth/github/callback`: máximo 5 intentos por IP cada 5 minutos (Fixed Window), con backoff exponencial en `Retry-After`.
- Bloqueo temporal (no permanente) de IP tras 15 intentos fallidos en 1 hora, gestionado en memoria (no en SQL Server, para no gastar ciclos del motor en esto) usando `IMemoryCache` o `IDistributedCache` liviano.
- Nunca reveles en el mensaje de error si el fallo fue "usuario no existe" vs "token OAuth inválido" — mensaje genérico único: `"No se pudo completar la autenticación"`.
- Valida el `state` parameter de OAuth2 contra CSRF en el flujo de redirección (obligatorio, no opcional).
**1.4 — Validación estricta del payload de retorno de OAuth**
- Nunca confíes ciegamente en los claims que regresan Google/GitHub. Valida: `email_verified = true` (Google), formato de email, longitud máxima de `nombre`/`avatar_url` antes de pasarlos al SP (previene payloads anómalos o intentos de fuzzing vía claims manipulados).
### Entregable de este módulo
- `AuthController` con endpoints `/auth/google/login`, `/auth/google/callback`, `/auth/github/login`, `/auth/github/callback`, `/auth/logout`, `/auth/refresh`.
- `IUsuarioRepository` (interfaz) + `SqlServerUsuarioRepository` (implementación, solo invoca SPs).
- `CookieJwtService` que emite/valida tokens exclusivamente vía cookies.
---
 
## MÓDULO 2 — Aprovisionamiento Automático de Base de Datos
 
### Requerimientos funcionales
- Al primer login, crear automáticamente: base de datos MySQL/motor asignado, usuario asociado, contraseña segura generada, permisos acotados solo a esa base.
- Registrar toda la operación de aprovisionamiento (auditoría).
### Requerimientos de seguridad específicos
 
**2.1 — El backend NUNCA decide nombres ni tamaños; solo dispara el SP**
- El nombre de la base de datos y del usuario se generan **dentro del SP** (ej. `usr_` + hash corto del UUID del usuario), nunca a partir de un string que el cliente envíe. Esto cierra la puerta a inyección de DDL y a colisión/enumeración de nombres.
- La contraseña generada se produce con `RandomNumberGenerator` criptográficamente seguro (mínimo 20 caracteres, alfanumérico + símbolos), generada del lado del backend o dentro del SP con `CRYPT_GEN_RANDOM`, nunca con `Guid.NewGuid()` (no es criptográficamente seguro) ni con librerías de "random" débiles.
- La contraseña se muestra **una sola vez** al usuario tras el aprovisionamiento; el backend no debe loguearla ni guardarla en texto plano en ninguna tabla — solo un hash de verificación si se necesita, o cifrado reversible con clave gestionada fuera del repo si el usuario necesita volver a consultarla.
**2.2 — Límite de recursos por aprovisionamiento (protección contra agotamiento de VPS)**
- Rate limit específico y estricto en el endpoint de aprovisionamiento: **máximo 1 base de datos nueva cada 10 minutos por usuario autenticado**, usando `TokenBucketLimiter` (permite ráfaga inicial de 1, recarga lenta).
- Límite absoluto de bases de datos activas por usuario (ej. 3), validado dentro del SP antes de crear una nueva — el backend no valida esto, solo recibe el error estructurado del SP si se excede.
- `MAXSIZE` obligatorio en la creación de cada base (20MB según tu documento base), fijado en el SP, no negociable desde el backend ni el cliente.
- Ningún parámetro que el cliente envíe puede alterar límites de recursos: prohíbe explícitamente que el DTO de entrada acepte campos como `maxSizeMB`, `maxConnections`, `region`, `plan`, etc. Si el body incluye campos no esperados, recházalo (usa `[JsonExtensionData]` para detectar y descartar propiedades desconocidas, o configura el serializador en modo estricto).
**2.3 — Validación de la operación de creación de MySQL de forma segura**
- Cuando el SP interactúa con el motor MySQL (vía `linked server`, `xp_cmdshell` está PROHIBIDO por seguridad, o vía un servicio intermediario controlado), nunca construyas el comando `CREATE DATABASE`/`CREATE USER` concatenando el input crudo. Sanitiza el nombre contra regex `^[a-zA-Z0-9_]{1,30}$` antes de cualquier ejecución dinámica.
### Entregable de este módulo
- `ProvisioningController` con endpoint `/provisioning/crear`, protegido con `[Authorize]` + rate limit `token` dedicado.
- El `usuarioId` se extrae siempre de la cookie/claim JWT, nunca del body (mitiga BOLA — Broken Object Level Authorization).
---
 
## MÓDULO 3 — Dashboard y Consulta de Credenciales
 
### Requerimientos funcionales
- Ver info de conexión, estado, espacio usado/máximo, fecha de creación, última actividad.
### Requerimientos de seguridad específicos
 
**3.1 — BOLA (Broken Object Level Authorization) — control crítico**
- Todo endpoint de este módulo filtra exclusivamente por el `usuarioId` del JWT/cookie. Ningún endpoint acepta `usuarioId` o `databaseId` como parámetro libre sin validar que pertenezca al usuario autenticado — la validación de pertenencia ocurre dentro del SP (`WHERE UsuarioId = @UsuarioIdDelToken`), no confiada al frontend.
- Si se expone un identificador de base de datos en la URL (ej. `/dashboard/db/{id}`), el SP debe verificar propiedad y retornar 404 (no 403, para no confirmar existencia del recurso a un atacante) si no coincide.
**3.2 — Exposición mínima de credenciales**
- El endpoint que retorna la contraseña de conexión debe:
  - Requerir reautenticación reciente o un segundo factor simple (ej. confirmar con un código temporal) si se va a re-exponer la contraseña después del primer aprovisionamiento — evalúa si tu alcance de Entrega #2 lo requiere; como mínimo, audita cada vez que se consulta.
  - Estar bajo un rate limit propio y estricto (ej. 5 consultas por hora) para prevenir scraping automatizado de credenciales.
### Entregable de este módulo
- `DashboardController` con endpoints de solo lectura, todos con `[Authorize]` + filtro por identidad del token.
---
 
## MÓDULO 4 — Landing Page / Métricas Públicas
 
### Requerimientos funcionales
- Estadísticas públicas: usuarios totales, bases creadas/activas, logins totales, usuarios activos, disponibilidad.
### Requerimientos de seguridad específicos
 
**4.1 — Este es el único conjunto de endpoints sin autenticación — tratarlo como superficie de ataque de alto riesgo**
- Rate limit agresivo por IP (Sliding Window, ej. 20 peticiones/minuto) — es el endpoint más fácil de atacar por DoS al no requerir cuenta.
- Las métricas deben venir de una **View agregada y cacheada** (ej. cache de 60 segundos en memoria del backend) para no golpear a SQL Server con cálculos agregados en cada request público. Esto también protege tu presupuesto de RAM/CPU del motor.
- Nunca expongas aquí datos identificables de usuarios individuales (emails, nombres) — solo agregados numéricos.
### Entregable de este módulo
- `LandingController` con endpoint público `/stats`, cacheado, rate-limitado, sin autenticación pero con throttling estricto.
---
 
## MÓDULO 5 — Middleware Transversal de Seguridad (Cross-Cutting)
 
Este módulo aplica a **toda** la API, se implementa una sola vez en `Program.cs` / middleware pipeline.
 
### 5.1 — Rate Limiting en capas (multi-estrategia)
Implementa las 4 estrategias de `System.Threading.RateLimiting` combinadas:
- **GlobalLimiter por IP** (Fixed Window): red de seguridad general, ej. 60 req/min.
- **Sliding Window** en endpoints de lectura (dashboard, landing): más preciso contra picos en el borde de ventana.
- **Token Bucket** en endpoints de escritura/creación (provisioning): permite ráfaga controlada, recarga lenta.
- **Concurrency Limiter** global: máximo de conexiones simultáneas procesándose a la vez (ej. 20), para no saturar el pool de threads/memoria del backend en un pico de tráfico — esta es tu defensa más directa contra saturación de recursos en la VPS de 4GB.
### 5.2 — Protección contra Slowloris y ataques a nivel de conexión (Kestrel)
Configura explícitamente en `Program.cs`:
```csharp
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxConcurrentConnections = 100;
    options.Limits.MaxConcurrentUpgradedConnections = 100;
    options.Limits.MaxRequestBodySize = 1_048_576; // 1MB
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10); // clave contra Slowloris
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    options.Limits.MinRequestBodyDataRate =
        new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(
            bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(5));
});
```
Slowloris explota conexiones que envían datos extremadamente lento para agotar el pool de threads/conexiones — `RequestHeadersTimeout` y `MinRequestBodyDataRate` son las defensas directas de Kestrel contra esto.
 
### 5.3 — Reverse proxy delante del backend (Nginx/Traefik) — no exponer Kestrel directo
- El backend NUNCA debe recibir tráfico de internet directamente. Un reverse proxy (Nginx) al frente:
  - Termina TLS.
  - Aplica un segundo nivel de rate limiting a nivel de conexión TCP (`limit_conn`, `limit_req` en Nginx) — defensa redundante contra Slowloris/flood antes de que el tráfico llegue siquiera al proceso .NET.
  - Oculta la topología interna: Angular y el navegador solo conocen la URL pública del proxy, nunca el puerto/host interno de Kestrel ni de SQL Server.
- Configura Nginx con `client_body_timeout`, `client_header_timeout` bajos (10-15s) como capa adicional anti-Slowloris a nivel de kernel/socket.
### 5.4 — Hardening a nivel de sistema operativo / kernel (VPS)
Incluye estas recomendaciones de `sysctl` para la VPS (fuera del código, en la config del servidor):
```
net.ipv4.tcp_syncookies = 1          # protección contra SYN flood
net.ipv4.tcp_max_syn_backlog = 2048
net.core.somaxconn = 1024
net.ipv4.tcp_fin_timeout = 15
```
Considera `fail2ban` apuntando a los logs de Nginx/backend para banear IPs con patrones de fuerza bruta/fuzzing automáticamente a nivel de firewall (`iptables`), fuera del proceso .NET — así el ataque ni siquiera consume RAM del backend.
 
### 5.5 — No exposición de rutas internas al frontend Angular
- Angular **nunca** debe tener hardcodeadas rutas de infraestructura interna (nombres de contenedores, puertos internos, IPs de la VPS). Todo el consumo se hace contra el dominio público del proxy (`api.tucelula.andrescortes.dev`), nunca contra `localhost:5000` ni IPs internas de Docker.
- Configura CORS de forma restrictiva — solo el origen exacto de tu frontend, nunca `AllowAnyOrigin()`:
```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
        policy.WithOrigins("https://tucelula.andrescortes.dev")
              .AllowCredentials() // necesario para cookies HttpOnly
              .WithMethods("GET", "POST", "PUT", "DELETE")
              .WithHeaders("Content-Type", "X-CSRF-TOKEN"));
});
```
- Swagger/OpenAPI **deshabilitado por completo en producción** — ni siquiera detrás de auth, simplemente no se registra el middleware si `!IsDevelopment()`.
### 5.6 — Headers de seguridad HTTP
```csharp
app.Use(async (context, next) =>
{
    context.Response.Headers.Remove("Server");
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Content-Security-Policy", "default-src 'self'");
    await next();
});
app.UseHsts();
```
 
### 5.7 — Validación estricta de entrada en TODOS los DTOs
- Usa `[Required]`, `[StringLength]`, `[EmailAddress]`, `[RegularExpression]` en cada DTO.
- Rechaza automáticamente propiedades JSON no declaradas en el DTO (previene mass assignment / fuzzing de campos extra):
```csharp
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.UnmappedMemberHandling =
        System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
});
```
 
### 5.8 — Logging y auditoría sin exposición de datos sensibles
- Loguea: IP, endpoint, usuarioId (si aplica), resultado (éxito/fallo), timestamp.
- **Nunca** loguees: JWT completo, contraseñas generadas, connection strings, headers de autorización crudos.
- Los eventos de auditoría de negocio (creación de base, cambios de permisos) se registran **dentro de SQL Server** vía el SP correspondiente (tabla `Auditoria`), no en archivos de log del backend — consistente con la Regla de Oro del proyecto.
---
 
## MÓDULO 6 — Presupuesto de Memoria (4GB RAM total)
 
Distribución objetivo que la IA debe respetar al configurar servicios:
 
| Componente | RAM objetivo |
|---|---|
| SQL Server (`max server memory`) | 2 - 2.5 GB |
| Backend .NET (Kestrel + pool de conexiones) | 300 - 400 MB |
| Nginx (reverse proxy) | 50 - 100 MB |
| Sistema operativo + Docker overhead | 500 MB - 1 GB |
| Margen de seguridad | resto |
 
Configura explícitamente:
- Pool de conexiones de Dapper/SqlClient acotado: `Max Pool Size=20;Min Pool Size=2` en la connection string — nunca dejes el pool sin límite explícito.
- `MemoryCache` del backend con `SizeLimit` definido, para que el cacheo de métricas del Módulo 4 no crezca sin control.
---
 
## MÓDULO 7 — Control de Repositorio Git (protección contra commits inseguros)
 
Incluye en la respuesta de la IA generadora:
 
**7.1 — Protección de rama principal**
- Instrucciones para configurar en GitHub/GitLab: `main`/`master` protegida, requiere Pull Request + al menos 1 revisión antes de merge, prohibido push directo (`force-push` deshabilitado).
**7.2 — Pre-commit hook contra secretos**
- Genera un hook (`.husky` o `pre-commit` nativo de Git) que ejecute un escaneo tipo `gitleaks` o `git-secrets` antes de cada commit, bloqueando el commit si detecta patrones de: API keys, connection strings con contraseña, JWT keys, `.env` accidentalmente agregado.
**7.3 — `.gitignore` completo (ya definido, inclúyelo siempre)**
```
bin/
obj/
.idea/
.vs/
*.user
.env
appsettings.Development.json
appsettings.Production.json
```
 
**7.4 — Nunca commitear parámetros que afecten recursos de la VPS**
- Ningún archivo versionado debe contener valores hardcodeados de `MaxConcurrentConnections`, `max server memory`, límites de contenedores Docker que difieran del `docker-compose.yml` base revisado por el equipo — cualquier cambio a estos valores debe pasar por PR con revisión, no por commit directo, ya que afectan directamente la estabilidad de la VPS compartida.
---
 
## INSTRUCCIÓN FINAL PARA LA IA GENERADORA
 
Genera el código organizado en la siguiente estructura de carpetas, respetando patrón Repositorio + DIP:
 
```
/src
  /Controllers      (Auth, Provisioning, Dashboard, Landing)
  /Services         (CookieJwtService, RateLimitPolicies, CacheService)
  /Repositories
    /Interfaces
    /SqlServer
  /Contracts        (DTOs con validación estricta)
  /Middleware       (SecurityHeaders, ExceptionHandling)
  Program.cs
docker-compose.yml
nginx.conf
.env.example        (sin valores reales, solo nombres de variables)
.gitignore
```
 
Para cada archivo generado, agrega comentarios breves explicando **qué control de seguridad de este documento implementa esa sección** (facilita la documentación de entregables que pide el caso de uso).
 
https://github.com/joserodriguez18/Aba