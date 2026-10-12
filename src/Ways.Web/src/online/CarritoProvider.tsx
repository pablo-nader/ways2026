import type { ReactNode } from 'react'

import { CARRITO_VACIO, CarritoContext } from './CarritoContext'

export function CarritoProvider({ children }: { children: ReactNode }) {
  return <CarritoContext.Provider value={CARRITO_VACIO}>{children}</CarritoContext.Provider>
}
