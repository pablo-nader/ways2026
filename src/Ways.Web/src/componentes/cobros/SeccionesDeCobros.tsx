import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../../api/cliente'
import { clienteDeCobros, clienteDeCuentaCorriente } from '../../api/cuentaCorriente'
import type { AplicacionDeReciboListada, ReciboDeCliente, VentaPendienteDeCobro } from '../../api/tipos'
import { fechaHoraATexto } from '../../formato/fechas'
import { formatearImporte } from '../../formato/importes'
import { EditorDeAplicacionesACobrar } from '../EditorDeAplicacionesACobrar'
import { aAplicacionesSolicitadas, validarAplicaciones, type ImportesPorVenta } from './aplicaciones'

function moneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

type PropsModalAplicar = {
  recibo: ReciboDeCliente
  /** `null` solo si todavía no llegó ninguna lista; el modal conserva la última válida mientras se recarga o falla. */
  ventas: VentaPendienteDeCobro[] | null
  cargandoVentas: boolean
  errorVentas: string
  onCerrar: () => void
  /** Se llama con el 2xx; el refresco de las secciones y del estado vive en el llamador. */
  onAplicado: () => void
  /** Un 409 deja la lista de ventas o el recibo viejos: el llamador los vuelve a pedir. */
  onDesactualizado: () => void
}

/**
 * Aplica lo disponible de un recibo a ventas del cliente. La ventana queda inerte de punta a punta mientras `aplicando`
 * (inputs, FIFO y botones), la guarda de reentrancia es el `ref` y la respuesta no navega ni puede ser reemplazada por
 * otra acción: el modal es lo único activo.
 */
function ModalAplicarCobro({ recibo, ventas: ventasRecibidas, cargandoVentas, errorVentas, onCerrar, onAplicado, onDesactualizado }: PropsModalAplicar) {
  const [ultimasVentas, setUltimasVentas] = useState<VentaPendienteDeCobro[]>(ventasRecibidas ?? [])
  if (ventasRecibidas !== null && ventasRecibidas !== ultimasVentas) setUltimasVentas(ventasRecibidas)
  const ventas = ultimasVentas
  const [importes, setImportes] = useState<ImportesPorVenta>({})
  const [aplicando, setAplicando] = useState(false)
  const aplicandoRef = useRef(false)
  const [error, setError] = useState('')

  const rechazoLocal = validarAplicaciones(ventas, importes, recibo.disponible)
  const aplicaciones = aAplicacionesSolicitadas(ventas, importes)

  async function aplicar() {
    if (aplicandoRef.current || cargandoVentas || errorVentas) return
    if (rechazoLocal) {
      setError(rechazoLocal)
      return
    }
    if (aplicaciones.length === 0) {
      setError('Cargá el importe de al menos una venta.')
      return
    }

    aplicandoRef.current = true
    setAplicando(true)
    setError('')

    try {
      await clienteDeCobros.aplicar(recibo.id, { aplicaciones })
    } catch (e) {
      aplicandoRef.current = false
      setAplicando(false)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo aplicar el cobro.')
      if (e instanceof ErrorApi && e.estado === 409) onDesactualizado()
      return
    }
    aplicandoRef.current = false
    setAplicando(false)
    onAplicado()
  }

  return (
    <>
      <div className="modal d-block" tabIndex={-1} role="dialog" aria-label={`Aplicar el recibo ${recibo.numeroVisible}`}>
        <div className="modal-dialog modal-lg" role="document">
          <div className="modal-content">
            <div className="modal-header">
              <h5 className="modal-title">Aplicar el recibo {recibo.numeroVisible}</h5>
            </div>
            <div className="modal-body">
              {error && <div className="alert alert-danger py-1 px-2 small">{error}</div>}
              {errorVentas && <div className="alert alert-danger py-1 px-2 small">{errorVentas}</div>}
              {cargandoVentas && <p className="small text-muted">Actualizando las ventas pendientes…</p>}
              <p className="small text-muted">
                Disponible para aplicar: {moneda(recibo.disponible)}. Lo que no se aplique sigue disponible en el recibo.
              </p>
              <EditorDeAplicacionesACobrar
                prefijoId="cc-aplicar"
                ventas={ventas}
                importes={importes}
                onCambiar={setImportes}
                tope={recibo.disponible}
                disabled={aplicando}
                rotuloRestante="Disponible restante"
              />
            </div>
            <div className="modal-footer">
              <button type="button" className="btn btn-outline-secondary" disabled={aplicando} onClick={onCerrar}>
                Cancelar
              </button>
              <button type="button" className="btn btn-primary" disabled={aplicando || cargandoVentas || !!errorVentas || aplicaciones.length === 0} onClick={aplicar}>
                {aplicando ? 'Aplicando…' : 'Aplicar'}
              </button>
            </div>
          </div>
        </div>
      </div>
      <div className="modal-backdrop show" />
    </>
  )
}

type PropsModalDesaplicar = {
  recibo: ReciboDeCliente
  aplicacion: AplicacionDeReciboListada
  onCerrar: () => void
  onDesaplicado: () => void
  onDesactualizado: () => void
}

/** Confirmación de la desaplicación; mismo criterio de ventana inerte y guarda de reentrancia que el modal de aplicar. */
function ModalDesaplicarCobro({ recibo, aplicacion, onCerrar, onDesaplicado, onDesactualizado }: PropsModalDesaplicar) {
  const [desaplicando, setDesaplicando] = useState(false)
  const desaplicandoRef = useRef(false)
  const [error, setError] = useState('')

  async function desaplicar() {
    if (desaplicandoRef.current) return
    desaplicandoRef.current = true
    setDesaplicando(true)
    setError('')

    try {
      await clienteDeCobros.desaplicar(recibo.id, aplicacion.id)
    } catch (e) {
      desaplicandoRef.current = false
      setDesaplicando(false)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo desaplicar el cobro.')
      if (e instanceof ErrorApi && (e.estado === 404 || e.estado === 409)) onDesactualizado()
      return
    }
    desaplicandoRef.current = false
    setDesaplicando(false)
    onDesaplicado()
  }

  return (
    <>
      <div className="modal d-block" tabIndex={-1} role="dialog" aria-label="Desaplicar cobro">
        <div className="modal-dialog" role="document">
          <div className="modal-content">
            <div className="modal-header">
              <h5 className="modal-title">Desaplicar cobro</h5>
            </div>
            <div className="modal-body">
              {error && <div className="alert alert-danger py-1 px-2 small">{error}</div>}
              <p>
                Se quita la aplicación de {moneda(aplicacion.importe)} del recibo {recibo.numeroVisible} a la venta{' '}
                {aplicacion.numeroVisibleDeVenta}. El importe vuelve a quedar disponible en el recibo y la venta vuelve a
                tener ese saldo pendiente.
              </p>
            </div>
            <div className="modal-footer">
              <button type="button" className="btn btn-outline-secondary" disabled={desaplicando} onClick={onCerrar}>
                Cancelar
              </button>
              <button type="button" className="btn btn-danger" disabled={desaplicando} onClick={desaplicar}>
                {desaplicando ? 'Desaplicando…' : 'Desaplicar'}
              </button>
            </div>
          </div>
        </div>
      </div>
      <div className="modal-backdrop show" />
    </>
  )
}

type Props = {
  idCliente: number
  /** Supervisor o Admin: ven «Aplicar» y «Desaplicar». El servidor vuelve a exigir el rol en cada request. */
  puedeSupervisar: boolean
  /** Sube cuando otra escritura de la pantalla (un pago a cuenta) pudo cambiar los recibos o las ventas. */
  versionExterna: number
  /** Se llama tras una escritura de estas secciones, para que el llamador refresque el estado de cuenta. */
  onEscritura: (mensaje: string) => void
}

/**
 * Secciones «Recibos» y «Ventas con saldo pendiente» de la cuenta corriente de un cliente. Cada lectura lleva su
 * propia generación (la última pedida gana) y su propio error; el refresco posterior a una escritura no pisa el toast
 * de éxito del llamador.
 */
export function SeccionesDeCobros({ idCliente, puedeSupervisar, versionExterna, onEscritura }: Props) {
  const [soloConDisponible, setSoloConDisponible] = useState(false)

  const [recibos, setRecibos] = useState<ReciboDeCliente[] | null>(null)
  const [errorRecibos, setErrorRecibos] = useState('')
  const generacionRecibosRef = useRef(0)

  const [ventas, setVentas] = useState<VentaPendienteDeCobro[] | null>(null)
  const [errorVentas, setErrorVentas] = useState('')
  const generacionVentasRef = useRef(0)

  /** El modal guarda el id y resuelve el recibo vigente en cada render; `respaldo` solo cubre una recarga de recibos fallida. */
  const [reciboAAplicar, setReciboAAplicar] = useState<{ id: number; respaldo: ReciboDeCliente } | null>(null)
  const [aviso, setAviso] = useState('')
  const [cargandoVentas, setCargandoVentas] = useState(true)
  const [clave, setClave] = useState({ idCliente, soloConDisponible })
  const [desaplicacion, setDesaplicacion] = useState<{ recibo: ReciboDeCliente; aplicacion: AplicacionDeReciboListada } | null>(null)

  const cargarRecibos = useCallback(() => {
    const miGeneracion = ++generacionRecibosRef.current
    setErrorRecibos('')
    clienteDeCuentaCorriente
      .listarRecibos(idCliente, soloConDisponible)
      .then((datos) => {
        if (generacionRecibosRef.current !== miGeneracion) return
        setRecibos(datos)
      })
      .catch((e) => {
        if (generacionRecibosRef.current !== miGeneracion) return
        setRecibos(null)
        setErrorRecibos(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los recibos.')
      })
  }, [idCliente, soloConDisponible])

  const cargarVentas = useCallback(() => {
    const miGeneracion = ++generacionVentasRef.current
    setErrorVentas('')
    setCargandoVentas(true)
    clienteDeCuentaCorriente
      .listarVentasPendientes(idCliente)
      .then((datos) => {
        if (generacionVentasRef.current !== miGeneracion) return
        setVentas(datos)
        setCargandoVentas(false)
      })
      .catch((e) => {
        if (generacionVentasRef.current !== miGeneracion) return
        setVentas(null)
        setCargandoVentas(false)
        setErrorVentas(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las ventas pendientes.')
      })
  }, [idCliente])

  useEffect(() => {
    cargarRecibos()
  }, [cargarRecibos, versionExterna])

  useEffect(() => {
    cargarVentas()
  }, [cargarVentas, versionExterna])

  if (clave.idCliente !== idCliente || clave.soloConDisponible !== soloConDisponible) {
    const cambioElCliente = clave.idCliente !== idCliente
    setClave({ idCliente, soloConDisponible })
    setRecibos(null)
    setErrorRecibos('')
    if (cambioElCliente) {
      setVentas(null)
      setErrorVentas('')
      setCargandoVentas(true)
      setReciboAAplicar(null)
      setDesaplicacion(null)
      setAviso('')
    }
  }

  const reciboVigente = reciboAAplicar
    ? (recibos?.find((r) => r.id === reciboAAplicar.id) ?? (recibos === null ? reciboAAplicar.respaldo : null))
    : null
  if (reciboAAplicar && recibos !== null && (reciboVigente === null || reciboVigente.disponible <= 0)) {
    setReciboAAplicar(null)
    setAviso(`El recibo ${reciboAAplicar.respaldo.numeroVisible} ya no tiene importe disponible para aplicar.`)
  }

  function recargar() {
    cargarRecibos()
    cargarVentas()
  }

  function alEscribir(mensaje: string) {
    setAviso('')
    setReciboAAplicar(null)
    setDesaplicacion(null)
    recargar()
    onEscritura(mensaje)
  }

  return (
    <>
      <section className="mt-4 d-print-none" aria-label="Recibos">
        <div className="d-flex justify-content-between align-items-center mb-2">
          <h6 className="mb-0">Recibos</h6>
          <div className="form-check mb-0">
            <input
              id="cc-recibos-solo-disponible"
              type="checkbox"
              className="form-check-input"
              checked={soloConDisponible}
              onChange={(e) => setSoloConDisponible(e.target.checked)}
            />
            <label className="form-check-label" htmlFor="cc-recibos-solo-disponible">
              Solo con disponible
            </label>
          </div>
        </div>
        {errorRecibos && <div className="alert alert-danger py-1 px-2 small">{errorRecibos}</div>}
        {aviso && (
          <div className="alert alert-warning py-1 px-2 small" role="status">
            {aviso}
          </div>
        )}
        <div className="table-responsive">
          <table className="table table-sm table-bordered align-middle">
            <thead>
              <tr>
                <th>Recibo</th>
                <th>Fecha</th>
                <th className="text-end">Neto cobrado</th>
                <th className="text-end">Aplicado</th>
                <th className="text-end">Disponible</th>
                {puedeSupervisar && <th />}
              </tr>
            </thead>
            <tbody>
              {recibos === null && !errorRecibos && (
                <tr>
                  <td colSpan={puedeSupervisar ? 6 : 5} className="text-center text-muted py-3">
                    Cargando recibos…
                  </td>
                </tr>
              )}
              {recibos?.length === 0 && (
                <tr>
                  <td colSpan={puedeSupervisar ? 6 : 5} className="text-center text-muted py-3">
                    {soloConDisponible ? 'No hay recibos con importe disponible.' : 'No hay recibos.'}
                  </td>
                </tr>
              )}
              {recibos?.map((recibo) => (
                <Fragment key={recibo.id}>
                  <tr>
                    <td>{recibo.numeroVisible}</td>
                    <td>{fechaHoraATexto(recibo.fecha)}</td>
                    <td className="text-end">{moneda(recibo.netoCobrado)}</td>
                    <td className="text-end">{moneda(recibo.aplicado)}</td>
                    <td className="text-end">{moneda(recibo.disponible)}</td>
                    {puedeSupervisar && (
                      <td className="text-end">
                        {recibo.disponible > 0 && (
                          <button
                            type="button"
                            className="btn btn-outline-primary btn-sm"
                            aria-label={`Aplicar el recibo ${recibo.numeroVisible}`}
                            disabled={ventas === null || ventas.length === 0}
                            title={
                              ventas === null
                                ? 'No se pudieron cargar las ventas pendientes.'
                                : ventas.length === 0
                                  ? 'El cliente no tiene ventas con saldo pendiente.'
                                  : undefined
                            }
                            onClick={() => {
                              setAviso('')
                              setReciboAAplicar({ id: recibo.id, respaldo: recibo })
                            }}
                          >
                            Aplicar
                          </button>
                        )}
                      </td>
                    )}
                  </tr>
                  {recibo.aplicaciones.map((aplicacion) => (
                    <tr key={aplicacion.id} className="small text-muted" data-testid={`cc-aplicacion-${aplicacion.id}`}>
                      <td colSpan={2} className="ps-4">
                        ↳ Venta {aplicacion.numeroVisibleDeVenta}{' '}
                        {aplicacion.origen === 'Historial' && <span className="badge text-bg-secondary">Historial</span>}
                      </td>
                      <td className="text-end">{fechaHoraATexto(aplicacion.fecha)}</td>
                      <td className="text-end">{moneda(aplicacion.importe)}</td>
                      <td />
                      {puedeSupervisar && (
                        <td className="text-end">
                          <button
                            type="button"
                            className="btn btn-outline-danger btn-sm"
                            aria-label={`Desaplicar ${moneda(aplicacion.importe)} de la venta ${aplicacion.numeroVisibleDeVenta}`}
                            onClick={() => setDesaplicacion({ recibo, aplicacion })}
                          >
                            Desaplicar
                          </button>
                        </td>
                      )}
                    </tr>
                  ))}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <section className="mt-4 d-print-none" aria-label="Ventas con saldo pendiente">
        <h6>Ventas con saldo pendiente</h6>
        {errorVentas && <div className="alert alert-danger py-1 px-2 small">{errorVentas}</div>}
        <div className="table-responsive">
          <table className="table table-sm table-bordered align-middle">
            <thead>
              <tr>
                <th>Venta</th>
                <th>Fecha</th>
                <th className="text-end">Parte en cuenta corriente</th>
                <th className="text-end">Cobrado</th>
                <th className="text-end">Saldo pendiente</th>
              </tr>
            </thead>
            <tbody>
              {ventas === null && !errorVentas && (
                <tr>
                  <td colSpan={5} className="text-center text-muted py-3">
                    Cargando ventas…
                  </td>
                </tr>
              )}
              {ventas?.length === 0 && (
                <tr>
                  <td colSpan={5} className="text-center text-muted py-3">
                    No hay ventas con saldo pendiente.
                  </td>
                </tr>
              )}
              {ventas?.map((venta) => (
                <tr key={venta.id}>
                  <td>{venta.numeroVisible}</td>
                  <td>{fechaHoraATexto(venta.fecha)}</td>
                  <td className="text-end">{moneda(venta.parteEnCuentaCorriente)}</td>
                  <td className="text-end">{moneda(venta.cobrado)}</td>
                  <td className="text-end">{moneda(venta.saldoPendiente)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      {reciboVigente && (
        <ModalAplicarCobro
          recibo={reciboVigente}
          ventas={ventas}
          cargandoVentas={cargandoVentas}
          errorVentas={errorVentas}
          onCerrar={() => setReciboAAplicar(null)}
          onAplicado={() => alEscribir(`Cobro aplicado: recibo ${reciboVigente.numeroVisible}.`)}
          onDesactualizado={recargar}
        />
      )}

      {desaplicacion && (
        <ModalDesaplicarCobro
          recibo={desaplicacion.recibo}
          aplicacion={desaplicacion.aplicacion}
          onCerrar={() => setDesaplicacion(null)}
          onDesaplicado={() =>
            alEscribir(`Cobro desaplicado: recibo ${desaplicacion.recibo.numeroVisible}, venta ${desaplicacion.aplicacion.numeroVisibleDeVenta}.`)
          }
          onDesactualizado={recargar}
        />
      )}
    </>
  )
}
