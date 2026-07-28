# Fase 0 — el sistema viejo, en Docker

Esto **no es** el sistema nuevo. Es el PHP original empaquetado para que el negocio siga
facturando mientras se hace la reescritura. Vive hasta el cutover y después se apaga.

Ver `docs/06-roadmap.md`. La regla de la Fase 0 es una sola: **reproducir, no mejorar.**

## Qué hay acá

| Ruta | Qué es |
|---|---|
| `app/` | Copia desplegable del sistema original. `alsina/` queda congelado como referencia histórica. |
| `Dockerfile` | `php:7.4-apache` + `mysqli`. Sirve `app/` en el puerto 80. |
| `config/php-ways.ini` | Zona horaria, sesiones y manejo de errores. |
| `config/apache-ways.conf` | VirtualHost, listado de directorios apagado, IP real detrás del proxy. |
| `config/mysql-ways.cnf` | **Lo más importante del directorio.** Reproduce el MySQL del hosting viejo. |
| `mysql.Dockerfile` | `mysql:5.7` con esa configuración copiada adentro y con permisos válidos. |
| `compose.yml` | Prueba local completa. No es lo que corre en EasyPanel. |
| `scripts/restaurar-dump.sh` | Restaura el dump y verifica que quedó bien. |

## Qué se cambió del original

Nada de lógica de negocio. Sólo lo que impedía que el sistema arranque fuera del hosting:

| Archivo | Cambio |
|---|---|
| `conexion.php` | Credenciales por variables de entorno en lugar de escritas en el código. |
| `filtrarArticulo.php`, `filtrarUsuario.php` | Tenían sus propias credenciales apuntando a `c1890978_ways`. Ahora usan `conexion.php`. |
| `combos.php`, `imprimirArticulos.php` | Apuntaban a `root@127.0.0.1` base `ways` (desarrollo). Ahora usan `conexion.php`. |
| `actualizar.php` | Tenía la IP y la clave `root2` de otro local escritas en el código. Ahora por entorno, y la pantalla queda deshabilitada si no se configura. |

Se sacaron del docroot `localhost.sql`, `sql/` y `cgi-bin/`: eran archivos publicados que no
tienen por qué ser accesibles desde la web.

El `diff` completo contra el original:

```bash
diff -r alsina legacy/app
```

## Las tres decisiones que no hay que tocar

**1. `init_connect = 'SET NAMES latin1'`.** El sistema nunca llama a `mysqli_set_charset()`:
trabajaba con la conexión en latin1 sobre tablas latin1, y por eso guarda bytes UTF-8 crudos
dentro de columnas latin1. En el dump se ve directo: los artículos nuevos (hasta el ID 8047)
están como `Aceite CaÃ±uelas`, y los que se ven "bien" cortan en el ID 2588 — o sea, la
conexión latin1 es el comportamiento actual. Si se fuerza utf8, se rompen los acentos de todo
el catálogo y, peor, cada venta nueva se guarda distinto que las viejas.

**2. `sql_mode = NO_ENGINE_SUBSTITUTION`.** El hosting viejo corría en modo laxo. Con el
`sql_mode` por defecto de MySQL 5.7/8.0 el legacy no arranca: hay `INSERT` que omiten columnas
`NOT NULL` sin default (`STRICT_TRANS_TABLES`) y estadísticas con `GROUP BY` parcial
(`ONLY_FULL_GROUP_BY`).

**3. `date.timezone = America/Argentina/Buenos_Aires`.** El legacy sólo llama a
`date_default_timezone_set()` en `index.php`. Los tickets, los cierres de caja y las ofertas
por hora se emiten desde otros archivos: sin esto el contenedor corre en UTC y las ventas
quedan tres horas adelantadas.

## Despliegue en EasyPanel

Dos servicios en el mismo proyecto.

### 1. Servicio MySQL

- Tipo **MySQL**, imagen **`mysql:5.7`** — el hosting viejo corría 5.7.44. `mysql:8.4` y
  `mysql:9` también funcionan, pero exigen un parche en el dump: ver la tabla de versiones
  más abajo.
- Base **`c1890978_alsina`** (ese nombre exacto), usuario `ways`, contraseñas nuevas.
  **Las viejas están en el historial de git: no se reusan.**

  El nombre no es negociable: el dump trae su propio `CREATE DATABASE c1890978_alsina` y su
  `USE`. Si el servicio se crea con otro nombre, el dump igual restaura en `c1890978_alsina`
  y el usuario `ways` queda sin permisos sobre esa base. Si ya pasó, se arregla así:

  ```sql
  GRANT ALL PRIVILEGES ON c1890978_alsina.* TO 'ways'@'%';
  FLUSH PRIVILEGES;
  ```
- **Mounts → File mount**: pegar el contenido de `config/mysql-ways.cnf` en
  `/etc/mysql/conf.d/ways.cnf`. Sin esto se corrompen los acentos.
- Volumen en `/var/lib/mysql`.
- Sin dominio ni puerto público: sólo se accede desde el otro servicio.

**Antes de restaurar nada, verificar que la configuración se aplicó.** MySQL ignora en
silencio cualquier archivo de configuración escribible por todos, y lo único que deja es una
línea en el log (`World-writable config file ... is ignored`). Si eso pasa, el servidor
arranca sin latin1 y en modo estricto, y te enterás cuando ya hay ventas corruptas:

```bash
docker exec -i $(docker ps -qf name=mysql) mysql -uroot -p'<clave-root>' -e "SELECT @@init_connect, @@sql_mode, @@character_set_server"
```

Tiene que devolver `SET NAMES latin1`, `NO_ENGINE_SUBSTITUTION` y `latin1`. Si no, corregir
los permisos del archivo montado (`chmod 0644`) y reiniciar el servicio.

Recién ahí, restaurar el dump **fresco** del hosting (no el del relevamiento):

```bash
{ echo "SET FOREIGN_KEY_CHECKS=0;"; cat localhost.sql; } | docker exec -i $(docker ps -qf name=mysql) mysql -uroot -p'<clave-root>' --default-character-set=utf8mb4
```

El `SET FOREIGN_KEY_CHECKS=0` **no es opcional**. La base de producción tiene 75 cajas con
`id_usuario` de usuarios que ya no existen (los IDs 12, 28, 107, 110 y 111). Sin desactivar
la verificación, la sección de constraints del final del dump corta con `ERROR 1452` y la
base queda con 5 de las 27 claves foráneas — y el import *parece* haber andado, porque los
datos están todos. Con la verificación apagada, las constraints se crean igual y quedan
exactamente como están hoy en producción.

Si importás por phpMyAdmin, es la casilla **"Habilitar verificación de claves foráneas"**,
que hay que **destildar**. Igual, para 97 MB phpMyAdmin no es el camino.

Verificar antes de seguir:

```bash
docker exec -i $(docker ps -qf name=mysql) mysql -uroot -p'<clave-root>' -e "SELECT nombre FROM c1890978_alsina.articulos WHERE ID=8047"
```

### 2. Servicio de la aplicación

- Tipo **App**, source: este repositorio, rama `main`.
- Build: **Dockerfile**. Build path `/legacy`, Dockerfile path `Dockerfile`
  (si tu versión de EasyPanel lo pide desde la raíz del repo: `legacy/Dockerfile`).
- Variables de entorno:

  ```
  DB_HOST=<host interno del servicio MySQL>
  DB_PORT=3306
  DB_NAME=c1890978_alsina
  DB_USER=ways
  DB_PASSWORD=<la clave nueva>
  ```

  El host interno lo muestra EasyPanel en el panel del servicio MySQL; tiene la forma
  `<proyecto>_<servicio>`.
- Volumen en `/var/lib/php/sessions`: sin esto, cada redeploy desloguea a todos los cajeros.
- Dominio apuntando al **puerto 80**, con HTTPS de Let's Encrypt.

### 3. Verificación manual — no se salta

La Fase 0 no está terminada hasta que un cajero haga esto de punta a punta:

1. Login y selección de local.
2. Buscar un artículo con acento o `ñ` (que se vea igual que en el sistema viejo).
3. Cargar por escaneo y por código corto.
4. Cerrar una venta con vuelto.
5. **Imprimir el ticket.** Si el ticket no sale idéntico, no está terminado.
6. Cerrar caja y comparar el total contra el sistema viejo.

### 4. Lo que falta después del despliegue

- Backup automático diario de la base a un bucket externo. **El sistema viejo no tenía.**
- Rotar las contraseñas de los usuarios operativos (están en el dump, con el hash del legacy).
- Dejar el hosting viejo en solo lectura hasta confirmar que el nuevo entorno opera bien.

## Prueba local

```bash
cp legacy/env.example legacy/.env
```

Editar las claves y levantar:

```bash
docker compose -f legacy/compose.yml --env-file legacy/.env up -d --build
```

La primera vez MySQL importa el dump de 97 MB desde `alsina/localhost.sql`: tarda varios
minutos y la app responde error de conexión hasta que termina.

```bash
docker compose -f legacy/compose.yml logs -f mysql
```

Después: <http://localhost:8080>. Para empezar de cero,
`docker compose -f legacy/compose.yml down -v`.

## Si algo falla

### `#6125 Failed to add the foreign key constraint. Missing unique key ... 'grupos'`

De las 10 tablas referenciadas por claves foráneas, `grupos` es **la única** cuyo `id` tiene
un índice no único (`ADD KEY id (id)`; las otras nueve tienen `PRIMARY KEY` o `UNIQUE KEY`).
Es una anomalía del esquema original. MySQL 5.7 y 8.0 la toleran; 8.4 y 9 no.

| Imagen | ¿Entra el dump tal cual? | Probado |
|---|---|---|
| `mysql:5.7` | Sí. Es lo que corría el hosting. | 27/27 claves foráneas |
| `mysql:8.0` | Sí. Sin soporte desde abril de 2026. | crea la clave foránea |
| `mysql:8.4` | No — `#6125`. Necesita el parche. | falla sin parche |
| `mysql:9` | No — `#6125`. Necesita el parche. | 27/27 **con** parche |

El parche es una línea: convierte ese índice en único, dejando `grupos` igual que sus nueve
hermanas. Verificado sobre los datos reales: 136 grupos, 136 ids distintos, ningún duplicado.
No hace falta editar el archivo de 97 MB, se aplica al vuelo durante el import:

```bash
{ echo "SET FOREIGN_KEY_CHECKS=0;"; sed '/^ALTER TABLE `grupos`$/{n;s/^  ADD KEY `id` (`id`);$/  ADD UNIQUE KEY `id` (`id`);/}' dump.sql; } | docker exec -i $(docker ps -qf name=mysql) mysql -uroot -p'<clave-root>' --default-character-set=utf8mb4
```

Para la prueba local con compose, la variable `PARCHE_GRUPOS=1` hace lo mismo:

```bash
MYSQL_IMAGE=mysql:9 PARCHE_GRUPOS=1 docker compose -f legacy/compose.yml up -d --build
```

Con MySQL 9.7.2 se verificó el camino completo: config aplicada, 27 claves foráneas, 345.665
ventas, PHP 7.4 conectando por `caching_sha2_password`, `0000-00-00` aceptado con el
`sql_mode` laxo, `float(10,2)` aceptado y los acentos intactos.

**Lo que el parche no cubre:** el legacy tiene cientos de consultas escritas a mano que nunca
corrieron sobre MySQL 9. Lo verificable de antemano ya se verificó — ninguna columna usa
palabras que MySQL 8 volvió reservadas, y sólo hay 4 `GROUP BY` en todo el código, 2 sin
`ORDER BY` explícito (MySQL 8 dejó de ordenar implícitamente al agrupar, así que esos dos
listados pueden salir en otro orden). Aun así, 5.7 sigue siendo el camino de menor riesgo
para la Fase 0: es el motor sobre el que este código corrió durante años.

**Ojo con el volumen.** MySQL no arranca sobre un directorio de datos creado por una versión
más nueva: si el servicio ya corrió con `mysql:9`, no alcanza con cambiar la imagen a 5.7.
Hay que borrar el volumen (o recrear el servicio) y volver a importar desde cero.

### `#1046 No database selected` en la primera tabla del dump

El `CREATE DATABASE` / `USE` del principio no corrió. Creá la base a mano **con el charset
correcto** y seleccionala antes de importar:

```sql
CREATE DATABASE IF NOT EXISTS `c1890978_alsina` DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;
```

### "Ocurrió un error" al cerrar un ticket, cargar un gasto o editar un usuario

El `sql_mode` del servicio MySQL quedó en el default estricto: **la configuración de
`ways.cnf` no se aplicó**. El legacy guarda fechas vacías como `0000-00-00` y hace `INSERT`
que omiten columnas `NOT NULL` sin default; con `NO_ZERO_DATE` y `STRICT_TRANS_TABLES` cada
una de esas operaciones falla.

Se comprueba en un segundo:

```bash
docker exec -i $(docker ps -qf name=mysql) mysql -uroot -p'<clave-root>' -e "SELECT @@sql_mode, @@init_connect"
```

Si no devuelve exactamente `NO_ENGINE_SUBSTITUTION` y `SET NAMES latin1`, el `.cnf` no está
llegando al servidor. Para saber en cuál de los dos casos estás:

```bash
docker exec -i $(docker ps -qf name=mysql) ls -l /etc/mysql/conf.d/
docker logs $(docker ps -qf name=mysql) 2>&1 | grep -i world-writable
```

- **El archivo no está** → falta el mount.
- **Está y aparece `World-writable ... is ignored`** → `chmod 0644` sobre el archivo en el host
  y reiniciar el servicio.

Para destrabar la caja **ahora mismo**, sin reiniciar nada:

```sql
SET GLOBAL sql_mode = 'NO_ENGINE_SUBSTITUTION';
SET GLOBAL init_connect = 'SET NAMES latin1';
```

Surte efecto en las conexiones nuevas, así que alcanza con recargar la página. **Pero se
pierde en el próximo reinicio del servicio**, y ahí la caja se rompe de vuelta sin que nadie
haya tocado nada. Es un parche para seguir facturando hoy, no la solución.

**La forma durable más simple: no usar archivo.** Si el servicio MySQL de EasyPanel tiene
campo de comando, la misma configuración se pasa como argumentos de `mysqld` y no hay nada
que montar ni que se pueda ignorar por permisos:

```
mysqld --sql-mode=NO_ENGINE_SUBSTITUTION --character-set-server=latin1 --collation-server=latin1_swedish_ci --init-connect="SET NAMES latin1" --max-allowed-packet=256M
```

Verificado sobre `mysql:5.7.44`: deja el servidor exactamente igual que con el `ways.cnf`.

**Si tampoco hay campo de comando, sacá el archivo del medio:** creá el servicio de base como
tipo **App** apuntando a este repositorio, con build path `/legacy` y Dockerfile
`mysql.Dockerfile`. Esa imagen ya trae el `.cnf` adentro con los permisos correctos, así que
no hay nada que montar ni que se pueda ignorar. Necesita un volumen en `/var/lib/mysql` y las
variables `MYSQL_ROOT_PASSWORD`, `MYSQL_DATABASE`, `MYSQL_USER` y `MYSQL_PASSWORD`.

## Riesgos conocidos

- **PHP 7.4 está fuera de soporte.** Es deliberado: reproduce el hosting original y es
  temporal. Para probar PHP 8.2:
  `docker build --build-arg PHP_IMAGE=php:8.2-apache -t ways-legacy legacy/`.
  Esperá roturas por comparaciones laxas y accesos a índices inexistentes.
- **El sistema no usa consultas preparadas.** Concatena `$_POST` y `$_GET` dentro del SQL en
  todo el código. Eso no se arregla en Fase 0 — se arregla reescribiéndolo. Mientras tanto,
  el sistema no debería quedar expuesto a internet abierta sin al menos restricción por IP
  o VPN.
- **`ventas` guarda los ítems serializados**, no como tabla. Cualquier reporte que dependa de
  eso sigue siendo tan lento como antes.
