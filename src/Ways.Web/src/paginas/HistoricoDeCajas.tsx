import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, filtrosDeHistoricoDeCajasVacios, rutasDeExportacion, type FiltrosDeHistoricoDeCajas } from '../api/reportes'
import type { PaginaDeHistoricoDeCajas, PuntoVentaListado } from '../api/tipos'
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
 * Histórico de cajas (stage-11-exportacion-reportes, Slice 6a, design: Web Composition) — G2:
 * turnos CERRADOS únicamente, con totales ya sumados de sus `arqueos_turno` persistidos (nunca
 * re-derivados) y descarga XLSX. Misma política que `/tablero` (`Politicas.LecturaDeReportes`:
 * Supervisor + Admin) — es la vista de gestión sobre turnos ajenos; el cajero ve su propio cierre
 * en `/caja` (spec historico-de-cajas: Role Split).
 *
 * Per `react-async-state` reglas 2/4/9: cada cambio de filtro dispara una nueva consulta gateada
 * por un único `useRef` de generación — una respuesta desactualizada nunca pisa un filtro que el
 * usuario ya cambió.
 */
export function HistoricoDeCajas() {
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

  const [filtros, setFiltros] = useState<FiltrosDeHistoricoDeCajas>(filtrosDeHistoricoDeCajasVacios())
  const [pagina, setPagina] = useState<PaginaDeHistoricoDeCajas | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [errorDescarga, setErrorDescarga] = useState('')
  const generacionRef = useRef(0)

  useEffect(() => {
    let vigente = true
    clienteDeOrganizacion
      .listarPuntosVenta()
      .then((lista) => {
        if (!vigente) return
        setPuntosVenta(lista)
      })
      .catch((e) => {
        if (!vigente) return
        setPuntosVenta([])
        setErrorPuntosVenta(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los puntos de venta.')
      })

    return () => {
      vigente = false
    }
  }, [])

  const cargar = useCallback(() => {
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    clienteDeReportes
      .historicoDeCajas(filtros)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el histórico de cajas.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [filtros])

  useEffect(() => {
    cargar()
  }, [cargar])

  const puntoVentaPorId = useMemo(() => {
    const indice: Record<number, PuntoVentaListado> = {}
    for (const pv of puntosVenta ?? []) indice[pv.id] = pv
    return indice
  }, [puntosVenta])

  function cambiarFiltro(cambios: Partial<Omit<FiltrosDeHistoricoDeCajas, 'pagina' | 'tamanio'>>) {
    setFiltros((prev) => ({ ...prev, ...cambios, pagina: 1 }))
  }

  function cambiarPagina(delta: number) {
    setFiltros((prev) => ({ ...prev, pagina: Math.max(1, prev.pagina + delta) }))
  }

  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1

  return (
    <div className="container-fluid py-4">
      <Box titulo="Histórico de cajas" variante="inverse">
        {error && (
          <div className="alert alert-danger rounded-0 d-flex justify-content-between align-items-center gap-2">
            <span>{error}</span>
            <button type="button" className="btn btn-sm btn-outline-danger rounded-0" onClick={cargar}>
              Reintentar
            </button>
          </div>
        )}
        {errorPuntosVenta && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorPuntosVenta}</div>}

        <div className="row g-2 align-items-end mb-3">
          <div className="col-md-3">
            <label className="form-label" htmlFor="historico-cajas-punto-venta">
              Punto de venta
            </label>
            <select
              id="historico-cajas-punto-venta"
              className="form-select rounded-0"
              value={filtros.idPuntoVenta ?? ''}
              onChange={(e) => cambiarFiltro({ idPuntoVenta: e.target.value === '' ? null : Number(e.target.value) })}
            >
              <option value="">Todos</option>
              {(puntosVenta ?? []).map((pv) => (
                <option key={pv.id} value={pv.id}>
                  {pv.nombre}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="historico-cajas-desde">
              Desde
            </label>
            <input
              id="historico-cajas-desde"
              type="date"
              className="form-control rounded-0"
              value={filtros.desde}
              onChange={(e) => cambiarFiltro({ desde: e.target.value })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="historico-cajas-hasta">
              Hasta
            </label>
            <input
              id="historico-cajas-hasta"
              type="date"
              className="form-control rounded-0"
              value={filtros.hasta}
              onChange={(e) => cambiarFiltro({ hasta: e.target.value })}
            />
          </div>
          <div className="col-auto">
            <BotonDeDescarga
              ruta={rutasDeExportacion.historicoDeCajas(filtros)}
              etiqueta="Descargar"
              onError={setErrorDescarga}
              onInicio={() => setErrorDescarga('')}
            />
          </div>
        </div>

        {errorDescarga && <div className="alert alert-danger rounded-0 py-1 px-2 small mb-2">{errorDescarga}</div>}

        {cargando && !pagina && <Cargando />}

        {pagina && (
          <>
            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Turno</th>
                    <th>Punto de venta</th>
                    <th>Apertura</th>
                    <th>Cierre</th>
                    <th className="text-end">Esperado</th>
                    <th className="text-end">Declarado</th>
                    <th className="text-end">Diferencia</th>
                  </tr>
                </thead>
                <tbody>
                  {pagina.items.map((f) => (
                    <tr key={f.idTurnoCaja}>
                      <td>#{f.idTurnoCaja}</td>
                      <td>{puntoVentaPorId[f.idPuntoVenta]?.nombre ?? `PV #${f.idPuntoVenta}`}</td>
                      <td>{formatearFechaHora(f.fechaApertura)}</td>
                      <td>{formatearFechaHora(f.fechaCierre)}</td>
                      <td className="text-end">{formatearMoneda(f.esperado)}</td>
                      <td className="text-end">{formatearMoneda(f.declarado)}</td>
                      <td className="text-end">{formatearMoneda(f.diferencia)}</td>
                    </tr>
                  ))}
                  {pagina.items.length === 0 && (
                    <tr>
                      <td colSpan={7} className="text-center text-muted py-4">
                        No hay turnos cerrados que coincidan con los filtros.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                Página {pagina.pagina} de {totalPaginas} — {pagina.total} turno(s)
              </span>
              <div className="d-flex gap-2">
                <button
                  type="button"
                  className="btn btn-sm btn-outline-secondary rounded-0"
                  disabled={pagina.pagina <= 1 || cargando}
                  onClick={() => cambiarPagina(-1)}
                >
                  Anterior
                </button>
                <button
                  type="button"
                  className="btn btn-sm btn-outline-secondary rounded-0"
                  disabled={pagina.pagina >= totalPaginas || cargando}
                  onClick={() => cambiarPagina(1)}
                >
                  Siguiente
                </button>
              </div>
            </div>
          </>
        )}
      </Box>
    </div>
  )
}
