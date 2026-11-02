import { useEffect, useMemo, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import {
  cobradoDePagos,
  evaluarCambioDeMedios,
  filasDesdePagos,
  MENSAJES_DE_CAMBIO_DE_MEDIOS,
  mediosParaCambiar,
  mensajeDeErrorDeCambioDeMedios,
  repartoIgual,
} from '../api/cambioDeMedios'
import { MAXIMO_DE_LINEAS_DE_PAGO, MAXIMO_DE_REFERENCIA_DE_LINEA } from '../api/lineasDePago'
import { filaPagoVacia } from '../api/pagos'
import type { FilaPago } from '../api/pagos'
import { clienteDeVentas, type ModoDeCambioDeMedios } from '../api/ventas'
import type { ComprobanteEmitido, MedioPagoListado, PagoDeVenta } from '../api/tipos'
import { formatearImporte } from '../formato/importes'
import { CampoImporte } from './CampoImporte'
import { Cargando } from './Cargando'
import { Modal } from './Modal'

type Props = {
  venta: { id: number; numeroVisible: string }
  /** Catálogo completo de medios de pago: el selector descarta solo la cuenta corriente. */
  medios: readonly MedioPagoListado[]
  /** `pos` corrige con el turno abierto (`PUT /api/ventas/{id}/medios`); `administracion`, cualquier turno. */
  modo?: ModoDeCambioDeMedios
  /** Se llama una sola vez, con la venta ya corregida en el servidor; la pantalla avisa y refresca su listado. */
  onGuardado: (comprobante: ComprobanteEmitido) => void
  onCerrar: () => void
}

function textoDeDiferencia(diferencia: number): string {
  if (diferencia > 0) return `Falta ${formatearImporte(diferencia, { simbolo: true })}`
  if (diferencia < 0) return `Sobra ${formatearImporte(-diferencia, { simbolo: true })}`
  return 'Sin diferencia'
}

/**
 * "Cambiar medio de pago" de una venta ya emitida, compartido por la caja (`VentasDelTurno`) y la administración. Lee la venta
 * (`GET /api/ventas/{id}`), precarga sus cobros y deja guardar solo cuando lo cobrado coincide con lo original; la
 * pantalla lo monta con `key={venta.id}`.
 *
 * Lectura: una generación descarta la respuesta de un montaje anterior. Escritura: ref sincrónico contra el doble clic y todo el
 * diálogo inerte (`Modal ocupado` + `fieldset disabled`) hasta que el servidor responde; la respuesta no se descarta porque
 * nada puede reemplazar el diálogo mientras tanto (la pantalla queda bloqueada) y `onGuardado` solo avisa y refresca.
 */
export function ModalDeCambioDeMedios({ venta, medios, modo = 'pos', onGuardado, onCerrar }: Props) {
  const [cargando, setCargando] = useState(true)
  const [errorDeCarga, setErrorDeCarga] = useState('')
  const [bloqueo, setBloqueo] = useState('')
  const [total, setTotal] = useState(0)
  const [cobradoOriginal, setCobradoOriginal] = useState(0)
  const [pagosOriginales, setPagosOriginales] = useState<PagoDeVenta[]>([])
  const [filas, setFilas] = useState<FilaPago[]>([])
  const [guardando, setGuardando] = useState(false)
  const [errorDelServidor, setErrorDelServidor] = useState('')
  const generacionRef = useRef(0)
  const guardandoRef = useRef(false)
  const siguienteIdRef = useRef(1)
  const cancelarRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    const miGeneracion = ++generacionRef.current
    clienteDeVentas
      .obtener(venta.id)
      .then((comprobante) => {
        if (generacionRef.current !== miGeneracion) return
        if (comprobante.estado !== 'Emitido') {
          setBloqueo(MENSAJES_DE_CAMBIO_DE_MEDIOS.venta_anulada)
        } else if (comprobante.pagos.some((p) => medios.find((m) => m.id === p.idMedioPago)?.comportamiento === 'CuentaCorriente')) {
          setBloqueo(MENSAJES_DE_CAMBIO_DE_MEDIOS.cambio_de_medios_con_cuenta_corriente)
        }
        const iniciales = filasDesdePagos(comprobante.pagos)
        siguienteIdRef.current = iniciales.length + 1
        setTotal(comprobante.total)
        setCobradoOriginal(cobradoDePagos(comprobante.pagos))
        setPagosOriginales(comprobante.pagos)
        setFilas(iniciales)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setErrorDeCarga(e instanceof ErrorApi ? e.message : 'No se pudo cargar la venta.')
      })
      .finally(() => {
        if (generacionRef.current === miGeneracion) setCargando(false)
      })
    return () => {
      generacionRef.current += 1
    }
    // La venta no cambia durante el montaje (la pantalla usa `key={venta.id}`) y el catálogo solo decide el bloqueo.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [venta.id])

  const elegibles = useMemo(() => mediosParaCambiar(medios), [medios])
  const evaluacion = useMemo(
    () => evaluarCambioDeMedios({ filas, medios, totalDeLaVenta: total, cobradoOriginal }),
    [filas, medios, total, cobradoOriginal],
  )

  const listo = !cargando && errorDeCarga === '' && bloqueo === ''
  const sinCambios = listo && evaluacion.error === null && repartoIgual(evaluacion.pagos, pagosOriginales)
  const puedeGuardar = listo && evaluacion.error === null && !sinCambios && !guardando

  /** Tras un rechazo del servidor el botón enfocado quedó deshabilitado durante la escritura y el foco cayó al documento. */
  useEffect(() => {
    if (errorDelServidor && !guardando) cancelarRef.current?.focus()
  }, [errorDelServidor, guardando])

  function cambiarFila(id: number, cambios: Partial<Omit<FilaPago, 'id'>>) {
    setErrorDelServidor('')
    setFilas((prev) => prev.map((f) => (f.id === id ? { ...f, ...cambios } : f)))
  }

  function agregarFila() {
    setErrorDelServidor('')
    const id = siguienteIdRef.current++
    setFilas((prev) => [...prev, filaPagoVacia(id)])
  }

  function quitarFila(id: number) {
    setErrorDelServidor('')
    setFilas((prev) => (prev.length <= 1 ? prev : prev.filter((f) => f.id !== id)))
  }

  async function guardar() {
    if (guardandoRef.current) return
    guardandoRef.current = true
    setGuardando(true)
    setErrorDelServidor('')
    try {
      const corregida = await clienteDeVentas.cambiarMedios(venta.id, { pagos: evaluacion.pagos }, modo)
      onGuardado(corregida)
    } catch (e) {
      setErrorDelServidor(mensajeDeErrorDeCambioDeMedios(e))
    } finally {
      guardandoRef.current = false
      setGuardando(false)
    }
  }

  const pie = (
    <>
      <button ref={cancelarRef} type="button" className="btn btn-outline-secondary" disabled={guardando} onClick={onCerrar}>
        Cancelar
      </button>
      <button type="button" className="btn btn-primary" disabled={!puedeGuardar} onClick={() => void guardar()}>
        {guardando ? 'Guardando…' : 'Guardar cambio'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={`Cambiar medio de pago — ${venta.numeroVisible}`}
      tamano="lg"
      desplazable
      ocupado={guardando}
      restaurarFoco={false}
      etiquetaCerrar="Cerrar cambio de medio de pago"
      pie={pie}
      onCerrar={onCerrar}
    >
      {cargando && <Cargando />}
      {errorDeCarga && (
        <div className="alert alert-danger mb-0" role="alert">
          {errorDeCarga}
        </div>
      )}
      {bloqueo && (
        <div className="alert alert-warning mb-0" role="alert">
          {bloqueo}
        </div>
      )}

      {listo && (
        <fieldset disabled={guardando} className="border-0 p-0 m-0">
          <p className="small text-muted">
            Corregí con qué medios se cobró la venta. Lo cobrado ({formatearImporte(cobradoOriginal, { simbolo: true })}) no puede cambiar.
          </p>

          <div className="d-flex flex-column gap-2">
            {filas.map((fila, indice) => {
              const numero = indice + 1
              const medio = fila.idMedioPago === '' ? undefined : medios.find((m) => m.id === fila.idMedioPago)
              const medioFaltante = fila.idMedioPago !== '' && medio === undefined
              return (
                <div key={fila.id} className="row g-2 align-items-end" role="group" aria-label={`Línea ${numero}`}>
                  <div className="col-md-3">
                    {indice === 0 && <div className="form-label small mb-1">Medio de pago</div>}
                    <select
                      aria-label={`Medio de la línea ${numero}`}
                      className="form-select"
                      value={fila.idMedioPago}
                      onChange={(e) => cambiarFila(fila.id, { idMedioPago: e.target.value === '' ? '' : Number(e.target.value) })}
                    >
                      <option value="">Elegir…</option>
                      {medioFaltante && <option value={fila.idMedioPago}>Medio #{fila.idMedioPago} (no disponible)</option>}
                      {elegibles.map((m) => (
                        <option key={m.id} value={m.id}>
                          {m.nombre}
                        </option>
                      ))}
                    </select>
                  </div>

                  <div className="col-md-3">
                    {indice === 0 && <div className="form-label small mb-1">Importe</div>}
                    <CampoImporte
                      aria-label={`Importe de la línea ${numero}`}
                      className="form-control"
                      valor={fila.importe}
                      onChange={(importe) => cambiarFila(fila.id, { importe })}
                    />
                  </div>

                  <div className="col-md-3">
                    {indice === 0 && <div className="form-label small mb-1">Referencia{medio?.requiereReferencia ? '' : ' (opcional)'}</div>}
                    <input
                      type="text"
                      aria-label={`Referencia de la línea ${numero}`}
                      className="form-control"
                      maxLength={MAXIMO_DE_REFERENCIA_DE_LINEA}
                      value={fila.referencia}
                      onChange={(e) => cambiarFila(fila.id, { referencia: e.target.value })}
                    />
                  </div>

                  <div className="col-md-2">
                    {indice === 0 && <div className="form-label small mb-1">Vuelto</div>}
                    <CampoImporte
                      aria-label={`Vuelto de la línea ${numero}`}
                      className="form-control"
                      disabled={!medio?.admiteVuelto}
                      valor={medio?.admiteVuelto ? (evaluacion.vueltoPorFila.get(fila.id) ?? 0) : null}
                      onChange={(vuelto) => cambiarFila(fila.id, { vueltoManual: vuelto })}
                    />
                  </div>

                  <div className="col-md-1 text-end">
                    <button
                      type="button"
                      className="btn btn-outline-secondary"
                      aria-label={`Quitar línea ${numero}`}
                      title="Quitar línea"
                      disabled={filas.length <= 1}
                      onClick={() => quitarFila(fila.id)}
                    >
                      ×
                    </button>
                  </div>
                </div>
              )
            })}
          </div>

          <div className="d-flex flex-wrap justify-content-between align-items-center gap-2 mt-2">
            <button
              type="button"
              className="btn btn-sm btn-outline-primary"
              disabled={filas.length >= MAXIMO_DE_LINEAS_DE_PAGO}
              onClick={agregarFila}
            >
              Agregar medio
            </button>
            <div className="text-end" aria-live="polite">
              <span className={`fw-semibold ${evaluacion.diferencia === 0 ? '' : 'text-danger'}`}>{textoDeDiferencia(evaluacion.diferencia)}</span>
            </div>
          </div>

          {evaluacion.error && (
            <div className="alert alert-warning py-2 px-3 small mt-3 mb-0" role="status">
              {evaluacion.error}
            </div>
          )}
          {sinCambios && (
            <div className="alert alert-info py-2 px-3 small mt-3 mb-0" role="status">
              Todavía no cambiaste nada.
            </div>
          )}
          {errorDelServidor && (
            <div className="alert alert-danger py-2 px-3 small mt-3 mb-0" role="alert">
              {errorDelServidor}
            </div>
          )}
        </fieldset>
      )}
    </Modal>
  )
}
