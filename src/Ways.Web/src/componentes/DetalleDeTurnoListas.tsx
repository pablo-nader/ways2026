import type { PagoDeTurno, TicketDeTurno } from '../api/tipos'
import { fechaHoraATexto } from '../formato/fechas'
import { formatearImporte } from '../formato/importes'
import { etiquetaDeOrigenFondos } from '../paginas/utilidadesPagosDelTurno'

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function formatearFechaHora(iso: string): string {
  return fechaHoraATexto(iso)
}

type Props = {
  tickets: TicketDeTurno[]
  pagos: PagoDeTurno[]
}

/** Las listas de tickets y de pagos del detalle de un turno cerrado: las comparten la pantalla
 * "Caja Z" y el modal de detalle de "Cierres de caja" del POS. */
export function DetalleDeTurnoListas({ tickets, pagos }: Props) {
  return (
    <>
      <h6>Tickets</h6>
      <div className="table-responsive mb-3">
        <table className="table table-sm table-striped table-bordered align-middle">
          <thead>
            <tr>
              <th>Número</th>
              <th>Fecha</th>
              <th>Estado</th>
              <th className="text-end">Total</th>
            </tr>
          </thead>
          <tbody>
            {tickets.map((t) => (
              <tr key={t.id}>
                <td>{t.numeroVisible}</td>
                <td>{formatearFechaHora(t.fecha)}</td>
                <td>{t.estado}</td>
                <td className="text-end">{formatearMoneda(t.total)}</td>
              </tr>
            ))}
            {tickets.length === 0 && (
              <tr>
                <td colSpan={4} className="text-center text-muted py-3">
                  Sin tickets.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      <h6>Pagos</h6>
      <div className="table-responsive">
        <table className="table table-sm table-striped table-bordered align-middle">
          <thead>
            <tr>
              <th>Fecha</th>
              <th>Categoría</th>
              <th className="text-end">Importe</th>
            </tr>
          </thead>
          <tbody>
            {pagos.map((g) => (
              <tr key={g.id}>
                <td>{formatearFechaHora(g.fecha)}</td>
                <td>
                  {g.categoria}
                  {g.origenFondos === 'Tesoreria' && <span className="badge bg-secondary ms-2">{etiquetaDeOrigenFondos(g.origenFondos)}</span>}
                </td>
                <td className="text-end">{formatearMoneda(g.importe)}</td>
              </tr>
            ))}
            {pagos.length === 0 && (
              <tr>
                <td colSpan={3} className="text-center text-muted py-3">
                  Sin pagos.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </>
  )
}
