/**
 * Cliente de gastos del turno (POS, stage-gastos-turno-carga-simple): alta contra el turno
 * abierto del punto de venta — el servidor resuelve el turno desde `idPuntoVenta`, nunca viaja
 * en el cuerpo (mismo criterio que `clienteDeCaja.abrir`).
 */
import { api } from './cliente'
import type { GastoRegistrado, SolicitudDeGasto } from './tipos'

export const clienteDeGastos = {
  /** `POST /api/gastos` — 201 + gasto registrado, o `409 turno_no_abierto` si el punto de venta
   * no tiene un turno abierto. */
  registrar: (solicitud: SolicitudDeGasto) => api.post<GastoRegistrado>('/gastos', solicitud),
}
