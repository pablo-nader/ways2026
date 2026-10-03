import { useEffect, useRef, useState } from 'react'
import { clienteDeCaja } from '../api/caja'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import { clienteDeGastos, copiaDeFalloDeEdicion } from '../api/gastos'
import { clienteDeProveedores } from '../api/proveedores'
import { CATEGORIAS_GASTO, ORIGENES_DE_FONDOS_GASTO } from '../api/tipos'
import type {
  AreaAlta,
  AreaListado,
  CategoriaGasto,
  DetalleDeTurno,
  GastoDeTurno,
  MedioPagoAlta,
  MedioPagoListado,
  OpcionDeProveedor,
  OrigenFondosGasto,
  SolicitudDeEdicionDeGasto,
  TurnoResumen,
} from '../api/tipos'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'
import { Box } from '../componentes/Box'
import { BotonIcono } from '../componentes/BotonIcono'
import { CampoImporte } from '../componentes/CampoImporte'
import { Cargando } from '../componentes/Cargando'
import { etiquetaDeProveedor, ordenarProveedoresPorEtiqueta } from './articulos/helpers'
import { ModalDeEdicionDeGasto } from './ModalDeEdicionDeGasto'
import {
  aSolicitudDeGasto,
  categoriaAlElegirProveedor,
  etiquetaDeCategoriaGasto,
  etiquetaDeOrigenFondos,
  formatearFechaHora,
  formatearMoneda,
  mediosValidosParaGasto,
  totalDeGastos,
} from './utilidadesGastosDelTurno'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')
const clienteAreas = clienteDeCatalogo<AreaListado, AreaAlta>('areas')

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
 *
 * Edición: cada gasto de un turno que sigue abierto (`turnoAbierto`) se corrige en un modal
 * (`PUT /api/gastos/{id}`) que comparte con el alta la guarda de reentrancia (`guardandoRef`) y la
 * generación del detalle: mientras la edición está abierta o en vuelo, el resto de la pantalla
 * queda inerte (regla 9 — se bloquea el supersede, no se reconcilia). Si el servidor responde
 * `409 gasto_turno_cerrado` el modal queda abierto con el motivo y el detalle se refresca.
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

  const [proveedores, setProveedores] = useState<OpcionDeProveedor[] | null>(null)
  const [errorProveedores, setErrorProveedores] = useState('')

  const [areas, setAreas] = useState<AreaListado[] | null>(null)

  const [edicion, setEdicion] = useState<GastoDeTurno | null>(null)
  const [guardandoEdicion, setGuardandoEdicion] = useState(false)
  const [errorEdicion, setErrorEdicion] = useState('')

  const [importe, setImporte] = useState<number | null>(null)
  const [idMedioPago, setIdMedioPago] = useState<number | ''>('')
  const [categoria, setCategoria] = useState<CategoriaGasto>('Otros')
  const [idProveedor, setIdProveedor] = useState<number | ''>('')
  const [observaciones, setObservaciones] = useState('')
  const [origenFondos, setOrigenFondos] = useState<OrigenFondosGasto>('CajaTurno')

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

    // JD-A1 (judgment-day): el selector opcional de proveedor NO puede pedir `clienteDeProveedores
    // .listar` (Admin-only, expone margen/cuit/contacto) — usa la proyección mínima dedicada
    // `GET /api/proveedores/opciones` (`Politicas.OperacionDePos`), ya filtrada a activos.
    clienteDeProveedores
      .opciones()
      .then((opciones) => {
        if (!vigente) return
        setProveedores(opciones)
      })
      .catch((e) => {
        if (!vigente) return
        setProveedores([])
        setErrorProveedores(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los proveedores.')
      })

    // El área solo se usa para precargar la edición: si no carga, el formulario conserva el valor
    // del gasto rotulado como no disponible, así que no hace falta un aviso aparte.
    clienteAreas
      .listar(false)
      .then((lista) => {
        if (!vigente) return
        setAreas(lista)
      })
      .catch(() => {
        if (!vigente) return
        setAreas([])
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
    setOrigenFondos('CajaTurno')
  }

  async function registrarGasto() {
    // regla 11 (react-async-state): guard de reentrancia de primera línea — un doble click en el
    // mismo tick le gana al re-render que deshabilita el botón.
    if (guardandoRef.current) return
    if (!turno || !puntoVenta) return

    // regla 14 (react-async-state): el aviso de éxito es un slot propio de ESTE submit — se
    // limpia antes de las validaciones client-side para que un intento fallido nunca lo deje
    // conviviendo con el error de validación (RDD-W1).
    setAviso('')

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
          origenFondos,
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

  function abrirEdicion(gasto: GastoDeTurno) {
    if (guardandoRef.current) return
    setAviso('')
    setErrorGuardar('')
    setErrorEdicion('')
    setEdicion(gasto)
  }

  function cancelarEdicion() {
    if (guardandoRef.current) return
    setEdicion(null)
    setErrorEdicion('')
  }

  async function guardarEdicion(solicitud: SolicitudDeEdicionDeGasto) {
    // regla 11: guarda de reentrancia síncrona, compartida con el alta.
    if (guardandoRef.current) return
    if (!turno || !edicion) return

    guardandoRef.current = true
    setGuardandoEdicion(true)
    setErrorEdicion('')

    // regla 3: invalida cualquier carga del detalle anterior a esta escritura.
    const miGeneracion = (generacionRef.current += 1)

    try {
      await clienteDeGastos.actualizar(edicion.id, solicitud)
      if (generacionRef.current !== miGeneracion) return
      setEdicion(null)
      setAviso('Gasto actualizado.')
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setErrorEdicion(copiaDeFalloDeEdicion(e))
    } finally {
      // Sin gate de generación a propósito: `guardandoRef` impide otra escritura en vuelo, así que
      // ninguna respuesta más nueva puede ser dueña de este flag y gatearlo lo dejaría trabado.
      guardandoRef.current = false
      setGuardandoEdicion(false)
    }

    // regla 6: el refresco queda aislado de la escritura; tras un 409 también corre, así el
    // `turnoAbierto` de la fila queda al día.
    await cargarDetalle(turno.id, miGeneracion)
  }

  const bloqueado = guardando || guardandoEdicion || edicion !== null

  const mediosParaGasto = mediosValidosParaGasto(medios ?? [])
  const proveedoresOrdenados = ordenarProveedoresPorEtiqueta(proveedores ?? [])
  const gastos = detalle?.gastos ?? []
  const total = totalDeGastos(gastos)

  const herramientas = (
    <button
      type="button"
      className="btn btn-sm btn-outline-secondary"
      disabled={bloqueado}
      onClick={() => void cargarTurnoYDetalle()}
    >
      {buscandoTurno || cargandoDetalle ? 'Actualizando…' : 'Refrescar'}
    </button>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Gastos del turno" variante="inverse" herramientas={herramientas}>
        {!puntoVenta && <div className="alert alert-warning mb-0">No hay un punto de venta asociado a este dispositivo.</div>}

        {puntoVenta && errorTurno && <div className="alert alert-danger">{errorTurno}</div>}

        {puntoVenta && !errorTurno && buscandoTurno && !turno && <Cargando />}

        {puntoVenta && !errorTurno && !buscandoTurno && !turno && (
          <div className="alert alert-warning mb-0">No hay un turno abierto en este punto de venta.</div>
        )}

        {turno && (
          <>
            {errorMedios && <div className="alert alert-warning py-1 px-2 small">{errorMedios}</div>}
            {errorProveedores && <div className="alert alert-warning py-1 px-2 small">{errorProveedores}</div>}
            {aviso && <div className="alert alert-success">{aviso}</div>}
            {errorGuardar && <div className="alert alert-danger">{errorGuardar}</div>}

            <fieldset disabled={bloqueado} className="row g-2 align-items-end border-0 p-0 m-0 mb-3">
              <div className="col-md-2">
                <label className="form-label" htmlFor="gasto-importe">
                  Importe
                </label>
                <CampoImporte id="gasto-importe" className="form-control" valor={importe} onChange={setImporte} />
              </div>

              <div className="col-md-2">
                <label className="form-label" htmlFor="gasto-medio-pago">
                  Medio de pago
                </label>
                <select
                  id="gasto-medio-pago"
                  className="form-select"
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
                  className="form-select"
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
                  className="form-select"
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

              <div className="col-md-2">
                <label className="form-label" htmlFor="gasto-origen-fondos">
                  Pagado desde
                </label>
                <select
                  id="gasto-origen-fondos"
                  className="form-select"
                  value={origenFondos}
                  onChange={(e) => setOrigenFondos(e.target.value as OrigenFondosGasto)}
                >
                  {ORIGENES_DE_FONDOS_GASTO.map((o) => (
                    <option key={o.valor} value={o.valor}>
                      {o.etiqueta}
                    </option>
                  ))}
                </select>
              </div>

              <div className="col-md-2">
                <button type="button" className="btn btn-primary w-100" onClick={() => void registrarGasto()}>
                  {guardando ? 'Guardando…' : 'Registrar'}
                </button>
              </div>

              <div className="col-12">
                <label className="form-label" htmlFor="gasto-observaciones">
                  Observaciones (opcional)
                </label>
                <textarea
                  id="gasto-observaciones"
                  className="form-control"
                  rows={1}
                  value={observaciones}
                  onChange={(e) => setObservaciones(e.target.value)}
                />
              </div>
            </fieldset>

            {errorDetalle && <div className="alert alert-danger">{errorDetalle}</div>}

            {cargandoDetalle && gastos.length === 0 && <Cargando />}

            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Hora</th>
                    <th>Categoría</th>
                    <th>Medio de pago</th>
                    <th className="text-end">Importe</th>
                    <th className="text-end">Acciones</th>
                  </tr>
                </thead>
                <tbody>
                  {gastos.map((g) => (
                    <tr key={g.id}>
                      <td>{formatearFechaHora(g.fecha)}</td>
                      <td>
                        {etiquetaDeCategoriaGasto(g.categoria)}
                        {g.origenFondos === 'Tesoreria' && (
                          <span className="badge bg-secondary ms-2">{etiquetaDeOrigenFondos(g.origenFondos)}</span>
                        )}
                      </td>
                      <td>{medios?.find((m) => m.id === g.idMedioPago)?.nombre ?? `Medio #${g.idMedioPago}`}</td>
                      <td className="text-end">{formatearMoneda(g.importe)}</td>
                      <td className="text-end text-nowrap">
                        {g.turnoAbierto && (
                          <BotonIcono
                            icono="editar"
                            etiqueta={`Editar gasto ${g.concepto}`}
                            disabled={bloqueado}
                            onClick={() => abrirEdicion(g)}
                          />
                        )}
                      </td>
                    </tr>
                  ))}
                  {gastos.length === 0 && !cargandoDetalle && (
                    <tr>
                      <td colSpan={5} className="text-center text-muted py-4">
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

      {edicion && (
        <ModalDeEdicionDeGasto
          key={edicion.id}
          gasto={edicion}
          ligadoACompra={edicion.idComprobanteCompra !== null}
          medios={mediosParaGasto}
          proveedores={proveedores ?? []}
          areas={areas ?? []}
          guardando={guardandoEdicion}
          error={errorEdicion}
          onGuardar={(solicitud) => void guardarEdicion(solicitud)}
          onCancelar={cancelarEdicion}
        />
      )}
    </div>
  )
}
