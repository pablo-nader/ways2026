import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, rutasDeExportacion } from '../api/reportes'
import type { Existencias as ExistenciasRespuesta, PuntoVentaListado } from '../api/tipos'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

function formatearCantidad(valor: number): string {
  return valor.toLocaleString('es-AR', { minimumFractionDigits: 0, maximumFractionDigits: 3 })
}

/**
 * Existencias (stage-11-exportacion-reportes, Slice 9 — web, droppable a Etapa 13; design: Web
 * Composition; proposal decisión 10) — pantalla modesta: `stock` ⋈ `articulos` de UN punto de
 * venta, sin filtro de fecha (el stock no tiene dimensión temporal) y sin paginado (agregado
 * acotado por construcción, design decisión 6). Misma política que `/tablero`
 * (`Politicas.LecturaDeReportes`: Supervisor + Admin).
 */
export function Existencias() {
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

  const [idPuntoVenta, setIdPuntoVenta] = useState<number | null>(null)
  const [existencias, setExistencias] = useState<ExistenciasRespuesta | null>(null)
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
      .existencias(idPuntoVenta)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setExistencias(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setExistencias(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las existencias.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [idPuntoVenta])

  useEffect(() => {
    cargar()
  }, [cargar])

  return (
    <div className="container-fluid py-4">
      <Box titulo="Existencias" variante="inverse">
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
          <p className="text-muted text-center py-4">No hay puntos de venta visibles para las existencias.</p>
        ) : idPuntoVenta === null ? (
          <Cargando />
        ) : (
          <>
            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-3">
                <label className="form-label" htmlFor="existencias-punto-venta">
                  Punto de venta
                </label>
                <select
                  id="existencias-punto-venta"
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
                  ruta={rutasDeExportacion.existencias(idPuntoVenta)}
                  etiqueta="Descargar"
                  onError={setErrorDescarga}
                  onInicio={() => setErrorDescarga('')}
                />
              </div>
            </div>

            {errorDescarga && <div className="alert alert-danger rounded-0 py-1 px-2 small mb-2">{errorDescarga}</div>}

            {cargando && !existencias && <Cargando />}

            {existencias && (
              <div className="table-responsive">
                <table className="table table-sm table-striped table-bordered align-middle">
                  <thead>
                    <tr>
                      <th>Artículo</th>
                      <th>Nombre</th>
                      <th className="text-end">Cantidad</th>
                    </tr>
                  </thead>
                  <tbody>
                    {existencias.filas.map((fila) => (
                      <tr key={fila.idArticulo}>
                        <td>{fila.idArticulo}</td>
                        <td>{fila.nombre}</td>
                        <td className="text-end">{formatearCantidad(fila.cantidad)}</td>
                      </tr>
                    ))}
                    {existencias.filas.length === 0 && (
                      <tr>
                        <td colSpan={3} className="text-center text-muted py-4">
                          No hay stock cargado para este punto de venta.
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
