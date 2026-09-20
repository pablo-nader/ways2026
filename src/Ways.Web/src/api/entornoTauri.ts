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
 * Token bearer de la SESIÓN del cajero (slice bearer, Parte B) — vive en memoria, no en disco:
 * a diferencia del secreto de dispositivo (que tiene que sobrevivir un restart de la app ANTES
 * de cualquier login), este token lo emite `POST /auth/login-dispositivo` recién cuando hay un
 * cajero logueado, y perderlo al cerrar la app simplemente manda de vuelta a
 * `LoginDeDispositivo` — ya el comportamiento esperado hoy con la cookie de sesión si el usuario
 * la borra. `cliente.ts` lo adjunta como header `Authorization` en cada request bajo Tauri.
 */
let tokenDeSesionBearer: string | null = null

export function establecerTokenDeSesionBearer(token: string | null): void {
  tokenDeSesionBearer = token
}

export function tokenDeSesionBearerActual(): string | null {
  return tokenDeSesionBearer
}
