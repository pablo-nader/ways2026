import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useLocation, useParams } from 'react-router'
import { formatearPorcentajeDeAjuste, rotuloDeAjusteManual } from '../api/ajusteManual'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import { clienteDeOrganizacion } from '../api/organizacion'
import {
  aSolicitudDeAjuste,
  aSolicitudDeReliquidacion,
  clienteDeCuentaCorriente,
  etiquetaDeMovimiento,
  parsearDetalleDeActualizacionPrecios,
  rangoUltimoMes,
  reliquidacionEsNoOp,
  saldoResultanteDeAjuste,
  validarAjusteLocal,
} from '../api/cuentaCorriente'
import type {
  ClienteListado,
  DetalleDeConsumo,
  EstadoDeCuenta,
  EstadoDeCuentaHeader,
  MedioPagoAlta,
  MedioPagoListado,
  MovimientoDeCuentaCorriente,
  PuntoVentaListado,
  ResultadoDeReliquidacion,
} from '../api/tipos'
import { puedeSupervisarCuentaCorriente } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { CampoImporte } from '../componentes/CampoImporte'
import { Cargando } from '../componentes/Cargando'
import { guardarPuntoVentaSeleccionado, leerPuntoVentaGuardado, ModalPagoACuenta } from '../componentes/ModalPagoACuenta'
import { formatearImporte } from '../formato/importes'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

function formatearDisponibilidad(valor: number | null): string {
  return valor === null ? 'Ilimitado' : formatearMoneda(valor)
}

/**
 * Detalle auditable por consumo (Fix 1: preview/commit de la reliquidación traían `detalle` pero
 * nunca se mostraba). Cada consumo queda siempre visible (id + delta); las líneas — histórico →
 * actual, delta y el `motivo` de las omitidas — quedan en un `<details>` expandible para que la
 * lista no se coma la pantalla cuando el cap de 500 trae muchos consumos. Una línea con ajuste
 * manual conservado muestra su rótulo y porcentaje bajo el precio actual: el servidor lo reaplica
 * sobre el neto nuevo, así que el total del día no es `cantidad × precio actual`.
 */
function DetalleDeConsumosDeReliquidacion({ detalle }: { detalle: DetalleDeConsumo[] }) {
  if (detalle.length === 0) return null
  return (
    <div className="mb-3">
      <div className="small text-muted mb-1">Detalle por consumo</div>
      {detalle.map((consumo) => (
        <details key={consumo.idMovimiento} className="mb-1">
          <summary>
            Movimiento #{consumo.idMovimiento} — {formatearMoneda(consumo.delta)}
          </summary>
          <table className="table table-sm table-bordered mb-0 mt-1">
            <thead>
              <tr>
                <th>Artículo</th>
                <th>Cant.</th>
                <th>Precio histórico</th>
                <th>Precio actual</th>
                <th>Delta</th>
                <th>Motivo</th>
              </tr>
            </thead>
            <tbody>
              {consumo.lineas.map((linea, indice) => (
                <tr key={indice}>
                  <td>{linea.idArticulo ?? '—'}</td>
                  <td>{linea.cantidad}</td>
                  <td>{formatearMoneda(linea.precioHistorico)}</td>
                  <td>
                    {linea.precioActual === null ? '—' : formatearMoneda(linea.precioActual)}
                    {linea.ajusteManualPorcentaje != null && (
                      <div className="small text-muted" data-testid="cc-reliq-ajuste-manual">
                        {`${rotuloDeAjusteManual(linea.ajusteManualPorcentaje)} ${formatearPorcentajeDeAjuste(linea.ajusteManualPorcentaje)}%`}
                      </div>
                    )}
                  </td>
                  <td>{formatearMoneda(linea.delta)}</td>
                  <td>{linea.motivo ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      ))}
    </div>
  )
}

/** Detalle del ledger para un movimiento `ActualizacionPrecios` (Fix 1b) — el `detalle` guardado
 * es el mismo JSON auditable de la reliquidación, nunca un texto libre: se parsea de forma
 * defensiva y se muestra un resumen legible; un JSON malformado cae al texto crudo, nunca rompe la
 * fila. */
function DetalleDeMovimientoDeActualizacionPrecios({ detalle }: { detalle: string | null }) {
  const consumos = parsearDetalleDeActualizacionPrecios(detalle)
  if (consumos === null) return <>{detalle ?? '—'}</>
  if (consumos.length === 0) return <>Sin consumos re-preciados.</>
  return (
    <details>
      <summary>
        {consumos.length} consumo{consumos.length === 1 ? '' : 's'} re-preciado{consumos.length === 1 ? '' : 's'}
      </summary>
      <ul className="mb-0 ps-3 small">
        {consumos.map((consumo) => (
          <li key={consumo.idMovimiento}>
            Movimiento #{consumo.idMovimiento}: {formatearMoneda(consumo.delta)}
          </li>
        ))}
      </ul>
    </details>
  )
}

function nombreDeCliente(idCliente: number, cliente: ClienteListado | null): string {
  if (!cliente) return `Cliente #${idCliente}`
  const nombreCompleto = cliente.razonSocial ?? [cliente.nombre, cliente.apellido].filter(Boolean).join(' ')
  return `#${String(cliente.numero).padStart(4, '0')} — ${nombreCompleto}`
}

type PropsModalAjuste = {
  idCliente: number
  puntosVenta: PuntoVentaListado[]
  header: EstadoDeCuentaHeader
  onCerrar: () => void
  onAntesDeEscribir: () => void
  onRegistrado: (movimiento: MovimientoDeCuentaCorriente) => void
}

/**
 * Modal de ajuste manual (Slice 6, design: Web Composition; spec: ajustes-de-cuenta-corriente —
 * gated por `SupervisionDeCuentaCorriente`, cosmético en pantalla, real en el servidor).
 * `react-async-state`: regla 9 (guard de reentrancia + deshabilitado de ventana completa), regla 3
 * (el llamador bumpea la generación del ledger ANTES del POST), regla 6 (el refetch posterior vive
 * en el padre). Sin turno (design: Open Questions — "provenance, not authority", mismo criterio
 * que la reliquidación): a diferencia del pago, este endpoint nunca llama a `ServicioDeTurnos`, así
 * que no hay ningún `turno_no_abierto` que recuperar acá (rule 10 sweep — ver el catch de abajo).
 */
function ModalAjusteDeCuenta({ idCliente, puntosVenta, header, onCerrar, onAntesDeEscribir, onRegistrado }: PropsModalAjuste) {
  const [idPuntoVenta, setIdPuntoVenta] = useState<number>(
    () => puntosVenta.find((p) => p.id === leerPuntoVentaGuardado())?.id ?? puntosVenta[0].id,
  )
  const [importe, setImporte] = useState<number | null>(null)
  const [detalle, setDetalle] = useState('')
  // Fix 2 (mismo patrón que la reliquidación): un ajuste manual también modifica el saldo del
  // cliente de forma directa — la confirmación explícita evita que un click apurado dispare un
  // ajuste sin que el supervisor haya leído el importe/detalle que está a punto de registrar.
  const [confirmado, setConfirmado] = useState(false)

  const [registrando, setRegistrando] = useState(false)
  const registrandoRef = useRef(false)
  const [error, setError] = useState('')

  const importeNumerico = importe ?? Number.NaN
  const saldoResultante = Number.isFinite(importeNumerico) ? saldoResultanteDeAjuste(header.saldo, importeNumerico) : null
  const puedeRegistrar = !registrando && confirmado

  function cambiarPuntoVenta(id: number) {
    if (registrandoRef.current) return
    setIdPuntoVenta(id)
    guardarPuntoVentaSeleccionado(id)
  }

  async function registrarAjuste() {
    // regla 9: guard de reentrancia de primera línea.
    if (registrandoRef.current) return
    if (!puedeRegistrar) return

    const rechazo = validarAjusteLocal({ importe: importeNumerico, detalle })
    if (rechazo) {
      setError(rechazo.mensaje)
      return
    }

    registrandoRef.current = true
    setRegistrando(true)
    setError('')

    // regla 3: bumpear la generación del ledger ANTES de la escritura.
    onAntesDeEscribir()

    try {
      const solicitud = aSolicitudDeAjuste(idPuntoVenta, importeNumerico, detalle)
      const movimiento = await clienteDeCuentaCorriente.registrarAjuste(idCliente, solicitud)
      registrandoRef.current = false
      setRegistrando(false)
      // regla 6: el refetch del ledger vive en el padre, aislado de este try/catch.
      onRegistrado(movimiento)
    } catch (e) {
      registrandoRef.current = false
      setRegistrando(false)
      // Rule 10 sweep (design: Web Composition — "the three modals are sibling surfaces"): el
      // ajuste manual no tiene turno (`ServicioDeCuentaCorriente.RegistrarAjusteAsync` nunca llama
      // a `ServicioDeTurnos`), así que `turno_no_abierto` es estructuralmente irreproducible acá —
      // replicar el panel de apertura de turno sería código muerto para un 409 que este endpoint
      // nunca emite. `ajuste_importe_invalido`/`ajuste_detalle_requerido`/
      // `cliente_sin_cuenta_corriente` caen en el mismo aviso genérico que ya usa el pago.
      setError(e instanceof ErrorApi ? e.message : 'No se pudo registrar el ajuste.')
    }
  }

  return (
    <>
      <div className="modal d-block" tabIndex={-1} role="dialog">
        <div className="modal-dialog" role="document">
          <div className="modal-content">
            <div className="modal-header">
              <h5 className="modal-title">Ajuste manual de cuenta corriente</h5>
            </div>
            <div className="modal-body">
              {error && <div className="alert alert-danger py-1 px-2 small">{error}</div>}

              <div className="mb-3" style={{ maxWidth: 320 }}>
                <label className="form-label" htmlFor="cc-ajuste-punto-venta">
                  Punto de venta
                </label>
                <select
                  id="cc-ajuste-punto-venta"
                  className="form-select"
                  value={idPuntoVenta}
                  disabled={registrando}
                  onChange={(e) => cambiarPuntoVenta(Number(e.target.value))}
                >
                  {puntosVenta.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.nombre}
                    </option>
                  ))}
                </select>
              </div>

              <div className="mb-3">
                <label className="form-label" htmlFor="cc-ajuste-importe">
                  Importe
                </label>
                <CampoImporte
                  id="cc-ajuste-importe"
                  className="form-control"
                  valor={importe}
                  disabled={registrando}
                  admiteNegativos
                  onChange={setImporte}
                />
                <div className="form-text">
                  Positivo aumenta la deuda del cliente, negativo la reduce. Nunca puede ser cero.
                </div>
              </div>

              <div className="mb-3">
                <label className="form-label" htmlFor="cc-ajuste-detalle">
                  Detalle (obligatorio)
                </label>
                <input
                  id="cc-ajuste-detalle"
                  type="text"
                  className="form-control"
                  value={detalle}
                  disabled={registrando}
                  onChange={(e) => setDetalle(e.target.value)}
                />
              </div>

              <div className="small text-muted">Saldo actual: {formatearMoneda(header.saldo)}</div>
              {saldoResultante !== null && <div className="fs-6">Saldo resultante: {formatearMoneda(saldoResultante)}</div>}

              <div className="form-check mt-3">
                <input
                  id="cc-ajuste-confirmacion"
                  type="checkbox"
                  className="form-check-input"
                  checked={confirmado}
                  disabled={registrando}
                  onChange={(e) => setConfirmado(e.target.checked)}
                />
                <label className="form-check-label" htmlFor="cc-ajuste-confirmacion">
                  Entiendo que este ajuste modifica el saldo del cliente.
                </label>
              </div>
            </div>
            <div className="modal-footer">
              <button type="button" className="btn btn-outline-secondary" disabled={registrando} onClick={onCerrar}>
                Cancelar
              </button>
              <button type="button" className="btn btn-primary" disabled={!puedeRegistrar} onClick={registrarAjuste}>
                {registrando ? 'Registrando…' : 'Registrar ajuste'}
              </button>
            </div>
          </div>
        </div>
      </div>
      <div className="modal-backdrop show" />
    </>
  )
}

type PropsModalReliquidacion = {
  idCliente: number
  puntosVenta: PuntoVentaListado[]
  onCerrar: () => void
  onAntesDeEscribir: () => void
  onEjecutada: (resultado: ResultadoDeReliquidacion) => void
}

/**
 * Modal de reliquidación a precio del día (Slice 6, design: Web Composition; spec:
 * reliquidacion-a-precio-del-dia) — preview PRIMERO (`GET`, sin lock, nunca autoritativo), después
 * la confirmación de irreversibilidad (mismo patrón que `CierreDeCaja.tsx`: checkbox explícito,
 * nunca pre-tildado), recién ahí el commit. `react-async-state`: regla 9 (guard de reentrancia +
 * deshabilitado de ventana completa — un doble submit re-precificaría al cliente dos veces), regla
 * 6 (un commit 2xx NUNCA se reporta como fallo, el refetch del ledger vive en el padre, aislado).
 * Sin turno (mismo motivo que `ModalAjusteDeCuenta`): sin recuperación de `turno_no_abierto` que
 * replicar, ese 409 es irreproducible en este endpoint.
 */
function ModalReliquidacion({ idCliente, puntosVenta, onCerrar, onAntesDeEscribir, onEjecutada }: PropsModalReliquidacion) {
  const [idPuntoVenta, setIdPuntoVenta] = useState<number>(
    () => puntosVenta.find((p) => p.id === leerPuntoVentaGuardado())?.id ?? puntosVenta[0].id,
  )

  const [preview, setPreview] = useState<ResultadoDeReliquidacion | null>(null)
  const [cargandoPreview, setCargandoPreview] = useState(true)
  const [errorPreview, setErrorPreview] = useState('')
  const generacionPreviewRef = useRef(0)

  const [confirmado, setConfirmado] = useState(false)
  const [ejecutando, setEjecutando] = useState(false)
  const ejecutandoRef = useRef(false)
  const [error, setError] = useState('')

  useEffect(() => {
    let vigente = true
    const miGeneracion = (generacionPreviewRef.current += 1)
    setCargandoPreview(true)
    setErrorPreview('')

    clienteDeCuentaCorriente
      .previsualizarReliquidacion(idCliente)
      .then((resultado) => {
        if (!vigente || generacionPreviewRef.current !== miGeneracion) return
        setPreview(resultado)
      })
      .catch((e) => {
        if (!vigente || generacionPreviewRef.current !== miGeneracion) return
        setPreview(null)
        setErrorPreview(e instanceof ErrorApi ? e.message : 'No se pudo cargar la vista previa de la reliquidación.')
      })
      .finally(() => {
        if (!vigente || generacionPreviewRef.current !== miGeneracion) return
        setCargandoPreview(false)
      })

    return () => {
      vigente = false
    }
  }, [idCliente])

  function cambiarPuntoVenta(id: number) {
    if (ejecutandoRef.current) return
    setIdPuntoVenta(id)
    guardarPuntoVentaSeleccionado(id)
  }

  const previewEsNoOp = preview !== null && reliquidacionEsNoOp(preview)
  const puedeEjecutar = !cargandoPreview && errorPreview === '' && preview !== null && !previewEsNoOp && confirmado && !ejecutando

  async function ejecutar() {
    // regla 9: guard de reentrancia de primera línea.
    if (ejecutandoRef.current) return
    if (!puedeEjecutar) return

    ejecutandoRef.current = true
    setEjecutando(true)
    setError('')

    // regla 3: bumpear la generación del ledger ANTES de la escritura.
    onAntesDeEscribir()

    try {
      const solicitud = aSolicitudDeReliquidacion(idPuntoVenta)
      const resultado = await clienteDeCuentaCorriente.ejecutarReliquidacion(idCliente, solicitud)
      ejecutandoRef.current = false
      setEjecutando(false)
      // regla 6: el refetch del ledger vive en el padre, aislado de este try/catch — un commit
      // 2xx nunca se reporta como fallo acá, incluida la variante no-op de la carrera preview↔commit.
      onEjecutada(resultado)
    } catch (e) {
      ejecutandoRef.current = false
      setEjecutando(false)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo ejecutar la reliquidación.')
    }
  }

  return (
    <>
      <div className="modal d-block" tabIndex={-1} role="dialog">
        <div className="modal-dialog" role="document">
          <div className="modal-content">
            <div className="modal-header">
              <h5 className="modal-title">Actualizar precios (reliquidación)</h5>
            </div>
            <div className="modal-body">
              {error && <div className="alert alert-danger py-1 px-2 small">{error}</div>}
              {errorPreview && <div className="alert alert-warning py-1 px-2 small">{errorPreview}</div>}

              {cargandoPreview && <Cargando />}

              {!cargandoPreview && preview && previewEsNoOp && (
                <p className="text-muted">No hay consumos pendientes de actualizar para este cliente.</p>
              )}

              {!cargandoPreview && preview && !previewEsNoOp && (
                <>
                  <div className="row g-3 mb-3">
                    <div className="col-md-6">
                      <div className="small text-muted">Delta estimado</div>
                      <div className="fs-6" data-testid="cc-reliq-delta-estimado">
                        {formatearMoneda(preview.delta)}
                      </div>
                    </div>
                    <div className="col-md-6">
                      <div className="small text-muted">Consumos cubiertos</div>
                      <div className="fs-6">{preview.idsMovimientosCubiertos.length}</div>
                    </div>
                  </div>
                  {preview.hayMas && (
                    <div className="alert alert-warning py-1 px-2 small">
                      Quedan más consumos pendientes — esta corrida no los cubre, va a hacer falta correr la
                      reliquidación de nuevo después.
                    </div>
                  )}

                  <DetalleDeConsumosDeReliquidacion detalle={preview.detalle} />

                  <div className="mb-3" style={{ maxWidth: 320 }}>
                    <label className="form-label" htmlFor="cc-reliq-punto-venta">
                      Punto de venta
                    </label>
                    <select
                      id="cc-reliq-punto-venta"
                      className="form-select"
                      value={idPuntoVenta}
                      disabled={ejecutando}
                      onChange={(e) => cambiarPuntoVenta(Number(e.target.value))}
                    >
                      {puntosVenta.map((p) => (
                        <option key={p.id} value={p.id}>
                          {p.nombre}
                        </option>
                      ))}
                    </select>
                  </div>

                  <div className="form-check mb-3">
                    <input
                      id="cc-reliq-confirmacion"
                      type="checkbox"
                      className="form-check-input"
                      checked={confirmado}
                      disabled={ejecutando}
                      onChange={(e) => setConfirmado(e.target.checked)}
                    />
                    <label className="form-check-label" htmlFor="cc-reliq-confirmacion">
                      Confirmo que quiero actualizar los precios de este cliente. La reliquidación es irreversible: no
                      se puede deshacer, la única corrección posible es un ajuste manual posterior.
                    </label>
                  </div>
                </>
              )}
            </div>
            <div className="modal-footer">
              <button type="button" className="btn btn-outline-secondary" disabled={ejecutando} onClick={onCerrar}>
                {previewEsNoOp ? 'Cerrar' : 'Cancelar'}
              </button>
              {!previewEsNoOp && (
                <button type="button" className="btn btn-danger" disabled={!puedeEjecutar} onClick={ejecutar}>
                  {ejecutando ? 'Ejecutando…' : 'Ejecutar reliquidación'}
                </button>
              )}
            </div>
          </div>
        </div>
      </div>
      <div className="modal-backdrop show" />
    </>
  )
}

type PropsPantalla = {
  idCliente: number
  clienteInfo: ClienteListado | null
  cargandoCliente: boolean
  errorCliente: string
  medios: MedioPagoListado[] | null
  errorMedios: string
  puntosVenta: PuntoVentaListado[] | null
  errorPuntosVenta: string
}

/** Remontada por `key={idCliente}` (regla 8) — ningún estado de acá (filtros, ledger, modal de
 * pago) sobrevive a un cambio de cliente. */
function PantallaCuentaCorriente({
  idCliente,
  clienteInfo,
  cargandoCliente,
  errorCliente,
  medios,
  errorMedios,
  puntosVenta,
  errorPuntosVenta,
}: PropsPantalla) {
  // design.md decisión 9: "the screen sends last-month by default" — se precarga acá para que los
  // inputs de filtro nunca muestren una ventana vacía mientras el default real (calculado por
  // `construirQueryEstadoDeCuenta`, Fix 1) ya viaja en la consulta.
  const [ventanaInicial] = useState(() => rangoUltimoMes())
  const [desde, setDesde] = useState(ventanaInicial.desde)
  const [hasta, setHasta] = useState(ventanaInicial.hasta)
  const [historico, setHistorico] = useState(false)

  const [estado, setEstado] = useState<EstadoDeCuenta | null>(null)
  const [cargandoEstado, setCargandoEstado] = useState(true)
  const [errorEstado, setErrorEstado] = useState('')
  const generacionEstadoRef = useRef(0)

  const [modalPagoAbierto, setModalPagoAbierto] = useState(false)
  const [modalAjusteAbierto, setModalAjusteAbierto] = useState(false)
  const [modalReliquidacionAbierto, setModalReliquidacionAbierto] = useState(false)
  // Aviso compartido por las tres acciones de escritura (pago, ajuste, reliquidación) — mismo
  // patrón de banner único que ya usaba el pago (rule 10 sweep: el aviso de éxito aplica parejo).
  const [aviso, setAviso] = useState('')

  const { usuario } = useAuth()
  // Cosmético (design: Web Composition — "el servidor vuelve a exigir SupervisionDeCuentaCorriente
  // en cada request"): un Vendedor no ve estos botones, pero incluso si forzara el DOM el 403 del
  // servidor sigue siendo la autoridad real.
  const esSupervisorOAdmin = usuario !== null && puedeSupervisarCuentaCorriente(usuario.rolId)

  // regla 2: cada cambio de filtro dispara una nueva consulta — una respuesta desactualizada
  // nunca puede pisar la más reciente.
  const cargarEstado = useCallback(() => {
    const miGeneracion = (generacionEstadoRef.current += 1)
    setCargandoEstado(true)
    setErrorEstado('')

    clienteDeCuentaCorriente
      .obtenerEstado(idCliente, desde, hasta, historico)
      .then((datos) => {
        if (generacionEstadoRef.current !== miGeneracion) return
        setEstado(datos)
      })
      .catch((e) => {
        if (generacionEstadoRef.current !== miGeneracion) return
        setEstado(null)
        setErrorEstado(e instanceof ErrorApi ? e.message : 'No se pudo cargar el estado de cuenta.')
      })
      .finally(() => {
        if (generacionEstadoRef.current !== miGeneracion) return
        setCargandoEstado(false)
      })
  }, [idCliente, desde, hasta, historico])

  useEffect(() => {
    cargarEstado()
  }, [cargarEstado])

  const esConsumidorFinal = clienteInfo?.esConsumidorFinal ?? false
  // Fix 2 (react-async-state regla 7): `clienteInfo !== null` falla cerrado tanto mientras la
  // identidad todavía carga como cuando el fetch terminó en error — un cliente NUNCA verificado
  // no puede habilitar el pago, sin importar qué valor por default tuviera `esConsumidorFinal`.
  const puedeIngresarPago =
    !cargandoCliente &&
    clienteInfo !== null &&
    errorCliente === '' &&
    medios !== null &&
    errorMedios === '' &&
    puntosVenta !== null &&
    errorPuntosVenta === '' &&
    puntosVenta.length > 0 &&
    !esConsumidorFinal

  let motivoBloqueoPago: string | undefined
  if (cargandoCliente) {
    motivoBloqueoPago = 'Cargando los datos del cliente…'
  } else if (errorCliente) {
    motivoBloqueoPago = 'No se pudo confirmar el cliente — no se puede ingresar un pago hasta que esto se resuelva.'
  } else if (esConsumidorFinal) {
    motivoBloqueoPago = 'El Consumidor Final no tiene cuenta corriente.'
  } else if (!puedeIngresarPago) {
    motivoBloqueoPago = 'No se pudieron cargar los datos necesarios para registrar un pago.'
  }

  // El ajuste manual y la reliquidación no necesitan medios de pago (ninguno de los dos mueve
  // plata física), pero sí cliente identificado y al menos un punto de venta — mismo criterio de
  // fail-closed que `puedeIngresarPago` (Fix 2, regla 7).
  const puedeSupervisarCC =
    esSupervisorOAdmin &&
    !cargandoCliente &&
    clienteInfo !== null &&
    errorCliente === '' &&
    puntosVenta !== null &&
    errorPuntosVenta === '' &&
    puntosVenta.length > 0 &&
    !esConsumidorFinal

  let motivoBloqueoSupervision: string | undefined
  if (cargandoCliente) {
    motivoBloqueoSupervision = 'Cargando los datos del cliente…'
  } else if (errorCliente) {
    motivoBloqueoSupervision = 'No se pudo confirmar el cliente — no se puede continuar hasta que esto se resuelva.'
  } else if (esConsumidorFinal) {
    motivoBloqueoSupervision = 'El Consumidor Final no tiene cuenta corriente.'
  } else if (!puedeSupervisarCC) {
    motivoBloqueoSupervision = 'No se pudieron cargar los datos necesarios.'
  }

  return (
    <div className="container-fluid py-4">
      <Box
        titulo={`Estado de cuenta — ${nombreDeCliente(idCliente, clienteInfo)}`}
        herramientas={
          <div className="d-flex gap-2">
            <button
              type="button"
              className="btn btn-sm btn-outline-secondary d-print-none"
              onClick={() => window.print()}
            >
              Imprimir
            </button>
            <Link className="btn btn-sm btn-outline-secondary d-print-none" to="/clientes">
              Volver a clientes
            </Link>
          </div>
        }
      >
        {/* Vista de impresión (design decisión 13: mismo componente, `@media print`, sin ruta ni
            fetch dedicados): equivalente del encabezado que llevan los exports XLSX — rango y
            generado por/cuándo. Cliente ya está en el título de `Box`, que se imprime igual. */}
        <div className="d-none d-print-block mb-3">
          <div className="small">
            Rango: {historico ? 'Histórico completo' : `${desde} a ${hasta}`}
          </div>
          <div className="small">
            Generado: {new Date().toLocaleString('es-AR')} — {usuario?.usuario ?? '—'}
          </div>
        </div>

        {aviso && <div className="alert alert-success">{aviso}</div>}
        {errorEstado && <div className="alert alert-danger">{errorEstado}</div>}
        {(errorCliente || errorMedios || errorPuntosVenta) && (
          <div className="alert alert-warning py-1 px-2 small">
            {errorCliente || errorMedios || errorPuntosVenta} No se pueden registrar operaciones de cuenta corriente
            hasta que esto se resuelva.
          </div>
        )}

        {cargandoEstado && !estado && <Cargando />}

        {estado && (
          <>
            <div className="row g-3 mb-3 align-items-end">
              <div className="col-md-3">
                <div className="small text-muted">Saldo</div>
                <div className="fs-5">{formatearMoneda(estado.header.saldo)}</div>
              </div>
              <div className="col-md-3">
                <div className="small text-muted">Límite de crédito</div>
                <div>{estado.header.creditoIlimitado ? 'Ilimitado' : formatearMoneda(estado.header.limiteCredito)}</div>
              </div>
              <div className="col-md-3">
                <div className="small text-muted">Disponibilidad</div>
                <div>{formatearDisponibilidad(estado.header.disponibilidad)}</div>
              </div>
              <div className="col-md-3 text-md-end">
                <div className="d-flex gap-2 justify-content-md-end flex-wrap d-print-none">
                  {esSupervisorOAdmin && (
                    <>
                      <button
                        type="button"
                        className="btn btn-outline-secondary"
                        disabled={!puedeSupervisarCC}
                        title={motivoBloqueoSupervision}
                        onClick={() => {
                          setAviso('')
                          setModalAjusteAbierto(true)
                        }}
                      >
                        Ajuste manual
                      </button>
                      <button
                        type="button"
                        className="btn btn-outline-danger"
                        disabled={!puedeSupervisarCC}
                        title={motivoBloqueoSupervision}
                        onClick={() => {
                          setAviso('')
                          setModalReliquidacionAbierto(true)
                        }}
                      >
                        Actualizar precios
                      </button>
                    </>
                  )}
                  <button
                    type="button"
                    className="btn btn-primary"
                    disabled={!puedeIngresarPago}
                    title={motivoBloqueoPago}
                    onClick={() => {
                      setAviso('')
                      setModalPagoAbierto(true)
                    }}
                  >
                    Ingresar pago
                  </button>
                </div>
              </div>
            </div>

            <div className="row g-2 align-items-end mb-3 d-print-none">
              <div className="col-md-3">
                <label className="form-label" htmlFor="cc-filtro-desde">
                  Desde
                </label>
                <input
                  id="cc-filtro-desde"
                  type="date"
                  className="form-control"
                  value={desde}
                  disabled={historico}
                  onChange={(e) => setDesde(e.target.value)}
                />
              </div>
              <div className="col-md-3">
                <label className="form-label" htmlFor="cc-filtro-hasta">
                  Hasta
                </label>
                <input
                  id="cc-filtro-hasta"
                  type="date"
                  className="form-control"
                  value={hasta}
                  disabled={historico}
                  onChange={(e) => setHasta(e.target.value)}
                />
              </div>
              <div className="col-md-3">
                <div className="form-check">
                  <input
                    id="cc-filtro-historico"
                    type="checkbox"
                    className="form-check-input"
                    checked={historico}
                    onChange={(e) => {
                      const marcado = e.target.checked
                      setHistorico(marcado)
                      if (marcado) {
                        // "Ver histórico" limpia la ventana — los inputs, deshabilitados y
                        // vacíos, reflejan que la consulta ya no tiene ningún recorte de fecha.
                        setDesde('')
                        setHasta('')
                      } else {
                        // Al destildar, la ventana efectiva vuelve a ser la de último mes — los
                        // inputs nunca quedan en blanco mostrando una ventana invisible.
                        const ventana = rangoUltimoMes()
                        setDesde(ventana.desde)
                        setHasta(ventana.hasta)
                      }
                    }}
                  />
                  <label className="form-check-label" htmlFor="cc-filtro-historico">
                    Ver histórico completo
                  </label>
                </div>
              </div>
            </div>

            {cargandoEstado && <p className="text-muted">Actualizando…</p>}

            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Fecha</th>
                    <th>Tipo</th>
                    <th>Detalle</th>
                    <th className="text-end">Importe</th>
                    <th className="text-end">Saldo</th>
                  </tr>
                </thead>
                <tbody>
                  {estado.movimientos.map((m) => (
                    <tr key={m.id}>
                      <td>{formatearFechaHora(m.fecha)}</td>
                      <td>{etiquetaDeMovimiento(m)}</td>
                      <td>
                        {m.tipo === 'ActualizacionPrecios' ? (
                          <DetalleDeMovimientoDeActualizacionPrecios detalle={m.detalle} />
                        ) : (
                          (m.detalle ?? '—')
                        )}
                      </td>
                      <td className="text-end">{formatearMoneda(m.importe)}</td>
                      <td className="text-end">{formatearMoneda(m.saldoResultante)}</td>
                    </tr>
                  ))}
                  {estado.movimientos.length === 0 && (
                    <tr>
                      <td colSpan={5} className="text-center text-muted py-4">
                        No hay movimientos en el período seleccionado.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </>
        )}
      </Box>

      {modalPagoAbierto && estado && medios && puntosVenta && (
        <ModalPagoACuenta
          idCliente={idCliente}
          puntosVenta={puntosVenta}
          medios={medios}
          header={estado.header}
          onCerrar={() => setModalPagoAbierto(false)}
          onAntesDeEscribir={() => {
            generacionEstadoRef.current += 1
          }}
          onRegistrado={(comprobante) => {
            setModalPagoAbierto(false)
            setAviso(`Pago registrado: comprobante ${comprobante.numeroVisible}.`)
            // regla 6: el refetch queda aislado del try/catch de la escritura del modal — si
            // falla, no pisa el aviso de éxito de arriba (solo el propio error de carga del
            // ledger, que ya tiene su mensaje distinguible).
            cargarEstado()
          }}
        />
      )}

      {modalAjusteAbierto && estado && puntosVenta && (
        <ModalAjusteDeCuenta
          idCliente={idCliente}
          puntosVenta={puntosVenta}
          header={estado.header}
          onCerrar={() => setModalAjusteAbierto(false)}
          onAntesDeEscribir={() => {
            generacionEstadoRef.current += 1
          }}
          onRegistrado={(movimiento) => {
            setModalAjusteAbierto(false)
            setAviso(`Ajuste registrado: ${formatearMoneda(movimiento.importe)}.`)
            // regla 6: el refetch queda aislado del try/catch de la escritura del modal.
            cargarEstado()
          }}
        />
      )}

      {modalReliquidacionAbierto && estado && puntosVenta && (
        <ModalReliquidacion
          idCliente={idCliente}
          puntosVenta={puntosVenta}
          onCerrar={() => setModalReliquidacionAbierto(false)}
          onAntesDeEscribir={() => {
            generacionEstadoRef.current += 1
          }}
          onEjecutada={(resultado) => {
            setModalReliquidacionAbierto(false)
            // regla 6: un commit 2xx nunca se reporta como fallo — el no-op de la carrera
            // preview↔commit (design: "a consumo committing during a run is simply picked up by
            // the next run") también es un aviso de éxito, no un error.
            setAviso(
              reliquidacionEsNoOp(resultado)
                ? 'No había nada para actualizar.'
                : `Precios actualizados: ${formatearMoneda(resultado.delta)} sobre ${resultado.idsMovimientosCubiertos.length} consumo(s).${resultado.hayMas ? ' Quedan más consumos pendientes — corré la reliquidación de nuevo.' : ''}`,
            )
            cargarEstado()
          }}
        />
      )}
    </div>
  )
}

/**
 * Pantalla de estado de cuenta (stage-7-cuenta-corriente, Slice 5, design: Web Composition):
 * header (saldo/acuerdo/disponibilidad), ledger newest-first con filtros desde/hasta/histórico y
 * el modal de pago a cuenta. Entrada desde una fila de `Clientes.tsx` (`Politicas.OperacionDePos`
 * — todo rol opera, a diferencia de `/clientes` que es admin-only: un Vendedor llega acá por URL
 * directa, no todavía desde una acción de `Clientes.tsx`).
 */
export function CuentaCorriente() {
  const { id } = useParams<{ id: string }>()
  const idCliente = Number(id)
  const idClienteValido = id !== undefined && Number.isFinite(idCliente)

  const location = useLocation()
  const clienteDeState = (location.state as { cliente?: ClienteListado } | null)?.cliente ?? null

  // El Vendedor SIEMPRE llega acá sin `location.state` (URL directa, único camino del rol) y
  // cualquier refresh lo pierde también — sin este fetch, el header mentía "Cliente #N" y el gate
  // de Consumidor Final quedaba deshabilitado del lado del cliente (el servidor lo sigue
  // rechazando, pero el botón se veía habilitado). Generación-gateado (regla 2): mientras se
  // resuelve, `puedeIngresarPago` falla cerrado vía `cargandoCliente`.
  const [clienteInfo, setClienteInfo] = useState<ClienteListado | null>(clienteDeState)
  const [cargandoCliente, setCargandoCliente] = useState(clienteDeState === null)
  const [errorCliente, setErrorCliente] = useState('')
  const generacionClienteRef = useRef(0)

  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [errorMedios, setErrorMedios] = useState('')

  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

  useEffect(() => {
    if (clienteDeState !== null || !idClienteValido) {
      setClienteInfo(clienteDeState)
      setCargandoCliente(false)
      setErrorCliente('')
      return
    }

    let vigente = true
    const miGeneracion = (generacionClienteRef.current += 1)
    setCargandoCliente(true)
    setErrorCliente('')

    clienteDeClientes
      .obtener(idCliente)
      .then((cliente) => {
        if (!vigente || generacionClienteRef.current !== miGeneracion) return
        setClienteInfo(cliente)
      })
      .catch((e) => {
        if (!vigente || generacionClienteRef.current !== miGeneracion) return
        setClienteInfo(null)
        // Fix 2 (react-async-state regla 7): sin este aviso, un fallo de red dejaba
        // `clienteInfo` en null y el gate de Consumidor Final (`?? false`) habilitaba
        // "Ingresar pago" para un cliente NUNCA verificado.
        setErrorCliente(e instanceof ErrorApi ? e.message : 'No se pudo confirmar el cliente.')
      })
      .finally(() => {
        if (!vigente || generacionClienteRef.current !== miGeneracion) return
        setCargandoCliente(false)
      })

    return () => {
      vigente = false
    }
  }, [idCliente, idClienteValido, clienteDeState])

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
        setErrorMedios(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los medios de pago.')
      })

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

  if (!idClienteValido) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Estado de cuenta" variante="warning">
          <p className="text-muted">No se especificó el cliente.</p>
          <Link className="btn btn-outline-secondary" to="/clientes">
            Volver a clientes
          </Link>
        </Box>
      </div>
    )
  }

  return (
    <PantallaCuentaCorriente
      key={idCliente}
      idCliente={idCliente}
      clienteInfo={clienteInfo}
      cargandoCliente={cargandoCliente}
      errorCliente={errorCliente}
      medios={medios}
      errorMedios={errorMedios}
      puntosVenta={puntosVenta}
      errorPuntosVenta={errorPuntosVenta}
    />
  )
}
