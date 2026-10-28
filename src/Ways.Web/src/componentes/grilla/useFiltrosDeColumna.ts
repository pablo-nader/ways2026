import { useCallback, useMemo, useState } from 'react'
import { filtrarFilas, hayFiltrosActivos, type ColumnasFiltrables, type ValoresDeFiltro } from './filtros'

/** Filtros por columna sobre las filas ya cargadas. `columnas` tiene que ser estable entre renders. */
export function useFiltrosDeColumna<T>(filas: readonly T[], columnas: ColumnasFiltrables<T>) {
  const [filtros, setFiltros] = useState<ValoresDeFiltro>({})

  const cambiar = useCallback((columna: string, valor: string) => setFiltros((previos) => ({ ...previos, [columna]: valor })), [])
  const limpiar = useCallback(() => setFiltros({}), [])
  const filtradas = useMemo(() => filtrarFilas(filas, columnas, filtros), [filas, columnas, filtros])

  return { filas: filtradas, filtros, cambiar, limpiar, hayFiltros: hayFiltrosActivos(filtros) }
}
