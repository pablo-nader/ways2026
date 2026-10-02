/**
 * Reglas duras de la venta local (stage-pos-venta-offline-web, Parte D/E) — puras, sin I/O, para
 * que la misma condición gatee tanto la UI (vista previa local, opciones ofrecidas) como el
 * encolado real en el outbox (nunca solo una de las dos, spec `react-async-state` regla 7: "una
 * copia claims-match-code sin el enforcement real es mentira").
 */
import type { ComportamientoMedioPago, InstantaneaDePos } from '../api/tipos'

/**
 * Medios que la venta local admite. `Efectivo` y `Electronico` (tarjeta, transferencia — se cobran
 * en una terminal externa) nunca esperan al servidor. `CuentaCorriente` solo para un cliente
 * identificado: el Consumidor Final nunca paga con cuenta corriente (regla 5 del servidor). El
 * límite de crédito no se juzga acá: lo consulta el encolado contra el servidor, y sin respuesta
 * la venta se registra igual marcada como no validada (decisión del dueño).
 */
export function medioAdmitidoOffline(comportamiento: ComportamientoMedioPago, esConsumidorFinal: boolean): boolean {
  if (comportamiento === 'Efectivo' || comportamiento === 'Electronico') return true
  return comportamiento === 'CuentaCorriente' && !esConsumidorFinal
}

export function pagosAdmitidosOffline(pagos: readonly { comportamiento: ComportamientoMedioPago }[], esConsumidorFinal: boolean): boolean {
  return pagos.length > 0 && pagos.every((p) => medioAdmitidoOffline(p.comportamiento, esConsumidorFinal))
}

/**
 * El Consumidor Final siempre se vende localmente; cualquier otro cliente, solo si está en la
 * instantánea del dispositivo (de ahí sale su lista de precios). Un cliente que no está cobra por
 * el camino online.
 */
export function clienteAdmitidoOffline(
  cliente: { id?: number; esConsumidorFinal: boolean } | null,
  instantanea: Pick<InstantaneaDePos, 'clientes'> | null,
): boolean {
  if (!cliente) return false
  if (cliente.esConsumidorFinal) return true
  return instantanea !== null && cliente.id !== undefined && instantanea.clientes.some((c) => c.idCliente === cliente.id)
}
