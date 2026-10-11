/**
 * Ajuste manual de precio por línea (porcentaje con signo: negativo = descuento, positivo =
 * recargo). Todo puro y sin DOM. La cuenta (`calcularTotalesDeLinea`, `totalesDeAjusteManual`) es
 * la única fuente que comparten la vista previa del carrito y el comprobante sintético de una venta
 * offline, para que ninguno pueda divergir del otro; la validación del porcentaje con signo que tipea
 * el cajero en la grilla y los rótulos del porcentaje viven acá también. Las plantillas de impresión usan solo los rótulos
 * y el formato del porcentaje (`rotuloDeAjusteManual`, `formatearPorcentajeDeAjuste`), nunca la
 * cuenta: imprimen los montos que ya trae el comprobante. El servidor recalcula cada monto con la
 * misma fórmula, en decimal, y es quien manda; acá solo se anticipa lo que va a cobrar.
 */
import { productoAPeso, productoDeDiferenciaAPeso, redondearImporte } from '../formato/importes'

/** Magnitud máxima admitida del porcentaje (el rango válido es de -100 a 100; el 0 no es un ajuste). */
export const PORCENTAJE_MAXIMO_DE_AJUSTE = 100

export type TipoDeAjusteManual = 'descuento' | 'recargo'

export type TotalesDeLinea = {
  /** `cantidad × precioOriginal` al peso, antes de ofertas. */
  bruto: number
  /** Descuento por oferta de la línea (`bruto − neto`), en positivo en una venta normal. */
  descuento: number
  /** `bruto − descuento`: la base sobre la que se aplica el ajuste manual. */
  neto: number
  /** Monto con signo del ajuste manual; `0` sin ajuste. */
  ajuste: number
  /** `neto + ajuste`. */
  total: number
}

/**
 * Monto del ajuste sobre `neto` (`neto × porcentaje / 100`), redondeado half-away-from-zero al
 * peso entero con aritmética decimal exacta (`productoAPeso`): dividir por 100 en punto flotante
 * pierde empates exactos (375 × 9,2 % es 34,5 y el flotante da 34.49999999999999), y el servidor,
 * que calcula en decimal, redondea el empate hacia arriba. El 100 entra como el factor `0.01`, que
 * es exacto en decimal.
 */
export function calcularAjusteManual(neto: number, porcentaje: number | null | undefined): number {
  if (porcentaje === null || porcentaje === undefined) return 0
  return productoAPeso([neto, porcentaje, 0.01])
}

/** Fórmula de línea del servidor (`CalculadorDeTotales`), toda en pesos enteros: el bruto es
 * `cantidad × precio` redondeado al peso, el neto es el neto VERDADERO `cantidad × (precio −
 * descuento unitario)` redondeado una sola vez, el descuento de la línea es `bruto − neto` (nunca
 * una cifra redondeada por su cuenta) y el ajuste se aplica sobre ese neto POSTERIOR a las
 * ofertas. Los productos son decimales exactos, con cualquier cantidad de decimales por unidad. */
export function calcularTotalesDeLinea(params: {
  cantidad: number
  precioOriginal: number
  descuentoUnitario: number
  porcentaje: number | null | undefined
}): TotalesDeLinea {
  const bruto = productoAPeso([params.cantidad, params.precioOriginal])
  const neto = productoDeDiferenciaAPeso(params.cantidad, params.precioOriginal, params.descuentoUnitario)
  const descuento = bruto - neto
  const ajuste = calcularAjusteManual(neto, params.porcentaje)
  return { bruto, descuento, neto, ajuste, total: neto + ajuste }
}

/**
 * Separa los ajustes (ya enteros, salen de `calcularAjusteManual`) por el SIGNO DEL PORCENTAJE (no del monto): el descuento manual suma
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

/** Porcentaje con signo para mostrar en el input de la grilla: el descuento lleva `-` y el recargo
 * va sin `+`, con coma decimal y sin ceros de relleno (`-10`, `12,5`). */
export function formatearPorcentajeDeAjusteConSigno(porcentaje: number): string {
  return String(porcentaje).replace('.', ',')
}

/** `porcentaje: null` es "sin ajuste" (texto vacío o cero): quien lo recibe quita el ajuste de la línea. */
export type ResultadoDeValidacionDePorcentaje = { ok: true; porcentaje: number | null } | { ok: false; mensaje: string }

const PATRON_DE_PORCENTAJE = /^[+-]?\d{1,3}(?:[.,]\d{1,2})?$/

/**
 * Valida el texto que tipea el cajero en la columna "Ajuste %": negativo = descuento, positivo (con
 * o sin `+`) = recargo, hasta 2 decimales con coma o punto, de -100 a 100. Vacío o cero no es un
 * error: significa quitar el ajuste.
 */
export function validarPorcentajeDeAjuste(texto: string): ResultadoDeValidacionDePorcentaje {
  const limpio = texto.trim()
  if (limpio === '') return { ok: true, porcentaje: null }
  if (!PATRON_DE_PORCENTAJE.test(limpio)) {
    return {
      ok: false,
      mensaje: 'Ingresá el porcentaje con hasta 2 decimales: negativo para descuento, positivo para recargo (por ejemplo -10 o 12,5).',
    }
  }
  const porcentaje = Number(limpio.replace(',', '.'))
  if (Math.abs(porcentaje) > PORCENTAJE_MAXIMO_DE_AJUSTE) {
    return { ok: false, mensaje: `El porcentaje debe estar entre -${PORCENTAJE_MAXIMO_DE_AJUSTE} y ${PORCENTAJE_MAXIMO_DE_AJUSTE}.` }
  }
  return { ok: true, porcentaje: porcentaje === 0 ? null : porcentaje }
}

const CODIGO_DE_AJUSTE_INVALIDO = 'ajuste_manual_invalido'

const REGLA_DEL_PORCENTAJE_DE_AJUSTE = 'el porcentaje de cada línea debe ser distinto de 0, entre -100 y 100 y con hasta 2 decimales'

/** Mensaje para el cajero cuando el servidor rechaza al COBRAR una venta por su ajuste manual: el
 * carrito sigue en pantalla, así que le indica dónde corregirlo. `null` si el código no es de este
 * tema (el llamador sigue con su mensaje habitual). */
export function mensajeDeRechazoDeAjusteManual(codigo: string): string | null {
  if (codigo !== CODIGO_DE_AJUSTE_INVALIDO) return null
  return `El servidor rechazó un ajuste manual: ${REGLA_DEL_PORCENTAJE_DE_AJUSTE}. Revisá los ajustes del carrito.`
}

/** Mensaje de la venta archivada cuando el servidor rechaza al DRENAR la cola una venta offline por
 * su ajuste manual: la venta ya se cobró y su carrito ya no existe, así que no hay nada que
 * revisar en pantalla y el mensaje solo informa. `null` si el código no es de este tema. */
export function mensajeDeRechazoDeAjusteManualAlDrenar(codigo: string): string | null {
  if (codigo !== CODIGO_DE_AJUSTE_INVALIDO) return null
  return `el servidor rechazó el ajuste manual de alguna línea de esta venta, que ya estaba cobrada: ${REGLA_DEL_PORCENTAJE_DE_AJUSTE}.`
}
