import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, rutasDeExportacion } from '../api/reportes'
import type { EstadoDeVencimiento, PuntoVentaListado, Vencimientos as VencimientosRespuesta } from '../api/tipos'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

function formatearCantidad(valor: number): string {
  return valor.toLocaleString('es-AR', { minimumFractionDigits: 0, maximumFractionDigits: 3 })
}

/** Espejo de la clasificación en cuatro estados del reporte — badge por fila (spec
 * lotes-y-vencimientos: "vencido si ya pasó, por_vencer si entra dentro del horizonte de alerta,
 * vigente más allá del horizonte, sin_fecha para el lote sin identificar"). */
const BADGE_POR_ESTADO: Record<EstadoDeVencimiento, { etiqueta: string; clase: string }> = {
  Vencido: { etiqueta: 'Vencido', clase: 'text-bg-danger' },
  PorVencer: { etiqueta: 'Por vencer', clase: 'text-bg-warning' },
  Vigente: { etiqueta: 'Vigente', clase: 'text-bg-success' },
  SinFecha: { etiqueta: 'Sin fecha', clase: 'text-bg-secondary' },
}

function BadgeDeEstado({ estado }: { estado: EstadoDeVencimiento }) {
  const { etiqueta, clase } = BADGE_POR_ESTADO[estado]
  return <span className={`badge rounded-0 ${clase}`}>{etiqueta}</span>
}

/**
 * Vencimientos (stage-12-lotes-vencimientos, Slice 15 — web; design decisión 15/16/17, spec
 * lotes-y-vencimientos: "Vencimientos Report Resolves 'Hoy' In The Punto De Venta's Own Zona
 * Horaria, With An Export Sibling"): lotes con saldo positivo de un punto de venta, clasificados
 * en las cuatro categorías del reporte. Mismo patrón que `Existencias.tsx` (selector de punto de
 * venta + botón de descarga + tabla, sin paginado — listado acotado por construcción salvo el
 * tope del export) más un filtro propio de `dias` (horizonte de alerta, opcional — el servidor
 * resuelve `dias_alerta_vencimiento` cuando se omite). Misma política que `/tablero`/
 * `/reportes/existencias` (`Politicas.LecturaDeReportes`: Supervisor + Admin).
 */
export function Vencimientos() {
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

  const [idPuntoVenta, setIdPuntoVenta] = useState<number | null>(null)
  const [dias, setDias] = useState('')
  const [vencimientos, setVencimientos] = useState<VencimientosRespuesta | null>(null)
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

  // `dias` vacío ⇒ `null` — deja que el servidor resuelva `dias_alerta_vencimiento` (nunca se
  // manda un valor inventado del lado del cliente cuando el servidor ya sabe resolverlo).
  const diasNumero = dias.trim() === '' ? null : Number(dias)

  const cargar = useCallback(() => {
    if (idPuntoVenta === null) return
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    clienteDeReportes
      .vencimientos(idPuntoVenta, diasNumero)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setVencimientos(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setVencimientos(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los vencimientos.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [idPuntoVenta, diasNumero])

  useEffect(() => {
    cargar()
  }, [cargar])

  return (
    <div className="container-fluid py-4">
      <Box titulo="Vencimientos" variante="inverse">
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
          <p className="text-muted text-center py-4">No hay puntos de venta visibles para los vencimientos.</p>
        ) : idPuntoVenta === null ? (
          <Cargando />
        ) : (
          <>
            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-3">
                <label className="form-label" htmlFor="vencimientos-punto-venta">
                  Punto de venta
                </label>
                <select
                  id="vencimientos-punto-venta"
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
              <div className="col-md-3">
                <label className="form-label" htmlFor="vencimientos-dias">
                  Días de alerta
                </label>
                <input
                  id="vencimientos-dias"
                  type="number"
                  step="1"
                  min="0"
                  className="form-control rounded-0"
                  placeholder="Default de la empresa"
                  value={dias}
                  onChange={(e) => setDias(e.target.value)}
                />
              </div>
              <div className="col-auto">
                <BotonDeDescarga
                  ruta={rutasDeExportacion.vencimientos(idPuntoVenta, diasNumero)}
                  etiqueta="Descargar"
                  onError={setErrorDescarga}
                  onInicio={() => setErrorDescarga('')}
                />
              </div>
            </div>

            {errorDescarga && <div className="alert alert-danger rounded-0 py-1 px-2 small mb-2">{errorDescarga}</div>}

            {cargando && !vencimientos && <Cargando />}

            {vencimientos && (
              <>
                <p className="text-muted small">
                  Vencimientos al {vencimientos.hoy} ({vencimientos.zonaHoraria}) — horizonte de alerta:{' '}
                  {vencimientos.diasDeAlerta} día(s).
                </p>
                <div className="table-responsive">
                  <table className="table table-sm table-striped table-bordered align-middle">
                    <thead>
                      <tr>
                        <th>Artículo</th>
                        <th>Lote</th>
                        <th>Vencimiento</th>
                        <th className="text-end">Cantidad</th>
                        <th>Estado</th>
                      </tr>
                    </thead>
                    <tbody>
                      {vencimientos.filas.map((fila) => (
                        <tr key={fila.idLote}>
                          <td>{fila.articulo}</td>
                          <td>{fila.codigoLote}</td>
                          <td>{fila.fechaVencimiento ?? '—'}</td>
                          <td className="text-end">{formatearCantidad(fila.cantidad)}</td>
                          <td>
                            <BadgeDeEstado estado={fila.estado} />
                          </td>
                        </tr>
                      ))}
                      {vencimientos.filas.length === 0 && (
                        <tr>
                          <td colSpan={5} className="text-center text-muted py-4">
                            No hay lotes con saldo positivo para este punto de venta.
                          </td>
                        </tr>
                      )}
                    </tbody>
                  </table>
                </div>
              </>
            )}
          </>
        )}
      </Box>
    </div>
  )
}
