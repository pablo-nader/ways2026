import { pluralizar } from '../../formato/pluralizar'

const TAMANIOS_DE_PAGINA = [25, 50, 100]

type Props = {
  pagina: number
  tamanio: number
  total: number
  /** Sustantivo en singular de lo que se lista ("venta", "retiro"). */
  sustantivo: string
  /** Nombre accesible del selector de tamaño de página ("Ventas por página"). */
  etiquetaDeTamanio: string
  /** `true` mientras hay una consulta en vuelo o una escritura pendiente: nada del paginador se puede accionar. */
  inerte: boolean
  onTamanio: (tamanio: number) => void
  onPagina: (delta: number) => void
}

/** Pie de una grilla paginada en el servidor: "Página N de M — X filas", tamaño de página y Anterior/Siguiente. */
export function PaginadorDeServidor({ pagina, tamanio, total, sustantivo, etiquetaDeTamanio, inerte, onTamanio, onPagina }: Props) {
  const totalPaginas = Math.max(1, Math.ceil(total / tamanio))

  return (
    <div className="d-flex justify-content-between align-items-center">
      <span className="small text-muted">
        Página {pagina} de {totalPaginas} — {pluralizar(total, sustantivo)}
      </span>
      <div className="d-flex gap-2 align-items-center">
        <select
          className="form-select form-select-sm w-auto"
          aria-label={etiquetaDeTamanio}
          value={tamanio}
          disabled={inerte}
          onChange={(e) => onTamanio(Number(e.target.value))}
        >
          {TAMANIOS_DE_PAGINA.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
        <button type="button" className="btn btn-sm btn-outline-secondary" disabled={pagina <= 1 || inerte} onClick={() => onPagina(-1)}>
          Anterior
        </button>
        <button
          type="button"
          className="btn btn-sm btn-outline-secondary"
          disabled={pagina >= totalPaginas || inerte}
          onClick={() => onPagina(1)}
        >
          Siguiente
        </button>
      </div>
    </div>
  )
}
