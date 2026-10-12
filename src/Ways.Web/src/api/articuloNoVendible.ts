import { ErrorApi } from './cliente'

/** `409` de ventas, presupuestos y remitos cuando una línea lleva un artículo con "Disponible a la venta" apagado. */
export const CODIGO_ARTICULO_NO_VENDIBLE = 'articulo_no_vendible'

const COPIA_SIN_DETALLE = 'Hay un artículo que no está disponible a la venta.'

/**
 * Copia del rechazo `articulo_no_vendible`, o `null` si el error es otro. El servidor ya escribe el mensaje en español y
 * nombra el artículo por su código interno ("El artículo A0001 no está disponible a la venta."): ese texto se usa tal
 * cual, y solo si viene vacío se cae a una frase genérica. Se le suma qué hacer, que el servidor no dice.
 */
export function copiaDeArticuloNoVendible(error: unknown): string | null {
  if (!(error instanceof ErrorApi) || error.codigo !== CODIGO_ARTICULO_NO_VENDIBLE) return null

  return `${error.message.trim() || COPIA_SIN_DETALLE} Sacalo para poder continuar.`
}
