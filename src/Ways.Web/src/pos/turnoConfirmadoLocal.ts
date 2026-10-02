/**
 * Último turno abierto que el servidor confirmó para un punto de venta, persistido en
 * `localStorage` del dispositivo: con un arranque sin red, el POS de escritorio sigue vendiendo con
 * ese turno en vez de quedar bloqueado. Nunca inventa un turno: solo se guarda lo que el servidor
 * confirmó, y se borra apenas el servidor confirma que no hay turno abierto (o que se cerró desde
 * este dispositivo). Abrir un turno sigue requiriendo al servidor.
 *
 * Riesgo residual: un turno cerrado desde OTRA máquina no se ve sin red; por eso la confirmación
 * vence a las `VIGENCIA_DEL_TURNO_CONFIRMADO_MS`, y una venta hecha con un turno ya cerrado la
 * rechaza el servidor al sincronizar.
 */
import type { TurnoResumen } from '../api/tipos'

const PREFIJO = 'ways.pos.turnoConfirmado.'

/** Cuánto se confía, sin red, en la última confirmación del servidor. */
export const VIGENCIA_DEL_TURNO_CONFIRMADO_MS = 24 * 60 * 60_000

/** Cuánto espera el POS de escritorio la respuesta del servidor sobre el turno antes de seguir con
 * el turno guardado (con red lenta). */
export const LIMITE_DE_ESPERA_DEL_TURNO_MS = 3_000

type TurnoGuardado = { turno: TurnoResumen; confirmadoEn: string }

function clave(idPuntoVenta: number): string {
  return `${PREFIJO}${idPuntoVenta}`
}

function esTurnoAbierto(valor: unknown, idPuntoVenta: number): valor is TurnoResumen {
  if (typeof valor !== 'object' || valor === null) return false
  const turno = valor as Partial<TurnoResumen>
  return typeof turno.id === 'number' && turno.idPuntoVenta === idPuntoVenta && turno.estado === 'Abierto'
}

/** `null` si no hay ninguno guardado, si lo guardado no es un turno abierto de este punto de venta,
 * si la confirmación tiene más de `VIGENCIA_DEL_TURNO_CONFIRMADO_MS` o si el almacenamiento no está
 * disponible. */
export function leerTurnoConfirmadoLocal(idPuntoVenta: number, ahora: Date = new Date()): TurnoResumen | null {
  try {
    const crudo = localStorage.getItem(clave(idPuntoVenta))
    if (!crudo) return null
    const guardado = JSON.parse(crudo) as Partial<TurnoGuardado> | null
    if (!guardado || !esTurnoAbierto(guardado.turno, idPuntoVenta) || typeof guardado.confirmadoEn !== 'string') return null
    const edad = ahora.getTime() - new Date(guardado.confirmadoEn).getTime()
    return Number.isFinite(edad) && edad <= VIGENCIA_DEL_TURNO_CONFIRMADO_MS ? guardado.turno : null
  } catch {
    return null
  }
}

/** Guarda el turno abierto confirmado (con la hora de esta confirmación) o, con `null`, borra el
 * guardado. */
export function guardarTurnoConfirmadoLocal(idPuntoVenta: number, turno: TurnoResumen | null, ahora: Date = new Date()): void {
  try {
    if (turno !== null && esTurnoAbierto(turno, idPuntoVenta)) {
      const guardado: TurnoGuardado = { turno, confirmadoEn: ahora.toISOString() }
      localStorage.setItem(clave(idPuntoVenta), JSON.stringify(guardado))
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
  try {
    const crudo = localStorage.getItem(clave(idPuntoVenta))
    const guardado = crudo ? (JSON.parse(crudo) as Partial<TurnoGuardado> | null) : null
    if (guardado?.turno?.id === idTurnoCerrado) localStorage.removeItem(clave(idPuntoVenta))
  } catch {
    // Mismo criterio que al guardar.
  }
}
