# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> This file used to be the original Spanish-language spec/prompt used to generate this codebase from scratch. It's been rewritten to describe what's actually implemented — for full setup steps, the endpoint table, and the encryption-key/provisioning-lifecycle deep dive, see `README.md`. For the SQL side line-by-line, see `../Aba/BASE-DE-DATOS-EXPLICADA.md`. The repo-wide overview (VPS, frontend, deployment) lives in the workspace root `../CLAUDE.md`.
>
> Spanish translation: `CLAUDE.es.md` (keep it in sync whenever this file changes).

## The golden rule

This backend is "dumb but not weak": it has **zero business logic**. Validation, calculations, permission assignment, quota enforcement, and auditing all live in SQL Server as Stored Procedures / Views / Functions under `sql/`. C# code only: authenticates, validates request *shape*, rate-limits, invokes a SP, and maps the result to an HTTP response. If a change feels like "business logic," it belongs in a new/modified SP, not in a `Service` or `Controller`.

Consequence: no string-concatenated SQL anywhere — all repository calls are parameterized SP invocations.

## Commands

```bash
dotnet restore abaproblem.csproj
dotnet build abaproblem.csproj -c Release      # what CI (.github/workflows/ci.yml) runs
dotnet run                                     # local dev; loads .env if present, never overrides a real env var
docker compose up --build                      # full local stack: sqlserver + backend + nginx
```

No test project exists in this repo. There is no linter beyond the C# compiler/nullable-reference warnings.

Git hooks (pre-commit secret scan) aren't active by default per clone — run `git config core.hooksPath hooks` once, and `chmod +x hooks/pre-commit` on Linux/macOS.

SQL scripts in `sql/` must run **in numeric order** against SQL Server — each depends on schema/SPs from the ones before it: `001_init_control_db.sql` → `002_stored_procedures.sql` → `003_sp_ip_whitelist.sql` → `004_logon_trigger_sqlserver.sql` → `005_logon_trigger_mysql.sql` → `006_resource_governor_y_limites_conexion.sql` → `007_extensiones_backend.sql` → `008_perfil_usuario.sql` → `009_celulas_socias.sql` → `010_paises_global.sql`.

## Architecture

Repository pattern + Dependency Inversion: controllers depend only on interfaces in `Repositories/Interfaces/`; the `Repositories/SqlServer/*` implementations do nothing but invoke stored procedures and map results to `Contracts/*` DTOs. Follow this pattern for any new persistence need — never call `SqlConnection`/Dapper directly from a controller or service.

```
Controllers/    Auth, Dashboard, Landing, Provisioning — one per functional module
Services/       CookieJwtService, RateLimitPolicies, CacheService, PasswordGenerator,
                LoginAttemptTracker, GeoIpService, TurnstileCaptchaService,
                MySqlProvisioningService, SqlServerProvisioningService,
                ProvisioningOrchestrator, ProvisioningRetryService,
                MySqlQuotaEnforcementService, MySqlWhitelistSyncService
Repositories/   Interfaces/ (contracts) + SqlServer/ (SP-invoking implementations only)
Contracts/      DTOs with strict validation; unmapped JSON properties are rejected globally
                (Program.cs: UnmappedMemberHandling.Disallow — blocks mass-assignment/fuzzing)
Middleware/     ExceptionHandling, SecurityHeaders, RequestAudit — applied globally, not per-controller
sql/            Numbered scripts, run in order (see Commands)
infra/          VPS OS/kernel hardening (sysctl.conf) + encryption-key backup script
hooks/          Versioned git hooks (pre-commit secret scan)
```

### Two database engines

- **SQL Server (`ABA_Control` / "MasterControl")**: the control plane — users, audit log, and the registry of every provisioned database. Runs in this repo's `docker-compose.yml`, sized inside the Módulo 6 RAM budget (see root `CLAUDE.md`).
- **MySQL**: the target engine for each student's provisioned database. It's **external** to this `docker-compose.yml` — the VPS's 4GB budget is already fully allocated to SQL Server + backend + Nginx + OS, so MySQL runs as a separate service the backend reaches via `MySql:AdminConnectionString`. The admin account it connects with must carry only `CREATE, DROP, CREATE USER, GRANT OPTION` + `SELECT` on `information_schema` — never `SUPER` or `GRANT ALL PRIVILEGES ON *.*`.

### Request pipeline (`Program.cs`)

Order matters and is commented in the file itself:

1. `ExceptionHandlingMiddleware` — wraps everything; no unhandled exception ever leaks internal details.
2. `UseForwardedHeaders()` — must run before anything reads `RemoteIpAddress`, so per-IP rate limiters see the real client IP behind Nginx. Only trusts `X-Forwarded-For` from the Docker network configured in `ReverseProxy:TrustedNetwork` (default `172.28.0.0/16`, matching `docker-compose.yml`'s subnet).
3. `SecurityHeadersMiddleware` — sets security headers on every response.
4. CORS (`frontend` policy — exact origin only, `AllowCredentials` for the HttpOnly cookies).
5. `RequestAuditMiddleware` — wraps auth/rate-limit/authorization/controllers to log the final status code and resolved `usuarioId`.
6. `UseAuthentication()` **before** `UseRateLimiter()` — the `provisioning` rate-limit policy partitions by authenticated user ID, so the user must already be resolved.
7. `UseRateLimiter()` → `UseAuthorization()` → `MapControllers()`.

### Auth (Módulo 1)

OAuth2 via Google and GitHub (`AddGoogle`/`AddGitHub`, PKCE on). A short-lived `External` cookie scheme carries the OAuth `state`/correlation; on success the app issues its own JWT, delivered **only** as an `HttpOnly`/`Secure`/`SameSite` cookie — never in a JSON body, never readable by frontend JS. `JwtBearer.OnMessageReceived` reads the token from that cookie (`CookieJwtService`) instead of an `Authorization` header.

CSRF uses Double Submit Cookie: a separate non-`HttpOnly` `XSRF-TOKEN`/`__CSRF` cookie that the frontend echoes back as the `X-CSRF-TOKEN` header on mutating requests; `IAntiforgery` validates the match. The CSRF cookie's `Domain` must be set explicitly to the frontend's host in production, or `document.cookie` in the SPA can't read it (see the comment block in `Program.cs` around `AddAntiforgery`).

Beyond the original spec, this implementation adds: `LoginAttemptTracker` (in-memory, backs the "auth" rate-limit policy and generic-failure-message behavior), `GeoIpService` (feeds the IP allowlist SPs in `sql/003`/`004`/`005` — geo-restricted logon triggers), and `TurnstileCaptchaService` (Cloudflare Turnstile, second line of defense after repeated failures). Both `GeoIpService` and the captcha `HttpClient`s use a hard 5s timeout — an external provider being slow/down must never block login.

### Rate limiting (Módulo 5.1, centralized in `Services/RateLimitPolicies.cs`)

| Policy | Strategy | Limit | Applies to |
|---|---|---|---|
| `global` (IP) + `globalConcurrency` | Fixed Window + Concurrency | 60 req/min, capped concurrent requests | Every request (default limiter) |
| `sliding` | Sliding Window | 10 req/min | Dashboard reads |
| `landing` | Sliding Window (IP) | 20 req/min | `GET /stats` (only unauthenticated surface) |
| `auth` | Fixed Window (IP) | 5 / 5 min | OAuth callbacks |
| `provisioning` | Token Bucket (user) | burst 1, refill 1 / 10 min | `POST /provisioning/crear` |
| `credenciales` | Fixed Window (user) | 5 / hour | Credential-reveal endpoint |

Kestrel itself is also hardened directly in `Program.cs` (`MaxConcurrentConnections`, `RequestHeadersTimeout`, `MinRequestBodyDataRate`) as a last line of defense against Slowloris-style attacks, even though the backend should never receive direct internet traffic (Nginx sits in front — see root `CLAUDE.md`).

### Provisioning lifecycle & encryption key

See root `CLAUDE.md` "Architecture" section and `README.md` "Módulo 2" for the `Estado` state machine (`pendiente`/`activa`/`error_aprovisionamiento`/`cuota_excedida`), the `ProvisioningOrchestrator` rollback guarantee, and why key rotation (`sp_RotarClaveCifrado`) is intentionally not exposed as an HTTP endpoint.

## Deployment & repo hygiene

- CI (`.github/workflows/ci.yml`): gitleaks secret scan + `dotnet build`, required status checks for merging to `main`.
- CD (`.github/workflows/cd.yml`): triggers only after CI succeeds on `main`; builds/pushes the image to GHCR, then deploys via `docker stack deploy -c stack.yml` on the VPS (Docker Swarm) — `stack.yml` lives on the VPS, not in this repo, since it's produced/consumed entirely through the CI/CD path.
- `.github/CODEOWNERS` requires review on anything touching shared VPS resource limits: `Program.cs`, `Services/RateLimitPolicies.cs`, `docker-compose.yml`, `Dockerfile`, `nginx.conf`, `infra/`, `sql/06_memory_budget.sql` — don't expect a direct push to `main` on these to succeed.
- Never commit `.env`, `appsettings.Development.json`, or `appsettings.Production.json` — all already gitignored; the pre-commit hook backs this up with a secret-pattern scan.
