/**
 * Cliente HTTP de las rutas del dispositivo de POS (stage-pos-venta-offline-web) — `GET
 * /api/pos/instantanea` y `POST /api/pos/rendicion-de-cola`. Ninguna de las dos lleva punto de
 * venta ni dispositivo: los deriva el servidor del dispositivo autenticado
 * (`ServicioDeInstantaneaDePos`/`ServicioDeRendicionDeCola`), nunca este cliente.
 */
import { api } from './cliente'
import type { InstantaneaDePos, SolicitudDeRendicionDeCola } from './tipos'

export const clienteDePos = {
  obtenerInstantanea: () => api.get<InstantaneaDePos>('/pos/instantanea'),
  /** `POST /api/pos/rendicion-de-cola` — 204 sin cuerpo: el dispositivo declara el estado de su
   * cola local para que el cierre de turno pueda verificarlo (`ReglaDeRendicionDeCola`). */
  rendirCola: (solicitud: SolicitudDeRendicionDeCola) => api.post<void>('/pos/rendicion-de-cola', solicitud),
}
