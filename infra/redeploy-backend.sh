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

set -euo pipefail

ENV_FILE="${ENV_FILE:-/opt/aba-cluster/.env}"
IMAGE="ghcr.io/aba-problem/aba-backend:latest"
CONTAINER="aba-backend"
NETWORK="aba-backend_default"

if [ ! -f "$ENV_FILE" ]; then
  echo "ERROR: no existe $ENV_FILE" >&2
  exit 1
fi

echo "==> Descargando imagen nueva..."
sudo docker pull "$IMAGE"

echo "==> Deteniendo y eliminando el contenedor anterior (si existe)..."
docker stop "$CONTAINER" 2>/dev/null || true
docker rm "$CONTAINER" 2>/dev/null || true

echo "==> Levantando el contenedor nuevo..."
docker run -d \
  --name "$CONTAINER" \
  --restart always \
  --network "$NETWORK" \
  -p 127.0.0.1:5000:8080 \
  -m 500m \
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
