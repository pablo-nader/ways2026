import { createContext, useMemo, useRef } from 'react'
import type { ReactNode } from 'react'
import type { LineaCarrito } from '../api/carrito'
import type { FilaPago } from '../api/pagos'
import type { ClienteListado, ResultadoDeResolucion } from '../api/tipos'

/**
 * stage-pos-adjustments: ticket en curso de una venta libre, guardado para sobrevivir a una
 * navegación de ida y vuelta (ej. `/pos` → `/caja` → `/pos`) sin perder el carrito — nunca
 * persistido en disco (`localStorage`/IndexedDB), se pierde con un reload/restart entero. Nunca
 * se guarda/restaura bajo `?idPresupuesto=`: esa venta se rehidrata siempre fresca desde el
 * servidor (ver `PantallaPos` en `Pos.tsx`).
 */
export type BorradorDeTicket = {
  lineas: LineaCarrito[]
  precios: Record<number, ResultadoDeResolucion>
  cantidadesEnEdicion: Record<number, string>
  filasPago: FilaPago[]
  /** Próximo valor de `proximaFilaPagoIdRef` — restaurado así en vez de derivarlo de
   * `filasPago` para que una fila agregada después de restaurar nunca reutilice el id de una
   * fila restaurada. */
  proximoIdFilaPago: number
  clienteSeleccionado: ClienteListado | null
}

export type AlmacenDeBorradoresDeTicket = {
  obtener: (clave: string) => BorradorDeTicket | undefined
  guardar: (clave: string, borrador: BorradorDeTicket) => void
  limpiar: (clave: string) => void
}

/**
 * `null` por defecto (ningún `Provider` en el árbol) — todo lo que lee este contexto en
 * `Pos.tsx` cae al comportamiento de siempre (carrito vacío, sin restaurar nada) sin necesitar
 * ninguna rama especial para tests u otros puntos de montaje que no envuelvan este `Provider`.
 */
export const BorradorDeTicketContext = createContext<AlmacenDeBorradoresDeTicket | null>(null)

/**
 * El almacén vive en un `Map` dentro de un `ref` (nunca en `useState`): guardar un borrador no
 * puede forzar un re-render del árbol entero que envuelve — solo el propio efecto de `Pos.tsx`
 * que escribe decide cuándo correr. El valor del contexto se arma una sola vez con `useMemo`
 * (deps vacías) para que sea una referencia estable durante toda la sesión: remontar
 * `PantallaPos` por `key` (cambio de punto de venta o de presupuesto) nunca vacía el almacén,
 * solo una navegación real fuera de este `Provider` (o un reload entero) lo hace.
 */
export function ProveedorDeBorradoresDeTicket({ children }: { children: ReactNode }) {
  const mapaRef = useRef<Map<string, BorradorDeTicket>>(new Map())

  const almacen = useMemo<AlmacenDeBorradoresDeTicket>(
    () => ({
      obtener: (clave) => mapaRef.current.get(clave),
      guardar: (clave, borrador) => {
        mapaRef.current.set(clave, borrador)
      },
      limpiar: (clave) => {
        mapaRef.current.delete(clave)
      },
    }),
    [],
  )

  return <BorradorDeTicketContext.Provider value={almacen}>{children}</BorradorDeTicketContext.Provider>
}
