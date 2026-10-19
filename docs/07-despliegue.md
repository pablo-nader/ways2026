# 07 — Despliegue

La imagen funciona en **dos modos** y decide sola cuál usar:

| Modo | Cuándo | Qué hace |
|---|---|---|
| **Base externa** | Hay `ConnectionStrings__Ways` o `DATABASE_URL` | Se conecta ahí. No levanta PostgreSQL adentro |
| **Todo en uno** | No hay ninguna de las dos | Inicializa y levanta su propio PostgreSQL 17 en `/var/lib/postgresql/data` |

En los dos casos la API aplica las migraciones y siembra roles y cuenta root al arrancar.
Es idempotente: se puede reiniciar todas las veces que haga falta.

---

## EasyPanel (servicio de app + servicio de base)

### 1. Servicio de base

Creá un servicio **Postgres**. EasyPanel te da una cadena así:

```
postgres://usuario:clave@nombre-interno:5432/basededatos?sslmode=disable
```

Usá el **host interno** (`aipos_aipos-postgres`), no el público: el tráfico no sale de la red del panel.

### 2. Servicio de app

| Campo | Valor |
|---|---|
| Source | GitHub → `pablo-nader/ways2026`, rama `main` |
| Build method | **Dockerfile** |
| Dockerfile path | `docker/Dockerfile` |
| Build context | `/` (la raíz del repo, **no** `docker/`) |
| Port | `8080` |

Dos cosas que se equivocan siempre:

- **El build context.** El `Dockerfile` copia `src/` y `Ways.slnx` desde la raíz.
  Si le ponés `docker/` como contexto, el build falla en el primer `COPY`.
- **El puerto.** EasyPanel asume 3000 por defecto. Si el build sale bien, el contenedor
  loguea `Now listening on: http://[::]:8080` y aun así el dominio devuelve
  **502 "Service is not reachable"**, es esto: el proxy está golpeando el puerto equivocado.

No hace falta pasar `INCLUIR_POSTGRES`: viene apagado por defecto, que es lo que corresponde
cuando la base es un servicio aparte. Encenderlo suma ~270 MB a la imagen sin usarse.

### 3. Variables de entorno

```env
ConnectionStrings__Ways=postgres://usuario:clave@aipos_aipos-postgres:5432/aipos?sslmode=disable
ASPNETCORE_ENVIRONMENT=Production

Semilla__Root__Usuario=root
Semilla__Root__Mail=test@test.com
Semilla__Root__Password=root
```

Notas:

- El **doble guión bajo** (`__`) es la forma de anidar secciones de configuración en .NET.
  `Semilla__Root__Password` es la sección `Semilla:Root`, clave `Password`.
- La cadena se acepta tanto en formato URI (`postgres://…`) como en el nativo de Npgsql
  (`Host=…;Port=…`). El conversor está en
  [CadenaDeConexion.cs](../src/Ways.Infrastructure/Persistencia/CadenaDeConexion.cs).
- `ASPNETCORE_URLS=http://+:8080` ya viene fijado en el `Dockerfile`. No hace falta ponerlo.
- La semilla root **solo corre si no existe ninguna cuenta root**. Una vez creada,
  cambiar estas variables no la modifica: la contraseña se cambia desde el ABM.

### 4. Salud

EasyPanel puede usar `GET /api/salud`, que es público y devuelve:

```json
{ "estado": "ok", "baseDeDatos": "ok" }
```

Responde `503` si la base no contesta.

### 5. Después del primer deploy

1. Entrá a la URL del servicio → te manda a `/admin` y de ahí a `/admin/login`.
2. Ingresá con `root` / `root`.
3. **Cambiá la contraseña de root desde Usuarios** antes de cargar nada real.
4. Creá tu cuenta admin y usá esa para el día a día.

---

## Mapa de URLs

La API sirve el front compilado (`wwwroot`) con tres entradas y el POS de escritorio aparte:

| URL | Qué sirve |
|---|---|
| `/` | La landing del comercio (`index.html`). En un host de la plataforma (`Plataforma:Hosts`: `aipos.site`, `localhost`…) redirige (302) a `/admin` |
| `/admin` y `/admin/...` | El back office y el POS web (`admin.html`); el router de React usa `basename="/admin"` |
| `/online` y `/online/...` | La tienda online (`online.html`) |
| `/pos.html` | El POS de escritorio: archivo estático, sin cambios (Tauri lo trae empaquetado y llama a `/api`) |
| `/api/...` | La API; una `/api/...` inexistente da 404, nunca un HTML |
| cualquier otra ruta sin extensión | La landing (`index.html`) |

**Rutas viejas.** Para que los favoritos sigan andando, `GET` y `HEAD` a `/login`, `/pos`, `/descargar`
y a cada pantalla del back office que antes colgaba de la raíz (`/caja`, `/articulos`, `/compras`…)
redirigen (302, conservando el query) a la misma ruta bajo `/admin`: `/pos?idPresupuesto=5` →
`/admin/pos?idPresupuesto=5`. La lista vive en `RutasDeLaSpa.RutasHeredadasDelBackOffice`
([RutasDeLaSpa.cs](../src/Ways.Api/Spa/RutasDeLaSpa.cs)) y la fuente de verdad es
[App.tsx](../src/Ways.Web/src/App.tsx): una prueba falla si las dos se desvían. `/api`, `/assets`,
`/online`, `/admin` y los archivos nunca se redirigen, salvo los de entrada: un `GET` o `HEAD` directo a
`/admin.html`, `/online.html` o `/index.html` redirige (302, conservando el query) a `/admin`, `/online` o `/`,
porque abiertos por el nombre del archivo el router de React no reconoce la ruta y la página queda en blanco.
`/pos.html` sigue sirviéndose tal cual.

En desarrollo (`npm run dev`, `vite preview`) un plugin de `vite.config.ts` aplica el mismo reparto:
`/admin/*` sale de `admin.html`, `/online/*` de `online.html` y el resto de `index.html`.

---

## Detrás del proxy inverso

EasyPanel termina el TLS y le habla HTTP al contenedor. La app lee `X-Forwarded-Proto`
(`UseForwardedHeaders` en [Program.cs](../src/Ways.Api/Program.cs)) para saber que la
conexión original era HTTPS y marcar la cookie de sesión como `Secure`.

Sin eso, la cookie viajaría sin el flag `Secure` aunque el usuario esté sobre HTTPS.

Las redes y proxies conocidos se vacían a propósito, porque en Docker la IP del proxy
es dinámica. Es seguro mientras el contenedor **solo** sea alcanzable a través del proxy;
si algún día lo exponés directo a internet, hay que restringirlo.

---

## Sesiones y redeploys

Las claves de Data Protection —las que firman la cookie— se guardan en la tabla
`"DataProtectionKeys"` de la base, no en el disco del contenedor.

Sin esto, cada redeploy generaría claves nuevas y **todos los usuarios logueados se caerían**.
Verificado: recrear el contenedor entero mantiene la sesión abierta.

La sesión vence tras **1 hora de inactividad**. Cada request dentro de esa ventana la renueva.
Al cerrar el navegador la cookie sobrevive (es persistente).

---

## Conector MCP (experimental)

Permite agregar Ways como conector personalizado de Claude (claude.ai, escritorio o celular).
Claude se autoriza con OAuth contra la propia API y llama al servidor MCP en `/mcp`. Las
herramientas corren con la identidad y el tenant de la cuenta que autorizó. Son de solo lectura
`quien_soy`, que devuelve el nombre de usuario, el correo, el rol y el tenant de la cuenta que
autorizó la conexión, más la hora del servidor, las consultas de ventas y de compras y la ficha
de un artículo (`articulo_info`) de las secciones siguientes. Las dos de códigos de artículos escriben, y solo agregan.

### Consultas de ventas

| Herramienta | Qué responde |
|---|---|
| `ventas_resumen` | Total vendido, comprobantes, ticket promedio y notas de crédito del período, con desglose por medio de pago, por punto de venta y por día (la tabla por día solo aparece si el período abarca 31 días distintos o menos, contados entre todas las empresas) |
| `ventas_agrupadas` | Lo vendido agrupado por `punto_venta`, `medio_pago`, `vendedor`, `dia`, `area`, `grupo`, `categoria` o `marca`, con importe, cantidad y % del total; `limite` de 1 a 50 grupos (20 por defecto). `dia` va del más reciente al más antiguo, así que el límite descarta los días viejos |
| `ventas_por_articulo` | Los artículos más vendidos, con filtros `texto`, `area`, `grupo`, `categoria`, `marca`, `proveedor` y `articulo`, `orden` por importe o cantidad y `limite`; el % es sobre todo lo vendido en artículos, sin los filtros |

Preguntas que responde: "¿cuánto vendimos hoy?", "¿cuánto vendió cada vendedor esta semana?", "¿qué
marca vendió más este mes?", "¿qué parte de lo vendido el mes pasado fue de la categoría Bebidas?",
"¿cuánto se vendió de Coca-Cola ayer?".

- **Rol:** solo Supervisor y Admin, igual que los reportes de la web (`LecturaDeReportes`). Un
  Vendedor recibe un texto que lo explica, no un error.
- **"Vendido":** el criterio del reporte de ventas de la web: comprobantes de venta no anulados, sin
  los espejos fiscales, con las notas de crédito restadas, al importe final.
- **Período:** un nombre (`hoy`, `ayer`, `esta_semana`, `semana_pasada`, `este_mes`, `mes_pasado`) o
  `desde` y `hasta` (`yyyy-MM-dd`, ambos inclusivos, separados como máximo 366 días). El "hoy" y el corte de cada día se
  toman en la zona horaria de la empresa (parámetro `zona_horaria`), no en UTC; la semana va de lunes
  a domingo y `esta_semana` y `este_mes` terminan hoy.
- **Alcance:** `empresa` y `punto_venta` por id o por nombre. Sin ellos se suman todas las empresas
  del tenant, cada una con su zona, y se muestra el desglose por empresa.
- **Medio de pago:** lo cobrado neto del vuelto (`importe - vuelto`). La tabla por medio de pago de la
  web suma el importe entregado, sin descontar el vuelto, así que en efectivo puede diferir.
- **Por artículo:** `area`, `grupo`, `categoria`, `marca` y `ventas_por_articulo` salen del reporte de
  ventas por artículo de la web (`ServicioDeReportesDeArticulos.ObtenerVentasPorArticuloAsync`): suman por
  línea de comprobante, el importe es la suma del total de las líneas, la cantidad son unidades, las notas
  de crédito restan y las líneas sin artículo no cuentan. La clasificación (área, grupo, categoría con sus
  subcategorías, marca y proveedor habitual) es la ACTUAL del artículo: si hoy cambió de categoría, sus
  ventas pasadas van a la nueva. Un artículo sin esa clasificación, o cuya clasificación está dada de baja,
  o que ya no es visible, cae en "(sin área)", "(sin grupo)", "(sin categoría)" o "(sin marca)". En
  `ventas_por_articulo`, `area`, `grupo`, `categoria` y `articulo` se pasan al reporte; `texto`, `marca` y
  `proveedor` recortan sus filas, y el % es siempre sobre las ventas de todos los artículos del período, sin
  filtros. Un filtro por nombre que no coincide con nada, o que coincide con varios, devuelve un error que
  lo nombra y lista hasta 10 candidatos con su id.
  `articulo` se resuelve igual que en `articulo_info` (ver abajo). En `ventas_agrupadas` por área, grupo,
  categoría o marca se agrupa por el id de la clasificación, no por su nombre: dos categorías con el mismo
  nombre son dos filas, y solo en ese caso la etiqueta lleva `(id N)`.
- La salida es texto con tablas `a | b | c`; cada tabla se corta en 31 filas y avisa cuántas faltan.

### Ficha de artículo

| Herramienta | Qué responde |
|---|---|
| `articulo_info` | "¿Qué precio y stock tiene X?", "¿cuánto se vendió de X en el último mes?", "¿a qué precio lo compramos la última vez y a quién?". Argumentos: `articulo` (obligatorio), `empresa` y `compras` (1 a 20, 5 por defecto) |

- **Artículo:** por id, código interno, código de barras activo o nombre. Se prueban en ese orden y la primera
  coincidencia exacta (sin distinguir mayúsculas) gana, antes de cualquier tope de filas; solo si no hay ninguna se
  busca el texto contenido en el nombre o en el código. Más de un candidato en un paso devuelve un error con hasta
  10 (`id | código | nombre`); ninguno, un error que lo dice.
- **Rol:** la tool está abierta a todos los roles. Los precios, el stock y la clasificación los ven todos. Las
  ventas de los últimos 30 días requieren `LecturaDeReportes` (Supervisor y Admin) y el costo nominal y las
  últimas compras requieren `LecturaDeCostos` (Supervisor y Admin); sin esos permisos la sección se reemplaza por
  una línea que lo avisa y no aparece ningún costo ni proveedor de compra.
- **Precios:** el vigente hoy en cada lista activa, resuelto con `ServicioDePrecios.PreciosVigentesEnLoteAsync`
  (la misma regla que el POS y la grilla: no se muestra un precio vencido ni uno programado a futuro; las listas
  derivadas se calculan sobre su lista base con el porcentaje). Con `empresa` solo las listas compartidas y las
  propias de esa empresa.
- **Stock:** una fila por punto de venta con saldo registrado, con la cantidad (y el mínimo solo para Supervisor y
  Admin, como en la web). Sin `empresa`, un punto de venta dado de baja que conserva stock figura como
  `(punto de venta id N)`; con `empresa` no aparece. Un servicio no maneja stock. Con `empresa`, solo sus
  puntos de venta.
- **Ventas:** cantidad e importe de los últimos 30 días contando hoy (`hoy - 29` a `hoy`), con el "hoy" y la zona
  horaria de cada empresa, sobre la misma fila del reporte de ventas por artículo de la web.
- **Costos y compras:** el costo nominal del artículo y las últimas `compras` compras confirmadas (no cuentan
  borradores ni anuladas) con una línea de ese artículo, de la más reciente a la más antigua por momento de
  recepción. Cada fila trae la fecha del comprobante, el proveedor, el número del comprobante, la cantidad, el costo
  unitario cargado, el descuento (un importe de la línea, no un porcentaje) y el costo efectivo por unidad de esa
  línea, con el mismo cálculo que usa la confirmación para el costo nominal
  (`CalculadorDeCompra.CalcularCostoEfectivoDesdeItem`); no indica si esa línea actualizó el costo.

### Consultas de compras

| Herramienta | Qué devuelve |
|---|---|
| `contexto_de_compras` | Puntos de venta con su empresa, percepciones por defecto de cada empresa, tipos de comprobante de compra activos, alícuotas de IVA activas y el rol del usuario. |
| `buscar_proveedor` | Proveedores por CUIT (ignorando guiones, puntos y espacios) o por nombre, hasta 10, con sus datos fiscales. |
| `buscar_articulos` | Hasta 100 líneas de una factura en una llamada: por línea, el primer match entre código del proveedor, código de barras y nombre, con hasta 5 candidatos, sin costos. |

Las herramientas de compras sirven para resolver los ids de una factura de proveedor antes de
cargarla; la carga del borrador todavía no está disponible. Son solo para Admin, igual que las
escrituras de compras de la API (`GestionDeCatalogo`): con otro rol responden un error sin datos.

### Códigos de artículos

| Herramienta | Qué hace |
|---|---|
| `asociar_codigo_de_barras(id_articulo, codigo, confirmar_codigo_no_estandar)` | Asocia un código de barras a un artículo y devuelve sus códigos actuales. |
| `asociar_codigo_de_proveedor(id_articulo, id_proveedor, codigo)` | Asocia a un artículo el código con que un proveedor lo imprime en su factura. |

Las herramientas de códigos escriben con `ServicioDeArticulos`, el mismo camino que la web, y
exigen la política `GestionDeCatalogo` de los endpoints del ABM: con otro rol devuelven un error
sin escribir. Son idempotentes: repetir la misma asociación no agrega nada y responde que ya
estaba. Si el código pertenece a otro artículo, no escriben y el error nombra a ese artículo
(nombre, código interno e id, y si está dado de baja) para que decida el usuario. Un artículo o
proveedor que no existe, o que es de otro tenant, da el mismo error de "no existe". Además,
`asociar_codigo_de_barras` no escribe un código que no sea un GTIN estándar (8, 12, 13 o 14 dígitos
con dígito verificador GS1 correcto) mientras la llamada no traiga `confirmar_codigo_no_estandar`
en `true`: el error le indica a Claude que le pida al usuario confirmar los dígitos leídos. Esa
regla es solo de la herramienta: la web solo advierte y la API acepta códigos no estándar.

Para sumar una herramienta: una clase de `Ways.Api` con `[McpServerToolType]` (el conector registra
todas las del ensamblado) que devuelva `CallToolResult` a través de `ResultadoDeHerramienta.EjecutarAsync`
y corte con `ErrorDeHerramienta` cuando Claude tenga que ver el motivo. Así sale como resultado con
`isError` y ese mensaje; una excepción que escapa de la herramienta, incluso una `McpException`, le
llega a Claude con un mensaje genérico del SDK.

### Encenderlo

```env
Mcp__Habilitado=true
Mcp__UrlPublica=https://aipos.site
Mcp__MailsHabilitados=admin@empresa.com;otra@empresa.com
```

- `Mcp__UrlPublica`: la URL pública, sin ruta. Fuera de Development es obligatoria y tiene que
  ser `https`. Si falta o no sirve, la app arranca igual, loguea **un** error y el conector queda
  apagado.
- `Mcp__MailsHabilitados`: los únicos mails que pueden autorizar, separados por coma o punto y
  coma, sin distinguir mayúsculas. Vacío = nadie. Un usuario de plataforma (root) nunca puede
  autorizar, aunque esté en la lista.

Cada request al conector deja una línea `Conector MCP: …` en el log (método, ruta, estado,
versión de protocolo, métodos JSON-RPC y parámetros OAuth), sin tokens, códigos ni contraseñas.

Si Claude no logra conectarse, buscá esas líneas a la hora del intento: `OAuth=` muestra qué pidió
Claude, `Error=` el error que devolvió `/connect/authorize` o `/connect/token`, y `Motivo=` por qué
se rechazó (por ejemplo `mail fuera de Mcp:MailsHabilitados`, o en `/mcp` `sin token`,
`token inválido o vencido`, `sesión revocada`). La línea `ways.bearer was not authenticated` en
cada request a `/mcp` es esperable: es la sesión normal de Ways descartando el token del conector.

### Agregarlo en claude.ai

1. Agregá un conector personalizado con la URL `https://<dominio>/mcp`, sin barra final.
2. En la configuración de OAuth elegí usar tu propio cliente ("Use your own OAuth client"):
   client ID `claude-ways` (se cambia con `Mcp__IdDeCliente`), **sin** client secret.
3. Claude abre la página de autorización de Ways: ingresá el mail y la contraseña de un usuario
   habilitado y tocá **Aprobar**.

Para revisar el servidor sin credenciales:

```bash
node src/Ways.Api/scripts/probar-conector-mcp.mjs https://aipos.site --solo-publico
```

### Límites

- **Las conexiones sobreviven a los deploys.** El cliente, las autorizaciones y los tokens se
  guardan en la base (tablas `*_oauth`, ver [doc 10 §11](10-modelo-de-datos/11-conector-mcp.md)),
  y los tokens se protegen con las mismas claves de Data Protection que la cookie de sesión. Un
  deploy o un reinicio no obliga a volver a autorizar desde Claude.
- La app crea esas tablas al arrancar con el flag encendido (migraciones propias del conector,
  historial en `historial_migraciones_conector_mcp`). Con el flag apagado no las crea ni las toca.
- Cambiar `Mcp__IdDeCliente` borra el cliente anterior con todas sus conexiones en el siguiente
  arranque.
- Una vez por día se borran los tokens y las autorizaciones que ya no sirven y tienen más de 14
  días.
- **Una sola instancia.** El cliente se sincroniza en cada arranque, y dos réplicas arrancando a la
  vez podrían chocar al crearlo.
- El access token dura 60 minutos (`Mcp__MinutosDeAccessToken`) y se renueva con el refresh token.
- El refresh token vence a los 14 días sin usarse y cada renovación entrega uno nuevo, así que una
  conexión que Claude sigue usando no vence. No se puede cortar una conexión sola desde la app: las
  de un usuario se cortan desactivándolo en Ways (lo que también le corta el acceso a Ways), y
  apagar el flag deja todas sin efecto mientras siga apagado (al volver a encenderlo, las que no
  vencieron vuelven a servir). Reiniciar ya no corta ninguna.
- La página de autorización no limita los intentos y comparte el bloqueo de cuenta del login: quien
  conozca un mail de `Mcp__MailsHabilitados` puede bloquear esa cuenta probando contraseñas
  incorrectas, igual que desde `/admin/login`.
- A un mail fuera de la lista la página le responde con el mismo texto y el mismo estado que a una
  contraseña incorrecta, pero más rápido, porque lo rechaza sin consultar la base: midiendo tiempos
  se puede saber si un mail está en la lista.
- Con `Mcp__UrlPublica` el conector trata sus rutas como https aunque la request llegue por http, así
  que la exigencia de HTTPS de OpenIddict nunca rechaza nada y que la conexión sea de verdad HTTPS
  depende solo del proxy (ver [Detrás del proxy inverso](#detrás-del-proxy-inverso)).

### Apagarlo

Sacá `Mcp__Habilitado` (o ponelo en `false`) y redeployá: no queda ninguna ruta ni log del
conector, y las conexiones existentes dejan de funcionar.

---

## Local

### Todo en uno

```bash
docker compose up -d --build
```

→ `http://localhost:8080`. La base queda en el volumen `ways_ways-pgdata`.

### Desarrollo

```bash
docker compose -f compose.dev.yml up -d
```

```bash
dotnet run --project src/Ways.Api
```

```bash
npm --prefix src/Ways.Web run dev
```

- API en `http://localhost:5080`
- Front en `http://localhost:5173`, con proxy de `/api` al 5080.

### Migraciones

```bash
dotnet ef migrations add NombreDeLaMigracion --project src/Ways.Infrastructure --startup-project src/Ways.Infrastructure --output-dir Persistencia/Migraciones
```

No hace falta aplicarlas a mano: la API las corre al arrancar.

---

## App de escritorio (Ways POS)

Clientes descargan el instalador de Windows desde `https://aipos.site/admin/descargar` (página pública
`Descargar.tsx` en `src/Ways.Web`, sin login, servida por el mismo fallback de SPA que el resto de
la app). El botón apunta al asset publicado en el último GitHub Release del repo público
`pablo-nader/ways2026` (`VITE_URL_DESCARGA_POS` permite overridearlo).

### Publicar un release

1. Subí la versión en los tres lugares: `src/Ways.Desktop/src-tauri/tauri.conf.json`,
   `src/Ways.Desktop/src-tauri/Cargo.toml` y `src/Ways.Desktop/package.json`.
2. Mergeá a `main`.
3. Taggeá y empujá:

   ```bash
   git tag pos-vX.Y.Z
   git push origin pos-vX.Y.Z
   ```

El workflow `.github/workflows/pos-escritorio-release.yml` compila el instalador NSIS en
`windows-latest`, verifica que la versión del tag coincida con `tauri.conf.json` (falla si no
coincide) y publica el GitHub Release con dos assets: `WaysPOS-setup.exe` (nombre estable, es el
que usa `/admin/descargar`) y `WaysPOS-<version>-setup.exe`. También se puede disparar a mano
(`workflow_dispatch`) contra un tag ya existente.

Notas:

- El instalador **todavía no está firmado digitalmente**: Windows SmartScreen va a mostrar
  "Windows protegió su PC" en la primera ejecución. El paso a paso ("Más información" →
  "Ejecutar de todas formas") está documentado en `/admin/descargar`.
- Antes de que alguien use la app, hay que tener aplicada la migración de `DispositivosPos`
  (vinculación de equipos) contra la base de ese tenant.

---

## Checklist antes de considerarlo productivo

- [ ] Contraseña de root cambiada desde el ABM.
- [ ] Cuenta admin propia creada; root queda solo para emergencias.
- [ ] `POSTGRES_PASSWORD` de la base cambiada (la que se compartió en chat quedó comprometida).
- [ ] Backup automático de la base configurado en el panel.
- [ ] Dominio con HTTPS.
- [ ] `ASPNETCORE_ENVIRONMENT=Production` (así `/openapi` no queda expuesto).
