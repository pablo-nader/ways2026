/**
 * Snapshot local del dispositivo + cajero + punto de venta ya confirmados por el servidor
 * (stage-pos-sesion-offline, judgment-day ronda 1, FIX 1 BLOCKER) — lo que `AppPos.tsx` necesita
 * para reconstruir el shell (`ShellPos`) SIN red, más allá del bearer que ya restaura
 * `entornoTauri.ts`.
 *
 * Por qué hace falta además del bearer: el bearer solo AUTENTICA — `AppPos` también necesita el
 * `DispositivoActual` (que hoy solo trae `GET /dispositivos/actual`, una llamada de RED) y el
 * `PuntoVentaListado` fijo del dispositivo (que hoy solo trae `resolverPuntoVentaDelDispositivo`,
 * otra llamada de red) para poder montar `ShellPos` en absoluto. Sin este snapshot, un restart
 * con la red caída restauraba un token inútil: `AppPos.tsx` (`cargarDispositivo`) igual llamaba a
 * `GET /dispositivos/actual` primero y, al fallar por red, nunca llegaba a consultar el bearer
 * restaurado — quedaba en `sin-red-pero-vinculado`, un callejón sin salida con un solo botón
 * "Reintentar" (ver el doc-comment de `AppPos`).
 *
 * Por qué `localStorage` (no un archivo de Rust, a diferencia de `entornoTauri.ts`): nada de esto
 * es secreto — el dispositivo/PV/cajero ya se muestran en pantalla ANTES de cualquier login
 * (`LoginDeDispositivo.tsx` los pinta desde sus props) y no autentican nada por sí solos (a
 * diferencia del bearer o la credencial de dispositivo). Mismo criterio ya establecido en este
 * codebase para datos de sesión no sensibles (`puntoVenta/almacenDePuntoVenta.ts`): `localStorage`,
 * degradando en silencio si no está disponible (modo privado, cuota agotada).
 *
 * Se lee SOLO cuando `GET /dispositivos/actual` no pudo ni contactar al servidor (`ErrorDeRed`) —
 * un 404 `dispositivo_no_vinculado` explícito, o cualquier otra respuesta real del servidor,
 * NUNCA lo consultan: la base sigue siendo la única autoridad (ver `AppPos.tsx`, `cargarDispositivo`).
 */
import type { DispositivoActual } from '../api/dispositivos'
import type { PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

export const CLAVE_SESION_DE_DISPOSITIVO_LOCAL = 'ways.pos.sesionDeDispositivo'

export type SesionDeDispositivoLocal = {
  dispositivo: DispositivoActual
  usuario: UsuarioAutenticado
  puntoVenta: PuntoVentaListado
}

/** Se llama en cada momento en que el servidor confirmó los tres datos juntos (`AppPos.tsx`:
 * `resolverSesionDelDispositivo` tras un `GET /auth/me` exitoso, o el login fresco vía
 * `LoginDeDispositivo`) — nunca desde el camino de reconstrucción offline, que solo LEE. */
export function guardarSesionDeDispositivoLocal(sesion: SesionDeDispositivoLocal): void {
  try {
    localStorage.setItem(CLAVE_SESION_DE_DISPOSITIVO_LOCAL, JSON.stringify(sesion))
  } catch {
    // Sin almacenamiento (modo privado, cuota agotada) un restart sin red no va a poder
    // reconstruir el POS solo — degrada al camino ya existente (`sin-red-pero-vinculado`), nunca
    // rompe el login/resolución que sí tuvo éxito.
  }
}

/** `null` si no hay nada guardado, el JSON está corrupto, o le falta alguno de los tres campos —
 * nunca lanza. No valida que el dispositivo/PV/usuario sigan existiendo del lado del servidor: eso
 * es exactamente lo que no se puede confirmar sin red (por eso el llamador solo lo usa como
 * último recurso, gateado además por tener un bearer restaurado y vigente, ver `AppPos.tsx`). */
export function leerSesionDeDispositivoLocal(): SesionDeDispositivoLocal | null {
  try {
    const crudo = localStorage.getItem(CLAVE_SESION_DE_DISPOSITIVO_LOCAL)
    if (!crudo) return null

    const guardada = JSON.parse(crudo) as Partial<SesionDeDispositivoLocal> | null
    if (!guardada || !guardada.dispositivo || !guardada.usuario || !guardada.puntoVenta) return null

    return { dispositivo: guardada.dispositivo, usuario: guardada.usuario, puntoVenta: guardada.puntoVenta }
  } catch {
    return null
  }
}
