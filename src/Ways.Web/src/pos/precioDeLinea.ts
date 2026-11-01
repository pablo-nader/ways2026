import { comboVigenteDeLinea } from '../api/combos'
import type { ResultadoDeResolucion } from '../api/tipos'
import { formatearImporte } from '../formato/importes'
import { formatearNumeroDecimal } from '../formato/numeros'

/** Cuánto de una línea cubre la oferta "solo múltiplos": `unidadesConOferta` a `precioConOferta` y el resto
 * (`unidadesSinOferta`) a `precioSinOferta`. */
export type DesgloseDeOferta = {
  unidadesConOferta: number
  precioConOferta: number
  unidadesSinOferta: number
  precioSinOferta: number
}

const TOLERANCIA = 1e-6

/**
 * Desglose de una línea cuya oferta "solo múltiplos" no alcanza a todas sus unidades, o `null` si el precio unitario
 * resuelto vale para toda la línea (oferta común, o múltiplo exacto) o no se puede reconstruir con certeza.
 *
 * Las ofertas comunes descuentan a todas las unidades fuera de combo; las "solo múltiplos" suman su descuento solo a las
 * cubiertas. Las unidades cubiertas salen entonces del descuento real de la línea (`descuentoDeLinea`), que ya trae el
 * reparto por familia, no de la cantidad de la línea sola.
 */
export function desgloseDeOferta(cantidad: number, resultado: ResultadoDeResolucion | undefined): DesgloseDeOferta | null {
  if (!resultado || resultado.descuentoDeLinea === undefined) return null
  if (resultado.precioOriginal === null || resultado.precioFinal === null) return null

  const descuentoEstricto = resultado.aplicadas.reduce((suma, a) => suma + (a.multiploDe != null ? a.descuentoUnitario : 0), 0)
  if (descuentoEstricto <= 0) return null

  const consumidas = comboVigenteDeLinea(cantidad, resultado.combo)?.unidadesConsumidas ?? 0
  const unidades = cantidad - consumidas
  if (unidades <= 0) return null

  const descuentoComun = resultado.aplicadas.reduce((suma, a) => suma + (a.multiploDe == null ? a.descuentoUnitario : 0), 0)
  const cubiertas = (resultado.descuentoDeLinea - unidades * descuentoComun) / descuentoEstricto
  const redondeadas = Math.round(cubiertas)
  if (Math.abs(cubiertas - redondeadas) > TOLERANCIA || redondeadas < 0 || redondeadas >= unidades) return null

  const precioSinOferta = resultado.precioFinal + descuentoEstricto
  if (precioSinOferta > resultado.precioOriginal - descuentoComun + TOLERANCIA) return null

  return {
    unidadesConOferta: redondeadas,
    precioConOferta: resultado.precioFinal,
    unidadesSinOferta: unidades - redondeadas,
    precioSinOferta,
  }
}

/** "2 × $ 3.750,00 + 1 × $ 4.000,00"; sin unidades cubiertas, solo el tramo a precio regular. */
export function textoDeDesglose(desglose: DesgloseDeOferta): string {
  const tramo = (unidades: number, precio: number) => `${formatearNumeroDecimal(unidades)} × ${formatearImporte(precio, { simbolo: true })}`
  const sinOferta = tramo(desglose.unidadesSinOferta, desglose.precioSinOferta)
  return desglose.unidadesConOferta === 0 ? sinOferta : `${tramo(desglose.unidadesConOferta, desglose.precioConOferta)} + ${sinOferta}`
}
