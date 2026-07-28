#!/bin/bash
# Importa el dump al inicializar el contenedor de MySQL (sólo la primera vez, cuando el
# volumen está vacío). Lo monta el compose en /docker-entrypoint-initdb.d/.
#
# No alcanza con dejar el .sql en initdb.d: hay que desactivar la verificación de claves
# foráneas antes de importar. La base de producción tiene filas huérfanas (cajas con
# id_usuario de usuarios borrados), así que la sección de constraints del final del dump
# falla con ERROR 1452 y el import se corta ahí, dejando la base sin la mayoría de sus
# foreign keys. Con las verificaciones apagadas, las constraints se crean igual y quedan
# exactamente como están en producción.

DUMP=/dump/localhost.sql

if [ ! -f "$DUMP" ]; then
    echo "[ways] No hay dump en $DUMP: se arranca con la base vacía."
    exit 0
fi

echo "[ways] Importando $DUMP (tarda varios minutos)"

# PARCHE_GRUPOS=1 convierte el índice de `grupos`.`id` en único. Sólo hace falta en MySQL 8.4
# y 9, que rechazan una clave foránea contra un índice no único (#6125). En 5.7 y 8.0 no se
# usa: el dump entra tal cual. Ver "Si algo falla" en el README.
filtrar_dump() {
    if [ -n "${PARCHE_GRUPOS:-}" ]; then
        echo "[ways] Aplicando el parche de clave única en grupos" >&2
        sed '/^ALTER TABLE `grupos`$/{n;s/^  ADD KEY `id` (`id`);$/  ADD UNIQUE KEY `id` (`id`);/}' "$DUMP"
    else
        cat "$DUMP"
    fi
}

{
    echo "SET FOREIGN_KEY_CHECKS=0;"
    filtrar_dump
} | mysql --protocol=socket -uroot -p"$MYSQL_ROOT_PASSWORD" --default-character-set=utf8mb4

echo "[ways] Import terminado"
