/**
 * Cliente HTTP de la instantánea de venta offline (stage-pos-venta-offline-web) — `GET
 * /api/pos/instantanea`. Sin parámetros: el propio dispositivo autenticado determina su punto de
 * venta del lado del servidor (`ServicioDeInstantaneaDePos`), nunca este cliente.
 */
import { api } from './cliente'
import type { InstantaneaDePos } from './tipos'

export const clienteDePos = {
  obtenerInstantanea: () => api.get<InstantaneaDePos>('/pos/instantanea'),
}
