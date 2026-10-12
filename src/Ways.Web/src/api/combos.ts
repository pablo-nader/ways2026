/**
 * Combos en el carrito del POS: la fórmula de línea con el ahorro de un combo y el rótulo de los combos
 * aplicados. Puro y sin DOM. La fórmula es la única que comparten la vista previa del carrito (online y
 * offline) y el comprobante sintético de una venta offline, para que lo mostrado, lo encolado y lo
 * impreso no puedan diverger; el servidor (`CalculadorDeTotales`) recalcula en decimal y es quien manda.
 */
import { calcularAjusteManual, calcularTotalesDeLinea, type TotalesDeLinea } from './ajusteManual'
import { productoRedondeado, redondearImporte } from '../formato/importes'
import type { ComboAplicadoEnPrevia, ComboDeLinea } from './tipos'

/**
 * Totales de una línea con el combo que la consumió, si lo hay: bruto = `cantidad × precioOriginal`;
 * descuento = descuento por oferta de las unidades NO consumidas (`descuentoUnitario × (cantidad −
 * unidadesConsumidas)`, redondeado) más `ahorro` del combo, que ya es un importe de línea; el ajuste manual
 * se aplica sobre el neto que ya incluye el combo. Sin combo es exactamente `calcularTotalesDeLinea`.
 */
export function calcularTotalesDeLineaConCombo(params: {
  cantidad: number
  precioOriginal: number
  descuentoUnitario: number
  porcentaje: number | null | undefined
  combo?: Pick<ComboDeLinea, 'unidadesConsumidas' | 'ahorro'> | null
}): TotalesDeLinea {
  const { combo, ...resto } = params
  if (!combo) return calcularTotalesDeLinea(resto)

  const bruto = productoRedondeado([params.cantidad, params.precioOriginal])
  const descuentoDeOferta = productoRedondeado([params.descuentoUnitario, params.cantidad - combo.unidadesConsumidas])
  const descuento = redondearImporte(descuentoDeOferta + combo.ahorro)
  const neto = redondearImporte(bruto - descuento)
  const ajuste = calcularAjusteManual(neto, params.porcentaje)
  return { bruto, descuento, neto, ajuste, total: redondearImporte(neto + ajuste) }
}

/** Rótulo de un combo aplicado: `Pancho + Coca × 2`; sin el sufijo cuando se aplicó una sola vez. */
export function rotuloDeComboAplicado(combo: Pick<ComboAplicadoEnPrevia, 'nombre' | 'veces'>): string {
  return combo.veces > 1 ? `${combo.nombre} × ${combo.veces}` : combo.nombre
}
