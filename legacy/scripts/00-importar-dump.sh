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

{
    echo "SET FOREIGN_KEY_CHECKS=0;"
    cat "$DUMP"
} | mysql --protocol=socket -uroot -p"$MYSQL_ROOT_PASSWORD" --default-character-set=utf8mb4

echo "[ways] Import terminado"
