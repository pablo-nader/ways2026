import { useMemo, useRef, useState } from 'react'
import { clienteDeCaja } from '../api/caja'
import { ErrorApi } from '../api/cliente'
import {
  aSolicitudDePagoACuenta,
  calcularImporteAplicado,
  clienteDeCuentaCorriente,
  disponibilidadPrevia,
  filaPagoACuentaVacia,
  filasAPagosACuentaParaCalculo,
  medioFisicoParaPagoACuenta,
  validarPagoACuentaLocal,
  type FilaPagoACuenta,
} from '../api/cuentaCorriente'
import type { ComprobanteEmitido, EstadoDeCuentaHeader, MedioPagoListado, PuntoVentaListado } from '../api/tipos'
import { formatearImporte } from '../formato/importes'
import { CampoImporte } from './CampoImporte'

const CLAVE_PUNTO_VENTA = 'ways.cuentaCorriente.idPuntoVenta'

export function leerPuntoVentaGuardado(): number | null {
  try {
    const crudo = localStorage.getItem(CLAVE_PUNTO_VENTA)
    return crudo ? Number(crudo) : null
  } catch {
    return null
  }
}

export function guardarPuntoVentaSeleccionado(id: number) {
  try {
    localStorage.setItem(CLAVE_PUNTO_VENTA, String(id))
  } catch {
    // localStorage puede no estar disponible (modo privado del navegador) — la selección
    // simplemente no persiste entre sesiones, el resto de la pantalla sigue funcionando.
  }
}

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function formatearDisponibilidad(valor: number | null): string {
  return valor === null ? 'Ilimitado' : formatearMoneda(valor)
}

type PropsAperturaEnModal = { idPuntoVenta: number; onAbierto: () => void; onCancelar: () => void }

/**
 * Rule 10 (react-async-state): mismo recurso de recuperación que `PanelGateTurno` (Pos.tsx) /
 * `FormularioApertura` (Caja.tsx) — un `409 turno_no_abierto` durante un pago a cuenta ofrece
 * abrir el turno ahí mismo, sin perder los datos del pago que ya se cargaron en el modal. El pago
 * NUNCA se reintenta solo (regla 9): el cajero vuelve a apretar «Registrar pago» a mano.
 */
function PanelAperturaDeTurnoEnModal({ idPuntoVenta, onAbierto, onCancelar }: PropsAperturaEnModal) {
  const [fondoInicial, setFondoInicial] = useState<number | null>(null)
  const [observaciones, setObservaciones] = useState('')
  const [abriendo, setAbriendo] = useState(false)
  const abriendoRef = useRef(false)
  const [error, setError] = useState('')

  async function abrir() {
    // regla 9: guard de reentrancia de primera línea.
    if (abriendoRef.current) return

    // `fondoInicial < 0` es inalcanzable: `CampoImporte` de este campo no tiene
    // `admiteNegativos`, así que nunca puede emitir un número negativo (judgment-day ronda 2).
    if (fondoInicial === null) {
      setError('El fondo inicial es obligatorio.')
      return
    }

    abriendoRef.current = true
    setAbriendo(true)
    setError('')

    try {
      await clienteDeCaja.abrir({
        idPuntoVenta,
        fondoInicial,
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
        {error && <div className="alert alert-danger py-1 px-2 small">{error}</div>}
        <div className="row g-2 align-items-end">
          <div className="col-md-6">
            <label className="form-label" htmlFor="cc-gate-fondo-inicial">
              Fondo inicial
            </label>
            <CampoImporte
              id="cc-gate-fondo-inicial"
              className="form-control"
              valor={fondoInicial}
              disabled={abriendo}
              onChange={setFondoInicial}
            />
          </div>
          <div className="col-md-6">
            <label className="form-label" htmlFor="cc-gate-observaciones">
              Observaciones
            </label>
            <input
              id="cc-gate-observaciones"
              type="text"
              className="form-control"
              value={observaciones}
              disabled={abriendo}
              onChange={(e) => setObservaciones(e.target.value)}
            />
          </div>
        </div>
      </div>
      <div className="modal-footer">
        <button type="button" className="btn btn-outline-secondary" disabled={abriendo} onClick={onCancelar}>
          Cancelar
        </button>
        <button type="button" className="btn btn-primary" disabled={abriendo} onClick={abrir}>
          {abriendo ? 'Abriendo…' : 'Abrir turno'}
        </button>
      </div>
    </>
  )
}

export type PropsModalPago = {
  idCliente: number
  puntosVenta: PuntoVentaListado[]
  medios: MedioPagoListado[]
  header: EstadoDeCuentaHeader
  onCerrar: () => void
  onAntesDeEscribir: () => void
  onRegistrado: (comprobante: ComprobanteEmitido) => void
  /** stage-pos-adjustments: fija el punto de venta (nunca un selector) — lo usa el modal de
   * cuenta corriente del POS, donde el PV lo da el turno de la sesión, no una elección manual.
   * `undefined` (comportamiento de siempre, `CuentaCorriente.tsx`) deja el selector editable. */
  puntoVentaFijo?: number
}

/**
 * Modal de pago a cuenta (design: Web Composition). `react-async-state`: regla 9 (guard de
 * reentrancia + deshabilitado de ventana completa mientras `registrando`), regla 3 (el llamador
 * bumpea la generación del ledger ANTES de este POST, vía `onAntesDeEscribir`), regla 6 (el
 * refetch posterior vive en el padre, fuera del try/catch de esta escritura), regla 10 (recupera
 * `turno_no_abierto` con el mismo patrón que `PanelGateTurno`/`FormularioApertura`).
 *
 * stage-pos-adjustments: extraído de `CuentaCorriente.tsx` (comportamiento sin cambios ahí) para
 * poder reusarlo desde el modal de cuenta corriente de `Pos.tsx`, con el punto de venta fijo del
 * turno en vez del selector manual (`puntoVentaFijo`).
 */
export function ModalPagoACuenta({
  idCliente,
  puntosVenta,
  medios,
  header,
  onCerrar,
  onAntesDeEscribir,
  onRegistrado,
  puntoVentaFijo,
}: PropsModalPago) {
  const [idPuntoVenta, setIdPuntoVenta] = useState<number>(
    () => puntoVentaFijo ?? puntosVenta.find((p) => p.id === leerPuntoVentaGuardado())?.id ?? puntosVenta[0].id,
  )

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

  function cambiarPuntoVenta(id: number) {
    if (registrandoRef.current || puntoVentaFijo !== undefined) return
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

    const rechazo = validarPagoACuentaLocal({ pagos: pagosCalculo })
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
          <div className="modal-content">
            {gateTurno ? (
              <PanelAperturaDeTurnoEnModal idPuntoVenta={idPuntoVenta} onAbierto={() => setGateTurno(false)} onCancelar={onCerrar} />
            ) : (
              <>
                <div className="modal-header">
                  <h5 className="modal-title">Ingresar pago a cuenta</h5>
                </div>
                <div className="modal-body">
                  {error && <div className="alert alert-danger py-1 px-2 small">{error}</div>}
                  <div className="mb-3" style={{ maxWidth: 320 }}>
                    <label className="form-label" htmlFor="cc-pago-punto-venta">
                      Punto de venta
                    </label>
                    {puntoVentaFijo !== undefined ? (
                      <div id="cc-pago-punto-venta" className="form-control-plaintext py-0">
                        {puntosVenta.find((p) => p.id === puntoVentaFijo)?.nombre ?? `PV #${puntoVentaFijo}`}
                      </div>
                    ) : (
                      <select
                        id="cc-pago-punto-venta"
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
                    )}
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
                            className="form-select"
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
                          <CampoImporte
                            id={`cc-pago-importe-${fila.id}`}
                            className="form-control"
                            valor={fila.importe}
                            disabled={registrando}
                            onChange={(v) => actualizarFila(fila.id, { importe: v })}
                          />
                        </div>
                        <div className="col-md-3">
                          <label className="form-label" htmlFor={`cc-pago-referencia-${fila.id}`}>
                            Referencia{medioDeFila?.requiereReferencia ? ' (obligatoria)' : ''}
                          </label>
                          <input
                            id={`cc-pago-referencia-${fila.id}`}
                            type="text"
                            className="form-control"
                            value={fila.referencia}
                            disabled={registrando}
                            onChange={(e) => actualizarFila(fila.id, { referencia: e.target.value })}
                          />
                        </div>
                        <div className="col-md-2">
                          <label className="form-label" htmlFor={`cc-pago-vuelto-${fila.id}`}>
                            Vuelto
                          </label>
                          <CampoImporte
                            id={`cc-pago-vuelto-${fila.id}`}
                            className="form-control"
                            valor={fila.vuelto}
                            disabled={registrando || !medioDeFila?.admiteVuelto}
                            onChange={(v) => actualizarFila(fila.id, { vuelto: v })}
                          />
                        </div>
                        <div className="col-md-2">
                          <button
                            type="button"
                            className="btn btn-outline-danger btn-sm w-100"
                            disabled={registrando || filas.length === 1}
                            onClick={() => quitarFila(fila.id)}
                          >
                            Quitar
                          </button>
                        </div>
                      </div>
                    )
                  })}

                  <button type="button" className="btn btn-outline-secondary btn-sm mb-3" disabled={registrando} onClick={agregarFila}>
                    + Agregar otro medio
                  </button>

                  <div className="mb-3">
                    <label className="form-label" htmlFor="cc-pago-observaciones">
                      Observaciones
                    </label>
                    <input
                      id="cc-pago-observaciones"
                      type="text"
                      className="form-control"
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
                  <button type="button" className="btn btn-outline-secondary" disabled={registrando} onClick={onCerrar}>
                    Cancelar
                  </button>
                  <button
                    type="button"
                    className="btn btn-primary"
                    disabled={registrando || pagosCalculo.length === 0}
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
