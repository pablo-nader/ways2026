import { useEffect, useRef, useState } from 'react'
import { Modal } from '../componentes/Modal'
import { formatearImporte } from '../formato/importes'
import { clienteDeCobrosMercadoPago, mensajeDeErrorDeCobroMercadoPago, type CobroMercadoPago } from '../api/cobrosMercadoPago'
import { CodigoQr } from './CodigoQr'
import {
  esperaHastaLaProximaConsulta,
  type EstadoSinAprobar,
  formatearCuentaRegresiva,
  mensajeDeImporteDistinto,
  segundosParaExpirar,
  TEXTO_POR_CIERRE_SIN_APROBAR,
  vistaDelCobro,
} from './cobroMercadoPago'

/** Cómo terminó el cobro: el modal se resuelve una sola vez y lo desmonta el llamador. */
export type ResultadoDelCobro =
  | { tipo: 'aprobado'; cobro: CobroMercadoPago }
  | { tipo: 'sin_aprobar'; estado: EstadoSinAprobar; cobro: CobroMercadoPago }
  | { tipo: 'importe_distinto'; cobro: CobroMercadoPago }

type PropsModalDeCobroMercadoPago = {
  cobro: CobroMercadoPago
  numeroVisible: string
  onResuelto: (resultado: ResultadoDelCobro) => void
  /** Imprime el QR en la térmica; sin esto (web sin caja de escritorio) el botón no se ofrece. */
  onImprimirQr?: (qrData: string) => void
  clienteDeCobros?: Pick<typeof clienteDeCobrosMercadoPago, 'consultar' | 'cancelar'>
  ahora?: () => number
}

const formatear = (valor: number) => formatearImporte(valor, { simbolo: true })

/**
 * Cobro con QR o con la terminal Point de Mercado Pago: muestra el QR del cobro ya creado (Point, en cambio, avisa que se cobra en la terminal), lo consulta cada 2 s (una consulta en vuelo a la vez, con más
 * espera tras un fallo) y se resuelve solo cuando el cobro termina (aprobado, cancelado, vencido) o el cajero revisa un importe
 * distinto. No se puede descartar: cerrarlo sin saber si el cliente pagó dejaría un cobro vivo sin dueño. Una respuesta que llega
 * después de resuelto o desmontado se ignora (generación).
 */
export function ModalDeCobroMercadoPago({ cobro: cobroInicial, numeroVisible, onResuelto, onImprimirQr, clienteDeCobros, ahora }: PropsModalDeCobroMercadoPago) {
  const clientes = clienteDeCobros ?? clienteDeCobrosMercadoPago
  const [cobro, setCobro] = useState(cobroInicial)
  const [cancelando, setCancelando] = useState(false)
  const [error, setError] = useState('')
  const [instante, setInstante] = useState(() => (ahora ?? Date.now)())

  const generacionRef = useRef(0)
  const resueltoRef = useRef(false)
  const cancelandoRef = useRef(false)
  const onResueltoRef = useRef(onResuelto)
  const ahoraRef = useRef(ahora ?? Date.now)
  useEffect(() => {
    onResueltoRef.current = onResuelto
    ahoraRef.current = ahora ?? Date.now
  })

  /** Aplica una lectura del servidor perteneciente a la generación `generacion`; resuelve una sola vez. */
  function aplicar(nuevo: CobroMercadoPago, generacion: number) {
    if (generacion !== generacionRef.current || resueltoRef.current) return
    setCobro(nuevo)
    const vista = vistaDelCobro(nuevo)
    if (vista.tipo === 'aprobado') {
      resueltoRef.current = true
      onResueltoRef.current({ tipo: 'aprobado', cobro: nuevo })
    } else if (vista.tipo === 'sin_aprobar') {
      resueltoRef.current = true
      onResueltoRef.current({ tipo: 'sin_aprobar', estado: vista.estado, cobro: nuevo })
    }
  }

  const idCobro = cobroInicial.idCobro
  useEffect(() => {
    const generacion = (generacionRef.current += 1)
    let temporizador: ReturnType<typeof setTimeout> | undefined
    let fallos = 0
    // Un cobro aprobado con otro importe ya no cambia: se deja de consultar y espera la revisión del cajero.
    let aRevisar = false

    function programar() {
      temporizador = setTimeout(() => void consultar(), esperaHastaLaProximaConsulta(fallos))
    }

    async function consultar() {
      if (generacion !== generacionRef.current || resueltoRef.current) return
      // Mientras se cancela manda la respuesta del cancelar: una consulta en paralelo podría pisarla con un estado viejo.
      if (cancelandoRef.current) {
        programar()
        return
      }
      try {
        const leido = await clientes.consultar(idCobro)
        if (generacion !== generacionRef.current) return
        if (!cancelandoRef.current) {
          fallos = 0
          aRevisar = vistaDelCobro(leido).tipo === 'importe_distinto'
          aplicar(leido, generacion)
        }
      } catch {
        if (generacion !== generacionRef.current) return
        fallos += 1
      }
      if (generacion === generacionRef.current && !resueltoRef.current && !aRevisar) programar()
    }

    // Un cobro que ya llegó cerrado (retomado tras una recarga) se resuelve sin esperar una consulta.
    aRevisar = vistaDelCobro(cobroInicial).tipo === 'importe_distinto'
    aplicar(cobroInicial, generacion)
    if (!resueltoRef.current && !aRevisar) programar()
    return () => {
      generacionRef.current += 1
      if (temporizador !== undefined) clearTimeout(temporizador)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [idCobro])

  useEffect(() => {
    const reloj = setInterval(() => setInstante(ahoraRef.current()), 1_000)
    return () => clearInterval(reloj)
  }, [])

  async function cancelar() {
    if (cancelandoRef.current || resueltoRef.current) return
    cancelandoRef.current = true
    setCancelando(true)
    setError('')
    const generacion = generacionRef.current
    try {
      const respuesta = await clientes.cancelar(idCobro)
      aplicar(respuesta, generacion)
    } catch (e) {
      if (generacion === generacionRef.current) setError(mensajeDeErrorDeCobroMercadoPago(e))
    } finally {
      cancelandoRef.current = false
      if (generacion === generacionRef.current) setCancelando(false)
    }
  }

  /** El contenido del QR de Mercado Pago puede no entrar en el QR nativo de la térmica: se avisa en el modal y el QR en pantalla sigue valiendo. */
  function imprimirQr(qrData: string) {
    try {
      onImprimirQr?.(qrData)
      setError('')
    } catch {
      setError('No se pudo imprimir el QR. El cliente puede pagar desde la pantalla.')
    }
  }

  function revisado() {
    if (resueltoRef.current) return
    resueltoRef.current = true
    onResueltoRef.current({ tipo: 'importe_distinto', cobro })
  }

  const vista = vistaDelCobro(cobro)
  const segundos = segundosParaExpirar(cobro.expiraEn, instante)

  return (
    <Modal titulo="Cobro con Mercado Pago" noDescartable rol="alertdialog" tamano="sm" restaurarFoco={false}>
      <div className="text-center" aria-live="polite">
        <p className="mb-1 text-body-secondary">Venta {numeroVisible}</p>
        <p className="fs-3 fw-bold mb-3">{formatear(cobro.importe)}</p>

        {vista.tipo === 'qr' && (
          <>
            <div className="d-flex justify-content-center mb-2">
              <CodigoQr valor={vista.qrData} />
            </div>
            <p className="mb-1">Pedile al cliente que escanee el QR con la app de Mercado Pago o de su banco.</p>
            {segundos !== null && <p className="text-body-secondary mb-0">Vence en {formatearCuentaRegresiva(segundos)}</p>}
          </>
        )}
        {vista.tipo === 'cobrando_en_terminal' && (
          <>
            <p className="mb-1">Cobrando en la terminal{vista.idTerminal !== null ? ` ${vista.idTerminal}` : ''}…</p>
            <p className="mb-1">Pedile al cliente que pague en la terminal.</p>
            {segundos !== null && <p className="text-body-secondary mb-0">Vence en {formatearCuentaRegresiva(segundos)}</p>}
            {vista.enTerminal && <p className="text-body-secondary mb-0">Para cancelarlo, hacelo en la terminal.</p>}
          </>
        )}
        {vista.tipo === 'revisar_terminal' && (
          <>
            <p className="mb-1">Revisá el resultado en la terminal.</p>
            <p className="text-body-secondary mb-0">Para cancelarlo, hacelo en la terminal.</p>
          </>
        )}
        {vista.tipo === 'verificando' && <p className="mb-0">Verificando el pago…</p>}
        {vista.tipo === 'informado_sin_datos' && (
          <p className="mb-0">Mercado Pago informa el pago, falta confirmarlo. Esperá unos segundos: no se puede cancelar.</p>
        )}
        {vista.tipo === 'importe_distinto' && <p className="text-danger mb-0">{mensajeDeImporteDistinto(vista.importe, vista.importePagado, formatear)}</p>}
        {vista.tipo === 'sin_aprobar' && <p className="mb-0">{TEXTO_POR_CIERRE_SIN_APROBAR[vista.estado]}</p>}
        {vista.tipo === 'esperando' && <p className="mb-0">Preparando el cobro…</p>}

        {error !== '' && (
          <p className="text-danger mt-2 mb-0" role="alert">
            {error}
          </p>
        )}

        <div className="d-flex gap-2 justify-content-center mt-3">
          {vista.tipo === 'importe_distinto' ? (
            <button type="button" className="btn btn-primary" autoFocus onClick={revisado}>
              Entendido
            </button>
          ) : (
            <>
              {vista.tipo === 'qr' && onImprimirQr && (
                <button type="button" className="btn btn-outline-secondary" onClick={() => imprimirQr(vista.qrData)}>
                  Imprimir QR
                </button>
              )}
              <button
                type="button"
                className="btn btn-outline-danger"
                disabled={
                  cancelando ||
                  vista.tipo === 'informado_sin_datos' ||
                  vista.tipo === 'revisar_terminal' ||
                  (vista.tipo === 'cobrando_en_terminal' && vista.enTerminal)
                }
                onClick={() => void cancelar()}
              >
                {cancelando ? 'Cancelando…' : 'Cancelar cobro'}
              </button>
            </>
          )}
        </div>
      </div>
    </Modal>
  )
}
