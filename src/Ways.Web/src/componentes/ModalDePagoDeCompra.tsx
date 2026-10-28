import { useEffect, useRef, useState } from 'react'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import { clienteDeCompras, fechaDeHoyParaPago, prepararPagoDeCompra } from '../api/compras'
import { lineaDePagoVacia, totalDeLineasDePago, type LineaDePagoEditable } from '../api/lineasDePago'
import type { MedioPagoAlta, MedioPagoListado, PagoAplicable, ResultadoDePagoDeCompra } from '../api/tipos'
import { formatearImporte, redondearImporte } from '../formato/importes'
import { CampoImporte } from './CampoImporte'
import { EditorDeLineasDePago } from './EditorDeLineasDePago'
import { Modal } from './Modal'
import { CampoFecha } from './CampoFecha'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

type Props = {
  idCompra: number
  /** Cómo se nombra la compra en pantalla: su número externo o `#id`. */
  etiquetaDeLaCompra: string
  saldoPendiente: number
  onCerrar: () => void
  /** Se llama una sola vez con el pago ya registrado; quien abrió el modal lo cierra y refresca. */
  onPagado: (resultado: ResultadoDePagoDeCompra) => void
}

/**
 * Pago de una compra confirmada, compartido por el detalle de la compra y la cuenta corriente del
 * proveedor. El modal es dueño de su escritura: los dos llamadores solo lo abren y, con el pago ya
 * registrado, lo cierran y refrescan su pantalla.
 *
 * `react-async-state`: guard de reentrancia de primera línea (ref, cubre el doble click del mismo
 * tick) más ventana completa deshabilitada y cierre bloqueado mientras `pagando`; el medio de pago se
 * carga con un flag de vigencia y el botón queda inerte hasta que llegan los medios; un pago que
 * resuelve con el modal ya desmontado no llama a `onPagado` dos veces ni toca estado. Un pago nunca se
 * reintenta solo: ante un error (incluido el resultado incierto) el operador decide.
 *
 * Se puede pagar con crédito que ya existe (pagos a cuenta tildados, cada uno con el importe a aplicar) y con dinero
 * nuevo (las líneas por medio), que puede superar el saldo: el excedente queda a favor en el pago nuevo. Si hay pagos
 * tildados y ninguna línea tiene medio elegido, no se paga dinero nuevo. La lista de pagos aplicables es una lectura
 * con flag de vigencia; si falla, el modal sigue sirviendo para pagar solo con líneas.
 */
export function ModalDePagoDeCompra({ idCompra, etiquetaDeLaCompra, saldoPendiente, onCerrar, onPagado }: Props) {
  const [hoy] = useState(() => fechaDeHoyParaPago())
  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [errorMedios, setErrorMedios] = useState('')

  const [aplicables, setAplicables] = useState<PagoAplicable[] | null>(null)
  const [errorAplicables, setErrorAplicables] = useState('')
  // idPago -> importe a aplicar; la presencia de la clave es el tilde.
  const [seleccion, setSeleccion] = useState<Record<number, number | null>>({})

  const [lineas, setLineas] = useState<LineaDePagoEditable[]>(() => [lineaDePagoVacia(1, saldoPendiente)])
  const [fecha, setFecha] = useState(hoy)
  const [concepto, setConcepto] = useState('')

  const [pagando, setPagando] = useState(false)
  const pagandoRef = useRef(false)
  const [error, setError] = useState('')
  const vigenteRef = useRef(true)

  useEffect(() => {
    vigenteRef.current = true
    return () => {
      vigenteRef.current = false
    }
  }, [])

  useEffect(() => {
    let vigente = true

    clienteMediosPago
      .listar(false)
      .then((lista) => {
        if (!vigente) return
        // La cuenta corriente es el crédito del cliente en una venta, nunca una salida real de dinero.
        setMedios(lista.filter((m) => m.comportamiento !== 'CuentaCorriente'))
      })
      .catch((e) => {
        if (!vigente) return
        setMedios([])
        setErrorMedios(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los medios de pago.')
      })

    return () => {
      vigente = false
    }
  }, [])

  useEffect(() => {
    let vigente = true

    clienteDeCompras
      .obtenerPagosAplicables(idCompra)
      .then((respuesta) => {
        if (vigente) setAplicables(respuesta.pagos)
      })
      .catch((e) => {
        if (!vigente) return
        setAplicables([])
        setErrorAplicables(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los pagos a cuenta.')
      })

    return () => {
      vigente = false
    }
  }, [idCompra])

  const totalAplicado = redondearImporte(Object.values(seleccion).reduce<number>((suma, importe) => suma + (importe ?? 0), 0))
  const restante = Math.max(0, redondearImporte(saldoPendiente - totalAplicado))
  const hayDineroNuevo = lineas.some((l) => l.idMedioPago !== '')
  const excedente = hayDineroNuevo ? redondearImporte(totalDeLineasDePago(lineas) - restante) : 0

  function alternarPago(pago: PagoAplicable, marcado: boolean) {
    setSeleccion((prev) => {
      const resto = { ...prev }
      delete resto[pago.idPago]
      if (!marcado) return resto
      const yaAplicado = Object.values(resto).reduce<number>((suma, importe) => suma + (importe ?? 0), 0)
      const libre = Math.max(0, redondearImporte(saldoPendiente - yaAplicado))
      return { ...resto, [pago.idPago]: Math.min(pago.disponible, libre) }
    })
  }

  async function pagar() {
    if (pagandoRef.current) return

    const aplicaciones = Object.entries(seleccion).map(([idPago, importe]) => ({ idPago: Number(idPago), importe: importe ?? 0 }))
    const excedido = (aplicables ?? []).find((p) => (seleccion[p.idPago] ?? 0) > p.disponible)
    if (excedido) {
      setError(`El pago #${excedido.idPago} tiene disponible ${formatearImporte(excedido.disponible, { simbolo: true })}.`)
      return
    }

    const preparado = prepararPagoDeCompra({ lineas, fecha, concepto, aplicaciones }, saldoPendiente, hoy)
    if ('error' in preparado) {
      setError(preparado.error)
      return
    }

    pagandoRef.current = true
    setPagando(true)
    setError('')

    try {
      const resultado = await clienteDeCompras.pagar(idCompra, preparado.solicitud)
      if (vigenteRef.current) onPagado(resultado)
    } catch (e) {
      pagandoRef.current = false
      if (!vigenteRef.current) return
      setPagando(false)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo registrar el pago.')
    }
  }

  const mediosCargando = medios === null || aplicables === null
  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCerrar} disabled={pagando}>
        Cancelar
      </button>
      <button type="button" className="btn btn-primary" onClick={() => void pagar()} disabled={pagando || mediosCargando}>
        {pagando ? 'Registrando…' : 'Registrar pago'}
      </button>
    </>
  )

  return (
    <Modal titulo={`Pagar compra ${etiquetaDeLaCompra}`} ocupado={pagando} pie={pie} onCerrar={onCerrar}>
      {(error || errorMedios) && <div className="alert alert-danger py-2 px-3 small">{error || errorMedios}</div>}

      <div className="small text-muted mb-3">
        Saldo pendiente de la compra: {formatearImporte(saldoPendiente, { simbolo: true })}
      </div>

      <fieldset disabled={pagando} className="row g-2 border-0 p-0 m-0">
        <div className="col-md-6">
          <label className="form-label" htmlFor="pago-compra-fecha">
            Fecha
          </label>
          <CampoFecha
            id="pago-compra-fecha"
            className="form-control"
            value={fecha}
            max={hoy}
            onChange={(valor) => setFecha(valor)}
          />
        </div>

        <div className="col-md-6">
          <label className="form-label" htmlFor="pago-compra-concepto">
            Concepto (opcional)
          </label>
          <input
            id="pago-compra-concepto"
            type="text"
            className="form-control"
            value={concepto}
            onChange={(e) => setConcepto(e.target.value)}
          />
          <div className="form-text">Si queda vacío se registra como «Pago &lt;tipo&gt; &lt;número&gt;».</div>
        </div>

        {errorAplicables && <div className="col-12 small text-warning">{errorAplicables}</div>}

        {aplicables !== null && aplicables.length > 0 && (
          <div className="col-12">
            <div className="fw-semibold mb-1">Usar pagos a cuenta</div>
            <div className="d-flex flex-column gap-1">
              {aplicables.map((pago) => {
                const marcado = pago.idPago in seleccion
                return (
                  <div key={pago.idPago} className="row g-2 align-items-center">
                    <div className="col-md-8">
                      <div className="form-check">
                        <input
                          id={`pago-a-cuenta-${pago.idPago}`}
                          type="checkbox"
                          className="form-check-input"
                          checked={marcado}
                          onChange={(e) => alternarPago(pago, e.target.checked)}
                        />
                        <label className="form-check-label" htmlFor={`pago-a-cuenta-${pago.idPago}`}>
                          {`Usar el pago #${pago.idPago}`}
                          <span className="text-muted small">
                            {` · ${pago.concepto} · disponible ${formatearImporte(pago.disponible, { simbolo: true })}`}
                          </span>
                        </label>
                      </div>
                    </div>
                    <div className="col-md-4">
                      <CampoImporte
                        aria-label={`Importe a aplicar del pago ${pago.idPago}`}
                        className="form-control"
                        valor={marcado ? seleccion[pago.idPago] : null}
                        disabled={!marcado}
                        onChange={(importe) => setSeleccion((prev) => ({ ...prev, [pago.idPago]: importe }))}
                      />
                    </div>
                  </div>
                )
              })}
            </div>
            {totalAplicado > 0 && (
              <div className="form-text">
                Se aplican {formatearImporte(totalAplicado, { simbolo: true })} de pagos a cuenta. Si no elegís medio en las
                líneas de abajo, no se paga dinero nuevo.
              </div>
            )}
          </div>
        )}

        <div className="col-12">
          <EditorDeLineasDePago
            lineas={lineas}
            medios={medios ?? []}
            onCambio={setLineas}
            objetivo={excedente > 0 ? null : restante}
          />
          {excedente > 0 && (
            <div className="small text-info mt-1">Quedan {formatearImporte(excedente, { simbolo: true })} a cuenta</div>
          )}
        </div>
      </fieldset>
    </Modal>
  )
}
