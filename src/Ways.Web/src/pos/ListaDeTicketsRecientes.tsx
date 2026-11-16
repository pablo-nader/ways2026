import type { VentaDeTurnoListado } from '../api/tipos'
import { Cargando } from '../componentes/Cargando'
import { fechaHoraATexto } from '../formato/fechas'
import { formatearImporte } from '../formato/importes'

type Props = {
  /** `null` mientras se lee. */
  tickets: readonly VentaDeTurnoListado[] | null
  error: string
  bloqueado: boolean
  alElegir: (idTicket: number) => void
  alReintentar: () => void
}

/** Los últimos tickets del turno para elegir cuál devolver sin tipear el número. */
export function ListaDeTicketsRecientes({ tickets, error, bloqueado, alElegir, alReintentar }: Props) {
  if (error !== '') {
    return (
      <div className="alert alert-warning d-flex justify-content-between align-items-center gap-2 mb-0" role="alert">
        <span>{error}</span>
        <button type="button" className="btn btn-sm btn-outline-secondary" disabled={bloqueado} onClick={alReintentar}>
          Reintentar
        </button>
      </div>
    )
  }
  if (tickets === null) return <Cargando />
  if (tickets.length === 0) return <p className="text-muted mb-0">Todavía no hay ventas en este turno.</p>
  return (
    <div className="table-responsive">
      <table className="table table-sm table-striped table-bordered align-middle mb-0">
        <thead>
          <tr>
            <th>Fecha</th>
            <th>Ticket</th>
            <th>Cliente</th>
            <th className="text-end">Total</th>
            <th>
              <span className="visually-hidden">Elegir</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {tickets.map((venta) => (
            <tr key={venta.id}>
              <td>{fechaHoraATexto(venta.fecha)}</td>
              <td>{venta.numeroVisible}</td>
              <td>{venta.nombreCliente}</td>
              <td className="text-end">{formatearImporte(venta.total, { simbolo: true })}</td>
              <td className="text-end">
                <button
                  type="button"
                  className="btn btn-sm btn-outline-primary"
                  aria-label={`Devolver del ticket ${venta.numeroVisible}`}
                  disabled={bloqueado}
                  onClick={() => alElegir(venta.id)}
                >
                  Elegir
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
