import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import {
  clienteDeAuditoria,
  esColumnaOrdenableDeAuditoria,
  filtrosDeAuditoriaVacios,
  puedeExportarAuditoria,
  rutasDeExportacion,
  type FiltrosDeConsultaDeAuditoria,
  type OrdenDeAuditoria,
} from '../api/auditoria'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { CATALOGO_DE_ACCIONES_AUDITADAS, type FilaDeAuditoria, type PaginaDeAuditoria, type PuntoVentaListado } from '../api/tipos'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { toast } from '../toasts/toasts'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { EncabezadoOrdenable } from '../componentes/grilla'
import { siguienteOrden } from '../componentes/grilla/ordenamiento'
import { PanelDeCambio } from './PanelDeCambio'
import { CampoFecha } from '../componentes/CampoFecha'
import { fechaHoraATexto } from '../formato/fechas'
import { atributosDeCampoEntero, parsearEnteroTipeado } from '../formato/numeros'
import { pluralizar } from '../formato/pluralizar'

function formatearFechaHora(iso: string): string {
  return fechaHoraATexto(iso)
}

/** Exportada para el test colocado (`Auditoria.test.tsx`, `web-descriptor-tests`). Fallback
 * `?? accion` (design decisión 15): una acción retirada del catálogo deja rastro consultable —
 * la fila muestra el código crudo (`modulo.retirado`) en vez de desaparecer o romper el render. */
export function etiquetaDeAccion(accion: string): string {
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
  // El orden es del servidor (el log pagina en el servidor: ordenar solo la página cargada
  // mentiría); `null` = lo más reciente primero. Si una consulta falla, la tabla desaparece junto
  // con su encabezado: nunca queda un encabezado que declare un orden que las filas no tienen.
  const [orden, setOrden] = useState<OrdenDeAuditoria>(null)
  const [pagina, setPagina] = useState<PaginaDeAuditoria | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
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
      .consultar(filtros, orden)
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
  }, [filtros, orden])

  useEffect(() => {
    cargar()
  }, [cargar])

  // Ascendente → descendente → sin orden. Cambiar el orden vuelve a la página 1 y conserva los
  // filtros; los dos setState se agrupan en un solo render, es decir en una sola consulta.
  function alternarOrden(columna: string) {
    if (!esColumnaOrdenableDeAuditoria(columna)) return
    setOrden((actual) => {
      const siguiente = siguienteOrden(actual, columna)
      return siguiente === null ? null : { columna, direccion: siguiente.direccion }
    })
    setFiltros((prev) => (prev.pagina === 1 ? prev : { ...prev, pagina: 1 }))
  }

  /** Un texto que no es un entero (letras, "," o ".") no se acepta: dejarlo pasar vaciaría el campo al volver a pintarlo. */
  function cambiarFiltroNumerico(campo: 'idActor' | 'idEntidad', texto: string) {
    const id = parsearEnteroTipeado(texto)
    if (id === null && texto.trim() !== '') return
    cambiarFiltro({ [campo]: id })
  }

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
          <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2">
            <span>{error}</span>
            <button type="button" className="btn btn-sm btn-outline-danger" onClick={cargar}>
              Reintentar
            </button>
          </div>
        )}
        {errorPuntosVenta && <div className="alert alert-warning py-1 px-2 small">{errorPuntosVenta}</div>}

        <div className="row g-2 align-items-end mb-3">
          <div className="col-md-2">
            <label className="form-label" htmlFor="auditoria-desde">
              Desde
            </label>
            <CampoFecha
              id="auditoria-desde"
              className="form-control"
              value={filtros.desde}
              onChange={(valor) => cambiarFiltro({ desde: valor })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="auditoria-hasta">
              Hasta
            </label>
            <CampoFecha
              id="auditoria-hasta"
              className="form-control"
              value={filtros.hasta}
              onChange={(valor) => cambiarFiltro({ hasta: valor })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="auditoria-accion">
              Acción
            </label>
            <select
              id="auditoria-accion"
              className="form-select"
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
              {...atributosDeCampoEntero()}
              className="form-control"
              placeholder="Id"
              value={filtros.idActor ?? ''}
              onChange={(e) => cambiarFiltroNumerico('idActor', e.target.value)}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="auditoria-entidad">
              Entidad
            </label>
            <input
              id="auditoria-entidad"
              type="text"
              className="form-control"
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
              {...atributosDeCampoEntero()}
              className="form-control"
              disabled={filtros.entidad === null}
              value={filtros.idEntidad ?? ''}
              onChange={(e) => cambiarFiltroNumerico('idEntidad', e.target.value)}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="auditoria-punto-venta">
              Punto de venta
            </label>
            <select
              id="auditoria-punto-venta"
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
              onError={toast.error}
            />
            {/* `/auditoria/export` exige desde/hasta no vacíos (AuditoriaEndpoints.cs) — con
                cualquiera de las dos fechas vacía, el botón queda deshabilitado con el motivo
                visible en vez de mandar una descarga que el servidor va a rechazar. */}
            {!puedeExportarAuditoria(filtros) && (
              <div className="small text-muted mt-1">Completá Desde y Hasta para descargar.</div>
            )}
          </div>
        </div>

        {cargando && !pagina && <Cargando />}

        {pagina && (
          <>
            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <EncabezadoOrdenable columna="fecha" orden={orden} onAlternar={alternarOrden}>
                      Fecha
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="accion" orden={orden} onAlternar={alternarOrden}>
                      Acción
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="entidad" orden={orden} onAlternar={alternarOrden}>
                      Entidad
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="idEntidad" orden={orden} onAlternar={alternarOrden}>
                      #Id
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="actor" orden={orden} onAlternar={alternarOrden}>
                      Actor
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="puntoVenta" orden={orden} onAlternar={alternarOrden}>
                      PV
                    </EncabezadoOrdenable>
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
                            <span title="Evento de toda la cuenta">—</span>
                          ) : (
                            (puntosVenta ?? []).find((pv) => pv.id === f.idPuntoVenta)?.nombre ?? `PV #${f.idPuntoVenta}`
                          )}
                        </td>
                        <td>
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-secondary"
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
                Página {pagina.pagina} de {totalPaginas} — {pluralizar(pagina.total, 'evento')}
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
