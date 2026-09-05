import { useContext } from 'react'
import { PuntoVentaContext } from './PuntoVentaContext'

export function usePuntoVenta() {
  const contexto = useContext(PuntoVentaContext)

  if (!contexto) {
    throw new Error('usePuntoVenta tiene que usarse dentro de una PuertaDePuntoVenta.')
  }

  return contexto
}
