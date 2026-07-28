#!/bin/bash
# Arranque del contenedor. Funciona en dos modos:
#
#   1. Base externa  — si viene ConnectionStrings__Ways o DATABASE_URL, la API se conecta
#                      ahí y NO se levanta PostgreSQL local. Es el modo para EasyPanel,
#                      Railway y cualquier panel con un servicio de base aparte.
#
#   2. Todo en uno   — sin esas variables, el contenedor inicializa y levanta su propio
#                      PostgreSQL en /var/lib/postgresql/data. Requiere volumen montado.
#
# Las migraciones y la semilla las corre siempre la API al iniciar
# (ver InicializadorDeBaseDeDatos), en los dos modos.
set -euo pipefail

log() { echo "[ways] $*"; }

BASE_EXTERNA="${ConnectionStrings__Ways:-${DATABASE_URL:-}}"

if [ -n "$BASE_EXTERNA" ]; then
    log "Base externa configurada. No se levanta PostgreSQL dentro del contenedor."
    exec dotnet /app/Ways.Api.dll
fi

# Credenciales del PostgreSQL embebido. Van acá y no en un ENV del Dockerfile para no
# hornear una contraseña en los metadatos de la imagen.
POSTGRES_DB="${POSTGRES_DB:-ways}"
POSTGRES_USER="${POSTGRES_USER:-ways}"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-ways}"

if [ ! -x "$PGBIN/initdb" ]; then
    log "ERROR: no hay base externa configurada y esta imagen se construyó sin PostgreSQL."
    log "       Definí ConnectionStrings__Ways o DATABASE_URL apuntando a tu base,"
    log "       o reconstruí la imagen con --build-arg INCLUIR_POSTGRES=true."
    exit 1
fi

log "Sin base externa configurada: modo todo-en-uno."

mkdir -p "$PGDATA" /var/log/postgresql
chown -R postgres:postgres "$PGDATA" /var/log/postgresql
chmod 700 "$PGDATA"

if [ ! -s "$PGDATA/PG_VERSION" ]; then
    log "Inicializando el cluster de PostgreSQL en $PGDATA"
    su postgres -c "$PGBIN/initdb -D '$PGDATA' \
        --encoding=UTF8 --locale=C.UTF-8 \
        --auth-local=trust --auth-host=scram-sha-256"

    # Solo loopback: la base no sale del contenedor.
    echo "listen_addresses = 'localhost'" >> "$PGDATA/postgresql.conf"
else
    log "Reutilizando el cluster existente en $PGDATA"
fi

log "Arrancando PostgreSQL"
su postgres -c "$PGBIN/pg_ctl -D '$PGDATA' -l /var/log/postgresql/server.log -w -t 60 start"

existe_rol=$(su postgres -c "psql -tAc \"SELECT 1 FROM pg_roles WHERE rolname='${POSTGRES_USER}'\"")
if [ "$existe_rol" != "1" ]; then
    log "Creando el rol ${POSTGRES_USER}"
    su postgres -c "psql -v ON_ERROR_STOP=1 -c \
        \"CREATE ROLE ${POSTGRES_USER} LOGIN PASSWORD '${POSTGRES_PASSWORD}'\""
fi

existe_base=$(su postgres -c "psql -tAc \"SELECT 1 FROM pg_database WHERE datname='${POSTGRES_DB}'\"")
if [ "$existe_base" != "1" ]; then
    log "Creando la base ${POSTGRES_DB}"
    su postgres -c "createdb -O ${POSTGRES_USER} ${POSTGRES_DB}"
fi

# Un apagado ordenado le evita al próximo arranque tener que recuperar el WAL.
apagar() {
    log "Deteniendo PostgreSQL"
    su postgres -c "$PGBIN/pg_ctl -D '$PGDATA' -m fast -w stop" || true
    exit 0
}
trap apagar SIGTERM SIGINT

export ConnectionStrings__Ways="Host=localhost;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"

log "Arrancando la API"
dotnet /app/Ways.Api.dll &
wait $!
