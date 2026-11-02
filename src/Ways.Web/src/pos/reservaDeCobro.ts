/**
 * Número de venta apartado para un cobro con QR de Mercado Pago, persistido en el almacén durable del POS.
 *
 * El cobro se crea en el servidor con el número que la venta va a llevar (`numeroPreasignado`). Apartar el número NO lo saca del
 * bloque: es el `proximo` del bloque y la reserva solo anota "este número es del cobro X". El bloque avanza recién cuando la venta
 * se encola con ese número (`encolarVentaOffline`) o se emite online con él (`consumirReservaDeCobro`). Así un cobro cancelado o
 * vencido no deja un hueco que bloquee el cierre del turno (`entregadoHasta` = `proximo - 1`).
 *
 * Mientras la reserva existe ninguna venta local del dispositivo corre: ninguna puede tomar ese número ni dejarlo atrás.
 */
import type { AlmacenClaveValor } from './almacenPos'
import type { BloqueDeNumeracionLocal, VentaEnCola, VentaRechazada } from './outboxOffline'

const CLAVE_RESERVA = 'reservaDeCobroMercadoPago'

export type ReservaDeCobro = {
  idPuntoVenta: number
  numero: number
  estado: 'en_curso'
  /** Identifica a esta reserva frente a otras pestañas o ventanas del dispositivo: quien la suelta, la anota o la consume tiene que
   * presentar el mismo valor. Ausente solo en una reserva guardada por una versión anterior. */
  dueno?: string
  /** Ausente solo entre apartar el número y crear el cobro en el servidor. */
  idCobro?: number
  idMedioPago?: number
  importe?: number
  creadoEn: string
}

/** La reserva guardada más si su número sigue siendo el próximo del bloque vivo del mismo punto de venta. */
export type ReservaLeida = ReservaDeCobro & { fueraDeBloque: boolean }

export async function leerReservaDeCobro(almacen: AlmacenClaveValor): Promise<ReservaDeCobro | null> {
  const reserva = await almacen.leer<ReservaDeCobro>(CLAVE_RESERVA)
  // Una reserva de una versión anterior (`libre`) ya no significa nada: su número salió del bloque y no se recupera.
  return reserva !== null && reserva !== undefined && reserva.estado === 'en_curso' ? reserva : null
}

/** `true` si la reserva quedó escrita y releída tal cual: una reserva que no se pudo confirmar no se da por tomada. */
export async function guardarReservaDeCobro(almacen: AlmacenClaveValor, reserva: ReservaDeCobro): Promise<boolean> {
  const escrito = await almacen.escribir(CLAVE_RESERVA, reserva)
  if (!escrito) return false
  const relectura = await leerReservaDeCobro(almacen)
  return relectura?.numero === reserva.numero && relectura.estado === reserva.estado && relectura.idCobro === reserva.idCobro && relectura.dueno === reserva.dueno
}

export async function borrarReservaDeCobro(almacen: AlmacenClaveValor): Promise<boolean> {
  return almacen.escribir<ReservaDeCobro | null>(CLAVE_RESERVA, null)
}

/** `true` si la venta de ese número ya está en la cola o rechazada: la reserva sobrevivió a una venta que ya se encoló. */
export function reservaYaSeVendio(reserva: ReservaDeCobro, outbox: readonly VentaEnCola[], rechazadas: readonly VentaRechazada[]): boolean {
  const coincide = (v: { numeroPreasignado: number; idPuntoVenta: number }) =>
    v.numeroPreasignado === reserva.numero && v.idPuntoVenta === reserva.idPuntoVenta
  return outbox.some(coincide) || rechazadas.some(coincide)
}

/** `true` si el número de la reserva es el próximo del bloque vivo de su punto de venta: la única forma en que la venta puede llevarlo. */
export function reservaEsElProximoDelBloque(reserva: ReservaDeCobro, bloque: BloqueDeNumeracionLocal | null): boolean {
  return bloque !== null && bloque.idPuntoVenta === reserva.idPuntoVenta && bloque.proximo === reserva.numero && reserva.numero <= bloque.hasta
}

const CLAVE_SIN_VENTA = 'cobrosMercadoPagoSinVenta'

/**
 * Un cobro aprobado cuya venta no se pudo cerrar y que el cajero apartó para liberar la caja: solo lo resuelve un supervisor.
 * `sincronizadoEn` ausente significa que el servidor todavía no lo marcó `sin_venta` (se apartó sin conexión, o la llamada falló):
 * mientras haya uno así no se inician cobros con Mercado Pago ni se cierra el turno, y se reintenta solo. Una entrada guardada antes
 * de que existiera este campo tampoco lo trae, y como el servidor nunca la apartó, también está pendiente.
 */
export type CobroSinVenta = {
  idCobro: number
  numero: number
  idPuntoVenta: number
  idMedioPago?: number
  importe: number
  importePagado: number | null
  apartadoEn: string
  sincronizadoEn?: string
}

export const apartadoPendienteDeSincronizar = (cobro: CobroSinVenta): boolean => cobro.sincronizadoEn === undefined

export const contarApartadosPendientes = (lista: readonly CobroSinVenta[]): number => lista.filter(apartadoPendienteDeSincronizar).length

/** Lista durable, solo de altas: nada la borra sola. */
export async function leerCobrosSinVenta(almacen: AlmacenClaveValor): Promise<CobroSinVenta[]> {
  const guardados = await almacen.leer<CobroSinVenta[]>(CLAVE_SIN_VENTA)
  return Array.isArray(guardados) ? guardados : []
}

/** `true` si el cobro quedó en la lista y releído (idempotente por `idCobro`): la reserva solo se suelta después de esto. */
export async function agregarCobroSinVenta(almacen: AlmacenClaveValor, cobro: CobroSinVenta): Promise<boolean> {
  const actuales = await leerCobrosSinVenta(almacen)
  if (!actuales.some((c) => c.idCobro === cobro.idCobro)) {
    if (!(await almacen.escribir(CLAVE_SIN_VENTA, [...actuales, cobro]))) return false
  }
  return (await leerCobrosSinVenta(almacen)).some((c) => c.idCobro === cobro.idCobro)
}

/** `true` si el cobro quedó marcado como sincronizado y releído (idempotente); `true` también si ya no está en la lista. */
export async function marcarCobroSinVentaSincronizado(almacen: AlmacenClaveValor, idCobro: number, sincronizadoEn: string): Promise<boolean> {
  const actuales = await leerCobrosSinVenta(almacen)
  const actual = actuales.find((c) => c.idCobro === idCobro)
  if (actual === undefined) return true
  if (actual.sincronizadoEn === undefined) {
    const nueva = actuales.map((c) => (c.idCobro === idCobro ? { ...c, sincronizadoEn } : c))
    if (!(await almacen.escribir(CLAVE_SIN_VENTA, nueva))) return false
  }
  return (await leerCobrosSinVenta(almacen)).find((c) => c.idCobro === idCobro)?.sincronizadoEn !== undefined
}

const NOMBRE_DEL_CANDADO = 'ways-pos-reserva-cobro'

/**
 * Serializa entre pestañas y ventanas del mismo dispositivo todo lo que lee, decide y escribe sobre la reserva y el bloque (Web Locks).
 * El candado no es reentrante: una operación bajo él nunca pide otra. Sin `navigator.locks` corre sin candado.
 */
export function conCandadoDeReserva<T>(operacion: () => Promise<T>): Promise<T> {
  const candados = typeof navigator === 'undefined' ? undefined : navigator.locks
  if (candados === undefined || typeof candados.request !== 'function') return operacion()
  return candados.request(NOMBRE_DEL_CANDADO, operacion) as Promise<T>
}

export function nuevoDuenoDeReserva(): string {
  const cripto = typeof globalThis.crypto === 'undefined' ? undefined : globalThis.crypto
  if (cripto !== undefined && typeof cripto.randomUUID === 'function') return cripto.randomUUID()
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}-${Math.random().toString(36).slice(2)}`
}

/** `true` si `dueno` es el de la reserva guardada (ambos ausentes en una reserva anterior a los dueños). */
export function esLaMismaReserva(reserva: ReservaDeCobro, numero: number, dueno: string | undefined): boolean {
  return reserva.numero === numero && reserva.dueno === dueno
}
