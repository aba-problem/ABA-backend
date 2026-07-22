#!/bin/sh
# ============================================================================
# Módulo 2 (auditoría de cierre) — Backup de Security:EncryptionPassphrase,
# SEPARADO físicamente del backup de MasterControl.
#
# Por qué separado: si el backup de la clave vive junto al backup de la base de
# datos (mismo disco, mismo bucket, mismo servidor), un solo punto de fallo
# (disco robado, bucket mal configurado, credencial de backup comprometida)
# compromete AMBOS a la vez: los datos cifrados Y la clave para descifrarlos.
# Sin la clave exacta, DECRYPTBYPASSPHRASE devuelve NULL — no hay "recuperar
# contraseña", es una pérdida permanente de TODAS las credenciales de BD.
#
# Uso:
#   ENV_FILE=.env ./infra/backup-encryption-key.sh /ruta/fuera-del-servidor-de-bd
#
# Requiere: gpg (backup cifrado con passphrase simétrica, pedida de forma interactiva).
# ============================================================================
set -eu

DESTINO="${1:?Uso: $0 <ruta-destino-DISTINTA-de-donde-vive-el-backup-de-MasterControl>}"
ENV_FILE="${ENV_FILE:-.env}"

if [ ! -f "$ENV_FILE" ]; then
    echo "ERROR: no se encontró $ENV_FILE" >&2
    exit 1
fi

PASSPHRASE=$(grep -E '^ENCRYPTION_PASSPHRASE=' "$ENV_FILE" | cut -d '=' -f2-)
if [ -z "$PASSPHRASE" ]; then
    echo "ERROR: ENCRYPTION_PASSPHRASE vacía en $ENV_FILE. Nada que respaldar." >&2
    exit 1
fi

command -v gpg >/dev/null 2>&1 || { echo "ERROR: gpg no está instalado." >&2; exit 1; }

mkdir -p "$DESTINO"
SELLO=$(date +%Y%m%d-%H%M%S)
ARCHIVO="$DESTINO/encryption-passphrase-$SELLO.gpg"

echo "$PASSPHRASE" | gpg --symmetric --cipher-algo AES256 --output "$ARCHIVO"
chmod 600 "$ARCHIVO"

echo "Backup cifrado escrito en: $ARCHIVO"
echo "Súbelo ahora a un vault/ubicación DISTINTA de donde vive el backup de MasterControl"
echo "(gestor de secretos del equipo, caja fuerte física, etc.) — no lo dejes solo en este disco."
