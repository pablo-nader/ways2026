/**
 * Número de venta reservado para un cobro con QR de Mercado Pago, persistido en el almacén durable del POS.
 *
 * El cobro se crea en el servidor con el número que la venta va a llevar (`numeroPreasignado`), así que el número se
 * toma del bloque local ANTES de encolar la venta. Ese número ya salió del bloque (`entregadoHasta` de la rendición lo
 * cuenta como entregado): si se perdiera, el cierre de turno quedaría esperando una venta que nunca llega. Por eso la
 * reserva vive en el almacén y tiene dos estados:
 * - `libre`: el número está tomado pero sin cobro vivo (se reservó, o el cobro se canceló o venció). La próxima venta del
 *   dispositivo lo usa antes de tomar uno nuevo — la misma venta si el cajero reintenta, o la siguiente si se fue.
 * - `en_curso`: hay un cobro creado en el servidor con ese número. Ninguna otra venta lo usa mientras tanto; al recargar,
 *   el POS ofrece retomarlo o cancelarlo.
 */
import type { AlmacenClaveValor } from './almacenPos'
import type { VentaEnCola, VentaRechazada } from './outboxOffline'

const CLAVE_RESERVA = 'reservaDeCobroMercadoPago'

export type ReservaDeCobro = {
  idPuntoVenta: number
  numero: number
  estado: 'libre' | 'en_curso'
  /** Solo `en_curso`. */
  idCobro?: number
  idMedioPago?: number
  importe?: number
  creadoEn: string
}

export async function leerReservaDeCobro(almacen: AlmacenClaveValor): Promise<ReservaDeCobro | null> {
  const reserva = await almacen.leer<ReservaDeCobro>(CLAVE_RESERVA)
  return reserva ?? null
}

/** `true` si la reserva quedó escrita y releída tal cual: una reserva que no se pudo confirmar no se da por tomada. */
export async function guardarReservaDeCobro(almacen: AlmacenClaveValor, reserva: ReservaDeCobro): Promise<boolean> {
  const escrito = await almacen.escribir(CLAVE_RESERVA, reserva)
  if (!escrito) return false
  const relectura = await leerReservaDeCobro(almacen)
  return relectura?.numero === reserva.numero && relectura.estado === reserva.estado && relectura.idCobro === reserva.idCobro
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

/** La reserva que una venta del punto de venta puede consumir sin tomar un número nuevo: solo una `libre`. */
export function reservaLibreDelPuntoDeVenta(reserva: ReservaDeCobro | null, idPuntoVenta: number): ReservaDeCobro | null {
  return reserva !== null && reserva.estado === 'libre' && reserva.idPuntoVenta === idPuntoVenta ? reserva : null
}
