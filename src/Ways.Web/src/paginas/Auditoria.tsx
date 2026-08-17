import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import {
  clienteDeAuditoria,
  filtrosDeAuditoriaVacios,
  puedeExportarAuditoria,
  rutasDeExportacion,
  type FiltrosDeConsultaDeAuditoria,
} from '../api/auditoria'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { CATALOGO_DE_ACCIONES_AUDITADAS, type FilaDeAuditoria, type PaginaDeAuditoria, type PuntoVentaListado } from '../api/tipos'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { PanelDeCambio } from './PanelDeCambio'

function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

function etiquetaDeAccion(accion: string): string {
  return CATALOGO_DE_ACCIONES_AUDITADAS.find((a) => a.valor === accion)?.etiqueta ?? accion
}

/**
 * Auditoria (stage-14-auditoria-trazabilidad, Slice 7, design decisión 17: "Web composition —
 * Auditoria.tsx") — filtros + paginado con el shape verbatim de `HistoricoDeCajas.tsx`
 * (`FiltrosDeConsultaDeAuditoria`, `filtrosDeAuditoriaVacios()`, `generacionRef` per
 * `react-async-state` regla 2, `cambiarFiltro` resetea a página 1, `cambiarPagina(±1)` con
 * `disabled` en los bordes) más el `BotonDeDescarga` de `Vencimientos.tsx` apuntando a
 * `rutasDeExportacion.auditoria`. Pantalla Admin-only (`Politicas.LecturaDeAuditoria`,
 * `puedeVerAuditoria` en `tipos.ts`) — nunca supervisor, vendedor ni root.
 */
export function Auditoria() {
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

  const [filtros, setFiltros] = useState<FiltrosDeConsultaDeAuditoria>(filtrosDeAuditoriaVacios())
  const [pagina, setPagina] = useState<PaginaDeAuditoria | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [errorDescarga, setErrorDescarga] = useState('')
  const [filaExpandidaId, setFilaExpandidaId] = useState<number | null>(null)
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

    clienteDeAuditoria
      .consultar(filtros)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el log de auditoría.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [filtros])

  useEffect(() => {
    cargar()
  }, [cargar])

  function cambiarFiltro(cambios: Partial<Omit<FiltrosDeConsultaDeAuditoria, 'pagina' | 'tamanio'>>) {
    setFiltros((prev) => ({ ...prev, ...cambios, pagina: 1 }))
  }

  function cambiarEntidad(valor: string) {
    // `idEntidad` sin `entidad` es un 400 del servidor (design decisión 16) — limpiarlo cuando
    // se vacía `entidad` evita mandar un filtro imposible.
    cambiarFiltro(valor === '' ? { entidad: null, idEntidad: null } : { entidad: valor })
  }

  function cambiarPagina(delta: number) {
    setFiltros((prev) => ({ ...prev, pagina: Math.max(1, prev.pagina + delta) }))
  }

  function alternarDetalle(fila: FilaDeAuditoria) {
    setFilaExpandidaId((actual) => (actual === fila.idAuditoria ? null : fila.idAuditoria))
  }

  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1

  return (
    <div className="container-fluid py-4">
      <Box titulo="Auditoría" variante="inverse">
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
          <div className="col-md-2">
            <label className="form-label" htmlFor="auditoria-desde">
              Desde
            </label>
            <input
              id="auditoria-desde"
              type="date"
              className="form-control rounded-0"
              value={filtros.desde}
              onChange={(e) => cambiarFiltro({ desde: e.target.value })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="auditoria-hasta">
              Hasta
            </label>
            <input
              id="auditoria-hasta"
              type="date"
              className="form-control rounded-0"
              value={filtros.hasta}
              onChange={(e) => cambiarFiltro({ hasta: e.target.value })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="auditoria-accion">
              Acción
            </label>
            <select
              id="auditoria-accion"
              className="form-select rounded-0"
              value={filtros.accion ?? ''}
              onChange={(e) => cambiarFiltro({ accion: e.target.value === '' ? null : e.target.value })}
            >
              <option value="">Todas</option>
              {CATALOGO_DE_ACCIONES_AUDITADAS.map((a) => (
                <option key={a.valor} value={a.valor}>
                  {a.etiqueta}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-1">
            <label className="form-label" htmlFor="auditoria-actor">
              Actor
            </label>
            <input
              id="auditoria-actor"
              type="number"
              className="form-control rounded-0"
              placeholder="Id"
              value={filtros.idActor ?? ''}
              onChange={(e) => cambiarFiltro({ idActor: e.target.value === '' ? null : Number(e.target.value) })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="auditoria-entidad">
              Entidad
            </label>
            <input
              id="auditoria-entidad"
              type="text"
              className="form-control rounded-0"
              placeholder="articulo, usuario…"
              value={filtros.entidad ?? ''}
              onChange={(e) => cambiarEntidad(e.target.value)}
            />
          </div>
          <div className="col-md-1">
            <label className="form-label" htmlFor="auditoria-id-entidad">
              #Id
            </label>
            <input
              id="auditoria-id-entidad"
              type="number"
              className="form-control rounded-0"
              disabled={filtros.entidad === null}
              value={filtros.idEntidad ?? ''}
              onChange={(e) => cambiarFiltro({ idEntidad: e.target.value === '' ? null : Number(e.target.value) })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="auditoria-punto-venta">
              Punto de venta
            </label>
            <select
              id="auditoria-punto-venta"
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
          <div className="col-auto">
            <BotonDeDescarga
              ruta={rutasDeExportacion.auditoria({
                desde: filtros.desde,
                hasta: filtros.hasta,
                accion: filtros.accion,
                idActor: filtros.idActor,
                entidad: filtros.entidad,
                idEntidad: filtros.idEntidad,
                idPuntoVenta: filtros.idPuntoVenta,
              })}
              etiqueta="Descargar"
              disabled={!puedeExportarAuditoria(filtros)}
              onError={setErrorDescarga}
              onInicio={() => setErrorDescarga('')}
            />
            {/* `/auditoria/export` exige desde/hasta no vacíos (AuditoriaEndpoints.cs) — con
                cualquiera de las dos fechas vacía, el botón queda deshabilitado con el motivo
                visible en vez de mandar una descarga que el servidor va a rechazar. */}
            {!puedeExportarAuditoria(filtros) && (
              <div className="small text-muted mt-1">Completá Desde y Hasta para descargar.</div>
            )}
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
                    <th>Fecha</th>
                    <th>Acción</th>
                    <th>Entidad</th>
                    <th>#Id</th>
                    <th>Actor</th>
                    <th>PV</th>
                    <th>Detalle</th>
                  </tr>
                </thead>
                <tbody>
                  {pagina.items.map((f) => (
                    <Fragment key={f.idAuditoria}>
                      <tr>
                        <td>{formatearFechaHora(f.creadoEl)}</td>
                        <td>{etiquetaDeAccion(f.accion)}</td>
                        <td>{f.entidad}</td>
                        <td>#{f.idEntidad}</td>
                        <td>{f.actor ?? `#${f.idActor}`}</td>
                        <td>
                          {f.idPuntoVenta === null ? (
                            <span title="Evento de todo el tenant">—</span>
                          ) : (
                            (puntosVenta ?? []).find((pv) => pv.id === f.idPuntoVenta)?.nombre ?? `PV #${f.idPuntoVenta}`
                          )}
                        </td>
                        <td>
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-secondary rounded-0"
                            onClick={() => alternarDetalle(f)}
                          >
                            {filaExpandidaId === f.idAuditoria ? 'Ocultar' : 'Ver'}
                          </button>
                        </td>
                      </tr>
                      {filaExpandidaId === f.idAuditoria && (
                        <tr>
                          <td colSpan={7} className="p-0">
                            <PanelDeCambio valorAnterior={f.valorAnterior} valorNuevo={f.valorNuevo} />
                          </td>
                        </tr>
                      )}
                    </Fragment>
                  ))}
                  {pagina.items.length === 0 && (
                    <tr>
                      <td colSpan={7} className="text-center text-muted py-4">
                        No hay eventos de auditoría que coincidan con los filtros.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                Página {pagina.pagina} de {totalPaginas} — {pagina.total} evento(s)
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
