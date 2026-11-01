import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import {
  clienteDeReportes,
  esColumnaOrdenableDeTesoreria,
  rangoUltimosSieteDias,
  rutasDeExportacion,
  type FiltrosDeTesoreria,
  type OrdenDeTesoreria,
} from '../api/reportes'
import { CATEGORIAS_PAGO } from '../api/tipos'
import type { EmpresaListado, MovimientoTesoreriaListado, PaginaDeMovimientosTesoreria, PuntoVentaListado, TipoMovimientoTesoreria } from '../api/tipos'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { toast } from '../toasts/toasts'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { EncabezadoOrdenable } from '../componentes/grilla'
import { siguienteOrden } from '../componentes/grilla/ordenamiento'
import { formatearImporte } from '../formato/importes'
import { CampoFecha } from '../componentes/CampoFecha'
import { fechaHoraATexto } from '../formato/fechas'
import { SeccionDeCuentas } from './tesoreria/SeccionDeCuentas'
import { pluralizar } from '../formato/pluralizar'

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function formatearFechaHora(iso: string): string {
  return fechaHoraATexto(iso)
}

/** Etiqueta en español de `TipoMovimientoTesoreria` — stage-gastos-origen-fondos-pos (PR2):
 * `Pago` empieza a aparecer en este libro (un pago de origen Tesoreria escribe su propia fila,
 * `ServicioDePagos.EscribirMovimientoDeTesoreriaAsync`) y necesitaba una columna propia; antes de
 * este PR la columna `tipo` se traía pero nunca se mostraba. */
function etiquetaDeTipoMovimiento(tipo: TipoMovimientoTesoreria): string {
  switch (tipo) {
    case 'RetiroCaja':
      return 'Retiro de caja'
    case 'Deposito':
      return 'Depósito'
    case 'Gasto':
      return 'Pago'
    case 'Ajuste':
      return 'Ajuste'
  }
}

/** stage-tesoreria-por-empresa (PR5): mismo criterio de "concepto efectivo" que
 * `ExportacionDeCaja.ConceptoEfectivo` (C#) — una fila `Pago` muestra la categoría/concepto del
 * pago de origen (más legible que el `concepto` genérico del movimiento); cualquier otra fila
 * muestra su propio `concepto`. */
function conceptoEfectivo(m: MovimientoTesoreriaListado): string {
  if (m.gastoConcepto === null) return m.concepto
  const etiquetaCategoria = CATEGORIAS_PAGO.find((c) => c.valor === m.gastoCategoria)?.etiqueta ?? m.gastoCategoria
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
 * `Pagos.tsx`), con `idPuntoVenta` como filtro OPCIONAL ("Todos" por defecto) sobre esa misma
 * cadena: un filtro puntual muestra un SUBCONJUNTO cuyos saldos (inicio/final) no se encadenan
 * entre sí — la pantalla muestra una nota cuando ese filtro está activo, en vez de dejar que el
 * lector asuma que la cadena visible es completa.
 *
 * Las filas se renderizan en el mismo orden que el backend las devuelve (`OrderBy(m => m.Id)`
 * mientras nadie pida otro) — sin ningún reordenamiento del lado del cliente, para no enmascarar
 * una regresión del orden de cadena detrás de un `sort` en pantalla. El orden por columna es
 * opt-in y también lo resuelve el servidor (`orden`/`direccion`, antes de paginar): cada fila
 * conserva el inicio/final persistidos, así que bajo otro orden la cadena deja de leerse fila a fila.
 */
export function Tesoreria() {
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorCatalogos, setErrorCatalogos] = useState('')

  const [filtros, setFiltros] = useState<FiltrosDeTesoreria | null>(null)
  // `null` = orden de cadena (id ascendente). Si una consulta falla, la tabla desaparece junto con
  // su encabezado: nunca queda un encabezado que declare un orden que las filas no tienen.
  const [orden, setOrden] = useState<OrdenDeTesoreria>(null)
  const [pagina, setPagina] = useState<PaginaDeMovimientosTesoreria | null>(null)
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  useEffect(() => {
    let vigente = true
    Promise.all([clienteDeOrganizacion.listarEmpresas(), clienteDeOrganizacion.listarPuntosVenta()])
      .then(([empresasCargadas, puntosVentaCargados]) => {
        if (!vigente) return
        setEmpresas(empresasCargadas)
        setPuntosVenta(puntosVentaCargados)
        // Empresa única: se preselecciona el filtro, mismo criterio que Pagos.tsx.
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
      .tesoreria(filtros, orden)
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
  }, [filtros, orden])

  useEffect(() => {
    cargar()
  }, [cargar])

  // Ascendente → descendente → orden de cadena. Cambiar el orden vuelve a la página 1 y conserva los
  // filtros; los dos setState se agrupan en un solo render, es decir en una sola consulta.
  function alternarOrden(columna: string) {
    if (!esColumnaOrdenableDeTesoreria(columna)) return
    setOrden((actual) => {
      const siguiente = siguienteOrden(actual, columna)
      return siguiente === null ? null : { columna, direccion: siguiente.direccion }
    })
    setFiltros((prev) => (prev === null || prev.pagina === 1 ? prev : { ...prev, pagina: 1 }))
  }

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
                <CampoFecha
                  id="tesoreria-desde"
                  className="form-control"
                  disabled={filtros === null}
                  value={filtros?.desde ?? ''}
                  onChange={(valor) => cambiarFiltro({ desde: valor })}
                />
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="tesoreria-hasta">
                  Hasta
                </label>
                <CampoFecha
                  id="tesoreria-hasta"
                  className="form-control"
                  disabled={filtros === null}
                  value={filtros?.hasta ?? ''}
                  onChange={(valor) => cambiarFiltro({ hasta: valor })}
                />
              </div>
              {filtros && (
                <div className="col-auto">
                  <BotonDeDescarga
                    ruta={rutasDeExportacion.tesoreria(filtros)}
                    etiqueta="Descargar"
                    onError={toast.error}
                  />
                </div>
              )}
            </div>

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
                            <EncabezadoOrdenable columna="tipo" orden={orden} onAlternar={alternarOrden}>
                              Tipo
                            </EncabezadoOrdenable>
                            <EncabezadoOrdenable columna="inicio" orden={orden} onAlternar={alternarOrden} className="text-end">
                              Inicio
                            </EncabezadoOrdenable>
                            <EncabezadoOrdenable columna="ingreso" orden={orden} onAlternar={alternarOrden} className="text-end">
                              Ingreso
                            </EncabezadoOrdenable>
                            <EncabezadoOrdenable columna="egreso" orden={orden} onAlternar={alternarOrden} className="text-end">
                              Egreso
                            </EncabezadoOrdenable>
                            <EncabezadoOrdenable columna="final" orden={orden} onAlternar={alternarOrden} className="text-end">
                              Final
                            </EncabezadoOrdenable>
                            <EncabezadoOrdenable columna="concepto" orden={orden} onAlternar={alternarOrden}>
                              Concepto
                            </EncabezadoOrdenable>
                            <EncabezadoOrdenable columna="puntoVenta" orden={orden} onAlternar={alternarOrden}>
                              Punto de venta
                            </EncabezadoOrdenable>
                            <EncabezadoOrdenable columna="empleado" orden={orden} onAlternar={alternarOrden}>
                              Empleado
                            </EncabezadoOrdenable>
                            <EncabezadoOrdenable columna="fecha" orden={orden} onAlternar={alternarOrden}>
                              Fecha
                            </EncabezadoOrdenable>
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
                        Página {pagina.pagina} de {totalPaginas} — {pluralizar(pagina.total, 'movimiento')}
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

      {empresas !== null && empresas.length > 0 && (
        <div className="mt-4">
          <SeccionDeCuentas idEmpresa={filtros?.idEmpresa ?? null} />
        </div>
      )}
    </div>
  )
}
