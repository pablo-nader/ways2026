import { createContext, useContext } from 'react'

import type { ArticuloParaElCarrito, ItemDelCarrito } from './carrito'

/**
 * El carrito de la tienda. `cantidad` cuenta las líneas (artículos distintos), no las unidades:
 * es lo que muestra el botón de la cabecera.
 */
export type EstadoDelCarrito = {
  items: readonly ItemDelCarrito[]
  cantidad: number
  total: number
  cantidadDe: (idArticulo: number) => number
  agregar: (articulo: ArticuloParaElCarrito, cantidad?: number) => void
  quitar: (idArticulo: number) => void
  cambiarCantidad: (idArticulo: number, cantidad: number) => void
  vaciar: () => void
}

export const CarritoContext = createContext<EstadoDelCarrito | null>(null)

export function useCarrito(): EstadoDelCarrito {
  const carrito = useContext(CarritoContext)
  if (carrito === null) throw new Error('useCarrito debe usarse dentro de un CarritoProvider')

  return carrito
}
