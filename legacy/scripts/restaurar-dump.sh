#!/usr/bin/env bash
# Restaura el dump del hosting viejo en el MySQL del contenedor.
#
# El dump de phpMyAdmin trae su propio CREATE DATABASE, USE y SET NAMES utf8mb4, así que se
# restaura con el usuario root y sin indicar base: sobre root no corre el init_connect que
# fuerza latin1, y esa es exactamente la combinación con la que fue generado.
#
#   DB_HOST=localhost DB_PORT=3307 MYSQL_ROOT_PASSWORD=... ./restaurar-dump.sh ../../alsina/localhost.sql
#
# Si el MySQL no está publicado hacia afuera (lo normal en EasyPanel), es más simple entrar
# por el contenedor:
#
#   docker exec -i <contenedor-mysql> mysql -uroot -p'<clave>' --default-character-set=utf8mb4 < dump.sql
set -euo pipefail

DUMP="${1:-}"
if [ -z "$DUMP" ] || [ ! -f "$DUMP" ]; then
    echo "uso: $0 <archivo.sql>" >&2
    exit 1
fi

: "${MYSQL_ROOT_PASSWORD:?falta MYSQL_ROOT_PASSWORD}"
DB_HOST="${DB_HOST:-127.0.0.1}"
DB_PORT="${DB_PORT:-3306}"
DB_NAME="${DB_NAME:-c1890978_alsina}"

mysql_root() {
    mysql --host="$DB_HOST" --port="$DB_PORT" --user=root \
          --password="$MYSQL_ROOT_PASSWORD" --default-character-set=utf8mb4 "$@"
}

echo "[ways] Restaurando $DUMP en $DB_HOST:$DB_PORT (esto tarda varios minutos)"
mysql_root < "$DUMP"

echo "[ways] Verificando"
mysql_root --table "$DB_NAME" -e "
    SELECT 'articulos' AS tabla, COUNT(*) AS filas FROM articulos
    UNION ALL SELECT 'ventas',   COUNT(*) FROM ventas
    UNION ALL SELECT 'usuarios', COUNT(*) FROM usuarios;
    SELECT DEFAULT_CHARACTER_SET_NAME AS charset_base
      FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '$DB_NAME';"

echo "[ways] Listo. El charset de la base tiene que decir latin1."
