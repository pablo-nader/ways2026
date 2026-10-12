import { createContext, useContext } from 'react'

/**
 * Estado del carrito de la tienda. Todavía no tiene operaciones ni forma de ítem: el contenido llega
 * con el catálogo, así que hoy `items` es siempre vacío y `cantidad` 0.
 */
export type EstadoDelCarrito = {
  items: readonly never[]
  cantidad: number
}

export const CARRITO_VACIO: EstadoDelCarrito = { items: [], cantidad: 0 }

export const CarritoContext = createContext<EstadoDelCarrito | null>(null)

export function useCarrito(): EstadoDelCarrito {
  const carrito = useContext(CarritoContext)
  if (carrito === null) throw new Error('useCarrito debe usarse dentro de un CarritoProvider')

  return carrito
}
