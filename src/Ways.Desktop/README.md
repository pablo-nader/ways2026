# Ways Desktop (Ways POS)

Aplicacion de escritorio para Windows (Tauri 2 + WebView2) que corre el punto
de venta como una app nativa: primer uso configura el servidor y la
impresora termica; despues siempre abre el POS (`pos.html`), una pagina LOCAL
bundleada con la app (no una pagina remota) que habla con la API por red,
con impresion RAW ESC/POS sin dialogos.

## Prerrequisitos

- Node.js (para el CLI de Tauri y para compilar `Ways.Web`).
- Rust estable con el target `x86_64-pc-windows-msvc` (`rustup default stable-x86_64-pc-windows-msvc`).
- Visual Studio Build Tools (MSVC) instalados.
- WebView2 Runtime (viene preinstalado en Windows 11 y en la mayoria de Windows 10 actualizados).
- Dependencias de `Ways.Web` instaladas (`npm install` ahi) -- `scripts/sync-frontend-local.mjs`
  compila ese proyecto antes de cada `dev`/`build`.

## Desarrollo

```bash
npm install
npm run dev
```

`npm run dev` corre `scripts/sync-frontend-local.mjs` (`beforeDevCommand` en
`tauri.conf.json`) antes de levantar la app: ese script compila `Ways.Web`
(`npm run build` ahi) y copia `pos.html` + sus assets a `ui/`, el
`frontendDist` de Tauri. `ui/index.html`, `ui/setup.js` y `ui/styles.css` son
la pagina de configuracion -- HTML/CSS/JS plano sin build step, escrito a
mano, y el script nunca los toca.

## Compilar

```bash
cargo test --manifest-path src-tauri/Cargo.toml   # tests unitarios (config, impresion, capacidades)
cargo build --manifest-path src-tauri/Cargo.toml  # build de desarrollo
npm run build                                     # instalador NSIS (release)
```

`npm run build` tambien corre `scripts/sync-frontend-local.mjs`
(`beforeBuildCommand`) antes de empaquetar. El instalador NSIS queda en
`src-tauri/target/release/bundle/nsis/`. Es una instalacion por usuario
(`installMode: currentUser`), sin privilegios de administrador.

## Dos ventanas, dos capacidades

La app usa DOS ventanas Tauri, cada una con su propia capacidad (ver
`src-tauri/capabilities/`) para que ninguna herede permisos que no necesita:

- **`main`** (pagina local `ui/index.html`, capacidad `configuracion.json`):
  configurar servidor/impresora e imprimir un ticket de prueba. Nunca
  `imprimir_raw`, `abrir_configuracion` ni las credenciales de dispositivo.
- **`pos`** (pagina local `ui/pos.html`, build de `Ways.Web`, capacidad
  `pos.json`): imprimir, volver a la configuracion y leer/escribir la
  credencial de dispositivo. Nunca `guardar_configuracion` ni
  `leer_configuracion`.

Al iniciar, si no existe `config.json` en el directorio de configuracion de
la app (`%APPDATA%/site.aipos.pos/config.json` en Windows), se muestra `main`.
Si ya existe, se muestra `pos` directamente. Guardar la configuracion, o usar
"Volver al POS", pasa de `main` a `pos` (creandola la primera vez); el boton
"Configuración" del POS o `abrir_configuracion` hacen el camino inverso. Si
una ventana ya destruida (por ejemplo, cerrada con el boton nativo "X" -- no
hay `prevent_close` en este crate) se vuelve a pedir, se la reconstruye en vez
de fallar.

La ventana `main` se recarga siempre que se vuelve a mostrar (su pagina solo
lee su estado en `DOMContentLoaded`, que no vuelve a disparar si ya existia).
La ventana `pos`, en cambio, solo se recarga cuando la URL del servidor
cambio desde la ultima vez que se mostro -- recargar sin necesidad tirar[ia]
el token bearer en memoria y el carrito en curso del cajero (ver
`entornoTauri.ts`). En ambos casos la recarga la dispara Rust con la API nativa
`WebviewWindow::reload()` (equivalente en efecto a que la propia pagina
llamara a `window.location.reload()`, pero sin inyectar ese ni ningun otro
script): la CSP (`script-src 'self'` sin `unsafe-eval`) podria bloquear un
script inyectado segun como WebView2 la aplique, asi que la API nativa evita
depender de eso.

La pagina del POS no tiene permiso para invocar `leer_configuracion`: la URL
del servidor le llega por el campo `url_servidor` de `info_app` (que si tiene
permitido), no por un comando nuevo.

## CSP y el alcance de `connect-src`

La CSP declarada en `tauri.conf.json` restringe `connect-src` a
`'self' https: http://localhost:* http://127.0.0.1:*` -- el esquema `https:`
sin un host especifico, no un origen exacto. Esto es deliberado, no un
descuido: la URL real del servidor la elige el usuario en tiempo de
ejecucion (pagina de configuracion, `config::normalizar_url_servidor`), y esa
URL recien se conoce despues de que Tauri ya arranco.

Investigado contra la fuente de `tauri` 2.11.5 (version pineada en
`Cargo.lock`): el crate SI expone un hook capaz de reescribir headers de
respuesta por request, `WebviewBuilder::on_web_resource_request` (ver el
ejemplo oficial embebido en `tauri::webview::WebviewBuilder`), que podria
angostar `connect-src` al origen exacto ya configurado. Pero la ventana
`main` se crea, en el primer arranque de la app (antes de que exista
`config.json`), a partir del array `windows` estatico de `tauri.conf.json` --
Tauri la construye internamente (`WebviewWindowBuilder::from_config`) ANTES
de que corra el closure `.setup()` de este crate, asi que no hay ningun punto
de este codigo desde el que colgarle ese hook a esa ventana en ese momento.
Aplicarlo de forma consistente exigiria sacar `main` del array estatico y
construirla siempre desde Rust (como ya se hace con `pos`) -- un cambio
estructural mas grande que el alcance de esta ronda, que no se puede
verificar en este entorno contra un WebView2 real. Por eso se deja `https:`
en vez de angostarlo a medias (que seria peor: dar una falsa sensacion de
"ya esta resuelto").

Que protege el `connect-src` actual: cualquier `fetch`/XHR a un esquema
distinto de `https`/`http(s)://localhost`/`127.0.0.1` (por ejemplo `ws:`,
`file:`, o un origen `http://` remoto). Que NO protege: un script bundleado
comprometido (supply-chain de una dependencia de `Ways.Web`) puede mandar el
token bearer en memoria o la credencial de dispositivo (ver
`entornoTauri.ts`, `credencial::leer`) a CUALQUIER host `https`, porque el
esquema no restringe el destino.

## Atajo de teclado

`Ctrl+Shift+F10` (atajo global, funciona aunque la ventana no tenga foco)
vuelve a mostrar la ventana de configuracion. El POS tambien puede volver ahi
desde un boton propio (`abrir_configuracion`).

## Impresion

`imprimir_raw` recibe bytes crudos (por ejemplo, una trama ESC/POS armada en
el frontend) y los manda al spooler de Windows con tipo de dato `RAW`, sin
mostrar ningun dialogo de impresion. Si no hay impresora configurada, se usa
la predeterminada de Windows.
