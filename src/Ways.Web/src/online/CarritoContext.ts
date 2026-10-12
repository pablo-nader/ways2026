import { createContext, useContext } from 'react'

import type { ArticuloParaElCarrito, ComboParaElCarrito, ItemDelCarrito } from './carrito'

/** Las operaciones del carrito. Su identidad no cambia entre renders: se pueden pasar a hijos memoizados. */
export type AccionesDelCarrito = {
  agregar: (articulo: ArticuloParaElCarrito, cantidad?: number) => void
  quitar: (idArticulo: number) => void
  cambiarCantidad: (idArticulo: number, cantidad: number) => void
  /** Pone al día nombre, precio, unidad e imagen de las líneas cuyo artículo figura en `articulos`. */
  sincronizar: (articulos: readonly ArticuloParaElCarrito[]) => void
  agregarCombo: (combo: ComboParaElCarrito, cantidad?: number) => void
  quitarCombo: (idCombo: number) => void
  cambiarCantidadCombo: (idCombo: number, cantidad: number) => void
  /**
   * Pone al día las líneas de combo con la lista de combos de la tienda; las que ya no figuran quedan
   * marcadas como no disponibles. Se llama solo con una lista leída bien: un error de lectura no dice nada.
   */
  sincronizarCombos: (combos: readonly ComboParaElCarrito[]) => void
  vaciar: () => void
}

/**
 * El carrito de la tienda. `cantidad` cuenta las líneas (artículos y combos distintos), no las
 * unidades. `cantidadQueSuma` es lo mismo sin las líneas de combo que ya no están disponibles
 * (se ven en el panel pero no suman al total): es lo que cuenta el botón de la cabecera.
 * `cantidades` y `cantidadesDeCombos` dan la cantidad de cada artículo o combo que está en el
 * carrito (los que no están, no figuran).
 */
export type EstadoDelCarrito = AccionesDelCarrito & {
  items: readonly ItemDelCarrito[]
  cantidad: number
  cantidadQueSuma: number
  total: number
  cantidades: ReadonlyMap<number, number>
  cantidadesDeCombos: ReadonlyMap<number, number>
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
