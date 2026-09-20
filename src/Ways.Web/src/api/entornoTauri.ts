/**
 * stage-desktop-pos, slice bearer: puente hacia el shell de escritorio (Tauri). `pos.html` sigue
 * siendo servido de forma remota en este slice (el shell todavía navega ahí con
 * `window.location.replace`, ver `Ways.Desktop/src-tauri/src/lib.rs`), pero YA corre dentro del
 * webview de Tauri — con `app.withGlobalTauri: true` (`tauri.conf.json`), eso alcanza para tener
 * `window.__TAURI__.core.invoke` disponible sin agregar la dependencia npm `@tauri-apps/api` (que
 * sería peso muerto para el build que corre en un navegador común).
 *
 * `corriendoEnTauri()` es el único punto que chequea `window.__TAURI__`: nunca acceder a esa
 * global desde otro lado del código, así el día que la slice 3 mueva el shell a una página local
 * este es el único archivo que hay que revisar.
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
