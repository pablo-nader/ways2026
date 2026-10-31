import { TAMANIOS_DE_PAGINA_DE_PADRON, ultimaPaginaDePadron } from '../../api/padron'
import { pluralizar } from '../../formato/pluralizar'

type Props = {
  /** Lo que respondió el servidor: la página, el tamaño y el total del conjunto filtrado. */
  pagina: number
  tamanio: number
  total: number
  /** Lo que se cuenta, en singular: "cliente". El plural se deriva solo. */
  unidad: string
  /** Plural de las excepciones a la regla general ("orden" → "órdenes"). */
  unidadPlural?: string
  /** Mientras se carga o una escritura está en vuelo, ningún control mueve la consulta. */
  deshabilitado: boolean
  onCambiarPagina: (delta: number) => void
  onCambiarTamanio: (tamanio: number) => void
}

/** Pie de un padrón paginado en el servidor: página, total, filas por página y Anterior/Siguiente. */
export function PaginadorDePadron({
  pagina,
  tamanio,
  total,
  unidad,
  unidadPlural,
  deshabilitado,
  onCambiarPagina,
  onCambiarTamanio,
}: Props) {
  const ultima = ultimaPaginaDePadron(total, tamanio)

  return (
    <div className="d-flex justify-content-between align-items-center">
      <span className="small text-muted">
        Página {pagina} de {ultima} — {pluralizar(total, unidad, unidadPlural)}
      </span>
      <div className="d-flex gap-2 align-items-center">
        <select
          className="form-select form-select-sm w-auto"
          aria-label="Filas por página"
          value={tamanio}
          disabled={deshabilitado}
          onChange={(e) => onCambiarTamanio(Number(e.target.value))}
        >
          {TAMANIOS_DE_PAGINA_DE_PADRON.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
        <button
          type="button"
          className="btn btn-sm btn-outline-secondary"
          disabled={deshabilitado || pagina <= 1}
          onClick={() => onCambiarPagina(-1)}
        >
          Anterior
        </button>
        <button
          type="button"
          className="btn btn-sm btn-outline-secondary"
          disabled={deshabilitado || pagina >= ultima}
          onClick={() => onCambiarPagina(1)}
        >
          Siguiente
        </button>
      </div>
    </div>
  )
}
