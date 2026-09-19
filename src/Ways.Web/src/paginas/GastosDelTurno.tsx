import { useEffect, useRef, useState } from 'react'
import { clienteDeCaja } from '../api/caja'
import { clienteDeCatalogo } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeGastos } from '../api/gastos'
import { CATEGORIAS_GASTO } from '../api/tipos'
import type {
  CategoriaGasto,
  DetalleDeTurno,
  MedioPagoAlta,
  MedioPagoListado,
  PaginaDe,
  ProveedorListado,
  TurnoResumen,
} from '../api/tipos'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'
import { Box } from '../componentes/Box'
import { CampoImporte } from '../componentes/CampoImporte'
import { Cargando } from '../componentes/Cargando'
import { etiquetaDeProveedor, ordenarProveedoresPorEtiqueta } from './articulos/helpers'
import {
  aSolicitudDeGasto,
  categoriaAlElegirProveedor,
  etiquetaDeCategoriaGasto,
  formatearFechaHora,
  formatearMoneda,
  mediosValidosParaGasto,
  totalDeGastos,
} from './utilidadesGastosDelTurno'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

/**
 * "Gastos del turno" (stage-gastos-turno-carga-simple, POS de escritorio): captura gastos contra
 * el turno ABIERTO del punto de venta fijo del dispositivo (`POST /api/gastos`, mismo criterio de
 * turno-resuelto-server-side que `Pos.tsx`/`Caja.tsx`) y lista los ya registrados del turno
 * (`GET /api/caja/turnos/{id}/detalle`, mismo endpoint que el Z-report). Sin turno abierto no hay
 * formulario, mismo criterio que `VentasDelTurno`.
 *
 * El medio de pago excluye `CuentaCorriente` (`mediosValidosParaGasto`): es el mecanismo de
 * crédito del CLIENTE en una venta, nunca una salida real de dinero de la caja. El proveedor
 * (opcional) reusa el mismo helper de etiqueta/orden por intercalación española que
 * `Articulos.tsx` (`articulos/helpers.ts`) — elegirlo cambia la categoría a "Proveedor"
 * (`categoriaAlElegirProveedor`, el cajero puede después volver a cambiarla a mano); limpiarlo
 * mientras la categoría sigue en "Proveedor" la vuelve a "Otros".
 *
 * `generacionRef` es COMPARTIDO entre la carga inicial/refresco del detalle y el alta de un gasto
 * (mismo criterio que `VentasDelTurno`/`ShellPos`): una respuesta desactualizada de cualquiera de
 * las dos nunca pisa un estado más nuevo. El formulario entero queda inerte mientras un alta está
 * en vuelo (react-async-state regla 5); el refresco posterior corre aislado del try/catch de la
 * escritura (regla 6), así que un alta ya confirmada nunca se reporta como fallida aunque el
 * refresco falle.
 */
export function GastosDelTurno() {
  const { puntoVenta } = usePuntoVenta()

  const [turno, setTurno] = useState<TurnoResumen | null>(null)
  const [buscandoTurno, setBuscandoTurno] = useState(true)
  const [errorTurno, setErrorTurno] = useState('')

  const [detalle, setDetalle] = useState<DetalleDeTurno | null>(null)
  const [cargandoDetalle, setCargandoDetalle] = useState(false)
  const [errorDetalle, setErrorDetalle] = useState('')

  const generacionRef = useRef(0)

  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [errorMedios, setErrorMedios] = useState('')

  const [proveedores, setProveedores] = useState<ProveedorListado[] | null>(null)
  const [errorProveedores, setErrorProveedores] = useState('')

  const [importe, setImporte] = useState<number | null>(null)
  const [idMedioPago, setIdMedioPago] = useState<number | ''>('')
  const [categoria, setCategoria] = useState<CategoriaGasto>('Otros')
  const [idProveedor, setIdProveedor] = useState<number | ''>('')
  const [observaciones, setObservaciones] = useState('')

  const [guardando, setGuardando] = useState(false)
  const guardandoRef = useRef(false)
  const [errorGuardar, setErrorGuardar] = useState('')
  const [aviso, setAviso] = useState('')

  // Carga inicial: medios de pago y proveedores — catálogos de tenant, independientes del turno
  // (nunca cambian si el turno se cierra/abre de nuevo). Cada uno con su propio try/catch: que
  // uno falle no bloquea al otro, mismo criterio que `Pos.tsx`.
  useEffect(() => {
    let vigente = true

    clienteMediosPago
      .listar(false)
      .then((lista) => {
        if (!vigente) return
        setMedios(lista)
      })
      .catch((e) => {
        if (!vigente) return
        setMedios([])
        setErrorMedios(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los medios de pago. No se puede registrar el gasto.')
      })

    // Mismo camino crudo que `Compras.tsx` (`clienteDeProveedores.listar` no acepta `tamanio`):
    // hasta 200 proveedores activos del tenant para el selector opcional.
    api
      .get<PaginaDe<ProveedorListado>>('/proveedores?tamanio=200')
      .then((pagina) => {
        if (!vigente) return
        setProveedores(pagina.items)
      })
      .catch((e) => {
        if (!vigente) return
        setProveedores([])
        setErrorProveedores(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los proveedores.')
      })

    return () => {
      vigente = false
    }
  }, [])

  async function cargarDetalle(idTurno: number, miGeneracion: number) {
    setCargandoDetalle(true)
    setErrorDetalle('')
    try {
      const datos = await clienteDeCaja.obtenerDetalle(idTurno)
      if (generacionRef.current !== miGeneracion) return
      setDetalle(datos)
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setDetalle(null)
      setErrorDetalle(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los gastos del turno.')
    } finally {
      if (generacionRef.current === miGeneracion) setCargandoDetalle(false)
    }
  }

  async function cargarTurnoYDetalle() {
    if (!puntoVenta) return
    const miGeneracion = (generacionRef.current += 1)
    setBuscandoTurno(true)
    setErrorTurno('')

    try {
      const turnoAbierto = await clienteDeCaja.obtenerAbierto(puntoVenta.id)
      if (generacionRef.current !== miGeneracion) return
      setTurno(turnoAbierto)

      if (!turnoAbierto) {
        setDetalle(null)
        return
      }
      await cargarDetalle(turnoAbierto.id, miGeneracion)
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setTurno(null)
      setDetalle(null)
      setErrorTurno(e instanceof ErrorApi ? e.message : 'No se pudo consultar el turno abierto.')
    } finally {
      if (generacionRef.current === miGeneracion) setBuscandoTurno(false)
    }
  }

  useEffect(() => {
    void cargarTurnoYDetalle()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [puntoVenta?.id])

  function alCambiarProveedor(valorCrudo: string) {
    const nuevoIdProveedor = valorCrudo === '' ? null : Number(valorCrudo)
    setIdProveedor(nuevoIdProveedor ?? '')
    // regla 1 (react-async-state): construye desde `prev`, nunca desde el `categoria` del
    // closure — dos cambios de proveedor en el mismo tick no pueden pisarse entre sí.
    setCategoria((prev) => categoriaAlElegirProveedor(nuevoIdProveedor, prev))
  }

  function limpiarFormulario() {
    setImporte(null)
    setIdMedioPago('')
    setCategoria('Otros')
    setIdProveedor('')
    setObservaciones('')
  }

  async function registrarGasto() {
    // regla 11 (react-async-state): guard de reentrancia de primera línea — un doble click en el
    // mismo tick le gana al re-render que deshabilita el botón.
    if (guardandoRef.current) return
    if (!turno || !puntoVenta) return

    if (importe === null || importe <= 0) {
      setErrorGuardar('El importe tiene que ser mayor a 0.')
      return
    }
    if (idMedioPago === '') {
      setErrorGuardar('Elegí un medio de pago.')
      return
    }

    guardandoRef.current = true
    setGuardando(true)
    setErrorGuardar('')
    setAviso('')

    // regla 3: bumpea la generación ANTES de la escritura — cualquier carga de detalle en vuelo
    // desde antes de este alta queda obsoleta aunque su respuesta llegue después.
    const miGeneracion = (generacionRef.current += 1)

    try {
      await clienteDeGastos.registrar(
        aSolicitudDeGasto({
          idPuntoVenta: puntoVenta.id,
          importe,
          idMedioPago,
          categoria,
          idProveedor: idProveedor === '' ? null : idProveedor,
          observaciones,
        }),
      )
      if (generacionRef.current !== miGeneracion) return
      limpiarFormulario()
      setAviso('Gasto registrado.')
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setErrorGuardar(e instanceof ErrorApi ? e.message : 'No se pudo registrar el gasto.')
    } finally {
      guardandoRef.current = false
      if (generacionRef.current === miGeneracion) setGuardando(false)
    }

    // regla 6: el refresco del detalle queda aislado del try/catch de la escritura — un alta ya
    // confirmada nunca se reporta como fallida aunque el refresco falle, y corre también tras un
    // error de escritura (por si algo cambió igual, mismo criterio que `Caja.tsx`).
    await cargarDetalle(turno.id, miGeneracion)
  }

  const mediosParaGasto = mediosValidosParaGasto(medios ?? [])
  const proveedoresOrdenados = ordenarProveedoresPorEtiqueta(proveedores ?? [])
  const gastos = detalle?.gastos ?? []
  const total = totalDeGastos(gastos)

  const herramientas = (
    <button
      type="button"
      className="btn btn-sm btn-outline-light rounded-0"
      disabled={guardando}
      onClick={() => void cargarTurnoYDetalle()}
    >
      {buscandoTurno || cargandoDetalle ? 'Actualizando…' : 'Refrescar'}
    </button>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Gastos del turno" variante="inverse" herramientas={herramientas}>
        {!puntoVenta && <div className="alert alert-warning rounded-0 mb-0">No hay un punto de venta asociado a este dispositivo.</div>}

        {puntoVenta && errorTurno && <div className="alert alert-danger rounded-0">{errorTurno}</div>}

        {puntoVenta && !errorTurno && buscandoTurno && !turno && <Cargando />}

        {puntoVenta && !errorTurno && !buscandoTurno && !turno && (
          <div className="alert alert-warning rounded-0 mb-0">No hay un turno abierto en este punto de venta.</div>
        )}

        {turno && (
          <>
            {errorMedios && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorMedios}</div>}
            {errorProveedores && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorProveedores}</div>}
            {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}
            {errorGuardar && <div className="alert alert-danger rounded-0">{errorGuardar}</div>}

            <fieldset disabled={guardando} className="row g-2 align-items-end border-0 p-0 m-0 mb-3">
              <div className="col-md-2">
                <label className="form-label" htmlFor="gasto-importe">
                  Importe
                </label>
                <CampoImporte id="gasto-importe" className="form-control rounded-0" valor={importe} onChange={setImporte} />
              </div>

              <div className="col-md-2">
                <label className="form-label" htmlFor="gasto-medio-pago">
                  Medio de pago
                </label>
                <select
                  id="gasto-medio-pago"
                  className="form-select rounded-0"
                  value={idMedioPago}
                  onChange={(e) => setIdMedioPago(e.target.value === '' ? '' : Number(e.target.value))}
                >
                  <option value="">Elegir…</option>
                  {mediosParaGasto.map((m) => (
                    <option key={m.id} value={m.id}>
                      {m.nombre}
                    </option>
                  ))}
                </select>
              </div>

              <div className="col-md-2">
                <label className="form-label" htmlFor="gasto-categoria">
                  Categoría
                </label>
                <select
                  id="gasto-categoria"
                  className="form-select rounded-0"
                  value={categoria}
                  onChange={(e) => setCategoria(e.target.value as CategoriaGasto)}
                >
                  {CATEGORIAS_GASTO.map((c) => (
                    <option key={c.valor} value={c.valor}>
                      {c.etiqueta}
                    </option>
                  ))}
                </select>
              </div>

              <div className="col-md-2">
                <label className="form-label" htmlFor="gasto-proveedor">
                  Proveedor (opcional)
                </label>
                <select
                  id="gasto-proveedor"
                  className="form-select rounded-0"
                  value={idProveedor}
                  onChange={(e) => alCambiarProveedor(e.target.value)}
                >
                  <option value="">Sin proveedor</option>
                  {proveedoresOrdenados.map((p) => (
                    <option key={p.id} value={p.id}>
                      {etiquetaDeProveedor(p)}
                    </option>
                  ))}
                </select>
              </div>

              <div className="col-md-3">
                <label className="form-label" htmlFor="gasto-observaciones">
                  Observaciones (opcional)
                </label>
                <textarea
                  id="gasto-observaciones"
                  className="form-control rounded-0"
                  rows={1}
                  value={observaciones}
                  onChange={(e) => setObservaciones(e.target.value)}
                />
              </div>

              <div className="col-md-1">
                <button type="button" className="btn btn-primary rounded-0 w-100" onClick={() => void registrarGasto()}>
                  {guardando ? 'Guardando…' : 'Registrar'}
                </button>
              </div>
            </fieldset>

            {errorDetalle && <div className="alert alert-danger rounded-0">{errorDetalle}</div>}

            {cargandoDetalle && gastos.length === 0 && <Cargando />}

            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Hora</th>
                    <th>Categoría</th>
                    <th>Medio de pago</th>
                    <th className="text-end">Importe</th>
                  </tr>
                </thead>
                <tbody>
                  {gastos.map((g) => (
                    <tr key={g.id}>
                      <td>{formatearFechaHora(g.fecha)}</td>
                      <td>{etiquetaDeCategoriaGasto(g.categoria)}</td>
                      <td>{medios?.find((m) => m.id === g.idMedioPago)?.nombre ?? `Medio #${g.idMedioPago}`}</td>
                      <td className="text-end">{formatearMoneda(g.importe)}</td>
                    </tr>
                  ))}
                  {gastos.length === 0 && !cargandoDetalle && (
                    <tr>
                      <td colSpan={4} className="text-center text-muted py-4">
                        Este turno todavía no tiene gastos.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="d-flex justify-content-end">
              <span className="small text-muted">Total: {formatearMoneda(total)}</span>
            </div>
          </>
        )}
      </Box>
    </div>
  )
}
