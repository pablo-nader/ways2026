import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { etiquetaDeSucursal } from '../api/organizacion'
import { clienteDeReportes, rutasDeExportacion } from '../api/reportes'
import type { EstadoDeVencimiento, FilaDeVencimiento, Vencimientos as VencimientosRespuesta } from '../api/tipos'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { toast } from '../toasts/toasts'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { EncabezadoOrdenable, useOrdenamiento, type ColumnasOrdenables } from '../componentes/grilla'
import { useSucursales } from '../componentes/useSucursales'
import { atributosDeCampoEntero, parsearEnteroTipeado } from '../formato/numeros'
import { pluralizar } from '../formato/pluralizar'

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

const COLUMNAS: ColumnasOrdenables<FilaDeVencimiento> = {
  articulo: { valor: (f) => f.articulo },
  lote: { valor: (f) => f.codigoLote },
  vencimiento: { valor: (f) => (f.fechaVencimiento === null ? null : new Date(f.fechaVencimiento)) },
  cantidad: { valor: (f) => f.cantidad },
  estado: { valor: (f) => BADGE_POR_ESTADO[f.estado].etiqueta },
}

function BadgeDeEstado({ estado }: { estado: EstadoDeVencimiento }) {
  const { etiqueta, clase } = BADGE_POR_ESTADO[estado]
  return <span className={`badge ${clase}`}>{etiqueta}</span>
}

/**
 * Vencimientos (stage-12-lotes-vencimientos, Slice 15 — web; design decisión 15/16/17, spec
 * lotes-y-vencimientos: "Vencimientos Report Resolves 'Hoy' In The Punto De Venta's Own Zona
 * Horaria, With An Export Sibling"): lotes con saldo positivo de una sucursal (el stock es de la sucursal),
 * clasificados en las cuatro categorías del reporte. Mismo patrón que `Existencias.tsx` (selector de sucursal
 * + botón de descarga + tabla, sin paginado — listado acotado por construcción salvo el
 * tope del export) más un filtro propio de `dias` (horizonte de alerta, opcional — el servidor
 * resuelve `dias_alerta_vencimiento` cuando se omite). Misma política que `/tablero`/
 * `/reportes/existencias` (`Politicas.LecturaDeReportes`: Supervisor + Admin).
 */
export function Vencimientos() {
  const { sucursales, error: errorSucursales } = useSucursales()
  const [sucursalElegida, setSucursalElegida] = useState<number | null>(null)
  // Mientras no se eligió ninguna, la primera: sin efecto que la fije después de la carga.
  const idSucursal = sucursalElegida ?? sucursales?.[0]?.id ?? null
  const variasEmpresas = new Set((sucursales ?? []).map((s) => s.idEmpresa)).size > 1
  const [dias, setDias] = useState('')
  const [vencimientos, setVencimientos] = useState<VencimientosRespuesta | null>(null)
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  // `dias` vacío ⇒ `null` — deja que el servidor resuelva `dias_alerta_vencimiento` (nunca se
  // manda un valor inventado del lado del cliente cuando el servidor ya sabe resolverlo).
  const diasNumero = parsearEnteroTipeado(dias)

  const cargar = useCallback(() => {
    if (idSucursal === null) return
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    clienteDeReportes
      .vencimientos(idSucursal, diasNumero)
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
  }, [idSucursal, diasNumero])

  useEffect(() => {
    cargar()
  }, [cargar])

  const filasDelReporte = useMemo(() => vencimientos?.filas ?? [], [vencimientos])
  const { filas, orden, alternar } = useOrdenamiento(filasDelReporte, COLUMNAS)

  return (
    <div className="container-fluid py-4">
      <Box titulo="Vencimientos" variante="inverse">
        {error && (
          <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2">
            <span>{error}</span>
            <button type="button" className="btn btn-sm btn-outline-danger" onClick={cargar}>
              Reintentar
            </button>
          </div>
        )}
        {errorSucursales && <div className="alert alert-warning py-1 px-2 small">{errorSucursales}</div>}

        {sucursales === null ? (
          <Cargando />
        ) : sucursales.length === 0 ? (
          <p className="text-muted text-center py-4">No hay sucursales visibles para los vencimientos.</p>
        ) : idSucursal === null ? (
          <Cargando />
        ) : (
          <>
            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-3">
                <label className="form-label" htmlFor="vencimientos-sucursal">
                  Sucursal
                </label>
                <select
                  id="vencimientos-sucursal"
                  className="form-select"
                  value={idSucursal}
                  onChange={(e) => setSucursalElegida(Number(e.target.value))}
                >
                  {sucursales.map((s) => (
                    <option key={s.id} value={s.id}>
                      {etiquetaDeSucursal(s, variasEmpresas)}
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
                  {...atributosDeCampoEntero()}
                  className="form-control"
                  placeholder="Default de la empresa"
                  value={dias}
                  onChange={(e) => setDias(e.target.value)}
                />
              </div>
              <div className="col-auto">
                <BotonDeDescarga
                  ruta={rutasDeExportacion.vencimientos(idSucursal, diasNumero)}
                  etiqueta="Descargar"
                  onError={toast.error}
                />
              </div>
            </div>

            {cargando && !vencimientos && <Cargando />}

            {vencimientos && (
              <>
                <p className="text-muted small">
                  Vencimientos al {vencimientos.hoy} ({vencimientos.zonaHoraria}) — horizonte de alerta:{' '}
                  {pluralizar(vencimientos.diasDeAlerta, 'día')}.
                </p>
                <div className="table-responsive">
                  <table className="table table-sm table-striped table-bordered align-middle">
                    <thead>
                      <tr>
                        <EncabezadoOrdenable columna="articulo" orden={orden} onAlternar={alternar}>
                          Artículo
                        </EncabezadoOrdenable>
                        <EncabezadoOrdenable columna="lote" orden={orden} onAlternar={alternar}>
                          Lote
                        </EncabezadoOrdenable>
                        <EncabezadoOrdenable columna="vencimiento" orden={orden} onAlternar={alternar}>
                          Vencimiento
                        </EncabezadoOrdenable>
                        <EncabezadoOrdenable columna="cantidad" orden={orden} onAlternar={alternar} className="text-end">
                          Cantidad
                        </EncabezadoOrdenable>
                        <EncabezadoOrdenable columna="estado" orden={orden} onAlternar={alternar}>
                          Estado
                        </EncabezadoOrdenable>
                      </tr>
                    </thead>
                    <tbody>
                      {filas.map((fila) => (
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
                            No hay lotes con saldo positivo para esta sucursal.
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
