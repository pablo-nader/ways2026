import type { ReactNode } from 'react'
import type { EstadoDeOrden } from './ordenamiento'

type Props = {
  /** Clave de la columna en el mapa de `useOrdenamiento`. */
  columna: string
  orden: EstadoDeOrden
  onAlternar: (columna: string) => void
  /** Clases del `<th>` (por ejemplo `text-end` en las columnas numéricas). */
  className?: string
  children: ReactNode
}

const ARIA_SORT = { ascendente: 'ascending', descendente: 'descending' } as const
const INDICADOR = { ascendente: '▲', descendente: '▼' } as const

/** Encabezado de columna que ordena al activarlo; reemplaza a un `<th>` dentro de un `<thead>` de Bootstrap. */
export function EncabezadoOrdenable({ columna, orden, onAlternar, className, children }: Props) {
  const direccion = orden?.columna === columna ? orden.direccion : null

  return (
    <th scope="col" className={className} aria-sort={direccion ? ARIA_SORT[direccion] : 'none'}>
      <button
        type="button"
        className="btn btn-link p-0 border-0 text-reset text-decoration-none fw-bold text-nowrap"
        onClick={() => onAlternar(columna)}
      >
        {children}
        <span aria-hidden="true" className={`ms-1${direccion ? '' : ' opacity-25'}`}>
          {direccion ? INDICADOR[direccion] : '↕'}
        </span>
      </button>
    </th>
  )
}
