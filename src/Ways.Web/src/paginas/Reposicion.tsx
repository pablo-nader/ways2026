import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, rutasDeExportacion } from '../api/reportes'
import { ROL } from '../api/tipos'
import type { PuntoVentaListado, Reposicion as ReposicionRespuesta } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { agruparPorProveedor, type GrupoDeReposicion } from './agruparPorProveedor'
import { itemsDeOrdenDesdeFilasDeReposicion } from './filasDeReposicionAOrdenDeCompra'

function formatearCantidad(valor: number): string {
  return valor.toLocaleString('es-AR', { minimumFractionDigits: 0, maximumFractionDigits: 3 })
}

/** `sugerido` (y cualquier otro campo nullable de la fila) renderiza `—`, NUNCA `0` — la celda
 * que un operador leería como "no comprar nada" cuando en realidad el dato no existe (spec
 * reposicion-de-stock: sugerido Is Null, Never Zero, When Reposicion Is Unset). */
function formatearCantidadNullable(valor: number | null): string {
  return valor === null ? '—' : formatearCantidad(valor)
}

/**
 * Reposición (stage-13-stock-inteligente, Slice 6 — web; design: "Reposicion.tsx — grouped by
 * proveedor (slice 6)"): artículos bajo su mínimo de un punto de venta, agrupados por proveedor
 * habitual — mismo shape que `Existencias.tsx` (selector de punto de venta + botón de descarga +
 * tabla, sin paginado). El agrupamiento es un FOLD puro sobre la lista ya ordenada que devuelve
 * el servidor (`agruparPorProveedor`, design decisión 4) — nunca un sort del lado del cliente.
 * Misma política que `/tablero`/`/reportes/existencias` (`Politicas.LecturaDeReportes`:
 * Supervisor + Admin).
 *
 * stage-16-ordenes-de-compra, Slice 6 (design decisión 16, tasks.md decisión 24): el botón "Generar
 * OC" por grupo se gatea a `grupo.idProveedor !== null` **y** `usuario.rolId === ROL.Admin` — las
 * escrituras de OC son Admin-only del lado del servidor (`GestionDeCatalogo`); un Supervisor sigue
 * viendo esta pantalla exactamente igual que hoy, solo sin el botón. El mapeo reposición→OC es
 * ENTERAMENTE client-side (`itemsDeOrdenDesdeFilasDeReposicion`) — postea a la `POST
 * /api/ordenes-compra` ya existente, sin tocar `GET /api/reportes/stock/reposicion`. El `Link`/
 * `navigate` de entrada pasa `state` con los datos ya resueltos (lección de la Slice 6 de la etapa
 * 15: nunca un destino que dependa de un fetch decorativo para recuperar lo que el origen ya
 * tenía).
 */
export function Reposicion() {
  const navigate = useNavigate()
  const { usuario } = useAuth()
  const esAdmin = usuario !== null && usuario.rolId === ROL.Admin

  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

  const [idPuntoVenta, setIdPuntoVenta] = useState<number | null>(null)
  const [reposicion, setReposicion] = useState<ReposicionRespuesta | null>(null)
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
        if (lista.length > 0) setIdPuntoVenta(lista[0].id)
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
    if (idPuntoVenta === null) return
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    clienteDeReportes
      .reposicion(idPuntoVenta, null)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setReposicion(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setReposicion(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar la reposición.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [idPuntoVenta])

  useEffect(() => {
    cargar()
  }, [cargar])

  const grupos = reposicion ? agruparPorProveedor(reposicion.filas) : []

  // El "Sin proveedor" (idProveedor null) nunca llega acá: no hay a quién enviarle la OC
  // (ordenes-de-compra/spec.md: "The Sin proveedor bucket cannot produce an OC") — el botón ni se
  // renderiza para ese grupo (mutation target #34c, parte 1). Pasa el header + los items ya
  // mapeados por `location.state`, nunca un fetch redundante en la pantalla destino.
  function generarOrdenDeCompra(grupo: GrupoDeReposicion) {
    if (grupo.idProveedor === null || idPuntoVenta === null) return
    navigate('/ordenes-compra/nueva', {
      state: { idProveedor: grupo.idProveedor, idPuntoVenta, items: itemsDeOrdenDesdeFilasDeReposicion(grupo.filas) },
    })
  }

  return (
    <div className="container-fluid py-4">
      <Box titulo="Reposición" variante="inverse">
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
          <p className="text-muted text-center py-4">No hay puntos de venta visibles para la reposición.</p>
        ) : idPuntoVenta === null ? (
          <Cargando />
        ) : (
          <>
            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-3">
                <label className="form-label" htmlFor="reposicion-punto-venta">
                  Punto de venta
                </label>
                <select
                  id="reposicion-punto-venta"
                  className="form-select rounded-0"
                  value={idPuntoVenta}
                  onChange={(e) => setIdPuntoVenta(Number(e.target.value))}
                >
                  {puntosVenta.map((pv) => (
                    <option key={pv.id} value={pv.id}>
                      {pv.nombre}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-auto">
                <BotonDeDescarga
                  ruta={rutasDeExportacion.reposicion(idPuntoVenta, null)}
                  etiqueta="Descargar"
                  onError={setErrorDescarga}
                  onInicio={() => setErrorDescarga('')}
                />
              </div>
            </div>

            {errorDescarga && <div className="alert alert-danger rounded-0 py-1 px-2 small mb-2">{errorDescarga}</div>}

            {cargando && !reposicion && <Cargando />}

            {reposicion && (
              <div className="table-responsive">
                <table className="table table-sm table-striped table-bordered align-middle">
                  <thead>
                    <tr>
                      <th>Artículo</th>
                      <th className="text-end">Cantidad</th>
                      <th className="text-end">Mínimo</th>
                      <th className="text-end">Reposición</th>
                      <th className="text-end">Sugerido</th>
                    </tr>
                  </thead>
                  <tbody>
                    {grupos.map((grupo) => (
                      <Fragment key={grupo.idProveedor ?? 'sin-proveedor'}>
                        <tr className="table-secondary">
                          <td colSpan={5}>
                            <div className="d-flex justify-content-between align-items-center">
                              <span>
                                {grupo.proveedor ?? 'Sin proveedor'} ({grupo.filas.length})
                              </span>
                              {grupo.idProveedor !== null && esAdmin && (
                                <button
                                  type="button"
                                  className="btn btn-sm btn-outline-primary rounded-0"
                                  onClick={() => generarOrdenDeCompra(grupo)}
                                >
                                  Generar OC
                                </button>
                              )}
                            </div>
                          </td>
                        </tr>
                        {grupo.filas.map((fila) => (
                          <tr key={fila.idArticulo}>
                            <td>{fila.articulo}</td>
                            <td className="text-end">{formatearCantidad(fila.cantidad)}</td>
                            <td className="text-end">{formatearCantidad(fila.minimo)}</td>
                            <td className="text-end">{formatearCantidadNullable(fila.reposicion)}</td>
                            <td className="text-end">{formatearCantidadNullable(fila.sugerido)}</td>
                          </tr>
                        ))}
                      </Fragment>
                    ))}
                    {reposicion.filas.length === 0 && (
                      <tr>
                        <td colSpan={5} className="text-center text-muted py-4">
                          No hay artículos bajo el mínimo para este punto de venta.
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}
      </Box>
    </div>
  )
}
