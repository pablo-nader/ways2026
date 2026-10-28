import { BotonIcono } from '../BotonIcono'
import type { DefinicionDeFiltro, ValoresDeFiltro } from './filtros'

type Props = {
  /** Exactamente una entrada por columna del `<thead>`, en su orden: la clave de la columna filtrable o `null` para una
   * celda sin filtro. El botón de limpiar filtros va dentro de la ÚLTIMA celda `null` (la de acciones); sin ninguna celda
   * `null` no se rinde botón. */
  celdas: readonly (string | null)[]
  columnas: Record<string, DefinicionDeFiltro>
  filtros: ValoresDeFiltro
  onCambio: (columna: string, valor: string) => void
  onLimpiar: () => void
  hayFiltros: boolean
  deshabilitado?: boolean
  /** Rinde el botón de limpiar filtros en la última celda `null` de `celdas` (por defecto sí). */
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
  const ultimaNula = celdas.lastIndexOf(null)

  return (
    <tr>
      {celdas.map((clave, posicion) => {
        const columna = clave === null ? undefined : columnas[clave]
        if (clave === null || !columna) {
          const conBoton = conLimpiar && posicion === ultimaNula

          return (
            <th key={posicion} scope="col" className={conBoton ? 'text-end' : undefined}>
              {conBoton && <BotonIcono icono="limpiar" onClick={onLimpiar} disabled={deshabilitado || !hayFiltros} />}
            </th>
          )
        }

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
    </tr>
  )
}
