import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, rangoUltimosSieteDias, rutasDeExportacion, type FiltrosDeTesoreria } from '../api/reportes'
import type { PaginaDeMovimientosTesoreria, PuntoVentaListado } from '../api/tipos'
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
 * Tesorería (stage-11-exportacion-reportes, Slice 7 — web, design: Web Composition) — G3: libro
 * encadenado de `movimientos_tesoreria` de UN punto de venta, ordenado por id (nunca por fecha,
 * spec tesoreria: Book Preserves Chain Order) — mezclar puntos de venta rompería el significado
 * de la cadena inicio/final (design decisión 11), así que a diferencia de `/caja/historico` acá
 * no hay opción "Todos": el filtro exige un punto de venta puntual. Misma política que `/tablero`
 * (`Politicas.LecturaDeReportes`: Supervisor + Admin).
 *
 * Las filas se renderizan en el mismo orden que el backend las devuelve (`OrderBy(m => m.Id)`) —
 * sin ningún reordenamiento del lado del cliente, para no enmascarar una regresión del orden de
 * cadena detrás de un `sort` en pantalla.
 */
export function Tesoreria() {
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

  const [filtros, setFiltros] = useState<FiltrosDeTesoreria | null>(null)
  const [pagina, setPagina] = useState<PaginaDeMovimientosTesoreria | null>(null)
  const [cargando, setCargando] = useState(false)
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
        if (lista.length > 0) {
          const rango = rangoUltimosSieteDias()
          setFiltros({ idPuntoVenta: lista[0].id, desde: rango.desde, hasta: rango.hasta, pagina: 1, tamanio: 25 })
        }
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
    if (filtros === null) return
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    clienteDeReportes
      .tesoreria(filtros)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el libro de tesorería.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [filtros])

  useEffect(() => {
    cargar()
  }, [cargar])

  function cambiarFiltro(cambios: Partial<Omit<FiltrosDeTesoreria, 'pagina' | 'tamanio'>>) {
    setFiltros((prev) => (prev ? { ...prev, ...cambios, pagina: 1 } : prev))
  }

  function cambiarPagina(delta: number) {
    setFiltros((prev) => (prev ? { ...prev, pagina: Math.max(1, prev.pagina + delta) } : prev))
  }

  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1

  return (
    <div className="container-fluid py-4">
      <Box titulo="Tesorería" variante="inverse">
        {error && (
          <div className="alert alert-danger rounded-0 d-flex justify-content-between align-items-center gap-2">
            <span>{error}</span>
            <button type="button" className="btn btn-sm btn-outline-danger rounded-0" onClick={cargar}>
              Reintentar
            </button>
          </div>
        )}
        {errorPuntosVenta && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorPuntosVenta}</div>}

        {puntosVenta === null ? (
          <Cargando />
        ) : puntosVenta.length === 0 ? (
          <p className="text-muted text-center py-4">No hay puntos de venta visibles para el libro de tesorería.</p>
        ) : filtros === null ? (
          <Cargando />
        ) : (
          <>
            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-3">
                <label className="form-label" htmlFor="tesoreria-punto-venta">
                  Punto de venta
                </label>
                <select
                  id="tesoreria-punto-venta"
                  className="form-select rounded-0"
                  value={filtros.idPuntoVenta}
                  onChange={(e) => cambiarFiltro({ idPuntoVenta: Number(e.target.value) })}
                >
                  {puntosVenta.map((pv) => (
                    <option key={pv.id} value={pv.id}>
                      {pv.nombre}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="tesoreria-desde">
                  Desde
                </label>
                <input
                  id="tesoreria-desde"
                  type="date"
                  className="form-control rounded-0"
                  value={filtros.desde}
                  onChange={(e) => cambiarFiltro({ desde: e.target.value })}
                />
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="tesoreria-hasta">
                  Hasta
                </label>
                <input
                  id="tesoreria-hasta"
                  type="date"
                  className="form-control rounded-0"
                  value={filtros.hasta}
                  onChange={(e) => cambiarFiltro({ hasta: e.target.value })}
                />
              </div>
              <div className="col-auto">
                <BotonDeDescarga
                  ruta={rutasDeExportacion.tesoreria(filtros)}
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
                        <th className="text-end">Inicio</th>
                        <th className="text-end">Ingreso</th>
                        <th className="text-end">Egreso</th>
                        <th className="text-end">Final</th>
                        <th>Concepto</th>
                        <th>Empleado</th>
                        <th>Fecha</th>
                      </tr>
                    </thead>
                    <tbody>
                      {pagina.items.map((m) => (
                        <tr key={m.id}>
                          <td className="text-end">{formatearMoneda(m.inicio)}</td>
                          <td className="text-end">{formatearMoneda(m.ingreso)}</td>
                          <td className="text-end">{formatearMoneda(m.egreso)}</td>
                          <td className="text-end">{formatearMoneda(m.final)}</td>
                          <td>{m.concepto}</td>
                          <td>Empleado #{m.idEmpleado}</td>
                          <td>{formatearFechaHora(m.fecha)}</td>
                        </tr>
                      ))}
                      {pagina.items.length === 0 && (
                        <tr>
                          <td colSpan={7} className="text-center text-muted py-4">
                            No hay movimientos que coincidan con los filtros.
                          </td>
                        </tr>
                      )}
                    </tbody>
                  </table>
                </div>

                <div className="d-flex justify-content-between align-items-center">
                  <span className="small text-muted">
                    Página {pagina.pagina} de {totalPaginas} — {pagina.total} movimiento(s)
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
          </>
        )}
      </Box>
    </div>
  )
}
