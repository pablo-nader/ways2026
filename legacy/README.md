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

- Tipo **MySQL**, imagen **`mysql:5.7`** (el hosting viejo corría 5.7.44; MySQL 8 también
  funciona, ver el comentario al final de `config/mysql-ways.cnf`).
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
docker exec -i $(docker ps -qf name=mysql) mysql -uroot -p'<clave-root>' --default-character-set=utf8mb4 < localhost.sql
```

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
