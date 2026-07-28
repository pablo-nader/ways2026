# Imagen de MySQL para el sistema legacy.
#
# Existe por un motivo puntual: MySQL **ignora en silencio** todo archivo de configuración
# que sea escribible por cualquier usuario, y un bind mount desde Windows o macOS llega con
# permisos 0777. El síntoma es una sola línea perdida en el log:
#
#   [Warning] World-writable config file '/etc/mysql/conf.d/ways.cnf' is ignored.
#
# ...y el servidor arranca sin latin1 y en modo estricto. Copiar el archivo dentro de la
# imagen con los permisos correctos elimina el problema en cualquier sistema operativo.
ARG MYSQL_IMAGE=mysql:5.7
FROM ${MYSQL_IMAGE}

COPY config/mysql-ways.cnf /etc/mysql/conf.d/ways.cnf
RUN chmod 0644 /etc/mysql/conf.d/ways.cnf && chown root:root /etc/mysql/conf.d/ways.cnf
