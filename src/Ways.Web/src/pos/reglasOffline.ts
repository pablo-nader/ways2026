/**
 * Reglas duras de la venta offline (stage-pos-venta-offline-web, Parte D/E) — puras, sin I/O, para
 * que la misma condición gatee tanto la UI (deshabilitar una opción) como el encolado real en el
 * outbox (nunca solo una de las dos, spec `react-async-state` regla 7: "una copia claims-match
 * -code sin el enforcement real es mentira").
 */
import type { ComportamientoMedioPago } from '../api/tipos'

/**
 * Medios que la venta local admite sin validar nada contra el servidor (decisión del dueño):
 * `Efectivo` y `Electronico` (tarjeta, transferencia — se cobran en una terminal externa, así que
 * registrar el pago no necesita ninguna validación del servidor). `CuentaCorriente` queda afuera:
 * la instantánea no puede validar el límite de crédito del cliente.
 */
export function medioAdmitidoOffline(comportamiento: ComportamientoMedioPago): boolean {
  return comportamiento === 'Efectivo' || comportamiento === 'Electronico'
}

export function pagosAdmitidosOffline(pagos: readonly { comportamiento: ComportamientoMedioPago }[]): boolean {
  return pagos.length > 0 && pagos.every((p) => medioAdmitidoOffline(p.comportamiento))
}

/**
 * Solo el Consumidor Final se vende sin el servidor. La instantánea ya trae el precio de cada
 * cliente en su lista (sirve para la vista previa), pero la venta local a otro cliente todavía no
 * está habilitada: cualquier otro cliente cobra por el camino online.
 */
export function clienteAdmitidoOffline(cliente: { esConsumidorFinal: boolean } | null): boolean {
  return cliente?.esConsumidorFinal === true
}
