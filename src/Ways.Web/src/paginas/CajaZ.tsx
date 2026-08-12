import { useEffect, useRef, useState } from 'react'
import { useParams } from 'react-router'
import { clienteDeCaja, rutasDeExportacionDeCaja } from '../api/caja'
import { ErrorApi } from '../api/cliente'
import type { DetalleDeTurno } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

/**
 * Caja Z (stage-11-exportacion-reportes, Slice 6b, design: Web Composition) — detalle del turno
 * cerrado: el mismo `ResumenDeTurno` que `/caja/cierre` usó para derivar el arqueo, más los
 * tickets y gastos del turno, con descarga XLSX. Misma política que `/caja`
 * (`Politicas.OperacionDePos`): el cajero puede leer su propio cierre — mismo gate que
 * `GET .../resumen`.
 *
 * `GET .../detalle` no discrimina por turno-ownership (spec historico-de-cajas: A Vendedor
 * Downloads Their Own Turno's Z-Report; verificado en la Slice 5b — `OperacionDePos` es un gate
 * de rol solo, sin claim de PV ni de turno): esta pantalla tampoco intenta un bloqueo cross-turno
 * que la API no tiene — cualquier Vendedor/Supervisor/Admin autenticado que conozca el id ve el
 * detalle, igual que del lado del servidor.
 */
export function CajaZ() {
  const { id } = useParams<{ id: string }>()
  const idTurno = id !== undefined ? Number(id) : Number.NaN
  const idTurnoValido = Number.isFinite(idTurno)

  const { usuario } = useAuth()

  const [detalle, setDetalle] = useState<DetalleDeTurno | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [errorDescarga, setErrorDescarga] = useState('')
  const generacionRef = useRef(0)

  useEffect(() => {
    if (!idTurnoValido) return
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    clienteDeCaja
      .obtenerDetalle(idTurno)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setDetalle(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setDetalle(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el detalle del turno.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [idTurno, idTurnoValido])

  if (!idTurnoValido) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Caja Z" variante="warning">
          <p className="text-muted mb-0">No se especificó el turno a mostrar.</p>
        </Box>
      </div>
    )
  }

  return (
    <div className="container-fluid py-4">
      <Box
        titulo={`Caja Z — turno #${idTurno}`}
        variante="inverse"
        herramientas={
          <div className="d-flex gap-2">
            <button type="button" className="btn btn-sm btn-outline-light rounded-0 d-print-none" onClick={() => window.print()}>
              Imprimir
            </button>
            <BotonDeDescarga
              ruta={rutasDeExportacionDeCaja.detalleDeTurno(idTurno)}
              etiqueta="Descargar"
              onError={setErrorDescarga}
              onInicio={() => setErrorDescarga('')}
              className="btn btn-sm btn-outline-secondary rounded-0 d-print-none"
            />
          </div>
        }
      >
        {/* Vista de impresión (design decisión 13: mismo componente, `@media print`, sin ruta ni
            fetch dedicados): equivalente del encabezado de los exports XLSX — generado por/cuándo.
            El turno ya está en el título de `Box`, que se imprime igual. */}
        <div className="d-none d-print-block mb-3">
          <div className="small">
            Generado: {new Date().toLocaleString('es-AR')} — {usuario?.usuario ?? '—'}
          </div>
        </div>

        {errorDescarga && <div className="alert alert-danger rounded-0 py-1 px-2 small mb-2">{errorDescarga}</div>}
        {error && (
          <div className="alert alert-danger rounded-0 d-flex justify-content-between align-items-center gap-2">
            <span>{error}</span>
          </div>
        )}

        {cargando && !detalle && <Cargando />}

        {detalle && (
          <>
            <div className="row g-3 mb-3">
              <div className="col-md-3">
                <div className="text-muted small">Tickets</div>
                <div className="fs-5">{detalle.resumen.cantidadTickets}</div>
              </div>
              <div className="col-md-3">
                <div className="text-muted small">Primer ticket</div>
                <div>
                  {detalle.resumen.primerTicket
                    ? `${detalle.resumen.primerTicket.codigo} #${detalle.resumen.primerTicket.numero}`
                    : '—'}
                </div>
              </div>
              <div className="col-md-3">
                <div className="text-muted small">Último ticket</div>
                <div>
                  {detalle.resumen.ultimoTicket
                    ? `${detalle.resumen.ultimoTicket.codigo} #${detalle.resumen.ultimoTicket.numero}`
                    : '—'}
                </div>
              </div>
              <div className="col-md-3">
                <div className="text-muted small">Retiros</div>
                <div>{formatearMoneda(detalle.resumen.egresos.retiros)}</div>
              </div>
            </div>

            <h6>Medios de pago (esperado)</h6>
            <div className="table-responsive mb-3">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Medio</th>
                    <th className="text-end">Esperado</th>
                  </tr>
                </thead>
                <tbody>
                  {detalle.resumen.medios.map((m) => (
                    <tr key={m.idMedioPago}>
                      <td>Medio #{m.idMedioPago}</td>
                      <td className="text-end">{formatearMoneda(m.importeEsperado)}</td>
                    </tr>
                  ))}
                  {detalle.resumen.medios.length === 0 && (
                    <tr>
                      <td colSpan={2} className="text-center text-muted">
                        Este turno no tuvo actividad: no hay ningún medio arqueado.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

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
                  {detalle.tickets.map((t) => (
                    <tr key={t.id}>
                      <td>{t.numeroVisible}</td>
                      <td>{formatearFechaHora(t.fecha)}</td>
                      <td>{t.estado}</td>
                      <td className="text-end">{formatearMoneda(t.total)}</td>
                    </tr>
                  ))}
                  {detalle.tickets.length === 0 && (
                    <tr>
                      <td colSpan={4} className="text-center text-muted py-3">
                        Sin tickets.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <h6>Gastos</h6>
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
                  {detalle.gastos.map((g) => (
                    <tr key={g.id}>
                      <td>{formatearFechaHora(g.fecha)}</td>
                      <td>{g.categoria}</td>
                      <td className="text-end">{formatearMoneda(g.importe)}</td>
                    </tr>
                  ))}
                  {detalle.gastos.length === 0 && (
                    <tr>
                      <td colSpan={3} className="text-center text-muted py-3">
                        Sin gastos.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </>
        )}
      </Box>
    </div>
  )
}
