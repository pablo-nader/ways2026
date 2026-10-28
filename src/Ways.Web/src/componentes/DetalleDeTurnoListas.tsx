import { EncabezadoOrdenable, useOrdenamiento, type ColumnasOrdenables } from './grilla'
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

const COLUMNAS_DE_TICKETS: ColumnasOrdenables<TicketDeTurno> = {
  numero: { valor: (t) => t.numeroVisible },
  fecha: { valor: (t) => new Date(t.fecha) },
  estado: { valor: (t) => t.estado },
  total: { valor: (t) => t.total },
}

const COLUMNAS_DE_PAGOS: ColumnasOrdenables<PagoDeTurno> = {
  fecha: { valor: (g) => new Date(g.fecha) },
  categoria: { valor: (g) => g.categoria },
  importe: { valor: (g) => g.importe },
}

type Props = {
  tickets: TicketDeTurno[]
  pagos: PagoDeTurno[]
}

/** Las listas de tickets y de pagos del detalle de un turno cerrado: las comparten la pantalla
 * "Caja Z" y el modal de detalle de "Cierres de caja" del POS. */
export function DetalleDeTurnoListas({ tickets, pagos }: Props) {
  const ordenTickets = useOrdenamiento(tickets, COLUMNAS_DE_TICKETS)
  const ordenPagos = useOrdenamiento(pagos, COLUMNAS_DE_PAGOS)

  return (
    <>
      <h6>Tickets</h6>
      <div className="table-responsive mb-3">
        <table className="table table-sm table-striped table-bordered align-middle">
          <thead>
            <tr>
              <EncabezadoOrdenable columna="numero" orden={ordenTickets.orden} onAlternar={ordenTickets.alternar}>
                Número
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="fecha" orden={ordenTickets.orden} onAlternar={ordenTickets.alternar}>
                Fecha
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="estado" orden={ordenTickets.orden} onAlternar={ordenTickets.alternar}>
                Estado
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="total" orden={ordenTickets.orden} onAlternar={ordenTickets.alternar} className="text-end">
                Total
              </EncabezadoOrdenable>
            </tr>
          </thead>
          <tbody>
            {ordenTickets.filas.map((t) => (
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
              <EncabezadoOrdenable columna="fecha" orden={ordenPagos.orden} onAlternar={ordenPagos.alternar}>
                Fecha
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="categoria" orden={ordenPagos.orden} onAlternar={ordenPagos.alternar}>
                Categoría
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="importe" orden={ordenPagos.orden} onAlternar={ordenPagos.alternar} className="text-end">
                Importe
              </EncabezadoOrdenable>
            </tr>
          </thead>
          <tbody>
            {ordenPagos.filas.map((g) => (
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
