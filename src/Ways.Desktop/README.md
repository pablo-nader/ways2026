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

La CSP declarada en `tauri.conf.json` (base para ambas ventanas) restringe
`connect-src` a `'self' https: http://localhost:* http://127.0.0.1:*` -- el
esquema `https:` sin un host especifico, no un origen exacto. Esto es
deliberado para la CSP ESTATICA: la URL real del servidor la elige el
usuario en tiempo de ejecucion (pagina de configuracion,
`config::normalizar_url_servidor`), y esa URL recien se conoce despues de
que Tauri ya arranco, mucho despues de que `tauri.conf.json` se congelo en
tiempo de compilacion.

Las DOS ventanas parten de esa misma CSP estatica, pero terminan con alcance
distinto porque solo una de las dos se puede angostar en runtime:

- **`pos`** (la que sostiene el token bearer en memoria, ver
  `entornoTauri.ts`, y puede leer la credencial de dispositivo, ver
  `credencial::leer`) SI se angosta: `mostrar_ventana_pos` le cuelga
  `WebviewWindowBuilder::on_web_resource_request` (confirmado contra
  `tauri-2.11.5/src/webview/webview_window.rs:235` y el ejemplo oficial
  embebido ahi) antes de `.build()`, porque esta ventana SIEMPRE se
  construye desde Rust (nunca desde el array estatico de `tauri.conf.json`).
  El hook reescribe el header `Content-Security-Policy` de la respuesta de
  `pos.html` (`csp_con_connect_src_angostado`) reemplazando
  `connect-src 'self' https: ...` por `connect-src 'self' <origen exacto>`,
  con `<origen exacto>` = la URL del servidor ya validada por
  `config::normalizar_url_servidor` (por ejemplo
  `https://empresa.aipos.site`, o `http://localhost:5173` en desarrollo).
  Confirmado contra `tauri-2.11.5/src/protocol/tauri.rs::get_response`: para
  las respuestas `tauri://` de un asset HTML, Tauri pone el header
  `Content-Security-Policy` ANTES de invocar `on_web_resource_request`, asi
  que el hook llega a tiempo para sobreescribirlo. La ventana se reconstruye
  (y el hook se vuelve a colgar con el origen nuevo) cada vez que la URL
  configurada cambia -- ver `necesita_recargar_pos`.
- **`main`** (pagina de configuracion) se queda con la CSP estatica
  `https:` sin angostar, a proposito: no sostiene el token bearer ni puede
  leer la credencial de dispositivo (ver "Dos ventanas, dos capacidades" mas
  arriba), asi que angostar su `connect-src` no protege nada que no este ya
  protegido por el split de capacidades. Ademas, a diferencia de `pos`,
  `main` se construye en el primer arranque (antes de que exista
  `config.json`) desde el array `windows` estatico de `tauri.conf.json` --
  Tauri la crea internamente (`WebviewWindowBuilder::from_config`) ANTES de
  que corra el closure `.setup()` de este crate, asi que en ese primer
  arranque no hay ningun punto de este codigo desde el que colgarle el hook
  a esa instancia. (En arranques posteriores, cuando `main` se reconstruye
  desde `mostrar_ventana_configuracion`, SI se construye desde Rust -- pero
  no se le agrego el hook ahi porque no hace falta: ver el punto anterior.)

Que protege el `connect-src` angostado de `pos`: cualquier `fetch`/XHR desde
esa pagina a un host distinto del origen configurado, incluyendo otro host
`https` cualquiera (antes permitido por el `https:` sin restriccion de host).
Que NO protege, ni en `pos` ni en `main`: un script bundleado comprometido
(supply-chain de una dependencia de `Ways.Web`, para `pos`) que se ejecuta
DENTRO del origen ya permitido puede seguir mandando el token bearer en
memoria o la credencial de dispositivo a ESE MISMO origen configurado --
angostar `connect-src` reduce a DONDE puede mandarlo un script comprometido
(ya no a cualquier host `https`), no si puede mandarlo al servidor legitimo
en si. `main` sigue exactamente como antes (esquema `https:` sin host): no
sostiene ningun secreto, asi que ese alcance mas amplio no agrega riesgo
nuevo.

**Lo que no se pudo verificar en este entorno** (no hay WebView2 real
disponible aca): que el header reescrito efectivamente llegue a WebView2 y
se aplique como CSP activa en tiempo de ejecucion, en vez de quedarse solo
en la respuesta HTTP interna de Tauri. La evidencia de codigo (arriba) es
consistente con que si se aplica -- es el mismo mecanismo que usa el ejemplo
oficial de Tauri para reescribir CSP, y corre sobre el mismo protocolo
`tauri://` que sirve `pos.html` -- pero falta una verificacion manual: abrir
la ventana `pos` con la app empaquetada, inspeccionar el header
`Content-Security-Policy` real (DevTools → Network, o
`webview.eval("fetch('https://otro-host-https-cualquiera').catch(e => alert(e))")`
apuntando a un host `https` que NO sea el configurado) y confirmar que la
llamada es bloqueada por CSP.

## Atajo de teclado

`Ctrl+Shift+F10` (atajo global, funciona aunque la ventana no tenga foco)
vuelve a mostrar la ventana de configuracion. El POS tambien puede volver ahi
desde un boton propio (`abrir_configuracion`).

## Impresion

`imprimir_raw` recibe bytes crudos (por ejemplo, una trama ESC/POS armada en
el frontend) y los manda al spooler de Windows con tipo de dato `RAW`, sin
mostrar ningun dialogo de impresion. Si no hay impresora configurada, se usa
la predeterminada de Windows.
