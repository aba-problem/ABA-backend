# CLAUDE.md (Español)

Este archivo le da contexto a Claude Code (claude.ai/code) para trabajar con el código de este repositorio.

> Traducción de `CLAUDE.md` (el archivo principal, en inglés). Manténlo sincronizado cada vez que `CLAUDE.md` cambie.
>
> Este archivo antes era el prompt/spec original en español usado para generar este código desde cero. Se reescribió (tanto la versión en inglés como esta) para describir lo que realmente está implementado — para los pasos completos de puesta en marcha, la tabla de endpoints y el detalle de la clave de cifrado/ciclo de vida del aprovisionamiento, ver `README.md`. Para el lado SQL línea por línea, ver `../Aba/BASE-DE-DATOS-EXPLICADA.md`. La visión general de todo el workspace (VPS, frontend, despliegue) está en el `CLAUDE.md` de la raíz (`../CLAUDE.md` / `../CLAUDE.es.md`).

## La regla de oro

Este backend es "tonto pero no débil": tiene **cero lógica de negocio**. Toda validación, cálculo, asignación de permisos, control de cuotas y auditoría vive en SQL Server como Stored Procedures / Views / Functions bajo `sql/`. El código en C# solo: autentica, valida la *forma* de la petición, aplica rate limiting, invoca un SP, y mapea el resultado a una respuesta HTTP. Si un cambio se siente como "lógica de negocio", pertenece a un SP nuevo o modificado, no a un `Service` o `Controller`.

Consecuencia: nunca SQL concatenado por strings — todas las llamadas del repositorio son invocaciones parametrizadas a SPs.

## Comandos

```bash
dotnet restore abaproblem.csproj
dotnet build abaproblem.csproj -c Release      # lo que corre CI (.github/workflows/ci.yml)
dotnet run                                     # dev local; carga .env si existe, nunca sobreescribe una variable de entorno real
docker compose up --build                      # stack local completo: sqlserver + backend + nginx
```

No existe proyecto de tests en este repo. No hay linter más allá del compilador de C# / los warnings de nullable reference types.

Los git hooks (escaneo de secretos en pre-commit) no están activos por defecto en cada clon — corre `git config core.hooksPath hooks` una vez, y `chmod +x hooks/pre-commit` en Linux/macOS.

Los scripts SQL en `sql/` deben correrse **en orden numérico** contra SQL Server — cada uno depende del esquema/SPs de los anteriores: `001_init_control_db.sql` → `002_stored_procedures.sql` → `003_sp_ip_whitelist.sql` → `004_logon_trigger_sqlserver.sql` → `005_logon_trigger_mysql.sql` → `006_resource_governor_y_limites_conexion.sql` → `007_extensiones_backend.sql` → `008_perfil_usuario.sql` → `009_n8n_workspaces.sql` → `010_api_keys.sql` → `011_dns_registros.sql` → `012_sesiones.sql` → `013_celulas_socias.sql` → `014_paises_global.sql`.

## Arquitectura

Patrón Repositorio + Inversión de Dependencias: los controllers dependen solo de las interfaces en `Repositories/Interfaces/`; las implementaciones en `Repositories/SqlServer/*` no hacen nada más que invocar stored procedures y mapear resultados a los DTOs de `Contracts/*`. Sigue este patrón para cualquier necesidad nueva de persistencia — nunca llames a `SqlConnection`/Dapper directamente desde un controller o service.

```
Controllers/    Auth, Dashboard, Landing, Provisioning — uno por módulo funcional
Services/       CookieJwtService, RateLimitPolicies, CacheService, PasswordGenerator,
                LoginAttemptTracker, GeoIpService, TurnstileCaptchaService,
                MySqlProvisioningService, SqlServerProvisioningService,
                ProvisioningOrchestrator, ProvisioningRetryService,
                MySqlQuotaEnforcementService, MySqlWhitelistSyncService
Repositories/   Interfaces/ (contratos) + SqlServer/ (implementaciones que solo invocan SPs)
Contracts/      DTOs con validación estricta; las propiedades JSON no mapeadas se rechazan
                globalmente (Program.cs: UnmappedMemberHandling.Disallow — bloquea mass-assignment/fuzzing)
Middleware/     ExceptionHandling, SecurityHeaders, RequestAudit — aplicados globalmente, no por controller
sql/            Scripts numerados, correr en orden (ver Comandos)
infra/          Hardening de SO/kernel del VPS (sysctl.conf) + script de backup de la clave de cifrado
hooks/          Git hooks versionados (escaneo de secretos en pre-commit)
```

### Dos motores de base de datos

- **SQL Server (`ABA_Control` / "MasterControl")**: el control plane — usuarios, log de auditoría, y el registro de cada base de datos aprovisionada. Corre en el `docker-compose.yml` de este repo, dimensionado dentro del presupuesto de RAM del Módulo 6 (ver el `CLAUDE.md` de la raíz).
- **MySQL**: el motor destino de cada base de datos de estudiante. Es **externo** a este `docker-compose.yml` — el presupuesto de 4GB del VPS ya está totalmente asignado a SQL Server + backend + Nginx + SO, así que MySQL corre como un servicio aparte al que el backend se conecta vía `MySql:AdminConnectionString`. La cuenta admin con la que se conecta debe tener solo `CREATE, DROP, CREATE USER, GRANT OPTION` + `SELECT` en `information_schema` — nunca `SUPER` ni `GRANT ALL PRIVILEGES ON *.*`.

### Pipeline de peticiones (`Program.cs`)

El orden importa y está comentado en el propio archivo:

1. `ExceptionHandlingMiddleware` — envuelve todo; ninguna excepción no controlada filtra detalles internos.
2. `UseForwardedHeaders()` — debe correr antes que cualquier lectura de `RemoteIpAddress`, para que los rate limiters por IP vean la IP real del cliente detrás de Nginx. Solo confía en `X-Forwarded-For` si viene de la red Docker configurada en `ReverseProxy:TrustedNetwork` (por defecto `172.28.0.0/16`, la misma subnet del `docker-compose.yml`).
3. `SecurityHeadersMiddleware` — agrega headers de seguridad a cada respuesta.
4. CORS (política `frontend` — solo el origen exacto, `AllowCredentials` para las cookies HttpOnly).
5. `RequestAuditMiddleware` — envuelve auth/rate-limit/authorization/controllers para loguear el status code final y el `usuarioId` ya resuelto.
6. `UseAuthentication()` **antes** que `UseRateLimiter()` — la política de rate limit `provisioning` particiona por el ID del usuario autenticado, así que el usuario ya debe estar resuelto.
7. `UseRateLimiter()` → `UseAuthorization()` → `MapControllers()`.

### Autenticación (Módulo 1)

OAuth2 vía Google y GitHub (`AddGoogle`/`AddGitHub`, con PKCE). Un esquema de cookie `External` de corta duración lleva el `state`/correlación de OAuth; si tiene éxito, la app emite su propio JWT, entregado **solo** como cookie `HttpOnly`/`Secure`/`SameSite` — nunca en un body JSON, nunca legible por JS del frontend. `JwtBearer.OnMessageReceived` lee el token desde esa cookie (`CookieJwtService`) en vez de un header `Authorization`.

El CSRF usa Double Submit Cookie: una cookie separada no-`HttpOnly` (`XSRF-TOKEN`/`__CSRF`) que el frontend reenvía como header `X-CSRF-TOKEN` en peticiones mutantes; `IAntiforgery` valida que coincidan. El `Domain` de la cookie CSRF debe fijarse explícitamente al host del frontend en producción, o `document.cookie` en el SPA no puede leerla (ver el bloque de comentarios en `Program.cs` alrededor de `AddAntiforgery`).

Más allá de la spec original, esta implementación agrega: `LoginAttemptTracker` (en memoria, respalda la política de rate limit "auth" y el mensaje de error genérico), `GeoIpService` (alimenta los SPs de whitelist de IP en `sql/003`/`004`/`005` — logon triggers con restricción geográfica), y `TurnstileCaptchaService` (Cloudflare Turnstile, segunda línea de defensa tras fallos repetidos). Tanto `GeoIpService` como el `HttpClient` del captcha usan un timeout fijo de 5s — que un proveedor externo esté lento o caído nunca debe bloquear el login.

### Rate limiting (Módulo 5.1, centralizado en `Services/RateLimitPolicies.cs`)

| Política | Estrategia | Límite | Se aplica a |
|---|---|---|---|
| `global` (IP) + `globalConcurrency` | Fixed Window + Concurrency | 60 req/min, tope de peticiones concurrentes | Toda petición (limiter por defecto) |
| `sliding` | Sliding Window | 10 req/min | Lecturas del dashboard |
| `landing` | Sliding Window (IP) | 20 req/min | `GET /stats` (única superficie sin auth) |
| `auth` | Fixed Window (IP) | 5 / 5 min | Callbacks de OAuth |
| `provisioning` | Token Bucket (usuario) | ráfaga 1, recarga 1 / 10 min | `POST /provisioning/crear` |
| `credenciales` | Fixed Window (usuario) | 5 / hora | Endpoint que revela credenciales |

Kestrel mismo también se endurece directamente en `Program.cs` (`MaxConcurrentConnections`, `RequestHeadersTimeout`, `MinRequestBodyDataRate`) como última línea de defensa contra ataques tipo Slowloris, aunque el backend nunca debería recibir tráfico directo de internet (Nginx va delante — ver el `CLAUDE.md` de la raíz).

### Ciclo de vida del aprovisionamiento y clave de cifrado

Ver la sección "Arquitectura" del `CLAUDE.md` de la raíz y "Módulo 2" de `README.md` para la máquina de estados de `Estado` (`pendiente`/`activa`/`error_aprovisionamiento`/`cuota_excedida`), la garantía de rollback de `ProvisioningOrchestrator`, y por qué la rotación de la clave (`sp_RotarClaveCifrado`) deliberadamente no está expuesta como endpoint HTTP.

## Despliegue e higiene del repositorio

- CI (`.github/workflows/ci.yml`): escaneo de secretos con gitleaks + `dotnet build`, status checks requeridos para fusionar a `main`.
- CD (`.github/workflows/cd.yml`): se dispara solo después de que CI tenga éxito en `main`; construye/publica la imagen en GHCR, y luego despliega vía `docker stack deploy -c stack.yml` en el VPS (Docker Swarm) — `stack.yml` vive en el VPS, no en este repo, ya que se produce/consume enteramente a través del flujo de CI/CD.
- `.github/CODEOWNERS` exige revisión en todo lo que toque límites de recursos compartidos del VPS: `Program.cs`, `Services/RateLimitPolicies.cs`, `docker-compose.yml`, `Dockerfile`, `nginx.conf`, `infra/`, `sql/06_memory_budget.sql` — no esperes que un push directo a `main` en estos archivos tenga éxito.
- Nunca commitear `.env`, `appsettings.Development.json` ni `appsettings.Production.json` — ya están en el `.gitignore`; el hook de pre-commit respalda esto con un escaneo de patrones de secretos.
