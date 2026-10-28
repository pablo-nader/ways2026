import { useCallback, useMemo, useState } from 'react'
import { ordenarFilas, siguienteOrden, type ColumnasOrdenables, type EstadoDeOrden } from './ordenamiento'

/**
 * Orden por columna de las filas ya cargadas. `columnas` tiene que ser estable entre renders (una constante del módulo o
 * un `useMemo`): si cambia en cada render, el orden se recalcula en cada uno.
 *
 * `ordenar` aplica el orden vigente a un subconjunto, para las grillas agrupadas (se ordena dentro de cada grupo y los
 * grupos conservan su posición).
 */
export function useOrdenamiento<T>(filas: readonly T[], columnas: ColumnasOrdenables<T>, inicial: EstadoDeOrden = null) {
  const [orden, setOrden] = useState<EstadoDeOrden>(inicial)

  const alternar = useCallback((columna: string) => setOrden((actual) => siguienteOrden(actual, columna)), [])
  const ordenar = useCallback((grupo: readonly T[]) => ordenarFilas(grupo, columnas, orden), [columnas, orden])
  const ordenadas = useMemo(() => ordenar(filas), [ordenar, filas])

  return { filas: ordenadas, orden, alternar, ordenar }
}
