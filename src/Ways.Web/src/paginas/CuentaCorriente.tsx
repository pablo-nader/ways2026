import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useLocation, useParams } from 'react-router'
import { clienteDeCaja } from '../api/caja'
import { clienteDeCatalogo } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import { clienteDeOrganizacion } from '../api/organizacion'
import {
  aSolicitudDeAjuste,
  aSolicitudDePagoACuenta,
  aSolicitudDeReliquidacion,
  calcularImporteAplicado,
  clienteDeCuentaCorriente,
  disponibilidadPrevia,
  etiquetaDeMovimiento,
  filaPagoACuentaVacia,
  filasAPagosACuentaParaCalculo,
  medioFisicoParaPagoACuenta,
  rangoUltimoMes,
  reliquidacionEsNoOp,
  saldoResultanteDeAjuste,
  validarAjusteLocal,
  validarPagoACuentaLocal,
  type FilaPagoACuenta,
} from '../api/cuentaCorriente'
import type {
  ClienteListado,
  ComprobanteEmitido,
  EstadoDeCuenta,
  EstadoDeCuentaHeader,
  MedioPagoAlta,
  MedioPagoListado,
  MovimientoDeCuentaCorriente,
  ParametroResuelto,
  PuntoVentaListado,
  ResultadoDeReliquidacion,
} from '../api/tipos'
import { puedeSupervisarCuentaCorriente } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

const CLAVE_PUNTO_VENTA = 'ways.cuentaCorriente.idPuntoVenta'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

function leerPuntoVentaGuardado(): number | null {
  try {
    const crudo = localStorage.getItem(CLAVE_PUNTO_VENTA)
    return crudo ? Number(crudo) : null
  } catch {
    return null
  }
}

function guardarPuntoVentaSeleccionado(id: number) {
  try {
    localStorage.setItem(CLAVE_PUNTO_VENTA, String(id))
  } catch {
    // localStorage puede no estar disponible (modo privado del navegador) — la selección
    // simplemente no persiste entre sesiones, el resto de la pantalla sigue funcionando.
  }
}

function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

function formatearDisponibilidad(valor: number | null): string {
  return valor === null ? 'Ilimitado' : formatearMoneda(valor)
}

function nombreDeCliente(idCliente: number, cliente: ClienteListado | null): string {
  if (!cliente) return `Cliente #${idCliente}`
  const nombreCompleto = cliente.razonSocial ?? [cliente.nombre, cliente.apellido].filter(Boolean).join(' ')
  return `#${String(cliente.numero).padStart(4, '0')} — ${nombreCompleto}`
}

type PropsAperturaEnModal = { idPuntoVenta: number; onAbierto: () => void; onCancelar: () => void }

/**
 * Rule 10 (react-async-state): mismo recurso de recuperación que `PanelGateTurno` (Pos.tsx) /
 * `FormularioApertura` (Caja.tsx) — un `409 turno_no_abierto` durante un pago a cuenta ofrece
 * abrir el turno ahí mismo, sin perder los datos del pago que ya se cargaron en el modal. El pago
 * NUNCA se reintenta solo (regla 9): el cajero vuelve a apretar «Registrar pago» a mano.
 */
function PanelAperturaDeTurnoEnModal({ idPuntoVenta, onAbierto, onCancelar }: PropsAperturaEnModal) {
  const [fondoInicial, setFondoInicial] = useState('')
  const [observaciones, setObservaciones] = useState('')
  const [abriendo, setAbriendo] = useState(false)
  const abriendoRef = useRef(false)
  const [error, setError] = useState('')

  async function abrir() {
    // regla 9: guard de reentrancia de primera línea.
    if (abriendoRef.current) return

    const fondo = Number(fondoInicial)
    if (fondoInicial.trim() === '' || !Number.isFinite(fondo) || fondo < 0) {
      setError('El fondo inicial tiene que ser un número mayor o igual a 0.')
      return
    }

    abriendoRef.current = true
    setAbriendo(true)
    setError('')

    try {
      await clienteDeCaja.abrir({
        idPuntoVenta,
        fondoInicial: fondo,
        observaciones: observaciones.trim() === '' ? null : observaciones.trim(),
      })
      onAbierto()
    } catch (e) {
      if (e instanceof ErrorApi && e.codigo === 'turno_ya_abierto') {
        // Autocuración (mismo criterio que FormularioApertura/PanelGateTurno): otra
        // pestaña/cajero ganó la carrera de apertura — el turno YA está abierto, la continuación
        // de éxito es la correcta.
        onAbierto()
      } else {
        setError(e instanceof ErrorApi ? e.message : 'No se pudo abrir el turno.')
      }
    } finally {
      abriendoRef.current = false
      setAbriendo(false)
    }
  }

  return (
    <>
      <div className="modal-header">
        <h5 className="modal-title">No hay un turno abierto</h5>
      </div>
      <div className="modal-body">
        <p className="text-muted">
          Para registrar un pago a cuenta hace falta abrir un turno de caja en este punto de venta. Los datos del
          pago que ya cargaste quedan como están — al abrir el turno volvés a este formulario para apretar
          «Registrar pago» de nuevo.
        </p>
        {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}
        <div className="row g-2 align-items-end">
          <div className="col-md-6">
            <label className="form-label" htmlFor="cc-gate-fondo-inicial">
              Fondo inicial
            </label>
            <input
              id="cc-gate-fondo-inicial"
              type="number"
              step="0.01"
              min="0"
              className="form-control rounded-0"
              value={fondoInicial}
              disabled={abriendo}
              onChange={(e) => setFondoInicial(e.target.value)}
            />
          </div>
          <div className="col-md-6">
            <label className="form-label" htmlFor="cc-gate-observaciones">
              Observaciones
            </label>
            <input
              id="cc-gate-observaciones"
              type="text"
              className="form-control rounded-0"
              value={observaciones}
              disabled={abriendo}
              onChange={(e) => setObservaciones(e.target.value)}
            />
          </div>
        </div>
      </div>
      <div className="modal-footer">
        <button type="button" className="btn btn-outline-secondary rounded-0" disabled={abriendo} onClick={onCancelar}>
          Cancelar
        </button>
        <button type="button" className="btn btn-primary rounded-0" disabled={abriendo} onClick={abrir}>
          {abriendo ? 'Abriendo…' : 'Abrir turno'}
        </button>
      </div>
    </>
  )
}

type PropsModalPago = {
  idCliente: number
  puntosVenta: PuntoVentaListado[]
  medios: MedioPagoListado[]
  header: EstadoDeCuentaHeader
  onCerrar: () => void
  onAntesDeEscribir: () => void
  onRegistrado: (comprobante: ComprobanteEmitido) => void
}

/**
 * Modal de pago a cuenta (design: Web Composition). `react-async-state`: regla 9 (guard de
 * reentrancia + deshabilitado de ventana completa mientras `registrando`), regla 3 (el llamador
 * bumpea la generación del ledger ANTES de este POST, vía `onAntesDeEscribir`), regla 6 (el
 * refetch posterior vive en el padre, fuera del try/catch de esta escritura), regla 10 (recupera
 * `turno_no_abierto` con el mismo patrón que `PanelGateTurno`/`FormularioApertura`).
 */
function ModalPagoACuenta({ idCliente, puntosVenta, medios, header, onCerrar, onAntesDeEscribir, onRegistrado }: PropsModalPago) {
  const [idPuntoVenta, setIdPuntoVenta] = useState<number>(
    () => puntosVenta.find((p) => p.id === leerPuntoVentaGuardado())?.id ?? puntosVenta[0].id,
  )

  const [parametros, setParametros] = useState<{ vueltoMaximo: number } | null>(null)
  const [errorParametros, setErrorParametros] = useState('')
  const generacionParametrosRef = useRef(0)

  const proximaFilaIdRef = useRef(1)
  const [filas, setFilas] = useState<FilaPagoACuenta[]>(() => [filaPagoACuentaVacia(proximaFilaIdRef.current++)])
  const [observaciones, setObservaciones] = useState('')

  const [registrando, setRegistrando] = useState(false)
  const registrandoRef = useRef(false)
  const [error, setError] = useState('')
  const [gateTurno, setGateTurno] = useState(false)

  const mediosFisicos = useMemo(() => medios.filter(medioFisicoParaPagoACuenta), [medios])
  const medioPorId = useMemo(() => {
    const indice: Record<number, MedioPagoListado> = {}
    for (const m of medios) indice[m.id] = m
    return indice
  }, [medios])

  // regla 2: cada cambio de punto de venta dispara una nueva resolución de vuelto_maximo — una
  // respuesta desactualizada nunca puede pisar la más reciente.
  useEffect(() => {
    // El botón de pago no puede correr con el vuelto_maximo del PV anterior mientras se resuelve
    // el nuevo — se limpia ANTES de cualquier chequeo, no solo en la rama sin PV.
    setParametros(null)

    const puntoVentaSeleccionado = puntosVenta.find((p) => p.id === idPuntoVenta) ?? null
    if (!puntoVentaSeleccionado) {
      setErrorParametros('')
      return
    }

    const miGeneracion = (generacionParametrosRef.current += 1)
    let vigente = true

    api
      .get<ParametroResuelto>(
        `/parametros/vuelto_maximo?idEmpresa=${puntoVentaSeleccionado.idEmpresa}&idPuntoVenta=${puntoVentaSeleccionado.id}`,
      )
      .then((valor) => {
        if (!vigente || generacionParametrosRef.current !== miGeneracion) return
        setParametros({ vueltoMaximo: Number(valor.valor) })
        setErrorParametros('')
      })
      .catch((e) => {
        if (!vigente || generacionParametrosRef.current !== miGeneracion) return
        setParametros(null)
        setErrorParametros(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los parámetros de pago.')
      })

    return () => {
      vigente = false
    }
  }, [idPuntoVenta, puntosVenta])

  function cambiarPuntoVenta(id: number) {
    if (registrandoRef.current) return
    setIdPuntoVenta(id)
    guardarPuntoVentaSeleccionado(id)
  }

  function actualizarFila(id: number, cambios: Partial<FilaPagoACuenta>) {
    if (registrandoRef.current) return
    // regla 1: updater funcional, nunca lee `filas` del cierre.
    setFilas((prev) => prev.map((f) => (f.id === id ? { ...f, ...cambios } : f)))
  }

  function agregarFila() {
    if (registrandoRef.current) return
    setFilas((prev) => [...prev, filaPagoACuentaVacia(proximaFilaIdRef.current++)])
  }

  function quitarFila(id: number) {
    if (registrandoRef.current) return
    setFilas((prev) => (prev.length <= 1 ? prev : prev.filter((f) => f.id !== id)))
  }

  const pagosCalculo = filasAPagosACuentaParaCalculo(filas, medioPorId)
  const importeAplicado = calcularImporteAplicado(pagosCalculo)
  const saldoEstimado = header.saldo - importeAplicado
  const disponibilidadEstimada = disponibilidadPrevia(saldoEstimado, header.limiteCredito, header.creditoIlimitado)

  async function registrarPago() {
    // regla 9: guard de reentrancia de primera línea.
    if (registrandoRef.current) return

    if (!parametros) {
      setError('No se pudieron cargar los parámetros de pago.')
      return
    }

    const rechazo = validarPagoACuentaLocal({ pagos: pagosCalculo, vueltoMaximo: parametros.vueltoMaximo })
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
      const solicitud = aSolicitudDePagoACuenta(idPuntoVenta, pagosCalculo, observaciones)
      const comprobante = await clienteDeCuentaCorriente.registrarPago(idCliente, solicitud)
      registrandoRef.current = false
      setRegistrando(false)
      // regla 6: el refetch del ledger vive en el padre, aislado de este try/catch.
      onRegistrado(comprobante)
    } catch (e) {
      registrandoRef.current = false
      setRegistrando(false)
      if (e instanceof ErrorApi && e.codigo === 'turno_no_abierto') {
        setGateTurno(true)
      } else {
        setError(e instanceof ErrorApi ? e.message : 'No se pudo registrar el pago.')
      }
    }
  }

  return (
    <>
      <div className="modal d-block" tabIndex={-1} role="dialog">
        <div className="modal-dialog modal-lg" role="document">
          <div className="modal-content rounded-0">
            {gateTurno ? (
              <PanelAperturaDeTurnoEnModal idPuntoVenta={idPuntoVenta} onAbierto={() => setGateTurno(false)} onCancelar={onCerrar} />
            ) : (
              <>
                <div className="modal-header">
                  <h5 className="modal-title">Ingresar pago a cuenta</h5>
                </div>
                <div className="modal-body">
                  {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}
                  {errorParametros && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorParametros}</div>}

                  <div className="mb-3" style={{ maxWidth: 320 }}>
                    <label className="form-label" htmlFor="cc-pago-punto-venta">
                      Punto de venta
                    </label>
                    <select
                      id="cc-pago-punto-venta"
                      className="form-select rounded-0"
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

                  {filas.map((fila) => {
                    const medioDeFila = fila.idMedioPago === '' ? null : medioPorId[fila.idMedioPago]
                    return (
                      <div className="row g-2 align-items-end mb-2" key={fila.id}>
                        <div className="col-md-3">
                          <label className="form-label" htmlFor={`cc-pago-medio-${fila.id}`}>
                            Medio de pago
                          </label>
                          <select
                            id={`cc-pago-medio-${fila.id}`}
                            className="form-select rounded-0"
                            value={fila.idMedioPago}
                            disabled={registrando}
                            onChange={(e) =>
                              actualizarFila(fila.id, { idMedioPago: e.target.value === '' ? '' : Number(e.target.value) })
                            }
                          >
                            <option value="">Elegir…</option>
                            {mediosFisicos.map((m) => (
                              <option key={m.id} value={m.id}>
                                {m.nombre}
                              </option>
                            ))}
                          </select>
                        </div>
                        <div className="col-md-2">
                          <label className="form-label" htmlFor={`cc-pago-importe-${fila.id}`}>
                            Importe
                          </label>
                          <input
                            id={`cc-pago-importe-${fila.id}`}
                            type="number"
                            step="0.01"
                            min="0"
                            className="form-control rounded-0"
                            value={fila.importe}
                            disabled={registrando}
                            onChange={(e) => actualizarFila(fila.id, { importe: e.target.value })}
                          />
                        </div>
                        <div className="col-md-3">
                          <label className="form-label" htmlFor={`cc-pago-referencia-${fila.id}`}>
                            Referencia{medioDeFila?.requiereReferencia ? ' (obligatoria)' : ''}
                          </label>
                          <input
                            id={`cc-pago-referencia-${fila.id}`}
                            type="text"
                            className="form-control rounded-0"
                            value={fila.referencia}
                            disabled={registrando}
                            onChange={(e) => actualizarFila(fila.id, { referencia: e.target.value })}
                          />
                        </div>
                        <div className="col-md-2">
                          <label className="form-label" htmlFor={`cc-pago-vuelto-${fila.id}`}>
                            Vuelto
                          </label>
                          <input
                            id={`cc-pago-vuelto-${fila.id}`}
                            type="number"
                            step="0.01"
                            min="0"
                            className="form-control rounded-0"
                            value={fila.vuelto}
                            disabled={registrando || !medioDeFila?.admiteVuelto}
                            onChange={(e) => actualizarFila(fila.id, { vuelto: e.target.value })}
                          />
                        </div>
                        <div className="col-md-2">
                          <button
                            type="button"
                            className="btn btn-outline-danger btn-sm rounded-0 w-100"
                            disabled={registrando || filas.length === 1}
                            onClick={() => quitarFila(fila.id)}
                          >
                            Quitar
                          </button>
                        </div>
                      </div>
                    )
                  })}

                  <button type="button" className="btn btn-outline-secondary btn-sm rounded-0 mb-3" disabled={registrando} onClick={agregarFila}>
                    + Agregar otro medio
                  </button>

                  <div className="mb-3">
                    <label className="form-label" htmlFor="cc-pago-observaciones">
                      Observaciones
                    </label>
                    <input
                      id="cc-pago-observaciones"
                      type="text"
                      className="form-control rounded-0"
                      value={observaciones}
                      disabled={registrando}
                      onChange={(e) => setObservaciones(e.target.value)}
                    />
                  </div>

                  <div className="row g-3">
                    <div className="col-md-4">
                      <div className="small text-muted">Importe aplicado</div>
                      <div>{formatearMoneda(importeAplicado)}</div>
                    </div>
                    <div className="col-md-4">
                      <div className="small text-muted">Saldo estimado tras el pago</div>
                      <div>{formatearMoneda(saldoEstimado)}</div>
                    </div>
                    <div className="col-md-4">
                      <div className="small text-muted">Disponibilidad estimada</div>
                      <div>{formatearDisponibilidad(disponibilidadEstimada)}</div>
                    </div>
                  </div>
                </div>
                <div className="modal-footer">
                  <button type="button" className="btn btn-outline-secondary rounded-0" disabled={registrando} onClick={onCerrar}>
                    Cancelar
                  </button>
                  <button
                    type="button"
                    className="btn btn-primary rounded-0"
                    disabled={registrando || !parametros || pagosCalculo.length === 0}
                    onClick={registrarPago}
                  >
                    {registrando ? 'Registrando…' : 'Registrar pago'}
                  </button>
                </div>
              </>
            )}
          </div>
        </div>
      </div>
      <div className="modal-backdrop show" />
    </>
  )
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
  const [importe, setImporte] = useState('')
  const [detalle, setDetalle] = useState('')

  const [registrando, setRegistrando] = useState(false)
  const registrandoRef = useRef(false)
  const [error, setError] = useState('')

  const importeNumerico = importe.trim() === '' ? Number.NaN : Number(importe)
  const saldoResultante = Number.isFinite(importeNumerico) ? saldoResultanteDeAjuste(header.saldo, importeNumerico) : null

  function cambiarPuntoVenta(id: number) {
    if (registrandoRef.current) return
    setIdPuntoVenta(id)
    guardarPuntoVentaSeleccionado(id)
  }

  async function registrarAjuste() {
    // regla 9: guard de reentrancia de primera línea.
    if (registrandoRef.current) return

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
          <div className="modal-content rounded-0">
            <div className="modal-header">
              <h5 className="modal-title">Ajuste manual de cuenta corriente</h5>
            </div>
            <div className="modal-body">
              {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}

              <div className="mb-3" style={{ maxWidth: 320 }}>
                <label className="form-label" htmlFor="cc-ajuste-punto-venta">
                  Punto de venta
                </label>
                <select
                  id="cc-ajuste-punto-venta"
                  className="form-select rounded-0"
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
                <input
                  id="cc-ajuste-importe"
                  type="number"
                  step="0.01"
                  className="form-control rounded-0"
                  value={importe}
                  disabled={registrando}
                  onChange={(e) => setImporte(e.target.value)}
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
                  className="form-control rounded-0"
                  value={detalle}
                  disabled={registrando}
                  onChange={(e) => setDetalle(e.target.value)}
                />
              </div>

              <div className="small text-muted">Saldo actual: {formatearMoneda(header.saldo)}</div>
              {saldoResultante !== null && <div className="fs-6">Saldo resultante: {formatearMoneda(saldoResultante)}</div>}
            </div>
            <div className="modal-footer">
              <button type="button" className="btn btn-outline-secondary rounded-0" disabled={registrando} onClick={onCerrar}>
                Cancelar
              </button>
              <button type="button" className="btn btn-primary rounded-0" disabled={registrando} onClick={registrarAjuste}>
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
          <div className="modal-content rounded-0">
            <div className="modal-header">
              <h5 className="modal-title">Actualizar precios (reliquidación)</h5>
            </div>
            <div className="modal-body">
              {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}
              {errorPreview && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorPreview}</div>}

              {cargandoPreview && <Cargando />}

              {!cargandoPreview && preview && previewEsNoOp && (
                <p className="text-muted">No hay consumos pendientes de actualizar para este cliente.</p>
              )}

              {!cargandoPreview && preview && !previewEsNoOp && (
                <>
                  <div className="row g-3 mb-3">
                    <div className="col-md-6">
                      <div className="small text-muted">Delta estimado</div>
                      <div className="fs-6">{formatearMoneda(preview.delta)}</div>
                    </div>
                    <div className="col-md-6">
                      <div className="small text-muted">Consumos cubiertos</div>
                      <div className="fs-6">{preview.idsMovimientosCubiertos.length}</div>
                    </div>
                  </div>
                  {preview.hayMas && (
                    <div className="alert alert-warning rounded-0 py-1 px-2 small">
                      Quedan más consumos pendientes — esta corrida no los cubre, va a hacer falta correr la
                      reliquidación de nuevo después.
                    </div>
                  )}

                  <div className="mb-3" style={{ maxWidth: 320 }}>
                    <label className="form-label" htmlFor="cc-reliq-punto-venta">
                      Punto de venta
                    </label>
                    <select
                      id="cc-reliq-punto-venta"
                      className="form-select rounded-0"
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
                      className="form-check-input rounded-0"
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
              <button type="button" className="btn btn-outline-secondary rounded-0" disabled={ejecutando} onClick={onCerrar}>
                {previewEsNoOp ? 'Cerrar' : 'Cancelar'}
              </button>
              {!previewEsNoOp && (
                <button type="button" className="btn btn-danger rounded-0" disabled={!puedeEjecutar} onClick={ejecutar}>
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
          <Link className="btn btn-sm btn-outline-light rounded-0" to="/clientes">
            Volver a clientes
          </Link>
        }
      >
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}
        {errorEstado && <div className="alert alert-danger rounded-0">{errorEstado}</div>}
        {(errorCliente || errorMedios || errorPuntosVenta) && (
          <div className="alert alert-warning rounded-0 py-1 px-2 small">
            {errorCliente || errorMedios || errorPuntosVenta} No se puede ingresar un pago hasta que esto se resuelva.
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
                <div className="d-flex gap-2 justify-content-md-end flex-wrap">
                  {esSupervisorOAdmin && (
                    <>
                      <button
                        type="button"
                        className="btn btn-outline-secondary rounded-0"
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
                        className="btn btn-outline-danger rounded-0"
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
                    className="btn btn-primary rounded-0"
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

            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-3">
                <label className="form-label" htmlFor="cc-filtro-desde">
                  Desde
                </label>
                <input
                  id="cc-filtro-desde"
                  type="date"
                  className="form-control rounded-0"
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
                  className="form-control rounded-0"
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
                    className="form-check-input rounded-0"
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
                      <td>{m.detalle ?? '—'}</td>
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
          <Link className="btn btn-outline-secondary rounded-0" to="/clientes">
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
