/**
 * Ajuste manual de precio por línea (porcentaje con signo: negativo = descuento, positivo =
 * recargo). Todo puro y sin DOM: es la única fuente de la cuenta que comparten la vista previa del
 * carrito, el comprobante sintético de una venta offline y las plantillas, para que ninguno pueda
 * divergir del otro. El servidor recalcula el monto con la misma fórmula y es quien manda; acá solo
 * se anticipa lo que va a cobrar.
 */
import { redondearImporte } from '../formato/importes'

/** Magnitud máxima admitida del porcentaje (el rango válido es de -100 a 100, sin incluir el 0). */
export const PORCENTAJE_MAXIMO_DE_AJUSTE = 100

export type TipoDeAjusteManual = 'descuento' | 'recargo'

export type TotalesDeLinea = {
  /** `cantidad × precioOriginal`, antes de ofertas. */
  bruto: number
  /** Descuento por oferta de la línea (`descuentoUnitario × cantidad`), en positivo. */
  descuento: number
  /** `bruto − descuento`: la base sobre la que se aplica el ajuste manual. */
  neto: number
  /** Monto con signo del ajuste manual; `0` sin ajuste. */
  ajuste: number
  /** `neto + ajuste`. */
  total: number
}

/**
 * Monto del ajuste sobre `neto`, redondeado half-away-from-zero a 2 decimales. Opera sobre enteros
 * (centavos × centésimas de punto porcentual) y recién divide al final: `neto × porcentaje / 100`
 * en punto flotante puede caer apenas por debajo de un empate exacto (p. ej. 1,005) y redondear
 * para el otro lado que el servidor, que calcula en decimal. `neto` y `porcentaje` llegan con 2
 * decimales como máximo (el validador de entrada lo garantiza).
 */
export function calcularAjusteManual(neto: number, porcentaje: number | null | undefined): number {
  if (porcentaje === null || porcentaje === undefined) return 0
  const centavos = Math.round(neto * 100)
  const centesimas = Math.round(porcentaje * 100)
  return redondearImporte((centavos * centesimas) / 1_000_000)
}

/** Fórmula de línea del servidor (`CalculadorDeTotales`): cada paso se redondea a 2 decimales
 * antes de pasar al siguiente, y el ajuste se aplica sobre el neto POSTERIOR a las ofertas. */
export function calcularTotalesDeLinea(params: {
  cantidad: number
  precioOriginal: number
  descuentoUnitario: number
  porcentaje: number | null | undefined
}): TotalesDeLinea {
  const bruto = redondearImporte(params.cantidad * params.precioOriginal)
  const descuento = redondearImporte(params.descuentoUnitario * params.cantidad)
  const neto = redondearImporte(bruto - descuento)
  const ajuste = calcularAjusteManual(neto, params.porcentaje)
  return { bruto, descuento, neto, ajuste, total: redondearImporte(neto + ajuste) }
}

/**
 * Separa los ajustes por el SIGNO DEL PORCENTAJE (no del monto): el descuento manual suma
 * `−ajuste` de las líneas con porcentaje negativo y el recargo suma `ajuste` de las de porcentaje
 * positivo, ambos en positivo en una venta normal. Un recargo nunca se netea contra un descuento,
 * así que un comprobante con los dos muestra los dos.
 */
export function totalesDeAjusteManual(lineas: readonly { porcentaje: number | null | undefined; ajuste: number }[]): {
  descuentoManualTotal: number
  recargoManualTotal: number
} {
  let descuento = 0
  let recargo = 0
  for (const { porcentaje, ajuste } of lineas) {
    if (porcentaje === null || porcentaje === undefined) continue
    if (porcentaje < 0) descuento -= ajuste
    else if (porcentaje > 0) recargo += ajuste
  }
  return { descuentoManualTotal: redondearImporte(descuento), recargoManualTotal: redondearImporte(recargo) }
}

export function tipoDeAjuste(porcentaje: number): TipoDeAjusteManual {
  return porcentaje < 0 ? 'descuento' : 'recargo'
}

/** Rótulo corto del ajuste de una línea según el signo de su porcentaje. */
export function rotuloDeAjusteManual(porcentaje: number): string {
  return tipoDeAjuste(porcentaje) === 'descuento' ? 'Desc. manual' : 'Recargo'
}

/** Magnitud del porcentaje sin signo y con coma decimal, sin ceros de relleno: `10`, `12,5`. */
export function formatearPorcentajeDeAjuste(porcentaje: number): string {
  return String(Math.abs(porcentaje)).replace('.', ',')
}

export type ResultadoDeValidacionDePorcentaje = { ok: true; porcentaje: number } | { ok: false; mensaje: string }

const PATRON_DE_PORCENTAJE = /^\d{1,3}(?:[.,]\d{1,2})?$/

/**
 * Valida el texto que tipea el cajero y le pone el signo según el tipo elegido. Se escribe la
 * magnitud sola (el tipo lo da el selector Descuento/Recargo): mayor que 0, hasta 100, hasta 2
 * decimales, con coma o punto decimal.
 */
export function validarPorcentajeDeAjuste(texto: string, tipo: TipoDeAjusteManual): ResultadoDeValidacionDePorcentaje {
  const limpio = texto.trim()
  if (limpio === '') return { ok: false, mensaje: 'Ingresá el porcentaje del ajuste.' }
  if (!PATRON_DE_PORCENTAJE.test(limpio)) {
    return { ok: false, mensaje: 'Ingresá el porcentaje sin signo y con hasta 2 decimales (por ejemplo 10 o 12,5).' }
  }
  const magnitud = Number(limpio.replace(',', '.'))
  if (magnitud <= 0 || magnitud > PORCENTAJE_MAXIMO_DE_AJUSTE) {
    return { ok: false, mensaje: `El porcentaje debe ser mayor que 0 y como máximo ${PORCENTAJE_MAXIMO_DE_AJUSTE}.` }
  }
  return { ok: true, porcentaje: tipo === 'descuento' ? -magnitud : magnitud }
}

const MENSAJES_DE_RECHAZO_DE_AJUSTE: Record<string, string> = {
  ajuste_manual_invalido:
    'El servidor rechazó un ajuste manual: el porcentaje de cada línea debe ser distinto de 0, entre -100 y 100 y con hasta 2 decimales. Revisá los ajustes del carrito.',
}

/** Mensaje para el cajero cuando el servidor rechaza una venta por su ajuste manual; `null` si el
 * código no es de este tema (el llamador sigue con su mensaje habitual). */
export function mensajeDeRechazoDeAjusteManual(codigo: string): string | null {
  return MENSAJES_DE_RECHAZO_DE_AJUSTE[codigo] ?? null
}
