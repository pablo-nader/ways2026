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
  credencial de dispositivo Y la sesion de cajero persistida (token bearer +
  vencimiento + snapshot minimo de dispositivo/PV/cajero para reconstruir el
  shell offline, judgment-day ronda 2 -- archivo aparte de la credencial de
  dispositivo, ver `sesion.rs`). Nunca `guardar_configuracion` ni
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
nuevo EN ESTE EJE (in-origin script).

### Riesgo real de persistir la sesion en disco (`sesion.rs`)

judgment-day ronda 1 (FIX 3, BLOCKER): la comparacion de arriba (script
in-origin) NO es la comparacion relevante para el archivo que agrega este
slice (`sesion.rs`, token bearer + vencimiento) -- esa solo rebate a un
script que ya corre DENTRO del origen permitido, que de por si ya podia leer
el bearer en memoria. Persistirlo en disco agrega una superficie DISTINTA,
que no existia antes y que un script in-origin no necesita:

- **Acceso al filesystem** por cualquier via ajena al webview: otro proceso
  del mismo usuario de Windows, una herramienta de administracion remota, o
  simplemente alguien con acceso fisico/RDP a la maquina puede leer
  `%APPDATA%/site.aipos.pos/sesion.credencial` sin pasar nunca por
  `pos.html` ni por su CSP -- la CSP angostada no protege un archivo en
  disco, solo trafico de red del webview.
- **Backups**: si el directorio de configuracion del usuario entra en algun
  backup (imagen de disco, backup de perfil, sincronizacion en la nube del
  perfil de Windows), ese token viaja con el backup, legible por quien
  acceda a esa copia mucho despues de que la maquina original ya no importe.
- **Perfiles/discos copiados o clonados**: clonar el disco o copiar el
  perfil de usuario a otra maquina (mantenimiento, reemplazo de equipo,
  imagen maestra) copia el archivo tal cual -- la sesion del cajero queda
  vigente en una maquina fisica distinta de la que la emitio, algo que un
  token solo-en-memoria nunca permitia (no sobrevivia al proceso).
- **Reinstalacion/reset que deja el perfil intacto** (caso planteado en la
  ronda 1): desinstalar la app SIN borrar
  `%APPDATA%/site.aipos.pos` (el instalador NSIS, `installMode:
  currentUser`, no lo hace por defecto) y volver a instalarla hereda en
  silencio la sesion del cajero anterior -- el equipo "recien reinstalado"
  arranca ya autenticado como quien uso la maquina antes, sin que nadie
  vuelva a pedir usuario/contraseña.

Mitigacion real (no la CSP, que es irrelevante para este eje): la ventana de
vigencia LOCAL del archivo es mucho mas corta que la del token real
(`entornoTauri.ts`, `VENTANA_SESION_OFFLINE_MS`, ver el doc-comment de
`calcularExpiracionPersistida`) y se renueva sola en cada contacto exitoso
con el servidor -- un archivo filtrado por cualquiera de las cuatro vias de
arriba, si nunca vuelve a tocar el servidor legitimo, deja de ser utilizable
en dias, no en los 365 dias de vida real del token.

judgment-day ronda 2 (FIX WARNING, Judge B): el analisis de arriba describe
`sesion.rs` como si solo llevara el token + vencimiento. Desde esta ronda el
mismo archivo carga ADEMAS un snapshot minimo del dispositivo/PV/cajero
(campo `snapshot`, ver el doc-comment de `SesionDeCajero` y de
`SnapshotDeSesionOffline` en `entornoTauri.ts`) -- antes vivia aparte, en un
`localStorage` del webview sin este analisis de riesgo. Que compra de mas
esa fusion, para las cuatro vias de arriba:

- **Sin el snapshot** (solo token + vencimiento, como describia esta
  seccion antes de esta ronda): copiar el archivo le da a quien lo copio
  una CREDENCIAL -- puede autenticarse contra el servidor como ese cajero
  (bearer sin revocacion, ver la comparacion de mas abajo), pero para
  operar el POS de escritorio con esa credencial hace falta ADEMAS
  contactar al servidor real (`GET /dispositivos/actual`, `GET /auth/me`,
  `resolverPuntoVentaDelDispositivo`) para reconstruir la pantalla.
- **Con el snapshot** (esta ronda en adelante): el mismo archivo copiado le
  da a quien lo copio, ademas de la credencial, un **POS DE ESCRITORIO
  COMPLETO Y OPERABLE offline** -- `AppPos.tsx` reconstruye el shell entero
  (empresa, punto de venta, cajero, catalogo/precios cacheados) sin volver a
  hablar con el servidor en absoluto mientras la ventana de vigencia LOCAL
  no haya vencido. Antes de esta ronda, un archivo copiado sin red disponible
  quedaba en el callejon sin salida `sin-red-pero-vinculado` (un boton
  "Reintentar" y nada mas, ver `AppPos.tsx`); ahora, sin red, produce una
  caja registradora funcional a nombre de otra persona.

La mitigacion sigue siendo la misma ventana corta de arriba -- se aplica
IGUAL a los dos componentes del archivo, porque es el mismo registro, la
misma escritura y la misma limpieza (logout/401/cierre de turno): un archivo
filtrado deja de poder reconstruir el shell offline exactamente en el mismo
plazo en el que deja de poder autenticar. La fusion en un solo registro es
justamente lo que garantiza eso -- ver el doc-comment de
`limpiarSesionDeCajeroPersistida` sobre por que dos almacenamientos
independientes (el diseño de la ronda 1) podian divergir y este ya no puede.

### Comparacion con el secreto de dispositivo: NO son equivalentes

La comparacion con el secreto de dispositivo (`credencial.rs`) que este
documento sugeria antes tambien estaba invertida. Los dos archivos NO dan el
mismo poder a quien los lea:

- El secreto de dispositivo, SOLO, no autentica a nadie como cajero: sigue
  exigiendo usuario + contraseña de un cajero real contra
  `POST /auth/login-dispositivo` (`AuthEndpoints.cs`, lineas 55-64) antes de
  emitir cualquier sesion. Robar ese archivo sin ademas conocer una
  contraseña de cajero no vende nada.
- El token de sesion (`sesion.rs`), SOLO, SI autentica como ese cajero: el
  esquema bearer (`ManejadorBearerDeSesion.cs`, lineas 33-72) no exige
  ningun otro dato ni ata el token al dispositivo que lo emitio -- copiado a
  cualquier otra maquina, con o sin la credencial de dispositivo de esa
  maquina, sigue autenticando como ese cajero mientras no haya vencido ni
  sido revocado del lado de `ValidadorDeSesion`.

Por eso la mitigacion real de este archivo es la ventana corta de arriba, no
una supuesta equivalencia de riesgo con la credencial de dispositivo.

judgment-day ronda 2: esta comparacion sigue siendo cierta en terminos de
AUTENTICACION (el secreto de dispositivo solo nunca autentica; el token de
sesion solo si autentica), pero ya no agota lo que compra copiar
`sesion.rs` -- ver la seccion de arriba sobre el snapshot: ahi la brecha con
el secreto de dispositivo es todavia mayor, porque el archivo copiado ya no
necesita ni contactar al servidor para operar.

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
