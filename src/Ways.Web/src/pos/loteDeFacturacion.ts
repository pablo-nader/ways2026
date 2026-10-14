/**
 * Disparador del lote de facturación del POS: pide `POST /api/fiscal/facturaciones/procesar`
 * sin esperar el resultado. Por punto de venta nunca hay dos llamadas en vuelo en toda la página
 * (el estado vive a nivel de módulo, así que un remontaje de la pantalla lo reutiliza); un pedido
 * que llega con una en curso deja marcada UNA vuelta más (por muchos pedidos que lleguen mientras
 * tanto). Sin conexión o sin punto de venta no llama, y cualquier fallo se descarta: no puede
 * afectar la venta, el drenaje ni la pantalla.
 */
import type { FiscalDeVenta } from '../api/tipos'
import { conTiempoLimite } from './tiempoLimite'

/** Una venta recién respondida necesita el lote si su facturación quedó sin resolver (`pendiente`, `sin_decidir`) o si el
 * servidor no informó estado fiscal (versión anterior o desconocido: ante la duda se pide). `aprobado`, `rechazado`,
 * `no_corresponde` y `cancelado` ya no tienen nada que procesar, y pedir el lote igual ocuparía la serie fiscal hasta 20 s
 * y haría que la venta siguiente saliera con ticket. */
export function ventaNecesitaElLote(fiscal: Pick<FiscalDeVenta, 'estado'> | null | undefined): boolean {
  if (!fiscal) return true
  return fiscal.estado === 'pendiente' || fiscal.estado === 'sin_decidir'
}

/** Peor caso del servidor (`ProcesadorDeFacturacionesPorLote`, valores por defecto de `OpcionesFiscales`): el presupuesto del
 * lote (20 s), más una firma de WSAA que arrancó justo antes de vencerlo y no se interrumpe (hasta su timeout de 30 s), más el
 * cierre de la decisión cortada (5 s) y la lectura de pendientes (5 s). */
export const PEOR_CASO_DEL_LOTE_EN_EL_SERVIDOR_MS = 20_000 + 30_000 + 5_000 + 5_000

/** Por encima del peor caso del servidor, con margen. Abortar la request corta solo la espera del cliente: el lote del servidor
 * sigue con su propio presupuesto. Un tope menor dejaría arrancar la vuelta pendiente con el lote anterior todavía vivo. */
export const TIEMPO_LIMITE_DEL_LOTE_DE_FACTURACION_MS = PEOR_CASO_DEL_LOTE_EN_EL_SERVIDOR_MS + 15_000

export type ParametrosDelDisparadorDeLote = {
  procesar: (idPuntoVenta: number, senal: AbortSignal) => Promise<unknown>
  /** Se consulta al pedir y otra vez antes de la vuelta pendiente, salvo en un pedido con `servidorAlcanzable`. */
  estaEnLinea: () => boolean
  tiempoLimiteMs?: number
}

type Pedido = {
  idPuntoVenta: number
  procesar: ParametrosDelDisparadorDeLote['procesar']
  estaEnLinea: () => boolean
  tiempoLimiteMs: number
  /** El llamador acaba de recibir una respuesta del servidor: la bandera de conexión, que se corrige más tarde, no la contradice. */
  servidorAlcanzable: boolean
}

type EstadoDelPunto = { enCurso: boolean; vueltaPendiente: Pedido | null }

const estadosPorPuntoDeVenta = new Map<number, EstadoDelPunto>()

/** Solo para tests: descarta el estado compartido entre instancias. */
export function reiniciarLotesDeFacturacionParaTests(): void {
  estadosPorPuntoDeVenta.clear()
}

function estadoDe(idPuntoVenta: number): EstadoDelPunto {
  let estado = estadosPorPuntoDeVenta.get(idPuntoVenta)
  if (!estado) {
    estado = { enCurso: false, vueltaPendiente: null }
    estadosPorPuntoDeVenta.set(idPuntoVenta, estado)
  }
  return estado
}

const hayConexion = (pedido: Pedido) => pedido.servidorAlcanzable || pedido.estaEnLinea()

async function correr(estado: EstadoDelPunto, pedido: Pedido): Promise<void> {
  estado.enCurso = true
  try {
    await conTiempoLimite((senal) => pedido.procesar(pedido.idPuntoVenta, senal), pedido.tiempoLimiteMs)
  } catch {
    // El lote es oportunista: lo que no se procesó queda pendiente para la próxima llamada.
  } finally {
    estado.enCurso = false
  }
  const siguiente = estado.vueltaPendiente
  estado.vueltaPendiente = null
  // La vuelta corre con los parámetros del último que la pidió, aunque esa instancia ya se haya desmontado.
  if (siguiente !== null && hayConexion(siguiente)) await correr(estado, siguiente)
}

/** `servidorAlcanzable` en `true` omite el chequeo de conexión de ESE pedido: lo usa quien acaba de recibir una respuesta del servidor. */
export type DisparadorDeLote = (idPuntoVenta: number | null, opciones?: { servidorAlcanzable?: boolean }) => void

export function crearDisparadorDeLote(params: ParametrosDelDisparadorDeLote): DisparadorDeLote {
  const { procesar, estaEnLinea, tiempoLimiteMs = TIEMPO_LIMITE_DEL_LOTE_DE_FACTURACION_MS } = params

  return (idPuntoVenta, opciones) => {
    if (idPuntoVenta === null) return
    const pedido: Pedido = { idPuntoVenta, procesar, estaEnLinea, tiempoLimiteMs, servidorAlcanzable: opciones?.servidorAlcanzable === true }
    if (!hayConexion(pedido)) return
    const estado = estadoDe(idPuntoVenta)
    if (estado.enCurso) {
      estado.vueltaPendiente = pedido
      return
    }
    void correr(estado, pedido)
  }
}
