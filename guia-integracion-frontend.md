# Guía de integración frontend — Entregable 3 (N8N, IA como Servicio, DNS Autoservicio)

Contrato de los endpoints nuevos para el equipo de frontend. Sigue las mismas reglas ya
establecidas para el resto de la API: cookies HttpOnly para sesión, header `X-CSRF-TOKEN`
en toda petición mutante (`POST`/`PUT`/`DELETE`), `credentials: 'include'` obligatorio en
cada fetch/axios.

| Endpoint | Método | Auth | Rate limit | Notas |
|---|---|---|---|---|
| `/n8n/crear` | POST | Cookie + CSRF | 1 cada 10 min por usuario | Devuelve `passwordTemporal` **una sola vez** — mostrar y no volver a pedir. |
| `/n8n/mi-workspace` | GET | Cookie | — | 404 si el usuario no tiene workspace activo. |
| `/n8n/mi-workspace` | DELETE | Cookie + CSRF | — | Soft delete. 404 si no había workspace activo. |
| `/apikeys/crear` | POST | Cookie + CSRF | 5/hora por usuario | Devuelve `keyCompleta` **una sola vez** — no se puede volver a consultar. |
| `/apikeys` | GET | Cookie | — | Solo `prefijo` (nunca la key completa). |
| `/apikeys/{id}/revocar` | POST | Cookie + CSRF | — | Idempotente. 404 si no existe o no es tuya. |
| `/apikeys/{id}/consumo` | GET | Cookie | — | Agregado por día, últimos 30 días. 404 si no es tuya. |
| `/ai/completar` | POST | Header `X-API-Key` (**NO** cookies) | 20/min por API key | Uso externo (scripts, CI), no pensado para el navegador del usuario. Solo andamiaje por ahora — sin proveedor de IA real integrado todavía. |
| `/dns/crear` | POST | Cookie + CSRF | 1 cada 5 min por usuario | Body: `{ subdominio, tipoRegistro: "A"\|"CNAME", valor }`. 409 si el subdominio ya está en uso. |
| `/dns/mis-registros` | GET | Cookie | — | — |
| `/dns/{id}` | DELETE | Cookie + CSRF | — | 404 si no existe o no es tuyo. |
| `/admin/dns` | GET | Cookie (rol `Admin`) | — | Lista de TODOS los registros, con `usuarioCorreo`. |
| `/admin/dns/{id}` | DELETE | Cookie + CSRF (rol `Admin`) | — | Elimina cualquier registro. |

## Notas de implementación para el frontend

- **`X-API-Key` nunca va en query string.** Si se necesita probar `/ai/completar` desde
  una herramienta tipo Postman/curl, usar siempre el header, nunca `?api_key=...`.
- **Secretos de un solo uso** (`passwordTemporal` de N8N, `keyCompleta` de API Keys):
  mostrarlos en un modal/toast con opción de copiar, y advertir explícitamente que no se
  podrán volver a ver. No los guardes en `localStorage`/`sessionStorage` ni los mandes a
  ningún analytics/logging del lado del cliente.
- **Rol Admin**: el frontend puede leer el rol decodificando el JWT solo para decisiones
  de *UI* (mostrar/ocultar el link a `/admin/dns`) — la autorización real siempre la
  aplica el backend; nunca confíes en el rol leído del lado del cliente para nada que
  afecte seguridad.
- **DNS**: `POST /dns/crear` puede devolver `503` si el proveedor real (Cloudflare) falla
  después de que ABA_Control ya validó la solicitud — es un error transitorio, reintentar
  tiene sentido (el subdominio no quedó reservado, `sp_ConfirmarRegistroDns` ya lo revirtió).
