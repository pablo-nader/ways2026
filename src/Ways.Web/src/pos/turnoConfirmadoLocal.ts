/**
 * Último turno abierto que el servidor confirmó para un punto de venta, persistido en
 * `localStorage` del dispositivo: con un arranque sin red, el POS de escritorio sigue vendiendo con
 * ese turno en vez de quedar bloqueado. Nunca inventa un turno: solo se guarda lo que el servidor
 * confirmó, y se borra apenas el servidor confirma que no hay turno abierto (o que se cerró desde
 * este POS). Abrir un turno sigue requiriendo al servidor.
 */
import type { TurnoResumen } from '../api/tipos'

const PREFIJO = 'ways.pos.turnoConfirmado.'

function clave(idPuntoVenta: number): string {
  return `${PREFIJO}${idPuntoVenta}`
}

function esTurnoAbierto(valor: unknown, idPuntoVenta: number): valor is TurnoResumen {
  if (typeof valor !== 'object' || valor === null) return false
  const turno = valor as Partial<TurnoResumen>
  return typeof turno.id === 'number' && turno.idPuntoVenta === idPuntoVenta && turno.estado === 'Abierto'
}

/** `null` si no hay ninguno guardado, si lo guardado no es un turno abierto de este punto de venta
 * o si el almacenamiento no está disponible. */
export function leerTurnoConfirmadoLocal(idPuntoVenta: number): TurnoResumen | null {
  try {
    const crudo = localStorage.getItem(clave(idPuntoVenta))
    if (!crudo) return null
    const guardado: unknown = JSON.parse(crudo)
    return esTurnoAbierto(guardado, idPuntoVenta) ? guardado : null
  } catch {
    return null
  }
}

/** Guarda el turno abierto confirmado o, con `null`, borra el guardado. */
export function guardarTurnoConfirmadoLocal(idPuntoVenta: number, turno: TurnoResumen | null): void {
  try {
    if (turno !== null && esTurnoAbierto(turno, idPuntoVenta)) {
      localStorage.setItem(clave(idPuntoVenta), JSON.stringify(turno))
    } else {
      localStorage.removeItem(clave(idPuntoVenta))
    }
  } catch {
    // Sin almacenamiento el turno solo no sobrevive al reinicio; el servidor sigue siendo la
    // fuente de verdad.
  }
}

/** Olvida el turno guardado si es el que se acaba de cerrar (cualquier pantalla que cierre un
 * turno lo llama: un turno cerrado nunca vuelve a habilitar la venta sin red). */
export function olvidarTurnoConfirmadoLocal(idPuntoVenta: number, idTurnoCerrado: number): void {
  if (leerTurnoConfirmadoLocal(idPuntoVenta)?.id === idTurnoCerrado) guardarTurnoConfirmadoLocal(idPuntoVenta, null)
}

/** Cuánto espera el POS de escritorio la respuesta del servidor sobre el turno antes de seguir con
 * el turno guardado (con red lenta). */
export const LIMITE_DE_ESPERA_DEL_TURNO_MS = 3_000
