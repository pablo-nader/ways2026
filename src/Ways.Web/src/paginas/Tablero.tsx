import { useCallback, useEffect, useRef, useState } from 'react'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, rangoUltimosSieteDias } from '../api/reportes'
import type {
  EmpresaListado,
  Granularidad,
  MedioPagoAlta,
  MedioPagoListado,
  PuntoVentaListado,
  ResumenDeGastos,
  ResumenDeVentas,
  TopArticulos,
  VentasPorMedioPago,
  VentasPorPuntoVenta,
  VentasPorVendedor,
} from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { GraficoDeBarras } from '../componentes/graficos/GraficoDeBarras'
import { GraficoDeLineas } from '../componentes/graficos/GraficoDeLineas'
import { aSerieDeGrafico } from '../componentes/graficos/series'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

/** `articulos/top` no trae un selector de "Top N" en esta slice — límite fijo, sensible para un
 * panel de tablero (ni tan corto que pierda contexto, ni tan largo que rompa el gráfico de
 * barras). *(ReportesEndpoints.cs: `limite` es `int?`, opcional — sin límite el backend
 * devuelve el ranking completo, no aplica un default propio; este valor es la elección del
 * cliente)*. */
const LIMITE_TOP_ARTICULOS = 10

const GRANULARIDADES: { valor: Granularidad; etiqueta: string }[] = [
  { valor: 'Dia', etiqueta: 'Día' },
  { valor: 'Semana', etiqueta: 'Semana' },
  { valor: 'Mes', etiqueta: 'Mes' },
]

function formatearMoneda(valor: number): string {
  return `$${valor.toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

/**
 * Hook compartido de los paneles de desglose (Slice 8): cada instancia trae su PROPIA generación,
 * `cargando` y `error` — nunca el par compartido de la card G1 (deviation registrada en tasks.md
 * antes de la tarea 7.6, vinculante para esta slice: react-async-state regla 5/10). `cargarDatos`
 * ya viene armado con sus propias dependencias vía `useCallback` del llamador, así que este hook
 * solo depende de esa referencia — evita pasar un array de dependencias dinámico a `useCallback`.
 */
function usePanelDeReporte<T>(cargarDatos: () => Promise<T>, mensajeError: string) {
  const [datos, setDatos] = useState<T | null>(null)
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  const cargar = useCallback(() => {
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    cargarDatos()
      .then((respuesta) => {
        if (generacionRef.current !== miGeneracion) return
        setDatos(respuesta)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setDatos(null)
        setError(e instanceof ErrorApi ? e.message : mensajeError)
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [cargarDatos, mensajeError])

  useEffect(() => {
    cargar()
  }, [cargar])

  return { datos, cargando, error, reintentar: cargar }
}

function PanelDeError({ error, onReintentar }: { error: string; onReintentar: () => void }) {
  return (
    <div className="alert alert-danger rounded-0 d-flex justify-content-between align-items-center gap-2 py-1 px-2 small">
      <span>{error}</span>
      <button type="button" className="btn btn-sm btn-outline-danger rounded-0" onClick={onReintentar}>
        Reintentar
      </button>
    </div>
  )
}

type PropsPanelPorPuntoVenta = { idEmpresa: number; desde: string; hasta: string; puntosVenta: PuntoVentaListado[] }

function PanelPorPuntoVenta({ idEmpresa, desde, hasta, puntosVenta }: PropsPanelPorPuntoVenta) {
  const cargarDatos = useCallback(
    () => clienteDeReportes.ventasPorPuntoVenta({ idEmpresa, desde, hasta }),
    [idEmpresa, desde, hasta],
  )
  const { datos, cargando, error, reintentar } = usePanelDeReporte<VentasPorPuntoVenta>(
    cargarDatos,
    'No se pudo cargar el desglose por punto de venta.',
  )
  const nombreDe = useCallback(
    (id: number) => puntosVenta.find((pv) => pv.id === id)?.nombre ?? `PV #${id}`,
    [puntosVenta],
  )

  return (
    <div className="border p-3 bg-white h-100">
      <h6>Ventas por punto de venta</h6>
      {error && <PanelDeError error={error} onReintentar={reintentar} />}
      {cargando && !datos && <Cargando />}
      {datos && (
        <>
          <GraficoDeBarras
            data={datos.filas.map((f) => ({ etiqueta: nombreDe(f.idPuntoVenta), valor: f.neto }))}
            alto={200}
            titulo="Ventas netas por punto de venta"
          />
          <table className="table table-sm mt-2 mb-0">
            <thead>
              <tr>
                <th>Punto de venta</th>
                <th>Neto</th>
                <th>TX</th>
                <th>Ticket promedio</th>
              </tr>
            </thead>
            <tbody>
              {datos.filas.map((f) => (
                <tr key={f.idPuntoVenta}>
                  <td>{nombreDe(f.idPuntoVenta)}</td>
                  <td>{formatearMoneda(f.neto)}</td>
                  <td>{f.cantidadTx}</td>
                  <td>{f.ticketPromedio === null ? '—' : formatearMoneda(f.ticketPromedio)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  )
}

type PropsPanelPorVendedor = { idEmpresa: number; desde: string; hasta: string; idPuntoVenta: number | null }

function PanelPorVendedor({ idEmpresa, desde, hasta, idPuntoVenta }: PropsPanelPorVendedor) {
  const cargarDatos = useCallback(
    () => clienteDeReportes.ventasPorVendedor({ idEmpresa, idPuntoVenta, desde, hasta }),
    [idEmpresa, idPuntoVenta, desde, hasta],
  )
  const { datos, cargando, error, reintentar } = usePanelDeReporte<VentasPorVendedor>(
    cargarDatos,
    'No se pudo cargar el desglose por vendedor.',
  )

  return (
    <div className="border p-3 bg-white h-100">
      <h6>Ventas por vendedor</h6>
      {error && <PanelDeError error={error} onReintentar={reintentar} />}
      {cargando && !datos && <Cargando />}
      {datos && (
        <>
          <GraficoDeBarras
            data={datos.filas.map((f) => ({ etiqueta: `Vendedor #${f.idEmpleado}`, valor: f.neto }))}
            alto={200}
            titulo="Ventas netas por vendedor"
          />
          <table className="table table-sm mt-2 mb-0">
            <thead>
              <tr>
                <th>Vendedor</th>
                <th>Neto</th>
                <th>TX</th>
                <th>Ticket promedio</th>
              </tr>
            </thead>
            <tbody>
              {datos.filas.map((f) => (
                <tr key={f.idEmpleado}>
                  <td>Vendedor #{f.idEmpleado}</td>
                  <td>{formatearMoneda(f.neto)}</td>
                  <td>{f.cantidadTx}</td>
                  <td>{f.ticketPromedio === null ? '—' : formatearMoneda(f.ticketPromedio)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  )
}

type PropsPanelPorMedioPago = {
  idEmpresa: number
  desde: string
  hasta: string
  idPuntoVenta: number | null
  mediosPago: MedioPagoListado[]
}

function PanelPorMedioPago({ idEmpresa, desde, hasta, idPuntoVenta, mediosPago }: PropsPanelPorMedioPago) {
  const cargarDatos = useCallback(
    () => clienteDeReportes.ventasPorMedioPago({ idEmpresa, idPuntoVenta, desde, hasta }),
    [idEmpresa, idPuntoVenta, desde, hasta],
  )
  const { datos, cargando, error, reintentar } = usePanelDeReporte<VentasPorMedioPago>(
    cargarDatos,
    'No se pudo cargar el desglose por medio de pago.',
  )
  const nombreDe = useCallback(
    (id: number) => mediosPago.find((m) => m.id === id)?.nombre ?? `Medio #${id}`,
    [mediosPago],
  )

  return (
    <div className="border p-3 bg-white h-100">
      <h6>Ventas por medio de pago</h6>
      {error && <PanelDeError error={error} onReintentar={reintentar} />}
      {cargando && !datos && <Cargando />}
      {datos && (
        <>
          <GraficoDeBarras
            data={datos.filas.map((f) => ({ etiqueta: nombreDe(f.idMedioPago), valor: f.neto }))}
            alto={200}
            titulo="Ventas netas por medio de pago"
          />
          <table className="table table-sm mt-2 mb-0">
            <thead>
              <tr>
                <th>Medio de pago</th>
                <th>Neto</th>
                <th>Cant. de pagos</th>
              </tr>
            </thead>
            <tbody>
              {datos.filas.map((f) => (
                <tr key={f.idMedioPago}>
                  <td>{nombreDe(f.idMedioPago)}</td>
                  <td>{formatearMoneda(f.neto)}</td>
                  <td>{f.cantidadPagos}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  )
}

type PropsPanelTopArticulos = { idEmpresa: number; desde: string; hasta: string; idPuntoVenta: number | null }

function PanelTopArticulos({ idEmpresa, desde, hasta, idPuntoVenta }: PropsPanelTopArticulos) {
  const cargarDatos = useCallback(
    () => clienteDeReportes.articulosTop({ idEmpresa, idPuntoVenta, desde, hasta, limite: LIMITE_TOP_ARTICULOS }),
    [idEmpresa, idPuntoVenta, desde, hasta],
  )
  const { datos, cargando, error, reintentar } = usePanelDeReporte<TopArticulos>(
    cargarDatos,
    'No se pudo cargar el ranking de artículos.',
  )

  return (
    <div className="border p-3 bg-white h-100">
      <h6>Top artículos</h6>
      {error && <PanelDeError error={error} onReintentar={reintentar} />}
      {cargando && !datos && <Cargando />}
      {datos && (
        <>
          <GraficoDeBarras
            data={datos.articulos.map((a) => ({ etiqueta: a.descripcion, valor: a.total }))}
            alto={200}
            titulo="Top artículos por monto neto vendido"
          />
          <table className="table table-sm mt-2 mb-0">
            <thead>
              <tr>
                <th>Artículo</th>
                <th>Cantidad</th>
                <th>Total</th>
              </tr>
            </thead>
            <tbody>
              {datos.articulos.map((a) => (
                <tr key={a.idArticulo}>
                  <td>{a.descripcion}</td>
                  <td>{a.cantidad}</td>
                  <td>{formatearMoneda(a.total)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  )
}

/**
 * Tablero (stage-10-agregacion-dashboard, Slice 7 — G1 parity; Slice 8 — filtro compartido +
 * paneles de desglose): serie de ventas, serie de gastos, netos y ticket promedio de los últimos
 * 7 días por defecto, con `desde`/`hasta`/`granularidad`/punto de venta editables — el mismo
 * filtro alimenta la card G1 y los cuatro paneles de desglose — por punto de venta, por vendedor,
 * por medio de pago, top artículos (spec tablero: Breakdown Panels Share Range And Granularity
 * Controls). `granularidad` solo la lee `ventas/resumen`/`gastos/resumen`: ninguno de los cuatro
 * breakdowns bucketea por tiempo, cada fila ya es su propio subtotal del período completo.
 * `idPuntoVenta` viaja a todos salvo `ventas/por-punto-venta` (sería agrupar y filtrar por el
 * mismo campo — design: Endpoints); `articulos/top` sí lo acepta, igual que por-vendedor/
 * por-medio-pago (`ReportesEndpoints.cs`: `int? idPuntoVenta`).
 *
 * Per `react-async-state` reglas 2/4/9: un único `useRef` de generación se bumpea antes de cada
 * fetch (disparado por cambio de empresa/rango/granularidad/PV), cada setter posterior a un
 * `await` y el `finally` que apaga `cargando` están gateados contra esa generación — una
 * respuesta desactualizada nunca pisa un filtro que el usuario ya cambió. `cargando` cubre la
 * ventana completa de la card G1 (ventas + gastos se piden y aplican como una sola unidad,
 * siempre disparados por el mismo evento de filtro — no son entidades operables
 * independientemente, a diferencia del listado + saldo de `Compras.tsx`). Cada panel de
 * desglose (Slice 8) es una entidad operable independiente y trae su PROPIA generación/`cargando`
 * (`usePanelDeReporte`) — no extiende este par compartido (deviation registrada en tasks.md antes
 * de la tarea 7.6, vinculante para esta slice).
 */
export function Tablero() {
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [idEmpresa, setIdEmpresa] = useState<number | null>(null)
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[]>([])
  const [mediosPago, setMediosPago] = useState<MedioPagoListado[]>([])
  const [errorEmpresas, setErrorEmpresas] = useState('')

  const [desde, setDesde] = useState(() => rangoUltimosSieteDias().desde)
  const [hasta, setHasta] = useState(() => rangoUltimosSieteDias().hasta)
  const [granularidad, setGranularidad] = useState<Granularidad>('Dia')
  const [idPuntoVenta, setIdPuntoVenta] = useState<number | null>(null)

  const [ventas, setVentas] = useState<ResumenDeVentas | null>(null)
  const [gastos, setGastos] = useState<ResumenDeGastos | null>(null)
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  useEffect(() => {
    let vigente = true
    Promise.all([clienteDeOrganizacion.listarEmpresas(), clienteDeOrganizacion.listarPuntosVenta(), clienteMediosPago.listar(false)])
      .then(([listaEmpresas, listaPuntosVenta, listaMediosPago]) => {
        if (!vigente) return
        setEmpresas(listaEmpresas)
        setPuntosVenta(listaPuntosVenta)
        setMediosPago(listaMediosPago)
        if (listaEmpresas.length > 0) setIdEmpresa(listaEmpresas[0].id)
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

    const filtros = { idEmpresa, idPuntoVenta, desde, hasta, granularidad }

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
  }, [idEmpresa, idPuntoVenta, desde, hasta, granularidad])

  useEffect(() => {
    cargar()
  }, [cargar])

  const puntosVentaDeLaEmpresa = puntosVenta.filter((pv) => pv.idEmpresa === idEmpresa)

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
                  onChange={(e) => {
                    setIdEmpresa(Number(e.target.value))
                    setIdPuntoVenta(null)
                  }}
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
              <div className="col-auto">
                <label className="form-label" htmlFor="tablero-granularidad">
                  Granularidad
                </label>
                <select
                  id="tablero-granularidad"
                  className="form-select rounded-0"
                  value={granularidad}
                  disabled={cargando}
                  onChange={(e) => setGranularidad(e.target.value as Granularidad)}
                >
                  {GRANULARIDADES.map((g) => (
                    <option key={g.valor} value={g.valor}>
                      {g.etiqueta}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-auto">
                <label className="form-label" htmlFor="tablero-punto-venta">
                  Punto de venta
                </label>
                <select
                  id="tablero-punto-venta"
                  className="form-select rounded-0"
                  value={idPuntoVenta ?? ''}
                  disabled={cargando}
                  onChange={(e) => setIdPuntoVenta(e.target.value === '' ? null : Number(e.target.value))}
                >
                  <option value="">Todos</option>
                  {puntosVentaDeLaEmpresa.map((pv) => (
                    <option key={pv.id} value={pv.id}>
                      {pv.nombre}
                    </option>
                  ))}
                </select>
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

            {idEmpresa !== null && (
              <div className="row g-3 mt-1">
                <div className="col-md-3">
                  <PanelPorPuntoVenta idEmpresa={idEmpresa} desde={desde} hasta={hasta} puntosVenta={puntosVentaDeLaEmpresa} />
                </div>
                <div className="col-md-3">
                  <PanelPorVendedor idEmpresa={idEmpresa} desde={desde} hasta={hasta} idPuntoVenta={idPuntoVenta} />
                </div>
                <div className="col-md-3">
                  <PanelPorMedioPago
                    idEmpresa={idEmpresa}
                    desde={desde}
                    hasta={hasta}
                    idPuntoVenta={idPuntoVenta}
                    mediosPago={mediosPago}
                  />
                </div>
                <div className="col-md-3">
                  <PanelTopArticulos idEmpresa={idEmpresa} desde={desde} hasta={hasta} idPuntoVenta={idPuntoVenta} />
                </div>
              </div>
            )}
          </>
        )}
      </Box>
    </div>
  )
}
