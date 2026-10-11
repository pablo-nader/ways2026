import { useEffect, useRef, useState } from 'react'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import { clienteDeCompras, fechaDeHoyParaPago, prepararPagoDeCompra } from '../api/compras'
import type { MedioPagoAlta, MedioPagoListado, ResultadoDePagoDeCompra } from '../api/tipos'
import { formatearImporte } from '../formato/importes'
import { CampoImporte } from './CampoImporte'
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
 */
export function ModalDePagoDeCompra({ idCompra, etiquetaDeLaCompra, saldoPendiente, onCerrar, onPagado }: Props) {
  const [hoy] = useState(() => fechaDeHoyParaPago())
  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [errorMedios, setErrorMedios] = useState('')

  const [importe, setImporte] = useState<number | null>(saldoPendiente)
  const [fecha, setFecha] = useState(hoy)
  const [idMedioPago, setIdMedioPago] = useState<number | null>(null)
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

  async function pagar() {
    if (pagandoRef.current) return

    const preparado = prepararPagoDeCompra({ importe, fecha, idMedioPago, concepto }, saldoPendiente, hoy)
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

  const mediosCargando = medios === null
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
          <label className="form-label" htmlFor="pago-compra-importe">
            Importe
          </label>
          <CampoImporte id="pago-compra-importe" className="form-control" valor={importe} onChange={setImporte} />
        </div>

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
          <label className="form-label" htmlFor="pago-compra-medio-pago">
            Medio de pago
          </label>
          <select
            id="pago-compra-medio-pago"
            className="form-select"
            value={idMedioPago ?? ''}
            onChange={(e) => setIdMedioPago(e.target.value === '' ? null : Number(e.target.value))}
          >
            <option value="">{mediosCargando ? 'Cargando…' : 'Elegir…'}</option>
            {(medios ?? []).map((m) => (
              <option key={m.id} value={m.id}>
                {m.nombre}
              </option>
            ))}
          </select>
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
      </fieldset>
    </Modal>
  )
}
