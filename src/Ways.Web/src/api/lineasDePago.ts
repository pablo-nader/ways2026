import type { LineaDePagoListada, LineaDePagoSolicitud } from './tipos'
import { estaEnRangoSoportado, formatearImporte, redondearImporte } from '../formato/importes'

export const MAXIMO_DE_LINEAS_DE_PAGO = 20
export const MAXIMO_DE_REFERENCIA_DE_LINEA = 100

/** Una línea en edición. `clave` identifica la fila en pantalla (no viaja al servidor). */
export type LineaDePagoEditable = {
  clave: number
  idMedioPago: number | ''
  importe: number | null
  referencia: string
}

export function lineaDePagoVacia(clave = 1, importe: number | null = null): LineaDePagoEditable {
  return { clave, idMedioPago: '', importe, referencia: '' }
}

function siguienteClave(lineas: readonly LineaDePagoEditable[]): number {
  return lineas.reduce((maxima, l) => Math.max(maxima, l.clave), 0) + 1
}

/** Agrega una línea al final; con el tope alcanzado devuelve la lista sin tocar. */
export function agregarLineaDePago(lineas: readonly LineaDePagoEditable[], importe: number | null = null): LineaDePagoEditable[] {
  if (lineas.length >= MAXIMO_DE_LINEAS_DE_PAGO) return [...lineas]
  return [...lineas, lineaDePagoVacia(siguienteClave(lineas), importe)]
}

/** Quita una línea por clave; la última que queda no se quita (un pago siempre lleva al menos una). */
export function quitarLineaDePago(lineas: readonly LineaDePagoEditable[], clave: number): LineaDePagoEditable[] {
  if (lineas.length <= 1) return [...lineas]
  return lineas.filter((l) => l.clave !== clave)
}

export function cambiarLineaDePago(
  lineas: readonly LineaDePagoEditable[],
  clave: number,
  cambios: Partial<Omit<LineaDePagoEditable, 'clave'>>,
): LineaDePagoEditable[] {
  return lineas.map((l) => (l.clave === clave ? { ...l, ...cambios } : l))
}

/** Suma de los importes cargados, redondeada a centavos; una línea sin importe suma 0. */
export function totalDeLineasDePago(lineas: readonly LineaDePagoEditable[]): number {
  return redondearImporte(lineas.reduce((suma, l) => suma + (l.importe ?? 0), 0))
}

/** Lo que falta (positivo) o sobra (negativo) para llegar al objetivo. */
export function diferenciaContraObjetivo(objetivo: number, lineas: readonly LineaDePagoEditable[]): number {
  return redondearImporte(objetivo - totalDeLineasDePago(lineas))
}

function tieneAMasDeDosDecimales(importe: number): boolean {
  return redondearImporte(importe, 2) !== importe
}

/** Motivo por el que una línea no se puede enviar, o `null` si es válida. El servidor vuelve a validar todo. */
export function errorDeLineaDePago(linea: LineaDePagoEditable): string | null {
  if (linea.idMedioPago === '') return 'elegí el medio de pago.'
  if (linea.importe === null || !(linea.importe > 0)) return 'ingresá un importe mayor a 0.'
  if (tieneAMasDeDosDecimales(linea.importe)) return 'el importe admite hasta 2 decimales.'
  if (!estaEnRangoSoportado(linea.importe)) return 'el importe es demasiado grande.'
  if (linea.referencia.trim().length > MAXIMO_DE_REFERENCIA_DE_LINEA) {
    return `la referencia admite hasta ${MAXIMO_DE_REFERENCIA_DE_LINEA} caracteres.`
  }
  return null
}

/** Primer problema de la lista de líneas (con el número de línea cuando es de una en particular), o `null`. */
export function errorDeLineasDePago(lineas: readonly LineaDePagoEditable[]): string | null {
  if (lineas.length === 0) return 'Agregá al menos un medio de pago.'
  if (lineas.length > MAXIMO_DE_LINEAS_DE_PAGO) return `Un pago admite hasta ${MAXIMO_DE_LINEAS_DE_PAGO} líneas.`
  for (const [indice, linea] of lineas.entries()) {
    const motivo = errorDeLineaDePago(linea)
    if (motivo !== null) return `Línea ${indice + 1}: ${motivo}`
  }
  return null
}

export function lineasDePagoValidas(lineas: readonly LineaDePagoEditable[]): boolean {
  return errorDeLineasDePago(lineas) === null
}

/** Cuerpo de las líneas: la referencia en blanco viaja `null`. El llamador garantiza `lineasDePagoValidas`. */
export function aLineasDeSolicitud(lineas: readonly LineaDePagoEditable[]): LineaDePagoSolicitud[] {
  return lineas.map((l) => {
    const referencia = l.referencia.trim()
    return { idMedioPago: l.idMedioPago === '' ? 0 : l.idMedioPago, importe: l.importe ?? 0, referencia: referencia === '' ? null : referencia }
  })
}

/** Precarga la edición con el reparto que el servidor devolvió. */
export function lineasEditablesDesdeListado(lineas: readonly LineaDePagoListada[]): LineaDePagoEditable[] {
  return lineas.map((l, indice) => ({
    clave: indice + 1,
    idMedioPago: l.idMedioPago,
    importe: l.importe,
    referencia: l.referencia ?? '',
  }))
}

type PagoConMedios = {
  nombreMedioPago?: string | null
  lineas?: readonly LineaDePagoListada[]
}

const MEDIO_NO_DISPONIBLE = '(no disponible)'

/** Texto de la columna "Medio de pago": con varias líneas, el reparto ("Efectivo $600,00 · Transferencia $400,00");
 * con una sola (o sin líneas, respuestas anteriores) el medio de siempre. */
export function textoDeMediosDelPago(pago: PagoConMedios): string {
  const lineas = pago.lineas ?? []
  if (lineas.length > 1) {
    return lineas
      .map((l) => `${l.nombreMedioPago ?? MEDIO_NO_DISPONIBLE} ${formatearImporte(l.importe, { simbolo: true })}`)
      .join(' · ')
  }
  return lineas[0]?.nombreMedioPago ?? pago.nombreMedioPago ?? MEDIO_NO_DISPONIBLE
}
