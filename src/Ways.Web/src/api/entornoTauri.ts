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
import type { DispositivoActual } from './dispositivos'
import type { PuntoVentaListado } from './tipos'

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
 * Lo mínimo de la persona logueada que el shell del POS de escritorio necesita para
 * reconstruirse offline (`ShellPos`/`VentasDelTurno`) — judgment-day ronda 2 (FIX CRITICAL,
 * Judge A): nunca el `UsuarioAutenticado` completo. Se descartan a propósito:
 * - `mail`: PII que ningún componente bajo este shell lee — nunca hace falta guardarlo.
 * - `rol` (el string): tampoco lo lee nada bajo este shell (solo `rolId`, el numérico, gatea
 *   algo real — ver abajo); guardarlo sería superficie sin uso.
 * - `ultimaConexion`/`idTenant`: no los lee nada bajo este shell, y para una reconstrucción
 *   OFFLINE serían valores potencialmente viejos y engañosos si algo llegara a mostrarlos.
 *
 * `rolId` SÍ viaja porque gatea una acción real (`VentasDelTurno.tsx`, `puedeAnular`, el botón
 * "Anular" de una venta) — pero es un gate cliente-side de conveniencia nomás: el servidor vuelve
 * a exigir la misma policy en el endpoint real de anulación, así que un `rolId` local
 * desactualizado (ej. el cajero fue degradado del lado del servidor mientras este archivo seguía
 * en disco) nunca puede lograr una anulación real, como mucho muestra u oculta el botón de forma
 * optimista hasta que la red vuelva a confirmar la sesión.
 */
export type UsuarioOfflineMinimo = { id: number; usuario: string; rolId: number }

/**
 * Snapshot del dispositivo/PV/cajero que el servidor confirmó juntos la última vez que se pudo
 * hablar con él (stage-pos-sesion-offline, judgment-day ronda 2, FIX CRITICAL) — antes vivía en
 * su propio `localStorage` (`sesionDeDispositivoLocal.ts`, eliminado en esta ronda), sin gate de
 * capacidad ni vencimiento propios y sin ningún disparador que lo limpiara nunca. Ahora es un
 * campo MÁS del mismo registro que el token (`SesionDeCajero`, `sesion.rs`): un solo
 * `guardar`/`leer`, y los tres disparadores que ya vacían el token (logout, 401, cierre de turno)
 * vacían este campo con el mismo `fs::write` — divergencia entre "hay token" y "hay snapshot"
 * queda estructuralmente imposible, no solo mitigada. `dispositivo`/`puntoVenta` viajan
 * COMPLETOS (a diferencia de `usuario`): ninguno de los dos trae PII, y ambos ya se muestran en
 * pantalla antes de cualquier login (`LoginDeDispositivo.tsx`).
 */
export type SnapshotDeSesionOffline = {
  dispositivo: DispositivoActual
  puntoVenta: PuntoVentaListado
  usuario: UsuarioOfflineMinimo
}

function esSnapshotDeSesionOfflineValido(valor: unknown): valor is SnapshotDeSesionOffline {
  if (!valor || typeof valor !== 'object') return false
  const candidato = valor as Partial<SnapshotDeSesionOffline>
  if (!candidato.dispositivo || !candidato.puntoVenta || !candidato.usuario || typeof candidato.usuario !== 'object') return false
  const usuario = candidato.usuario as Partial<UsuarioOfflineMinimo>
  return typeof usuario.id === 'number' && typeof usuario.usuario === 'string' && typeof usuario.rolId === 'number'
}

/**
 * Espejo en memoria de la ventana de vigencia LOCAL con la que se escribió por última vez el
 * registro persistido — separado de `tokenDeSesionBearer` (arriba, que sigue siendo el único que
 * `cliente.ts` adjunta a cada request) porque responde una pregunta DISTINTA: no "¿hay un bearer
 * en memoria?" sino "¿ese bearer sigue dentro de la ventana LOCAL que se confirmó la última vez
 * que se pudo hablar con el servidor?". `null` hasta que se restaura o se persiste una sesión al
 * menos una vez en este proceso — ver `snapshotDeSesionOfflineVigente`, más abajo.
 */
let ventanaLocalDeSesion: string | null = null

/**
 * Espejo en memoria del snapshot persistido (ver `SnapshotDeSesionOffline`) — se lee junto con el
 * bearer al restaurar (`restaurarSesionDeCajeroPersistida`) y se actualiza junto con él en cada
 * confirmación fresca del servidor (`refrescarVentanaDeSesionPersistida`), para que `AppPos.tsx`
 * pueda reconstruir el shell offline sin un segundo viaje de IPC. `null` si nunca se guardó uno
 * en este proceso o si la sesión se limpió (logout/401/cierre de turno).
 */
let snapshotDeSesionOffline: SnapshotDeSesionOffline | null = null

/**
 * Ventana de vigencia LOCAL de la sesión persistida en disco (judgment-day ronda 1, FIX 2b) —
 * deliberadamente mucho más corta que los 365 días del token real (`AuthEndpoints.cs`,
 * `/auth/login-dispositivo`): ese vencimiento largo lo necesita la cookie/bearer para no volver a
 * pedir login en uso normal, pero el ARCHIVO en disco es lo que un backup, un perfil copiado o un
 * disco clonado podrían filtrar (ver `Ways.Desktop/README.md`). 3 días cubre un corte de red largo
 * (viernes a lunes) más el margen de un restart, sin dejar un archivo filtrado utilizable durante
 * meses — se refresca solo (`refrescarVentanaDeSesionPersistida`, más abajo) cada vez que el
 * dispositivo vuelve a hablar con el servidor con éxito, así que un cajero activo nunca la nota.
 */
export const VENTANA_SESION_OFFLINE_MS = 3 * 24 * 60 * 60 * 1000

/**
 * Acota el vencimiento que de verdad se persiste en disco al menor entre el que devolvió el
 * servidor (`expiraElServidor`, 365 días) y `ahora + VENTANA_SESION_OFFLINE_MS` — nunca alarga el
 * vencimiento real del token, solo puede acortar cuánto tiempo queda UTILIZABLE el archivo local.
 * Si el vencimiento del servidor ya vence antes de esa ventana (o no se puede parsear), se
 * devuelve tal cual vino, sin reformatear — evita renormalizar innecesariamente un valor que ya
 * era el más corto de los dos. Función pura para poder testearla con un reloj controlado.
 */
export function calcularExpiracionPersistida(expiraElServidor: string, ahora: Date): string {
  const limiteVentana = ahora.getTime() + VENTANA_SESION_OFFLINE_MS
  const expiraServidorMs = new Date(expiraElServidor).getTime()
  if (!Number.isFinite(expiraServidorMs) || expiraServidorMs <= limiteVentana) {
    return expiraElServidor
  }
  return new Date(limiteVentana).toISOString()
}

/**
 * Persiste el registro completo de la sesión del cajero — token + vencimiento explícito +
 * snapshot de dispositivo/PV/cajero (comando `guardar_sesion_de_cajero`, `sesion.rs`) — nunca en
 * IndexedDB/localStorage, mismo criterio que `guardarCredencialDeDispositivo`. `snapshot` es
 * opcional (`null` por defecto): el único lugar donde el token se obtiene de un login real
 * (`dispositivos.ts`, `iniciarSesion`) todavía no conoce el punto de venta en ese momento (se
 * resuelve un instante después, ver `LoginDeDispositivo.tsx`) — esta función en sí es un
 * passthrough tonto hacia el comando de Rust, nunca decide la ventana ni el contenido del
 * snapshot.
 *
 * judgment-day ronda 2 (FIX CRITICAL): esta es la ÚNICA función que escribe el registro
 * persistido — por eso también es la única que actualiza los espejos en memoria
 * (`ventanaLocalDeSesion`, `snapshotDeSesionOffline`), SIEMPRE juntos, para que nunca puedan
 * divergir entre sí ni respecto de lo que de verdad se mandó a disco.
 *
 * A diferencia de `guardarCredencialDeDispositivo`, esta función NUNCA lanza: un fallo de IPC acá
 * no puede significar que el login mismo falló (`establecerTokenDeSesionBearer` ya corrió, el
 * cajero YA está autenticado en memoria) — el único costo de no poder persistir es que un restart
 * posterior va a volver a pedir login, exactamente el comportamiento de hoy sin este slice. No es
 * comparable al secreto de dispositivo (`PantallaDeVinculacion.tsx`), que si no se persiste queda
 * irrecuperable para siempre (el servidor nunca lo vuelve a entregar) y por eso sí necesita un
 * camino de error dedicado. No hace nada fuera de Tauri (ni siquiera actualiza los espejos: sin
 * Tauri no hay ningún registro persistido del que ser espejo).
 */
export async function guardarSesionDeCajeroPersistida(
  token: string,
  expiraEl: string,
  snapshot: SnapshotDeSesionOffline | null = null,
): Promise<void> {
  const tauri = puenteTauri()
  if (!tauri) return
  ventanaLocalDeSesion = expiraEl
  snapshotDeSesionOffline = snapshot
  try {
    await tauri.core.invoke('guardar_sesion_de_cajero', { sesion: { token, expira_el: expiraEl, snapshot } })
  } catch {
    // Ver el doc-comment de arriba: un fallo de IPC acá nunca debe impedir que el login en curso
    // se reporte como exitoso.
  }
}

/**
 * Limpia la sesión de cajero persistida ENTERA — token, vencimiento Y snapshot juntos, delegando
 * en `guardarSesionDeCajeroPersistida('', '')` (ver el doc-comment de `sesion.rs` sobre por qué
 * no hay un comando de limpieza separado, ni uno separado para el snapshot: es el mismo
 * registro). Se llama en los tres disparadores CLIENTE que dejan de considerar vigente la sesión
 * LOCAL: logout explícito (`ShellPos.tsx`, que además limpia el bearer en memoria con
 * `establecerTokenDeSesionBearer(null)` — esta función sola nunca alcanza para cerrar sesión, ver
 * más abajo), un 401 del servidor (suscripto a `alPerderLaSesion` desde `pos/main.tsx`, para no
 * crear un import circular con `cliente.ts`, que ya importa de este archivo), y el cierre de turno
 * (`Pos.tsx`, `cierreConfirmado`, mismo criterio).
 *
 * judgment-day ronda 2 (FIX CRITICAL): antes de esta ronda, el snapshot vivía en un
 * `localStorage` separado (`sesionDeDispositivoLocal.ts`, eliminado) que NINGUNO de estos tres
 * disparadores tocaba — un cajero deslogueado podía quedar con el snapshot todavía vivo para
 * siempre. Al ser ahora el mismo registro, limpiar el token limpia el snapshot con él: no existe
 * ningún camino de código que pueda hacer una cosa sin la otra.
 *
 * Honestidad sobre lo que estos tres disparadores NO hacen del lado del servidor (judgment-day
 * ronda 1, FIX 2): el bearer es stateless y sin lista de revocación — `POST /auth/logout`
 * (`AuthEndpoints.cs`) solo hace `SignOutAsync` sobre la COOKIE, nunca invalida un token bearer ya
 * emitido, y el cierre de turno no toca ninguna tabla de autenticación. Un token copiado mientras
 * estaba vivo sigue siendo válido para el servidor durante toda su vida real (365 días,
 * `AuthEndpoints.MapearAuth`) sin importar que el cajero haya cerrado sesión o cerrado su turno
 * acá — construir revocación real del lado del servidor es un proyecto propio, fuera de alcance de
 * este slice. Lo que estos tres disparadores SÍ logran es acotado pero real: sacan el token (y
 * ahora el snapshot) de este dispositivo (memoria + disco), así que un restart posterior de ESTE
 * equipo ya no los restaura solos — el único mecanismo que limita cuánto puede durar un archivo
 * FILTRADO cuya escritura de limpieza en sí falló (IPC, disco lleno, antivirus) es la ventana
 * corta de `calcularExpiracionPersistida`, no esta función: eso es un límite inherente a persistir
 * algo en disco, no algo que este slice pueda cerrar del todo. Nunca lanza, mismo motivo que
 * `guardarSesionDeCajeroPersistida`. No hace nada fuera de Tauri.
 */
export async function limpiarSesionDeCajeroPersistida(): Promise<void> {
  await guardarSesionDeCajeroPersistida('', '')
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
 *
 * judgment-day ronda 2 (FIX CRITICAL): además del bearer, restaura los dos espejos en memoria
 * del registro completo (`ventanaLocalDeSesion`, `snapshotDeSesionOffline`) — son el mismo
 * `leer_sesion_de_cajero`, nunca un segundo viaje de IPC. Un `snapshot` con una forma inválida
 * (archivo corrupto, o un JSON que le falta algún campo del cajero) se descarta con `null` en vez
 * de instalarse a medias — mismo criterio permisivo que el resto del archivo.
 */
export async function restaurarSesionDeCajeroPersistida(): Promise<void> {
  const tauri = puenteTauri()
  if (!tauri) return
  try {
    const resultado = (await tauri.core.invoke('leer_sesion_de_cajero')) as
      | { token?: unknown; expira_el?: unknown; snapshot?: unknown }
      | null
    if (!resultado || typeof resultado.token !== 'string' || typeof resultado.expira_el !== 'string') return
    if (resultado.token === '' || !sesionPersistidaEsValida(resultado.expira_el, new Date())) return
    establecerTokenDeSesionBearer(resultado.token)
    ventanaLocalDeSesion = resultado.expira_el
    snapshotDeSesionOffline = esSnapshotDeSesionOfflineValido(resultado.snapshot) ? resultado.snapshot : null
  } catch {
    // Sin IPC no hay nada para restaurar — mismo criterio permisivo que el resto del archivo.
  }
}

/**
 * Snapshot de dispositivo/PV/cajero listo para reconstruir el shell offline (`AppPos.tsx`) —
 * SOLO si hay un bearer en memoria Y la ventana de vigencia LOCAL con la que se guardó todavía no
 * pasó contra `ahora`. Nunca es la verdad de si el token sigue vigente del lado del SERVIDOR: eso
 * solo lo sabe `GET /auth/me`, y este camino es precisamente el que corre cuando no se lo pudo ni
 * consultar.
 *
 * judgment-day ronda 2 (FIX SUGGESTION, judge B): a diferencia de `restaurarSesionDeCajeroPersistida`,
 * que valida esto UNA sola vez al arrancar el proceso, esta función se llama en el momento en que
 * `AppPos.tsx` de verdad va a USAR la sesión restaurada — una sesión de escritorio larga que cruzó
 * la ventana local en vuelo (sin volver a hablar con el servidor con éxito, que es lo único que la
 * estira, ver `refrescarVentanaDeSesionPersistida`) ya no puede colarse por acá con un token que,
 * del lado LOCAL, dejó de considerarse vigente.
 */
export function snapshotDeSesionOfflineVigente(ahora: Date): SnapshotDeSesionOffline | null {
  if (!tokenDeSesionBearerActual()) return null
  if (!ventanaLocalDeSesion || !sesionPersistidaEsValida(ventanaLocalDeSesion, ahora)) return null
  return snapshotDeSesionOffline
}

/**
 * Refresca la ventana de vigencia LOCAL de la sesión persistida (judgment-day ronda 1, FIX 2b) —
 * se llama cada vez que el dispositivo confirma que TODAVÍA puede hablar con el servidor con éxito
 * (`AppPos.tsx`, `resolverSesionDelDispositivo`, después de que `GET /auth/me` responde 200): un
 * cajero activo, con red normal, nunca ve expirar el archivo local aunque la ventana en sí sea
 * corta, porque se estira de nuevo en cada contacto real. Si NO hay contacto real (el escenario
 * que este slice existe para cubrir: la red está caída) la ventana no se toca y sigue corriendo
 * desde el último contacto confirmado — es precisamente esa falta de refresco la que acota cuánto
 * puede durar un archivo filtrado que nunca vuelve a tocar el servidor legítimo.
 *
 * judgment-day ronda 2 (FIX CRITICAL): `snapshot` viaja junto con la ventana refrescada, en la
 * MISMA escritura — es el mismo momento en que el servidor confirmó los tres datos juntos (`GET
 * /auth/me` + `resolverPuntoVentaDelDispositivo`), reemplazando lo que antes hacía por separado
 * `guardarSesionDeDispositivoLocal` sobre un `localStorage` aparte.
 *
 * No hace nada si no hay un token en memoria (nada que refrescar) ni fuera de Tauri. Nunca lanza,
 * mismo motivo que `guardarSesionDeCajeroPersistida`.
 */
export async function refrescarVentanaDeSesionPersistida(snapshot: SnapshotDeSesionOffline): Promise<void> {
  const token = tokenDeSesionBearerActual()
  if (!token) return
  await guardarSesionDeCajeroPersistida(token, new Date(Date.now() + VENTANA_SESION_OFFLINE_MS).toISOString(), snapshot)
}
