# ABA-backend

Backend .NET Web API (C#) "tonto pero no débil": sin lógica de negocio (vive en
Stored Procedures/Views de SQL Server), con seguridad perimetral en cada capa.
Especificación completa en [CLAUDE.md](CLAUDE.md).

## Estructura

```
Controllers/    Auth, Provisioning, Dashboard, Landing
Services/       CookieJwtService, RateLimitPolicies, CacheService, PasswordGenerator,
                LoginAttemptTracker, MySqlProvisioningService, ProvisioningOrchestrator,
                ProvisioningRetryService, MySqlQuotaEnforcementService
Repositories/   Interfaces + SqlServer (solo invocan Stored Procedures)
Contracts/      DTOs con validación estricta
Middleware/     SecurityHeaders, ExceptionHandling, RequestAudit
sql/            Scripts en orden de ejecución (01 → 08)
infra/          Hardening de SO/kernel para la VPS + backup de la clave de cifrado
hooks/          Git hooks versionados (pre-commit anti-secretos)
```

## Arquitectura: dos motores de base de datos

- **SQL Server (`MasterControl`)**: control plane — usuarios, auditoría, y el registro
  de cada base de datos aprovisionada. Vive en `docker-compose.yml`, dentro del
  presupuesto de RAM del Módulo 6.
- **MySQL**: motor destino de cada base de datos por usuario (control 2.3: "servicio
  intermediario controlado"). Es **externo** a este `docker-compose.yml` — el
  presupuesto de RAM del Módulo 6 (4GB) ya está totalmente asignado a SQL Server +
  backend + Nginx + SO; añadir un motor MySQL adicional en el mismo VPS lo rompería.
  El backend se conecta a él vía `MySql:AdminConnectionString` (`.env`).

## Puesta en marcha

1. Copia `.env.example` a `.env` y rellena los valores reales (nunca los commitees).
2. Ejecuta los scripts de `sql/` **en orden** contra la instancia de SQL Server:
   `01_schema.sql` → `02_sp_ObtenerOCrearUsuario.sql` → `03_sp_AprovisionarBaseDatos.sql`
   → `04_dashboard.sql` → `05_landing.sql` → `06_memory_budget.sql`
   → `07_provisioning_lifecycle.sql` → `08_encryption_key_management.sql`.
3. Crea en el MySQL externo la cuenta administrativa que usará el backend, con
   privilegios **mínimos** (nunca `SUPER` ni `GRANT ALL PRIVILEGES ON *.*`):
   ```sql
   CREATE USER 'aba_provisioner'@'%' IDENTIFIED BY '<password-fuerte>';
   GRANT CREATE, DROP, CREATE USER ON *.* TO 'aba_provisioner'@'%';
   GRANT GRANT OPTION ON *.* TO 'aba_provisioner'@'%';
   ```
   (No hace falta `GRANT SELECT ON information_schema.*` — en MySQL 8 ese esquema es virtual y el acceso
   ya es implícito para cualquier cuenta; intentar otorgarlo explícitamente falla con `ERROR 1044`.
   Confirmado al reconstruir el MySQL de producción el 2026-08-06.)
4. Levanta todo con:
   ```
   docker compose up --build
   ```
   Nginx queda como único punto de entrada (`localhost:80`); `backend` y `sqlserver`
   no publican puertos al host (Módulo 5.3). El backend **falla el arranque** si
   `Security:EncryptionPassphrase` no coincide con el hash ya registrado (ver § Módulo 2).

## Endpoints

Inventario completo de los 11 controllers en `Controllers/` — no solo los 4 módulos originales.
"Auth" indica el esquema real de autenticación (`[Authorize]` = cookie JWT del usuario;
`ApiKey`/`PartnerApiKey` = header, para consumo server-to-server, nunca navegador).

### Auth (`AuthController`, `/auth`)

| Método | Ruta | Auth |
|---|---|---|
| GET | `/auth/google/login`, `/auth/github/login` | público |
| GET | `/auth/{google\|github}/procesar` | público (callback OAuth) |
| POST | `/auth/refresh` | cookie JWT |
| POST | `/auth/logout` | `[Authorize]` |
| GET | `/auth/csrf` | público |

### Provisioning — estudiantes (`ProvisioningController`, `/provisioning`)

| Método | Ruta | Auth | Rate limit |
|---|---|---|---|
| POST | `/provisioning/crear` | `[Authorize]` | `provisioning` (1 base / 10 min por usuario) |

### Dashboard (`DashboardController`, `/dashboard`)

| Método | Ruta | Auth |
|---|---|---|
| GET | `/dashboard/bases` | `[Authorize]` |
| GET | `/dashboard/bases/{id}` | `[Authorize]` |
| GET | `/dashboard/bases/{id}/credencial` | `[Authorize]` |
| DELETE | `/dashboard/bases/{id}` | `[Authorize]` |
| GET | `/dashboard/perfil` | `[Authorize]` |

### Landing (`LandingController`)

| Método | Ruta | Auth |
|---|---|---|
| GET | `/stats` | público (rate limit `landing`) |

### Módulo 8 — Aprovisionamiento para células socias (`PartnersProvisioningController`, `/partners/databases`)

Server-to-server; ver [`Aba/API-CELULAS-SOCIAS.md`](../Aba/API-CELULAS-SOCIAS.md) para el contrato completo pensado
para equipos externos.

| Método | Ruta | Auth | Rate limit |
|---|---|---|---|
| POST | `/partners/databases` | `AuthSchemes.PartnerApiKey` (`Authorization: Bearer <key>`) | `partners` (ráfaga 5, recarga 1/2min por célula) |
| GET | `/partners/databases` | `AuthSchemes.PartnerApiKey` | `partners` |
| GET | `/partners/databases/{id}` | `AuthSchemes.PartnerApiKey` | `partners` |
| DELETE | `/partners/databases/{id}` | `AuthSchemes.PartnerApiKey` | `partners` |
| POST | `/partners/databases/{id}/credenciales/reset` | `AuthSchemes.PartnerApiKey` | `partners` |

### API Keys — gestión propia (`ApiKeysController`, `/apikeys`)

Gestión vía cookie JWT (la app web del usuario administrando sus keys). El *uso* de la key es un
esquema separado, ver `AiController` abajo.

| Método | Ruta | Auth | Rate limit |
|---|---|---|---|
| POST | `/apikeys/crear` | `[Authorize]` | `apikeys-crear` |
| GET | `/apikeys` | `[Authorize]` | — |
| POST | `/apikeys/{id}/revocar` | `[Authorize]` | — |
| GET | `/apikeys/{id}/consumo` | `[Authorize]` | — |

### IA como Servicio (`AiController`, `/ai`)

| Método | Ruta | Auth |
|---|---|---|
| POST | `/ai/completar` | `AuthSchemes.ApiKey` (header `X-API-Key`) |

Andamiaje de auth/rate-limit/auditoría completo; la integración con un proveedor real de IA
queda pendiente (swap trivial una vez decidido el proveedor).

### DNS autoservicio (`DnsController`, `/dns`)

| Método | Ruta | Auth |
|---|---|---|
| POST | `/dns/crear` | `[Authorize]` |
| GET | `/dns/mis-registros` | `[Authorize]` |
| DELETE | `/dns/{id}` | `[Authorize]` |

### DNS administración (`AdminDnsController`, `/admin/dns`)

| Método | Ruta | Auth |
|---|---|---|
| GET | `/admin/dns` | `[Authorize(Roles = "Admin")]` |
| DELETE | `/admin/dns/{id}` | `[Authorize(Roles = "Admin")]` |

### N8N — autoservicio de workspace (`N8nController`, `/n8n`)

| Método | Ruta | Auth |
|---|---|---|
| POST | `/n8n/crear` | `[Authorize]` |
| GET | `/n8n/mi-workspace` | `[Authorize]` |
| DELETE | `/n8n/mi-workspace` | `[Authorize]` |

### Sesiones — historial de acceso (`SesionesController`, `/sesiones`)

| Método | Ruta | Auth |
|---|---|---|
| GET | `/sesiones` | `[Authorize]` |

## Módulo 2 — Aprovisionamiento real y gestión de la clave de cifrado

### Ciclo de vida de una base aprovisionada
`Estado` en `dbo.BasesDatos` refleja la realidad, no una suposición:

| Estado | Significado |
|---|---|
| `pendiente` | Reservado en `MasterControl`, aún no confirmado en MySQL |
| `activa` | Existe y funciona en MySQL |
| `error_aprovisionamiento` | MySQL falló; `ProvisioningRetryService` reintenta cada 5 min con la misma contraseña ya generada |
| `cuota_excedida` | Superó `MaxSizeMB`; `MySqlQuotaEnforcementService` (cada 10 min) le revocó escritura en MySQL hasta que el uso vuelva a bajar |

Ninguna base queda "activa" en `MasterControl` sin existir realmente en MySQL — si el
paso de MySQL falla, `ProvisioningOrchestrator` revierte el registro en la misma operación.

### Backup de `Security:EncryptionPassphrase`
Sin esta clave, **todas** las contraseñas cifradas en `PasswordCifrada` son irrecuperables
para siempre (cifrado simétrico: sin la clave exacta, `DECRYPTBYPASSPHRASE` devuelve `NULL`,
no un error). Ejecuta el backup en una ubicación **distinta** de donde vive el backup de
`MasterControl` — nunca el mismo disco/bucket:

```
ENV_FILE=.env ./infra/backup-encryption-key.sh /ruta/fuera-del-servidor-de-bd
```

### Rotación de la clave
`sp_RotarClaveCifrado` (en [08_encryption_key_management.sql](sql/08_encryption_key_management.sql))
descifra con la clave vieja y re-cifra con la nueva en una sola transacción — si la clave
vieja es incorrecta para una sola fila, se aborta TODA la rotación (no queda nada a medio
rotar). **No está expuesto como endpoint HTTP a propósito** — rotar esta clave es una
operación de altísimo privilegio; se invoca vía `sqlcmd`/SSMS con acceso restringido:

```
sqlcmd -S <servidor> -d MasterControl -Q "EXEC sp_RotarClaveCifrado @ClaveVieja='...', @ClaveNueva='...'"
```

Después de rotar, actualiza `ENCRYPTION_PASSPHRASE` en `.env` con la clave nueva y reinicia
el backend — el chequeo de arranque comparará contra el hash que el propio SP ya actualizó.

### Validación de arranque
`Program.cs` llama a `sp_VerificarOInicializarClaveCifrado` antes de aceptar tráfico. Si
la clave configurada no coincide con el hash esperado, el proceso lanza una excepción y
**no arranca** — preferible a operar silenciosamente con descifrados corruptos.

## Módulo 5 — Verificación de despliegue (no solo "el archivo existe")

`infra/sysctl.conf` no se aplica solo. Tras copiarlo a la VPS:

```
sudo cp infra/sysctl.conf /etc/sysctl.d/99-abaproblem.conf
sudo sysctl -p /etc/sysctl.d/99-abaproblem.conf
```

Y **confirma** que cada valor quedó activo (no asumas que `sysctl -p` sin errores basta):

```
sysctl net.ipv4.tcp_syncookies net.ipv4.tcp_max_syn_backlog net.core.somaxconn net.ipv4.tcp_fin_timeout
```

Debe imprimir exactamente los valores de `infra/sysctl.conf`, no los defaults del kernel.

## Redeploy manual a producción (`infra/redeploy-backend.sh`)

El CD (`cd.yml`) solo actualiza el servicio Swarm, que no sirve tráfico real —
`aba-backend` (el contenedor manual al que apunta Nginx Proxy Manager) hay que
redesplegarlo a mano después de cada merge a `main` que deba llegar a producción
(ver `Aba/INFRAESTRUCTURA.md` § 7.5 para el porqué de esta desconexión).

**El script vive suelto en la VPS** (`/home/admin/redeploy-backend.sh`), no como un
`git clone` del repo — así que un `git push` a este repo **no lo actualiza solo**.
Cada vez que se modifique `infra/redeploy-backend.sh`, hay que sincronizarlo a mano
antes de volver a correrlo:

```bash
# Desde tu máquina, con el repo actualizado:
scp infra/redeploy-backend.sh admin@178.105.217.45:/home/admin/redeploy-backend.sh
```

Después, conectado por SSH a la VPS:

```bash
chmod +x /home/admin/redeploy-backend.sh   # solo hace falta si scp no preservó el permiso
ENV_FILE=/opt/aba-cluster/.env /home/admin/redeploy-backend.sh
```

El script hace `docker pull` de la imagen más reciente de GHCR, para/elimina el
contenedor anterior, lo vuelve a levantar (incluido el volumen de
`/opt/aba-cluster/keys:/keys` que usa `AddDataProtection().PersistKeysToFileSystem`
para el cifrado de tokens CSRF — ver `Program.cs`), y termina verificando
`GET /stats` contra `https://api.aba.andrescortes.dev` para confirmar que el
contenedor nuevo realmente responde antes de darlo por terminado.

## Módulo 7 — Control de repositorio

### 7.1 — Protección de la rama `main`
Configurar en GitHub → *Settings → Branches → Add branch ruleset* (o *Branch protection rule* en el modo clásico) sobre `main`:

- **Require a pull request before merging** — mínimo 1 aprobación.
- **Require status checks to pass before merging** — selecciona los jobs `secret-scan` y `build` de [.github/workflows/ci.yml](.github/workflows/ci.yml).
- **Do not allow force pushes** (deshabilitar force-push).
- **Do not allow deletions** de la rama.
- Opcional: **Require review from Code Owners** para que [.github/CODEOWNERS](.github/CODEOWNERS) sea vinculante (control 7.4: nadie cambia parámetros de recursos de la VPS sin revisión).

### 7.2 — Pre-commit hook contra secretos
El repo trae el hook versionado en [hooks/pre-commit](hooks/pre-commit) (usa `gitleaks` si está instalado; si no, cae a un escaneo por patrones). Actívalo una vez por clon:

```
git config core.hooksPath hooks
```

En Linux/macOS/Git Bash asegúrate de que sea ejecutable: `chmod +x hooks/pre-commit`.

### 7.3 — `.gitignore`
Ya incluye `bin/`, `obj/`, `.idea/`, `.vs/`, `*.user`, `.env`, `appsettings.Development.json` y `appsettings.Production.json`.

### 7.4 — Cambios a parámetros de recursos de la VPS
`Program.cs`, `docker-compose.yml`, `nginx.conf`, `infra/` y `sql/06_memory_budget.sql` están cubiertos por [.github/CODEOWNERS](.github/CODEOWNERS): cualquier cambio pasa por PR con revisión, nunca por push directo a `main`.
