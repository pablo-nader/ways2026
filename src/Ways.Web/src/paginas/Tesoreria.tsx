import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, rangoUltimosSieteDias, rutasDeExportacion, type FiltrosDeTesoreria } from '../api/reportes'
import { CATEGORIAS_GASTO } from '../api/tipos'
import type { EmpresaListado, MovimientoTesoreriaListado, PaginaDeMovimientosTesoreria, PuntoVentaListado, TipoMovimientoTesoreria } from '../api/tipos'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { formatearImporte } from '../formato/importes'

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

/** Etiqueta en español de `TipoMovimientoTesoreria` — stage-gastos-origen-fondos-pos (PR2):
 * `Gasto` empieza a aparecer en este libro (un gasto de origen Tesoreria escribe su propia fila,
 * `ServicioDeGastos.EscribirMovimientoDeTesoreriaAsync`) y necesitaba una columna propia; antes de
 * este PR la columna `tipo` se traía pero nunca se mostraba. */
function etiquetaDeTipoMovimiento(tipo: TipoMovimientoTesoreria): string {
  switch (tipo) {
    case 'RetiroCaja':
      return 'Retiro de caja'
    case 'Deposito':
      return 'Depósito'
    case 'Gasto':
      return 'Gasto'
    case 'Ajuste':
      return 'Ajuste'
  }
}

/** stage-tesoreria-por-empresa (PR5): mismo criterio de "concepto efectivo" que
 * `ExportacionDeCaja.ConceptoEfectivo` (C#) — una fila `Gasto` muestra la categoría/concepto del
 * gasto de origen (más legible que el `concepto` genérico del movimiento); cualquier otra fila
 * muestra su propio `concepto`. */
function conceptoEfectivo(m: MovimientoTesoreriaListado): string {
  if (m.gastoConcepto === null) return m.concepto
  const etiquetaCategoria = CATEGORIAS_GASTO.find((c) => c.valor === m.gastoCategoria)?.etiqueta ?? m.gastoCategoria
  return `${etiquetaCategoria} — ${m.gastoConcepto}`
}

/**
 * Tesorería (stage-11-exportacion-reportes, Slice 7 — web, design: Web Composition) — G3: libro
 * encadenado de `movimientos_tesoreria`, ordenado por id (nunca por fecha, spec tesoreria: Book
 * Preserves Chain Order). Misma política que `/tablero` (`Politicas.LecturaDeReportes`:
 * Supervisor + Admin).
 *
 * stage-tesoreria-por-empresa (PR5): la cadena es por EMPRESA (doc 10 §7) — el filtro ancla pasa
 * a ser `idEmpresa` (preseleccionada cuando el tenant tiene una sola, mismo criterio que
 * `Gastos.tsx`), con `idPuntoVenta` como filtro OPCIONAL ("Todos" por defecto) sobre esa misma
 * cadena: un filtro puntual muestra un SUBCONJUNTO cuyos saldos (inicio/final) no se encadenan
 * entre sí — la pantalla muestra una nota cuando ese filtro está activo, en vez de dejar que el
 * lector asuma que la cadena visible es completa.
 *
 * Las filas se renderizan en el mismo orden que el backend las devuelve (`OrderBy(m => m.Id)`) —
 * sin ningún reordenamiento del lado del cliente, para no enmascarar una regresión del orden de
 * cadena detrás de un `sort` en pantalla.
 */
export function Tesoreria() {
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorCatalogos, setErrorCatalogos] = useState('')

  const [filtros, setFiltros] = useState<FiltrosDeTesoreria | null>(null)
  const [pagina, setPagina] = useState<PaginaDeMovimientosTesoreria | null>(null)
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const [errorDescarga, setErrorDescarga] = useState('')
  const generacionRef = useRef(0)

  useEffect(() => {
    let vigente = true
    Promise.all([clienteDeOrganizacion.listarEmpresas(), clienteDeOrganizacion.listarPuntosVenta()])
      .then(([empresasCargadas, puntosVentaCargados]) => {
        if (!vigente) return
        setEmpresas(empresasCargadas)
        setPuntosVenta(puntosVentaCargados)
        // Empresa única: se preselecciona el filtro, mismo criterio que Gastos.tsx.
        if (empresasCargadas.length === 1) {
          const rango = rangoUltimosSieteDias()
          setFiltros({
            idEmpresa: empresasCargadas[0].id,
            idPuntoVenta: null,
            desde: rango.desde,
            hasta: rango.hasta,
            pagina: 1,
            tamanio: 25,
          })
        }
      })
      .catch((e) => {
        if (!vigente) return
        setEmpresas([])
        setPuntosVenta([])
        setErrorCatalogos(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los catálogos.')
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

  function cambiarEmpresa(valorCrudo: string) {
    if (valorCrudo === '') {
      // judgment-day PR5, hallazgo #5 (react-async-state): bumpear la generación ACÁ — sin esto,
      // una respuesta de `cargar()` ya en vuelo (para la empresa recién deseleccionada) todavía
      // pasa el chequeo `generacionRef.current !== miGeneracion` y repuebla la página después de
      // que el usuario ya volvió al estado "sin empresa elegida".
      generacionRef.current += 1
      setFiltros(null)
      setPagina(null)
      return
    }

    const idEmpresa = Number(valorCrudo)
    setFiltros((prev) => {
      const rango = rangoUltimosSieteDias()
      return {
        idEmpresa,
        idPuntoVenta: null,
        desde: prev?.desde ?? rango.desde,
        hasta: prev?.hasta ?? rango.hasta,
        pagina: 1,
        tamanio: 25,
      }
    })
  }

  function cambiarPagina(delta: number) {
    setFiltros((prev) => (prev ? { ...prev, pagina: Math.max(1, prev.pagina + delta) } : prev))
  }

  const puntosVentaDeLaEmpresa = useMemo(
    () => (puntosVenta ?? []).filter((pv) => filtros !== null && pv.idEmpresa === filtros.idEmpresa),
    [puntosVenta, filtros],
  )

  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1

  return (
    <div className="container-fluid py-4">
      <Box titulo="Tesorería" variante="inverse">
        {error && (
          <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2">
            <span>{error}</span>
            <button type="button" className="btn btn-sm btn-outline-danger" onClick={cargar}>
              Reintentar
            </button>
          </div>
        )}
        {errorCatalogos && <div className="alert alert-warning py-1 px-2 small">{errorCatalogos}</div>}

        {empresas === null || puntosVenta === null ? (
          <Cargando />
        ) : empresas.length === 0 ? (
          <p className="text-muted text-center py-4">No hay empresas visibles para el libro de tesorería.</p>
        ) : (
          <>
            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-3">
                <label className="form-label" htmlFor="tesoreria-empresa">
                  Empresa
                </label>
                <select
                  id="tesoreria-empresa"
                  className="form-select"
                  value={filtros?.idEmpresa ?? ''}
                  onChange={(e) => cambiarEmpresa(e.target.value)}
                >
                  <option value="">Elegir…</option>
                  {empresas.map((emp) => (
                    <option key={emp.id} value={emp.id}>
                      {emp.razonSocial}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-3">
                <label className="form-label" htmlFor="tesoreria-punto-venta">
                  Punto de venta
                </label>
                <select
                  id="tesoreria-punto-venta"
                  className="form-select"
                  value={filtros?.idPuntoVenta ?? ''}
                  disabled={filtros === null}
                  onChange={(e) => cambiarFiltro({ idPuntoVenta: e.target.value === '' ? null : Number(e.target.value) })}
                >
                  <option value="">Todos</option>
                  {puntosVentaDeLaEmpresa.map((pv) => (
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
                  className="form-control"
                  disabled={filtros === null}
                  value={filtros?.desde ?? ''}
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
                  className="form-control"
                  disabled={filtros === null}
                  value={filtros?.hasta ?? ''}
                  onChange={(e) => cambiarFiltro({ hasta: e.target.value })}
                />
              </div>
              {filtros && (
                <div className="col-auto">
                  <BotonDeDescarga
                    ruta={rutasDeExportacion.tesoreria(filtros)}
                    etiqueta="Descargar"
                    onError={setErrorDescarga}
                    onInicio={() => setErrorDescarga('')}
                  />
                </div>
              )}
            </div>

            {errorDescarga && <div className="alert alert-danger py-1 px-2 small mb-2">{errorDescarga}</div>}

            {filtros === null ? (
              <p className="text-muted text-center py-4">Elegí una empresa para ver su libro de tesorería.</p>
            ) : (
              <>
                {filtros.idPuntoVenta !== null && (
                  <div className="alert alert-info py-1 px-2 small mb-2">
                    Los saldos (inicio/final) pertenecen a la cadena completa de la empresa: este filtro muestra solo
                    las filas originadas en este punto de venta, un subconjunto que no se encadena entre sí.
                  </div>
                )}

                {cargando && !pagina && <Cargando />}

                {pagina && (
                  <>
                    <div className="table-responsive">
                      <table className="table table-sm table-striped table-bordered align-middle">
                        <thead>
                          <tr>
                            <th>Tipo</th>
                            <th className="text-end">Inicio</th>
                            <th className="text-end">Ingreso</th>
                            <th className="text-end">Egreso</th>
                            <th className="text-end">Final</th>
                            <th>Concepto</th>
                            <th>Punto de venta</th>
                            <th>Empleado</th>
                            <th>Fecha</th>
                          </tr>
                        </thead>
                        <tbody>
                          {pagina.items.map((m) => (
                            <tr key={m.id}>
                              <td>{etiquetaDeTipoMovimiento(m.tipo)}</td>
                              <td className="text-end">{formatearMoneda(m.inicio)}</td>
                              <td className="text-end">{formatearMoneda(m.ingreso)}</td>
                              <td className="text-end">{formatearMoneda(m.egreso)}</td>
                              <td className="text-end">{formatearMoneda(m.final)}</td>
                              <td>{conceptoEfectivo(m)}</td>
                              <td>{m.idPuntoVenta === null ? '—' : (m.nombrePuntoVenta ?? '(no disponible)')}</td>
                              <td>Empleado #{m.idEmpleado}</td>
                              <td>{formatearFechaHora(m.fecha)}</td>
                            </tr>
                          ))}
                          {pagina.items.length === 0 && (
                            <tr>
                              <td colSpan={9} className="text-center text-muted py-4">
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
                          className="btn btn-sm btn-outline-secondary"
                          disabled={pagina.pagina <= 1 || cargando}
                          onClick={() => cambiarPagina(-1)}
                        >
                          Anterior
                        </button>
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-secondary"
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
          </>
        )}
      </Box>
    </div>
  )
}
