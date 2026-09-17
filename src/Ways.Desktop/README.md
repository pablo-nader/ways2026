# Ways Desktop (Ways POS)

Aplicacion de escritorio para Windows (Tauri 2 + WebView2) que corre el punto
de venta como una app nativa: primer uso configura el servidor y la
impresora termica, despues siempre abre `{servidor}/pos.html` en su propia
ventana, con impresion RAW ESC/POS sin dialogos.

## Prerrequisitos

- Node.js (para el CLI de Tauri).
- Rust estable con el target `x86_64-pc-windows-msvc` (`rustup default stable-x86_64-pc-windows-msvc`).
- Visual Studio Build Tools (MSVC) instalados.
- WebView2 Runtime (viene preinstalado en Windows 11 y en la mayoria de Windows 10 actualizados).

## Desarrollo

```bash
npm install
npm run dev
```

`npm run dev` levanta la app apuntando a la carpeta local `ui/` (la pagina de
configuracion). No hay servidor de desarrollo de frontend: `ui/` es HTML/CSS/JS
plano sin build step.

## Compilar

```bash
cargo test --manifest-path src-tauri/Cargo.toml   # tests unitarios (config e impresion)
cargo build --manifest-path src-tauri/Cargo.toml  # build de desarrollo
npm run build                                     # instalador NSIS (release)
```

El instalador NSIS queda en `src-tauri/target/release/bundle/nsis/`. Es una
instalacion por usuario (`installMode: currentUser`), sin privilegios de
administrador.

## Como funciona la configuracion

- Al iniciar, si no existe `config.json` en el directorio de configuracion de
  la app (`%APPDATA%/site.aipos.pos/config.json` en Windows), la ventana
  principal muestra la pagina local empaquetada `ui/index.html`.
- Esa pagina permite cargar la URL del servidor (debe empezar con `https://`,
  o con `http://localhost` / `http://127.0.0.1` para desarrollo) y elegir la
  impresora termica (o dejar la predeterminada de Windows), con un boton
  "Imprimir prueba".
- Al guardar, la app:
  1. persiste `config.json`;
  2. otorga en runtime (`Manager::add_capability` + `CapabilityBuilder`) una
     capacidad restringida al origen exacto configurado, con acceso *solo* a
     `imprimir_raw`, `listar_impresoras`, `abrir_configuracion` e `info_app`;
  3. navega la ventana principal a `{servidor}/pos.html`.
- En arranques siguientes, si `config.json` ya existe, se repite el paso 2 y
  se navega directo a `{servidor}/pos.html` sin mostrar la pagina local.

## Atajo de teclado

`Ctrl+Shift+F10` (atajo global, funciona aunque la ventana no tenga foco)
vuelve a mostrar la pagina local de configuracion. La pagina remota tambien
puede llamar a `abrir_configuracion` desde un boton propio.

## Impresion

`imprimir_raw` recibe bytes crudos (por ejemplo, una trama ESC/POS armada en
el frontend) y los manda al spooler de Windows con tipo de dato `RAW`, sin
mostrar ningun dialogo de impresion. Si no hay impresora configurada, se usa
la predeterminada de Windows.
