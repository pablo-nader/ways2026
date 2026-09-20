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
"Configuración" del POS o `abrir_configuracion` hacen el camino inverso. Cada
vez que se vuelve a mostrar una ventana que YA EXISTIA (no en su primera
creacion, que ya carga todo de cero) se la recarga (`window.location.reload()`)
para no operar con estado cacheado de una configuracion vieja.

La pagina del POS no tiene permiso para invocar `leer_configuracion`: la URL
del servidor le llega por el campo `url_servidor` de `info_app` (que si tiene
permitido), no por un comando nuevo.

## Atajo de teclado

`Ctrl+Shift+F10` (atajo global, funciona aunque la ventana no tenga foco)
vuelve a mostrar la ventana de configuracion. El POS tambien puede volver ahi
desde un boton propio (`abrir_configuracion`).

## Impresion

`imprimir_raw` recibe bytes crudos (por ejemplo, una trama ESC/POS armada en
el frontend) y los manda al spooler de Windows con tipo de dato `RAW`, sin
mostrar ningun dialogo de impresion. Si no hay impresora configurada, se usa
la predeterminada de Windows.
