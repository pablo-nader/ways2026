/**
 * stage-desktop-pos: puente hacia el shell de escritorio (Tauri). Desde la slice 3, `pos.html` es
 * una página LOCAL bundleada con la app (`Ways.Desktop/ui/pos.html`, copiada ahí por
 * `scripts/sync-frontend-local.mjs`) — ya no se navega a ningún origen remoto. Sigue corriendo
 * dentro del webview de Tauri, con `app.withGlobalTauri: true` (`tauri.conf.json`), que alcanza
 * para tener `window.__TAURI__.core.invoke` disponible sin agregar la dependencia npm
 * `@tauri-apps/api` (que sería peso muerto para el build que corre en un navegador común).
 *
 * `corriendoEnTauri()` es el único punto que chequea `window.__TAURI__`: nunca acceder a esa
 * global desde otro lado del código.
 */

type PuenteTauri = { core: { invoke: (comando: string, args?: Record<string, unknown>) => Promise<unknown> } }

function puenteTauri(): PuenteTauri | null {
  if (typeof window === 'undefined') return null
  const conTauri = window as unknown as { __TAURI__?: PuenteTauri }
  return conTauri.__TAURI__ ?? null
}

export function corriendoEnTauri(): boolean {
  return puenteTauri() !== null
}

/** Persiste el secreto de dispositivo del lado de Rust (comando `guardar_credencial_de_dispositivo`,
 * `credencial.rs`) — nunca en `localStorage`. No hace nada fuera de Tauri (la web normal no tiene
 * dispositivos). */
export async function guardarCredencialDeDispositivo(secreto: string): Promise<void> {
  const tauri = puenteTauri()
  if (!tauri) return
  await tauri.core.invoke('guardar_credencial_de_dispositivo', { secreto })
}

/** Lee el secreto guardado, si hay uno. `null` fuera de Tauri o sin credencial guardada — nunca
 * lanza: un fallo de IPC se trata igual que "no hay credencial" (mismo criterio permisivo que
 * `credencial::leer` del lado de Rust). */
export async function leerCredencialDeDispositivo(): Promise<string | null> {
  const tauri = puenteTauri()
  if (!tauri) return null
  try {
    const resultado = await tauri.core.invoke('leer_credencial_de_dispositivo')
    return typeof resultado === 'string' ? resultado : null
  } catch {
    return null
  }
}

/**
 * URL absoluta del servidor bajo Tauri (slice 3) — cacheada en memoria por `inicializarUrlServidor`.
 * `cliente.ts` la antepone a `/api${ruta}` porque, con `pos.html` local, el origen de la página es
 * `http://tauri.localhost`: una ruta relativa ahí la resuelve el protocolo de asset de Tauri, no
 * la red. `null` fuera de Tauri (donde `cliente.ts` nunca la usa) o mientras no se pudo leer.
 */
let urlServidorCacheada: string | null = null

/**
 * Lee `url_servidor` desde el comando `info_app` (`InfoApp::url_servidor`, `comandos.rs`) y lo
 * cachea. El POS no tiene permitido `leer_configuracion` (exclusivo de la ventana de
 * configuración, ver `capabilities/configuracion.json`) — se reusa `info_app`, que sí tiene, en
 * vez de agregar un comando nuevo solo para esto. Hay que esperar esta promesa ANTES de la primera
 * llamada a la API (`main.tsx` la espera antes de montar `AppPos`): si corriera después, esa
 * primera llamada saldría con la URL todavía en `null` y, bajo Tauri, apuntaría mal. No hace nada
 * fuera de Tauri.
 */
export async function inicializarUrlServidor(): Promise<void> {
  const tauri = puenteTauri()
  if (!tauri) return

  try {
    const info = (await tauri.core.invoke('info_app')) as { url_servidor?: string | null }
    urlServidorCacheada = info.url_servidor ?? null
  } catch {
    // Sin IPC no hay forma de saber la URL: se fuerza a null (nunca se deja un valor cacheado de
    // un llamado anterior) — mismo criterio permisivo que el resto de este archivo (un fallo de
    // IPC nunca lanza), pero determinista: esta función solo corre una vez al arrancar
    // (`main.tsx`), así que no hay "valor anterior todavía válido" que tenga sentido conservar.
    urlServidorCacheada = null
  }
}

/** URL absoluta a anteponer a `/api${ruta}` bajo Tauri, o cadena vacía (relativa) en cualquier
 * otro caso — para poder usarla siempre sin un `if` en cada llamada de `cliente.ts`. */
export function urlBaseApi(): string {
  if (!corriendoEnTauri()) return ''
  return urlServidorCacheada ?? ''
}

/**
 * Token bearer de la SESIÓN del cajero (slice bearer, Parte B) — la copia en memoria que
 * `cliente.ts` adjunta como header `Authorization` en cada request bajo Tauri. `POST
 * /auth/login-dispositivo` lo emite recién cuando hay un cajero logueado.
 *
 * stage-pos-sesion-offline: hasta esta etapa esta variable vivía SOLO en memoria — un restart del
 * shell de escritorio, un F5, o un reboot del PC la perdían siempre, sin importar si la sesión
 * seguía vigente del lado del servidor, obligando a repreguntar `LoginDeDispositivo` (que
 * necesita red) incluso cuando no había pasado nada raro. Ahora se persiste del lado de Rust
 * (`guardarSesionDeCajeroPersistida`, más abajo) junto con su vencimiento explícito, y se
 * restaura al arrancar (`restaurarSesionDeCajeroPersistida`) — ver esas dos funciones para el
 * ciclo de vida completo. El secreto de DISPOSITIVO (`credencial.rs`) sigue siendo un archivo
 * aparte con un ciclo de vida distinto (sobrevive a todos los cajeros que usan el equipo).
 */
let tokenDeSesionBearer: string | null = null

export function establecerTokenDeSesionBearer(token: string | null): void {
  tokenDeSesionBearer = token
}

export function tokenDeSesionBearerActual(): string | null {
  return tokenDeSesionBearer
}

/**
 * Persiste el token de sesión del cajero + su vencimiento explícito (comando
 * `guardar_sesion_de_cajero`, `sesion.rs`) — nunca en IndexedDB/localStorage, mismo criterio que
 * `guardarCredencialDeDispositivo`. Se llama junto con `establecerTokenDeSesionBearer` en el
 * único lugar donde el token se obtiene de un login real (`dispositivos.ts`, `iniciarSesion`).
 *
 * A diferencia de `guardarCredencialDeDispositivo`, esta función NUNCA lanza: un fallo de IPC acá
 * no puede significar que el login mismo falló (`establecerTokenDeSesionBearer` ya corrió, el
 * cajero YA está autenticado en memoria) — el único costo de no poder persistir es que un restart
 * posterior va a volver a pedir login, exactamente el comportamiento de hoy sin este slice. No es
 * comparable al secreto de dispositivo (`PantallaDeVinculacion.tsx`), que si no se persiste queda
 * irrecuperable para siempre (el servidor nunca lo vuelve a entregar) y por eso sí necesita un
 * camino de error dedicado. No hace nada fuera de Tauri.
 */
export async function guardarSesionDeCajeroPersistida(token: string, expiraEl: string): Promise<void> {
  const tauri = puenteTauri()
  if (!tauri) return
  try {
    await tauri.core.invoke('guardar_sesion_de_cajero', { sesion: { token, expira_el: expiraEl } })
  } catch {
    // Ver el doc-comment de arriba: un fallo de IPC acá nunca debe impedir que el login en curso
    // se reporte como exitoso.
  }
}

/**
 * Limpia la sesión de cajero persistida — reusa `guardar_sesion_de_cajero` con `token`/`expira_el`
 * vacíos (ver el doc-comment de `sesion.rs` sobre por qué no hay un comando de limpieza separado).
 * Se llama en los tres casos donde la sesión deja de ser válida: logout explícito
 * (`ShellPos.tsx`), un 401 del servidor (suscripto a `alPerderLaSesion` desde `pos/main.tsx`, para
 * no crear un import circular con `cliente.ts`, que ya importa de este archivo), y el cierre de
 * turno (`Pos.tsx`, `cierreConfirmado`). Nunca lanza, mismo motivo que
 * `guardarSesionDeCajeroPersistida`. No hace nada fuera de Tauri.
 */
export async function limpiarSesionDeCajeroPersistida(): Promise<void> {
  const tauri = puenteTauri()
  if (!tauri) return
  try {
    await tauri.core.invoke('guardar_sesion_de_cajero', { sesion: { token: '', expira_el: '' } })
  } catch {
    // Ver el doc-comment de arriba.
  }
}

/**
 * `true` si `expiraEl` (el mismo valor que ya devolvió `POST /auth/login-dispositivo`, persistido
 * tal cual) todavía no pasó, comparado contra `ahora` — clausula PURA (sin IPC, sin `Date.now()`
 * directo) para poder testearla con un reloj controlado. Una fecha vacía o no parseable (archivo
 * corrupto, o el string vacío que produce una limpieza) es SIEMPRE inválida: nunca se asume
 * vigente sin poder confirmarlo, mismo criterio permisivo que el resto de este archivo pero
 * invertido (acá "no se puede confirmar" cae a `false`, no a un valor cacheado).
 *
 * Sin `Number.isFinite` explícito a propósito (mutation-proof-tests, "guardas inmatables no se
 * shippean", PR #257): `new Date('').getTime()`/`new Date('no-es-una-fecha').getTime()` devuelven
 * `NaN`, y `NaN > cualquierNumero` es SIEMPRE `false` en JS — un chequeo de finitud agregado ahí
 * quedaría sobredeterminado por esa comparación (ningún test podría matarlo, se probó a mano y
 * sobrevivió la mutación), así que el comentario documenta la garantía en vez de un `if` muerto.
 */
export function sesionPersistidaEsValida(expiraEl: string, ahora: Date): boolean {
  const expira = new Date(expiraEl).getTime()
  return expira > ahora.getTime()
}

/**
 * Lee la sesión de cajero persistida y, si es válida (token no vacío Y no vencida contra el reloj
 * local, `sesionPersistidaEsValida`), la instala en memoria (`establecerTokenDeSesionBearer`)
 * para que `cliente.ts` la adjunte en la próxima request. Deliberadamente NO llama a la red (ni a
 * `/dispositivos/actual` ni a `/auth/me`): eso sigue siendo trabajo de `AppPos.tsx`
 * (`resolverSesionDelDispositivo`), que ya es la única fuente de verdad sobre si una sesión sigue
 * vigente del lado del servidor — esta función solo decide si hay algo localmente vigente que
 * vale la pena intentar. Si no hay nada, o está vencida, no hace nada (el bearer en memoria queda
 * `null`): el camino es idéntico al de hoy, `AppPos` cae al login del dispositivo. Una sesión
 * vencida detectada acá NUNCA se limpia del archivo (a propósito: los únicos tres disparadores de
 * limpieza son logout/401/cierre de turno, ver `limpiarSesionDeCajeroPersistida` — agregar un
 * cuarto acá sería silencioso e innecesario, la próxima restauración la va a volver a descartar
 * igual sin ningún efecto observable).
 *
 * Se llama una sola vez al arrancar (`pos/main.tsx`), junto con `inicializarUrlServidor` y ANTES
 * de montar `AppPos` — si `AppPos` montara antes de esto, su primer `GET /auth/me` saldría sin el
 * bearer restaurado todavía. Nunca lanza. No hace nada fuera de Tauri.
 */
export async function restaurarSesionDeCajeroPersistida(): Promise<void> {
  const tauri = puenteTauri()
  if (!tauri) return
  try {
    const resultado = (await tauri.core.invoke('leer_sesion_de_cajero')) as { token?: unknown; expira_el?: unknown } | null
    if (!resultado || typeof resultado.token !== 'string' || typeof resultado.expira_el !== 'string') return
    if (resultado.token === '' || !sesionPersistidaEsValida(resultado.expira_el, new Date())) return
    establecerTokenDeSesionBearer(resultado.token)
  } catch {
    // Sin IPC no hay nada para restaurar — mismo criterio permisivo que el resto del archivo.
  }
}
