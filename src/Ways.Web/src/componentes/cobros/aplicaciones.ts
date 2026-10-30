import type { AplicacionDeCobroSolicitada, VentaPendienteDeCobro } from '../../api/tipos'
import { formatearImporte } from '../../formato/importes'

/** Importe elegido por venta, indexado por el id de la venta. Una venta sin clave (o con 0) no recibe nada. */
export type ImportesPorVenta = Record<number, number | null>

/** Tope de ventas por solicitud (espejo del límite del servidor: `aplicacion_demasiadas_ventas`). */
export const MAXIMO_DE_VENTAS_POR_APLICACION = 20

function aCentavos(valor: number): number {
  return Math.round(valor * 100)
}

function deCentavos(centavos: number): number {
  return centavos / 100
}

function compararPorAntiguedad(a: VentaPendienteDeCobro, b: VentaPendienteDeCobro): number {
  return Date.parse(a.fecha) - Date.parse(b.fecha) || a.id - b.id
}

/**
 * Reparte `fondos` entre las ventas más viejas primero (por fecha y, a igual fecha, por id), llenando el saldo pendiente
 * de cada una antes de pasar a la siguiente. Devuelve solo las ventas que reciben algo; lo que sobra queda sin aplicar.
 * Opera en centavos enteros para no arrastrar error de coma flotante.
 */
export function repartirFifo(ventas: VentaPendienteDeCobro[], fondos: number): ImportesPorVenta {
  const reparto: ImportesPorVenta = {}
  let restante = Number.isFinite(fondos) ? aCentavos(fondos) : 0
  if (restante <= 0) return reparto

  for (const venta of [...ventas].sort(compararPorAntiguedad)) {
    if (restante <= 0) break
    const saldo = aCentavos(venta.saldoPendiente)
    if (saldo <= 0) continue
    const tomado = Math.min(saldo, restante)
    reparto[venta.id] = deCentavos(tomado)
    restante -= tomado
  }
  return reparto
}

/** Suma de lo cargado sobre las ventas que siguen pendientes (un importe de una venta que ya no figura no cuenta). */
export function totalAplicado(ventas: VentaPendienteDeCobro[], importes: ImportesPorVenta): number {
  let centavos = 0
  for (const venta of ventas) {
    const importe = importes[venta.id]
    if (importe !== null && importe !== undefined && importe > 0) centavos += aCentavos(importe)
  }
  return deCentavos(centavos)
}

/** Las ventas con un importe mayor a cero, en el orden de la lista. */
export function ventasConImporte(ventas: VentaPendienteDeCobro[], importes: ImportesPorVenta): VentaPendienteDeCobro[] {
  return ventas.filter((v) => (importes[v.id] ?? 0) > 0)
}

/**
 * Primer motivo por el que lo cargado no se puede enviar, o `null` si sirve (o si no se cargó nada). Espejo local de las
 * reglas del servidor, nunca autoritativo: el servidor vuelve a validar.
 */
export function validarAplicaciones(
  ventas: VentaPendienteDeCobro[],
  importes: ImportesPorVenta,
  tope: number,
): string | null {
  const elegidas = ventasConImporte(ventas, importes)
  if (elegidas.length === 0) return null

  if (elegidas.length > MAXIMO_DE_VENTAS_POR_APLICACION) {
    return `Se puede aplicar a lo sumo a ${MAXIMO_DE_VENTAS_POR_APLICACION} ventas por vez.`
  }
  for (const venta of elegidas) {
    const importe = importes[venta.id] as number
    if (aCentavos(importe) > aCentavos(venta.saldoPendiente)) {
      return `El importe de la venta ${venta.numeroVisible} supera su saldo pendiente (${formatearImporte(venta.saldoPendiente, { simbolo: true })}).`
    }
  }
  const aplicado = totalAplicado(ventas, importes)
  if (aCentavos(aplicado) > aCentavos(tope)) {
    return `Lo aplicado (${formatearImporte(aplicado, { simbolo: true })}) supera el importe disponible (${formatearImporte(tope, { simbolo: true })}).`
  }
  return null
}

/** Cuerpo `aplicaciones` listo para enviar: solo ventas con importe, en el orden de la lista. */
export function aAplicacionesSolicitadas(
  ventas: VentaPendienteDeCobro[],
  importes: ImportesPorVenta,
): AplicacionDeCobroSolicitada[] {
  return ventasConImporte(ventas, importes).map((v) => ({
    idComprobanteVenta: v.id,
    importe: importes[v.id] as number,
  }))
}
