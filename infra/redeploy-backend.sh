#!/usr/bin/env bash
# Redeploy manual del contenedor aba-backend (el que realmente sirve tráfico,
# NO el servicio Swarm — ver Aba/INFRAESTRUCTURA.md §7.5 para el porqué).
#
# El cd.yml del repo solo actualiza el servicio Swarm, que no está conectado
# a nada (sin ports:, en una red que ni NPM ni MySQL comparten). Hasta que se
# arregle esa desconexión de raíz, correr esto a mano en la VPS después de
# cada merge a main que deba llegar a producción.
#
# Uso: ENV_FILE=/opt/aba-cluster/.env ./redeploy-backend.sh
#
# Este archivo vive suelto en la VPS (/home/admin/redeploy-backend.sh), no como un
# git clone del repo — un "git push" a este repo NO actualiza esa copia solo. Después
# de cualquier cambio acá, sincronizarlo a mano antes de volver a correrlo:
#   scp infra/redeploy-backend.sh admin@178.105.217.45:/home/admin/redeploy-backend.sh
# Detalle completo: README.md § "Redeploy manual a producción".

set -euo pipefail

ENV_FILE="${ENV_FILE:-/opt/aba-cluster/.env}"
IMAGE="ghcr.io/aba-problem/aba-backend:latest"
CONTAINER="aba-backend"
NETWORK="aba-backend_default"
KEYS_DIR="${KEYS_DIR:-/opt/aba-cluster/keys}"

if [ ! -f "$ENV_FILE" ]; then
  echo "ERROR: no existe $ENV_FILE" >&2
  exit 1
fi

echo "==> Descargando imagen nueva..."
sudo docker pull "$IMAGE"

echo "==> Deteniendo y eliminando el contenedor anterior (si existe)..."
docker stop "$CONTAINER" 2>/dev/null || true
docker rm "$CONTAINER" 2>/dev/null || true

# Program.cs configura AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo("/keys"))
# para que el key ring (usado para cifrar el token antiforgery/CSRF) sobreviva un redeploy en
# vez de regenerarse en memoria cada vez. El proceso corre como usuario no-root (uid 1654) dentro
# del contenedor, así que la carpeta del host tiene que existir y ser de ese dueño ANTES de montarla
# — si no, el contenedor arranca pero /auth/csrf y /auth/refresh fallan con
# "UnauthorizedAccessException: Access to the path '/keys' is denied." (visto en producción el
# 2026-08-09, ver Aba/BITACORA-ALTA-RAFT-2026-08-09.md). docker-compose.yml (solo dev local) ya
# resuelve esto con un volumen + init container; acá se hace a mano porque este script es lo único
# que despliega el contenedor real.
echo "==> Preparando volumen de Data Protection Keys ($KEYS_DIR)..."
sudo mkdir -p "$KEYS_DIR"
sudo chown -R 1654:1654 "$KEYS_DIR"

echo "==> Levantando el contenedor nuevo..."
docker run -d \
  --name "$CONTAINER" \
  --restart always \
  --network "$NETWORK" \
  -p 127.0.0.1:5000:8080 \
  -m 500m \
  -v "$KEYS_DIR:/keys" \
  --env-file "$ENV_FILE" \
  -e DOTNET_COUNTER_MetricsEnabled=false \
  -e DOTNET_GCServer=false \
  -e DOTNET_GCHighMemPercent=60 \
  "$IMAGE"

echo "==> Esperando arranque..."
sleep 5

echo "==> Estado del contenedor:"
docker ps --filter "name=$CONTAINER"

echo "==> Últimos logs:"
docker logs "$CONTAINER" --tail 15

echo "==> Verificación contra producción real:"
STATUS=$(curl -s -o /dev/null -w "%{http_code}" https://api.aba.andrescortes.dev/stats)
echo "GET /stats -> $STATUS"
if [ "$STATUS" != "200" ]; then
  echo "ADVERTENCIA: la verificación no dio 200. Revisá los logs de arriba antes de dar esto por terminado." >&2
  exit 1
fi

echo "==> Listo."
