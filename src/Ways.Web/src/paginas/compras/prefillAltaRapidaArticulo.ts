import { redondearImporte } from '../../formato/importes'

export type PrefillDeAltaRapidaDeArticulo = {
  nombre: string
  codigoProveedor: string
}

/**
 * Decide si lo tipeado en el buscador es un nombre o un código del proveedor: es nombre si tiene
 * un espacio o alguna palabra formada solo por letras de 4 o más caracteres; si no (p. ej.
 * `AB-1234`, `7790001`), es un código. Es una sugerencia para precargar el alta rápida, nunca una
 * validación: el operador puede corregir ambos campos.
 */
export function prefillDeAltaRapidaDeArticulo(termino: string): PrefillDeAltaRapidaDeArticulo {
  const limpio = termino.trim()
  const esNombre = /\s/.test(limpio) || /^\p{L}{4,}$/u.test(limpio)
  return esNombre ? { nombre: limpio, codigoProveedor: '' } : { nombre: '', codigoProveedor: limpio }
}

/**
 * El costo de la línea admite 4 decimales pero `articulos.costo_lista` es `numeric(14,2)`: se
 * redondea al precargar el alta rápida para que lo que se ve en el campo sea lo que se guarda.
 */
export function costoDeListaDeAltaRapida(costoDeLaLinea: number | null): number | null {
  return costoDeLaLinea === null ? null : redondearImporte(costoDeLaLinea, 2)
}
