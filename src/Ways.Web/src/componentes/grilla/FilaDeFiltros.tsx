import { BotonIcono } from '../BotonIcono'
import type { DefinicionDeFiltro, ValoresDeFiltro } from './filtros'

type Props = {
  /** Una entrada por columna del `<thead>`, en su orden: la clave de la columna filtrable o `null` para una celda sin
   * filtro (por ejemplo la de acciones). */
  celdas: readonly (string | null)[]
  columnas: Record<string, DefinicionDeFiltro>
  filtros: ValoresDeFiltro
  onCambio: (columna: string, valor: string) => void
  onLimpiar: () => void
  hayFiltros: boolean
  deshabilitado?: boolean
  /** Agrega al final una celda con el botón de limpiar filtros (por defecto sí: ocupa el lugar de las acciones). */
  conLimpiar?: boolean
}

/** Segunda fila del `<thead>`: un filtro por columna. Se coloca debajo de la fila de encabezados. */
export function FilaDeFiltros({
  celdas,
  columnas,
  filtros,
  onCambio,
  onLimpiar,
  hayFiltros,
  deshabilitado = false,
  conLimpiar = true,
}: Props) {
  return (
    <tr>
      {celdas.map((clave, posicion) => {
        const columna = clave === null ? undefined : columnas[clave]
        if (clave === null || !columna) return <th key={posicion} scope="col" />

        const nombre = `Filtrar por ${columna.etiqueta.toLowerCase()}`
        const valor = filtros[clave] ?? ''

        return (
          <th key={clave} scope="col">
            {columna.opciones ? (
              <select
                className="form-select form-select-sm"
                aria-label={nombre}
                value={valor}
                onChange={(e) => onCambio(clave, e.target.value)}
                disabled={deshabilitado}
              >
                <option value="">Todos</option>
                {columna.opciones.map((o) => (
                  <option key={o.valor} value={o.valor}>
                    {o.etiqueta}
                  </option>
                ))}
              </select>
            ) : (
              <input
                type="search"
                className="form-control form-control-sm"
                aria-label={nombre}
                value={valor}
                onChange={(e) => onCambio(clave, e.target.value)}
                disabled={deshabilitado}
              />
            )}
          </th>
        )
      })}
      {conLimpiar && (
        <th scope="col" className="text-end">
          <BotonIcono icono="limpiar" onClick={onLimpiar} disabled={deshabilitado || !hayFiltros} />
        </th>
      )}
    </tr>
  )
}
