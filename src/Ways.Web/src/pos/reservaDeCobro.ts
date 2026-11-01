/**
 * Número de venta apartado para un cobro con QR de Mercado Pago, persistido en el almacén durable del POS.
 *
 * El cobro se crea en el servidor con el número que la venta va a llevar (`numeroPreasignado`). Apartar el número NO lo saca del
 * bloque: es el `proximo` del bloque y la reserva solo anota "este número es del cobro X". El bloque avanza recién cuando la venta
 * se encola con ese número (`encolarVentaOffline`) o se emite online con él (`consumirReservaDeCobro`). Así un cobro cancelado o
 * vencido no deja un hueco que bloquee el cierre del turno (`entregadoHasta` = `proximo - 1`).
 *
 * Mientras la reserva existe ninguna otra venta del dispositivo corre: ninguna puede tomar ese número ni dejarlo atrás.
 */
import type { AlmacenClaveValor } from './almacenPos'
import type { BloqueDeNumeracionLocal, VentaEnCola, VentaRechazada } from './outboxOffline'

const CLAVE_RESERVA = 'reservaDeCobroMercadoPago'

export type ReservaDeCobro = {
  idPuntoVenta: number
  numero: number
  estado: 'en_curso'
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

/** `true` si el número de la reserva es el próximo del bloque vivo de su punto de venta: la única forma en que la venta puede llevarlo. */
export function reservaEsElProximoDelBloque(reserva: ReservaDeCobro, bloque: BloqueDeNumeracionLocal | null): boolean {
  return bloque !== null && bloque.idPuntoVenta === reserva.idPuntoVenta && bloque.proximo === reserva.numero && reserva.numero <= bloque.hasta
}
