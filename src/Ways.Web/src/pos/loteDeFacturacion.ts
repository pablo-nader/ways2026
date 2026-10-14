/**
 * Disparador del lote de facturación del POS: pide `POST /api/fiscal/facturaciones/procesar`
 * sin esperar el resultado. Nunca hay dos llamadas en vuelo; un pedido que llega con una en curso
 * deja marcada UNA vuelta más (por muchos pedidos que lleguen mientras tanto). Sin conexión o sin
 * punto de venta no llama, y cualquier fallo se descarta: no puede afectar la venta, el drenaje
 * ni la pantalla.
 */
import { conTiempoLimite } from './tiempoLimite'

/** Por encima del presupuesto de 20 s del servidor, para no cortar un lote que está por terminar. */
export const TIEMPO_LIMITE_DEL_LOTE_DE_FACTURACION_MS = 30_000

export type ParametrosDelDisparadorDeLote = {
  procesar: (idPuntoVenta: number, senal: AbortSignal) => Promise<unknown>
  /** Se consulta al pedir y otra vez antes de la vuelta pendiente: la conexión puede cambiar en el medio. */
  estaEnLinea: () => boolean
  tiempoLimiteMs?: number
}

export function crearDisparadorDeLote(params: ParametrosDelDisparadorDeLote): (idPuntoVenta: number | null) => void {
  const { procesar, estaEnLinea, tiempoLimiteMs = TIEMPO_LIMITE_DEL_LOTE_DE_FACTURACION_MS } = params
  let enCurso = false
  let vueltaPendiente: number | null = null

  async function correr(idPuntoVenta: number): Promise<void> {
    enCurso = true
    try {
      await conTiempoLimite((senal) => procesar(idPuntoVenta, senal), tiempoLimiteMs)
    } catch {
      // El lote es oportunista: lo que no se procesó queda pendiente para la próxima llamada.
    } finally {
      enCurso = false
    }
    const siguiente = vueltaPendiente
    vueltaPendiente = null
    if (siguiente !== null && estaEnLinea()) await correr(siguiente)
  }

  return (idPuntoVenta) => {
    if (idPuntoVenta === null || !estaEnLinea()) return
    if (enCurso) {
      vueltaPendiente = idPuntoVenta
      return
    }
    void correr(idPuntoVenta)
  }
}
