import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, rangoUltimosSieteDias } from '../api/reportes'
import type { EmpresaListado, ResumenDeGastos, ResumenDeVentas } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { GraficoDeLineas } from '../componentes/graficos/GraficoDeLineas'
import { aSerieDeGrafico } from '../componentes/graficos/series'

function formatearMoneda(valor: number): string {
  return `$${valor.toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

/**
 * Tablero (stage-10-agregacion-dashboard, Slice 7 — G1 parity): serie de ventas, serie de
 * gastos, netos y ticket promedio de los últimos 7 días por defecto, con `desde`/`hasta`
 * editables. La granularidad queda fija en `Dia` esta slice — el selector de granularidad y el
 * filtro por punto de venta llegan con los paneles de breakdown (Slice 8).
 *
 * Per `react-async-state` reglas 2/4/9: un único `useRef` de generación se bumpea antes de cada
 * fetch (disparado por cambio de empresa/rango), cada setter posterior a un `await` y el
 * `finally` que apaga `cargando` están gateados contra esa generación — una respuesta de 7 días
 * desactualizada nunca pisa un rango que el usuario ya cambió. `cargando` cubre la ventana
 * completa (ventas + gastos se piden y aplican como una sola unidad, siempre disparados por el
 * mismo evento de filtro — no son entidades operables independientemente, a diferencia del
 * listado + saldo de `Compras.tsx`).
 */
export function Tablero() {
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [idEmpresa, setIdEmpresa] = useState<number | null>(null)
  const [errorEmpresas, setErrorEmpresas] = useState('')

  const [desde, setDesde] = useState(() => rangoUltimosSieteDias().desde)
  const [hasta, setHasta] = useState(() => rangoUltimosSieteDias().hasta)

  const [ventas, setVentas] = useState<ResumenDeVentas | null>(null)
  const [gastos, setGastos] = useState<ResumenDeGastos | null>(null)
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  useEffect(() => {
    let vigente = true
    clienteDeOrganizacion
      .listarEmpresas()
      .then((lista) => {
        if (!vigente) return
        setEmpresas(lista)
        if (lista.length > 0) setIdEmpresa(lista[0].id)
      })
      .catch((e) => {
        if (!vigente) return
        setEmpresas([])
        setErrorEmpresas(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las empresas.')
      })

    return () => {
      vigente = false
    }
  }, [])

  const cargar = useCallback(() => {
    if (idEmpresa === null) return
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    const filtros = { idEmpresa, idPuntoVenta: null, desde, hasta, granularidad: 'Dia' as const }

    Promise.all([clienteDeReportes.ventasResumen(filtros), clienteDeReportes.gastosResumen(filtros)])
      .then(([resumenVentas, resumenGastos]) => {
        if (generacionRef.current !== miGeneracion) return
        setVentas(resumenVentas)
        setGastos(resumenGastos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setVentas(null)
        setGastos(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el tablero.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [idEmpresa, desde, hasta])

  useEffect(() => {
    cargar()
  }, [cargar])

  return (
    <div className="container-fluid py-4">
      <Box titulo="Tablero" variante="inverse">
        {errorEmpresas && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorEmpresas}</div>}

        {empresas === null ? (
          <Cargando />
        ) : empresas.length === 0 ? (
          <p className="text-muted text-center py-4">No hay empresas visibles para mostrar el tablero.</p>
        ) : (
          <>
            <div className="row g-3 align-items-end mb-4">
              <div className="col-auto">
                <label className="form-label" htmlFor="tablero-empresa">
                  Empresa
                </label>
                <select
                  id="tablero-empresa"
                  className="form-select rounded-0"
                  value={idEmpresa ?? ''}
                  disabled={cargando}
                  onChange={(e) => setIdEmpresa(Number(e.target.value))}
                >
                  {empresas.map((e) => (
                    <option key={e.id} value={e.id}>
                      {e.razonSocial}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-auto">
                <label className="form-label" htmlFor="tablero-desde">
                  Desde
                </label>
                <input
                  id="tablero-desde"
                  type="date"
                  className="form-control rounded-0"
                  value={desde}
                  disabled={cargando}
                  onChange={(e) => setDesde(e.target.value)}
                />
              </div>
              <div className="col-auto">
                <label className="form-label" htmlFor="tablero-hasta">
                  Hasta
                </label>
                <input
                  id="tablero-hasta"
                  type="date"
                  className="form-control rounded-0"
                  value={hasta}
                  disabled={cargando}
                  onChange={(e) => setHasta(e.target.value)}
                />
              </div>
            </div>

            {error && (
              <div className="alert alert-danger rounded-0 d-flex justify-content-between align-items-center gap-2">
                <span>{error}</span>
                <button type="button" className="btn btn-sm btn-outline-danger rounded-0" onClick={cargar}>
                  Reintentar
                </button>
              </div>
            )}

            {cargando && !ventas && !gastos && <Cargando />}

            {ventas && gastos && (
              <>
                <div className="row g-3 mb-4">
                  <div className="col-md-3">
                    <div className="border p-3 bg-white text-center">
                      <div className="text-muted small">Ventas netas</div>
                      <div className="fs-4">{formatearMoneda(ventas.netoVendido)}</div>
                    </div>
                  </div>
                  <div className="col-md-3">
                    <div className="border p-3 bg-white text-center">
                      <div className="text-muted small">Gastos</div>
                      <div className="fs-4">{formatearMoneda(gastos.importeTotal)}</div>
                    </div>
                  </div>
                  <div className="col-md-3">
                    <div className="border p-3 bg-white text-center">
                      <div className="text-muted small">Ticket promedio</div>
                      <div className="fs-4">{ventas.ticketPromedio === null ? '—' : formatearMoneda(ventas.ticketPromedio)}</div>
                    </div>
                  </div>
                  <div className="col-md-3">
                    <div className="border p-3 bg-white text-center">
                      <div className="text-muted small">Transacciones</div>
                      <div className="fs-4">{ventas.cantidadTx}</div>
                    </div>
                  </div>
                </div>

                <div className="row g-3">
                  <div className="col-md-6">
                    <div className="border p-3 bg-white">
                      <h6>Serie de ventas</h6>
                      <GraficoDeLineas data={aSerieDeGrafico(ventas.serie.map((b) => ({ etiqueta: b.etiqueta, valor: b.neto })))} alto={240} titulo="Serie de ventas netas por período" />
                    </div>
                  </div>
                  <div className="col-md-6">
                    <div className="border p-3 bg-white">
                      <h6>Serie de gastos</h6>
                      <GraficoDeLineas data={aSerieDeGrafico(gastos.serie.map((b) => ({ etiqueta: b.etiqueta, valor: b.importe })))} alto={240} titulo="Serie de gastos por período" />
                    </div>
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
