import { createContext, useContext } from 'react'

import type { ArticuloParaElCarrito, ItemDelCarrito } from './carrito'

/** Las operaciones del carrito. Su identidad no cambia entre renders: se pueden pasar a hijos memoizados. */
export type AccionesDelCarrito = {
  agregar: (articulo: ArticuloParaElCarrito, cantidad?: number) => void
  quitar: (idArticulo: number) => void
  cambiarCantidad: (idArticulo: number, cantidad: number) => void
  /** Pone al día nombre, precio, unidad e imagen de las líneas cuyo artículo figura en `articulos`. */
  sincronizar: (articulos: readonly ArticuloParaElCarrito[]) => void
  vaciar: () => void
}

/**
 * El carrito de la tienda. `cantidad` cuenta las líneas (artículos distintos), no las unidades:
 * es lo que muestra el botón de la cabecera. `cantidades` da la cantidad de cada artículo que está
 * en el carrito (los que no están, no figuran).
 */
export type EstadoDelCarrito = AccionesDelCarrito & {
  items: readonly ItemDelCarrito[]
  cantidad: number
  total: number
  cantidades: ReadonlyMap<number, number>
}

export const CarritoContext = createContext<EstadoDelCarrito | null>(null)

/**
 * Las operaciones solas, en un contexto aparte que no cambia cuando cambia el contenido del
 * carrito: quien solo necesita operar (las tarjetas del catálogo) no se vuelve a dibujar por eso.
 */
export const AccionesDelCarritoContext = createContext<AccionesDelCarrito | null>(null)

export function useCarrito(): EstadoDelCarrito {
  const carrito = useContext(CarritoContext)
  if (carrito === null) throw new Error('useCarrito debe usarse dentro de un CarritoProvider')

  return carrito
}

export function useAccionesDelCarrito(): AccionesDelCarrito {
  const acciones = useContext(AccionesDelCarritoContext)
  if (acciones === null) throw new Error('useAccionesDelCarrito debe usarse dentro de un CarritoProvider')

  return acciones
}
