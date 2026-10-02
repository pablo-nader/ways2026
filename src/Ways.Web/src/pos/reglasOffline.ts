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
 * La instantánea congela el precio contra la lista del Consumidor Final (spec del backend) — un
 * cliente distinto vería precios de walk-in, no los suyos. Solo el Consumidor Final puede vender
 * offline; cualquier otro cliente exige la resolución de precio online real.
 */
export function clienteAdmitidoOffline(cliente: { esConsumidorFinal: boolean } | null): boolean {
  return cliente?.esConsumidorFinal === true
}
