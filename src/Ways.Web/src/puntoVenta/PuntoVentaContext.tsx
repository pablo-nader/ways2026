import { createContext } from 'react'
import type { PuntoVentaListado } from '../api/tipos'

export type EstadoDePuntoVenta = {
  puntosVenta: PuntoVentaListado[]
  puntoVenta: PuntoVentaListado | null
  /** Cambia el punto de venta activo; un id que no está en la lista se ignora. */
  elegir: (id: number) => void
  /** Vuelve a pedir la lista y reconcilia la selección; si falla, rechaza sin tocar el estado. */
  recargar: () => Promise<void>
}

export const PuntoVentaContext = createContext<EstadoDePuntoVenta | null>(null)
