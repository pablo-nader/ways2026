/**
 * Reglas duras de la venta offline (stage-pos-venta-offline-web, Parte D/E) — puras, sin I/O, para
 * que la misma condición gatee tanto la UI (deshabilitar una opción) como el encolado real en el
 * outbox (nunca solo una de las dos, spec `react-async-state` regla 7: "una copia claims-match
 * -code sin el enforcement real es mentira").
 */
import type { ComportamientoMedioPago } from '../api/tipos'

/**
 * "Offline es solo efectivo" (decisión del dueño) — la instantánea no puede validar el límite de
 * crédito de cuenta corriente contra el servidor, y ningún otro medio no-efectivo (`Electronico`:
 * tarjeta, transferencia) tiene una razón para operar sin conexión tampoco; la regla es deliberada
 * y simple, no "cuenta corriente en particular". Un medio Efectivo es el único admitido.
 */
export function medioAdmitidoOffline(comportamiento: ComportamientoMedioPago): boolean {
  return comportamiento === 'Efectivo'
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
