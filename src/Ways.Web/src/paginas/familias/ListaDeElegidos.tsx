import type { ArticuloElegido } from './SelectorDeArticulos'

/** Los artículos que se eligieron para agrupar, cada uno con su "×" para quitarlo. */
export function ListaDeElegidos({
  elegidos,
  bloqueado,
  vacio,
  onQuitar,
}: {
  elegidos: readonly ArticuloElegido[]
  bloqueado: boolean
  /** Lo que se dice cuando no hay ninguno. */
  vacio: string
  onQuitar: (id: number) => void
}) {
  if (elegidos.length === 0) return <p className="text-muted small mb-2">{vacio}</p>

  return (
    <ul className="list-unstyled d-flex flex-wrap gap-2 mb-2">
      {elegidos.map((a) => (
        <li key={a.id}>
          <span className="badge bg-body-secondary text-body border d-flex align-items-center gap-2 py-2 px-2">
            {a.codigoInterno} — {a.nombre}
            <button
              type="button"
              className="btn btn-sm btn-outline-danger py-0 px-1"
              aria-label={`Quitar ${a.codigoInterno} ${a.nombre}`}
              disabled={bloqueado}
              onClick={() => onQuitar(a.id)}
            >
              ×
            </button>
          </span>
        </li>
      ))}
    </ul>
  )
}
