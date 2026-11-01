import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import {
  clienteDeReportes,
  esColumnaOrdenableDeHistoricoDeCajas,
  filtrosDeHistoricoDeCajasVacios,
  rutasDeExportacion,
  type FiltrosDeHistoricoDeCajas,
  type OrdenDeHistoricoDeCajas,
} from '../api/reportes'
import type { PaginaDeHistoricoDeCajas, PuntoVentaListado } from '../api/tipos'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { EncabezadoOrdenable } from '../componentes/grilla'
import { siguienteOrden } from '../componentes/grilla/ordenamiento'
import { InsigniaDeRecalculo } from '../componentes/InsigniaDeRecalculo'
import { formatearImporte } from '../formato/importes'
import { CampoFecha } from '../componentes/CampoFecha'
import { fechaHoraATexto } from '../formato/fechas'
import { pluralizar } from '../formato/pluralizar'

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function formatearFechaHora(iso: string): string {
  return fechaHoraATexto(iso)
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
  // `null` = el orden de siempre del listado (cierre más reciente primero). Si una consulta falla, la tabla desaparece junto
  // con su encabezado: nunca queda un encabezado que declare un orden que las filas no tienen.
  const [orden, setOrden] = useState<OrdenDeHistoricoDeCajas>(null)
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
      .historicoDeCajas(filtros, orden)
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
  }, [filtros, orden])

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

  // Ascendente → descendente → orden de siempre. Cambiar el orden vuelve a la página 1 y conserva los filtros; los dos
  // setState se agrupan en un solo render, es decir en una sola consulta.
  function alternarOrden(columna: string) {
    if (!esColumnaOrdenableDeHistoricoDeCajas(columna)) return
    setOrden((actual) => {
      const siguiente = siguienteOrden(actual, columna)
      return siguiente === null ? null : { columna, direccion: siguiente.direccion }
    })
    setFiltros((prev) => (prev.pagina === 1 ? prev : { ...prev, pagina: 1 }))
  }

  function cambiarPagina(delta: number) {
    setFiltros((prev) => ({ ...prev, pagina: Math.max(1, prev.pagina + delta) }))
  }

  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1

  return (
    <div className="container-fluid py-4">
      <Box titulo="Histórico de cajas" variante="inverse">
        {error && (
          <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2">
            <span>{error}</span>
            <button type="button" className="btn btn-sm btn-outline-danger" onClick={cargar}>
              Reintentar
            </button>
          </div>
        )}
        {errorPuntosVenta && <div className="alert alert-warning py-1 px-2 small">{errorPuntosVenta}</div>}

        <div className="row g-2 align-items-end mb-3">
          <div className="col-md-3">
            <label className="form-label" htmlFor="historico-cajas-punto-venta">
              Punto de venta
            </label>
            <select
              id="historico-cajas-punto-venta"
              className="form-select"
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
            <CampoFecha
              id="historico-cajas-desde"
              className="form-control"
              value={filtros.desde}
              onChange={(valor) => cambiarFiltro({ desde: valor })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="historico-cajas-hasta">
              Hasta
            </label>
            <CampoFecha
              id="historico-cajas-hasta"
              className="form-control"
              value={filtros.hasta}
              onChange={(valor) => cambiarFiltro({ hasta: valor })}
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

        {errorDescarga && <div className="alert alert-danger py-1 px-2 small mb-2">{errorDescarga}</div>}

        {cargando && !pagina && <Cargando />}

        {pagina && (
          <>
            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <EncabezadoOrdenable columna="turno" orden={orden} onAlternar={alternarOrden}>
                      Turno
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="puntoVenta" orden={orden} onAlternar={alternarOrden}>
                      Punto de venta
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="apertura" orden={orden} onAlternar={alternarOrden}>
                      Apertura
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="cierre" orden={orden} onAlternar={alternarOrden}>
                      Cierre
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="esperado" orden={orden} onAlternar={alternarOrden} className="text-end">
                      Esperado
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="declarado" orden={orden} onAlternar={alternarOrden} className="text-end">
                      Declarado
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="diferencia" orden={orden} onAlternar={alternarOrden} className="text-end">
                      Diferencia
                    </EncabezadoOrdenable>
                  </tr>
                </thead>
                <tbody>
                  {pagina.items.map((f) => (
                    <tr key={f.idTurnoCaja}>
                      <td>
                        <Link to={`/caja/turnos/${f.idTurnoCaja}/z`}>#{f.idTurnoCaja}</Link>
                        <InsigniaDeRecalculo fechaRecalculo={f.fechaRecalculo} />
                      </td>
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
                Página {pagina.pagina} de {totalPaginas} — {pluralizar(pagina.total, 'turno')}
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
      </Box>
    </div>
  )
}
