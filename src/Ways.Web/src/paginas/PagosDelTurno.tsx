import { useEffect, useMemo, useRef, useState } from 'react'
import { clienteDeCaja } from '../api/caja'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import { toast } from '../toasts/toasts'
import { errorDeLineasDePago, lineaDePagoVacia, type LineaDePagoEditable } from '../api/lineasDePago'
import { clienteDePagos, copiaDeFalloDeEdicion, esPagoOriginadoEnPersonal } from '../api/registroDePagos'
import { clienteDeProveedores } from '../api/proveedores'
import { CATEGORIAS_PAGO, ORIGENES_DE_FONDOS_PAGO } from '../api/tipos'
import type {
  AreaAlta,
  AreaListado,
  CategoriaPago,
  DetalleDeTurno,
  PagoDeTurno,
  MedioPagoAlta,
  MedioPagoListado,
  OpcionDeProveedor,
  OrigenFondosPago,
  SolicitudDeEdicionDePagoConLineas,
  TurnoResumen,
} from '../api/tipos'
import { pulsoDeCajon } from '../impresion/plantillas'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'
import { Box } from '../componentes/Box'
import { BotonIcono } from '../componentes/BotonIcono'
import { Cargando } from '../componentes/Cargando'
import { EditorDeLineasDePago } from '../componentes/EditorDeLineasDePago'
import { EncabezadoOrdenable, useOrdenamiento, type ColumnasOrdenables } from '../componentes/grilla'
import { etiquetaDeProveedor, ordenarProveedoresPorEtiqueta } from './articulos/helpers'
import { ModalDeEdicionDePago } from './ModalDeEdicionDePago'
import type { CajaDeEscritorio } from './Pos'
import {
  aSolicitudDePago,
  areaPorDefecto,
  categoriaAlElegirProveedor,
  efectivoDisponibleEnCaja,
  etiquetaDeCategoriaPago,
  etiquetaDeOrigenFondos,
  faltanteDeEfectivo,
  formatearFechaHora,
  formatearMoneda,
  haySalidaDeEfectivoDeCaja,
  importeEnEfectivoDeCaja,
  mediosValidosParaPago,
  textoDeMedioDelPagoDeTurno,
  totalDePagos,
} from './utilidadesPagosDelTurno'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')
const clienteAreas = clienteDeCatalogo<AreaListado, AreaAlta>('areas')

/**
 * "Pagos del turno" (stage-gastos-turno-carga-simple, POS de escritorio): captura pagos contra
 * el turno ABIERTO del punto de venta fijo del dispositivo (`POST /api/pagos`, mismo criterio de
 * turno-resuelto-server-side que `Pos.tsx`/`Caja.tsx`) y lista los ya registrados del turno
 * (`GET /api/caja/turnos/{id}/detalle`, mismo endpoint que el Z-report). Sin turno abierto no hay
 * formulario, mismo criterio que `VentasDelTurno`.
 *
 * El pago se reparte entre hasta 20 medios (`EditorDeLineasDePago`; el importe del pago es la suma de las
 * líneas). Los medios excluyen `CuentaCorriente` (`mediosValidosParaPago`): es el mecanismo de
 * crédito del CLIENTE en una venta, nunca una salida real de dinero de la caja. El proveedor
 * (opcional) reusa el mismo helper de etiqueta/orden por intercalación española que
 * `Articulos.tsx` (`articulos/helpers.ts`) — elegirlo cambia la categoría a "Proveedor"
 * (`categoriaAlElegirProveedor`, el cajero puede después volver a cambiarla a mano); limpiarlo
 * mientras la categoría sigue en "Proveedor" la vuelve a "Otros".
 *
 * `generacionRef` es COMPARTIDO entre la carga inicial/refresco del detalle y el alta de un pago
 * (mismo criterio que `VentasDelTurno`/`ShellPos`): una respuesta desactualizada de cualquiera de
 * las dos nunca pisa un estado más nuevo. El formulario entero queda inerte mientras un alta está
 * en vuelo (react-async-state regla 5); el refresco posterior corre aislado del try/catch de la
 * escritura (regla 6), así que un alta ya confirmada nunca se reporta como fallida aunque el
 * refresco falle.
 *
 * Edición: cada pago de un turno que sigue abierto (`turnoAbierto`) se corrige en un modal
 * (`PUT /api/pagos/{id}`, con el reparto por medio) que comparte con el alta la guarda de reentrancia (`guardandoRef`) y la
 * generación del detalle: mientras la edición está abierta o en vuelo, el resto de la pantalla
 * queda inerte (regla 9 — se bloquea el supersede, no se reconcilia). Si el servidor responde
 * `409 gasto_turno_cerrado` el modal queda abierto con el motivo y el detalle se refresca.
 *
 * Efectivo de caja: un pago con alguna línea en medio Efectivo pagado desde la caja del turno
 * muestra el efectivo disponible del resumen del turno y, si la suma de ESAS líneas lo supera,
 * avisa cuánto falta sin impedir el registro. Con el seam de escritorio (`cajaDeEscritorio`), un
 * alta confirmada de ese tipo encola el pulso del cajón.
 */
export function PagosDelTurno({ cajaDeEscritorio }: { cajaDeEscritorio?: CajaDeEscritorio } = {}) {
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
  const [errorAreas, setErrorAreas] = useState('')

  const [edicion, setEdicion] = useState<PagoDeTurno | null>(null)
  const [guardandoEdicion, setGuardandoEdicion] = useState(false)
  const [errorEdicion, setErrorEdicion] = useState('')

  const [lineas, setLineas] = useState<LineaDePagoEditable[]>(() => [lineaDePagoVacia()])
  const [categoria, setCategoria] = useState<CategoriaPago>('Otros')
  const [idProveedor, setIdProveedor] = useState<number | ''>('')
  const [idAreaElegida, setIdAreaElegida] = useState<number | ''>('')
  const [observaciones, setObservaciones] = useState('')
  const [origenFondos, setOrigenFondos] = useState<OrigenFondosPago>('CajaTurno')

  const [guardando, setGuardando] = useState(false)
  const guardandoRef = useRef(false)
  const [errorGuardar, setErrorGuardar] = useState('')

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
        setErrorMedios(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los medios de pago. No se puede registrar el pago.')
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

    // Hasta que responde (`areas === null`) el alta queda deshabilitada; si falla, queda en `[]` y
    // el aviso dice que los pagos se registran sin área.
    clienteAreas
      .listar(false)
      .then((lista) => {
        if (!vigente) return
        setAreas(lista)
      })
      .catch(() => {
        if (!vigente) return
        setAreas([])
        setErrorAreas('No se pudieron cargar las áreas: los pagos se registran sin área.')
      })

    return () => {
      vigente = false
    }
  }, [])

  /** Con `propagar`, un fallo vigente se relanza sin tocar el banner ni el detalle: lo avisa quien refresca tras una escritura. */
  async function cargarDetalle(idTurno: number, miGeneracion: number, propagar = false) {
    setCargandoDetalle(true)
    setErrorDetalle('')
    try {
      const datos = await clienteDeCaja.obtenerDetalle(idTurno)
      if (generacionRef.current !== miGeneracion) return
      setDetalle(datos)
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      if (propagar) throw e
      setDetalle(null)
      setErrorDetalle(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los pagos del turno.')
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
    setLineas([lineaDePagoVacia()])
    setCategoria('Otros')
    setIdProveedor('')
    setIdAreaElegida('')
    setObservaciones('')
    setOrigenFondos('CajaTurno')
  }
  /** Si la escritura ya se confirmó, un fallo del refresco no la desdice: se avisa aparte. */
  async function refrescarDetalle(idTurno: number, miGeneracion: number, escrituraConfirmada: boolean) {
    try {
      await cargarDetalle(idTurno, miGeneracion, escrituraConfirmada)
    } catch (e) {
      toast.advertencia(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los pagos del turno.')
    }
  }

  async function registrarPago() {
    // regla 11 (react-async-state): guard de reentrancia de primera línea — un doble click en el
    // mismo tick le gana al re-render que deshabilita el botón.
    if (guardandoRef.current) return
    if (!turno || !puntoVenta) return

    const errorDeLineas = errorDeLineasDePago(lineas)
    if (errorDeLineas !== null) {
      setErrorGuardar(errorDeLineas)
      return
    }

    const abreElCajon = haySalidaDeEfectivoDeCaja(lineas, medios ?? [], origenFondos)

    guardandoRef.current = true
    setGuardando(true)
    setErrorGuardar('')

    // regla 3: bumpea la generación ANTES de la escritura — cualquier carga de detalle en vuelo
    // desde antes de este alta queda obsoleta aunque su respuesta llegue después.
    const miGeneracion = (generacionRef.current += 1)
    let escrituraConfirmada = false

    try {
      await clienteDePagos.registrar(
        aSolicitudDePago({
          idPuntoVenta: puntoVenta.id,
          lineas,
          categoria,
          idProveedor: idProveedor === '' ? null : idProveedor,
          idArea: idArea === '' ? null : idArea,
          observaciones,
          origenFondos,
        }),
      )
      escrituraConfirmada = true
      // El alta ya está confirmada: el cajón se abre aunque la respuesta llegue desactualizada.
      if (abreElCajon) {
        try {
          cajaDeEscritorio?.encolarImpresion('la apertura de cajón', pulsoDeCajon())
        } catch {
          // Un fallo sincrónico del seam no desmiente el alta ya confirmada.
        }
      }
      if (generacionRef.current !== miGeneracion) return
      limpiarFormulario()
      toast.exito('Pago registrado.')
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      toast.error(e instanceof ErrorApi ? e.message : 'No se pudo registrar el pago.')
    } finally {
      guardandoRef.current = false
      if (generacionRef.current === miGeneracion) setGuardando(false)
    }

    // regla 6: el refresco del detalle queda aislado del try/catch de la escritura — un alta ya
    // confirmada nunca se reporta como fallida aunque el refresco falle, y corre también tras un
    // error de escritura (por si algo cambió igual, mismo criterio que `Caja.tsx`).
    await refrescarDetalle(turno.id, miGeneracion, escrituraConfirmada)
  }

  function abrirEdicion(pago: PagoDeTurno) {
    if (guardandoRef.current) return
    setErrorGuardar('')
    setErrorEdicion('')
    setEdicion(pago)
  }

  function cancelarEdicion() {
    if (guardandoRef.current) return
    setEdicion(null)
    setErrorEdicion('')
  }

  async function guardarEdicion(solicitud: SolicitudDeEdicionDePagoConLineas) {
    // regla 11: guarda de reentrancia síncrona, compartida con el alta.
    if (guardandoRef.current) return
    if (!turno || !edicion) return

    guardandoRef.current = true
    setGuardandoEdicion(true)
    setErrorEdicion('')

    // regla 3: invalida cualquier carga del detalle anterior a esta escritura.
    const miGeneracion = (generacionRef.current += 1)
    let escrituraConfirmada = false

    try {
      await clienteDePagos.actualizar(edicion.id, solicitud)
      escrituraConfirmada = true
      if (generacionRef.current !== miGeneracion) return
      setEdicion(null)
      toast.exito('Pago actualizado.')
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
    await refrescarDetalle(turno.id, miGeneracion, escrituraConfirmada)
  }

  const bloqueado = guardando || guardandoEdicion || edicion !== null

  const mediosParaPago = mediosValidosParaPago(medios ?? [])
  const proveedoresOrdenados = ordenarProveedoresPorEtiqueta(proveedores ?? [])
  const pagos = detalle?.gastos ?? []
  const total = totalDePagos(pagos)
  const columnasDePagos = useMemo<ColumnasOrdenables<PagoDeTurno>>(
    () => ({
      hora: { valor: (g) => new Date(g.fecha) },
      categoria: { valor: (g) => etiquetaDeCategoriaPago(g.categoria) },
      medio: { valor: (g) => textoDeMedioDelPagoDeTurno(g, medios) },
      importe: { valor: (g) => g.importe },
    }),
    [medios],
  )
  const ordenDePagos = useOrdenamiento(pagos, columnasDePagos)
  const idArea = idAreaElegida !== '' ? idAreaElegida : areaPorDefecto(areas ?? [])
  const saleDeLaCaja = haySalidaDeEfectivoDeCaja(lineas, medios ?? [], origenFondos)
  const efectivoDisponible = detalle ? efectivoDisponibleEnCaja(detalle.resumen) : null
  const faltante =
    efectivoDisponible === null ? 0 : faltanteDeEfectivo(importeEnEfectivoDeCaja(lineas, medios ?? [], origenFondos), efectivoDisponible)

  const herramientas = (
    <BotonIcono
      icono="actualizar"
      ocupado={buscandoTurno || cargandoDetalle}
      disabled={bloqueado}
      onClick={() => void cargarTurnoYDetalle()}
    />
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Pagos del turno" variante="inverse" herramientas={herramientas}>
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
            {errorAreas && <div className="alert alert-warning py-1 px-2 small">{errorAreas}</div>}
            {errorGuardar && <div className="alert alert-danger">{errorGuardar}</div>}

            <fieldset disabled={bloqueado} className="row g-2 align-items-end border-0 p-0 m-0 mb-3">
              <div className="col-12">
                <EditorDeLineasDePago lineas={lineas} medios={mediosParaPago} onCambio={(cambio) => setLineas(cambio)} />
              </div>

              <div className="col-md-3">
                <label className="form-label" htmlFor="pago-categoria">
                  Categoría
                </label>
                <select
                  id="pago-categoria"
                  className="form-select"
                  value={categoria}
                  onChange={(e) => setCategoria(e.target.value as CategoriaPago)}
                >
                  {CATEGORIAS_PAGO.map((c) => (
                    <option key={c.valor} value={c.valor}>
                      {c.etiqueta}
                    </option>
                  ))}
                </select>
              </div>

              <div className="col-md-3">
                <label className="form-label" htmlFor="pago-proveedor">
                  Proveedor (opcional)
                </label>
                <select
                  id="pago-proveedor"
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

              <div className="col-md-3">
                <label className="form-label" htmlFor="pago-area">
                  Área
                </label>
                <select
                  id="pago-area"
                  className="form-select"
                  value={idArea}
                  onChange={(e) => setIdAreaElegida(Number(e.target.value))}
                >
                  {(areas ?? []).length === 0 && (
                    <option value="" disabled>
                      Sin áreas
                    </option>
                  )}
                  {(areas ?? []).map((a) => (
                    <option key={a.id} value={a.id}>
                      {a.nombre}
                    </option>
                  ))}
                </select>
              </div>

              <div className="col-md-3">
                <label className="form-label" htmlFor="pago-origen-fondos">
                  Pagado desde
                </label>
                <select
                  id="pago-origen-fondos"
                  className="form-select"
                  value={origenFondos}
                  onChange={(e) => setOrigenFondos(e.target.value as OrigenFondosPago)}
                >
                  {ORIGENES_DE_FONDOS_PAGO.map((o) => (
                    <option key={o.valor} value={o.valor}>
                      {o.etiqueta}
                    </option>
                  ))}
                </select>
              </div>

              {saleDeLaCaja && efectivoDisponible !== null && (
                <div className="col-12">
                  <div className="small text-muted">Disponible en caja: {formatearMoneda(efectivoDisponible)}</div>
                  {faltante > 0 && (
                    <div className="alert alert-warning py-1 px-2 small mt-1 mb-0" role="alert">
                      Falta {formatearMoneda(faltante)} en la caja para pagar este pago.
                    </div>
                  )}
                </div>
              )}

              <div className="col-md-10">
                <label className="form-label" htmlFor="pago-observaciones">
                  Observaciones (opcional)
                </label>
                <textarea
                  id="pago-observaciones"
                  className="form-control"
                  rows={1}
                  value={observaciones}
                  onChange={(e) => setObservaciones(e.target.value)}
                />
              </div>

              <div className="col-md-2">
                <button type="button" className="btn btn-primary w-100" disabled={areas === null} onClick={() => void registrarPago()}>
                  {guardando ? 'Guardando…' : 'Registrar'}
                </button>
              </div>
            </fieldset>

            {errorDetalle && <div className="alert alert-danger">{errorDetalle}</div>}

            {cargandoDetalle && pagos.length === 0 && <Cargando />}

            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <EncabezadoOrdenable columna="hora" orden={ordenDePagos.orden} onAlternar={ordenDePagos.alternar}>
                      Hora
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="categoria" orden={ordenDePagos.orden} onAlternar={ordenDePagos.alternar}>
                      Categoría
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="medio" orden={ordenDePagos.orden} onAlternar={ordenDePagos.alternar}>
                      Medio de pago
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="importe" orden={ordenDePagos.orden} onAlternar={ordenDePagos.alternar} className="text-end">
                      Importe
                    </EncabezadoOrdenable>
                    <th className="text-end">Acciones</th>
                  </tr>
                </thead>
                <tbody>
                  {ordenDePagos.filas.map((g) => (
                    <tr key={g.id}>
                      <td>{formatearFechaHora(g.fecha)}</td>
                      <td>
                        {etiquetaDeCategoriaPago(g.categoria)}
                        {g.origenFondos === 'Tesoreria' && (
                          <span className="badge bg-secondary ms-2">{etiquetaDeOrigenFondos(g.origenFondos)}</span>
                        )}
                      </td>
                      <td>{textoDeMedioDelPagoDeTurno(g, medios)}</td>
                      <td className="text-end">{formatearMoneda(g.importe)}</td>
                      <td className="text-end text-nowrap">
                        {g.turnoAbierto && !esPagoOriginadoEnPersonal(g) && (
                          <BotonIcono
                            icono="editar"
                            etiqueta={`Editar pago ${g.concepto}`}
                            disabled={bloqueado}
                            onClick={() => abrirEdicion(g)}
                          />
                        )}
                      </td>
                    </tr>
                  ))}
                  {pagos.length === 0 && !cargandoDetalle && (
                    <tr>
                      <td colSpan={5} className="text-center text-muted py-4">
                        Este turno todavía no tiene pagos.
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
        <ModalDeEdicionDePago
          key={edicion.id}
          pago={edicion}
          ligadoACompra={edicion.idComprobanteCompra !== null}
          medios={mediosParaPago}
          proveedores={proveedores ?? []}
          areas={areas ?? []}
          guardando={guardandoEdicion}
          error={errorEdicion}
          onGuardarConLineas={(solicitud) => void guardarEdicion(solicitud)}
          onCancelar={cancelarEdicion}
        />
      )}
    </div>
  )
}
