import type { PuntoVentaListado } from '../api/tipos'

export type ColorDePuntoVenta = 'color_1' | 'color_2'

/**
 * Color de la franja de cabecera según el punto de venta activo (paridad con el legacy, que
 * pinta `color_<id>`). Se decide por la POSICIÓN del punto de venta en la lista ordenada por id:
 * renombrar o agregar puntos de venta no cambia el color de los que ya existían.
 */
export function colorDePuntoVenta(
  puntoVenta: Pick<PuntoVentaListado, 'id'> | null,
  puntosVenta: readonly Pick<PuntoVentaListado, 'id'>[],
): ColorDePuntoVenta {
  if (!puntoVenta) return 'color_1'

  const ordenados = [...puntosVenta].sort((a, b) => a.id - b.id)
  const posicion = ordenados.findIndex((p) => p.id === puntoVenta.id)

  return posicion % 2 === 1 ? 'color_2' : 'color_1'
}
