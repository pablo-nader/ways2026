/**
 * Intervalo del ciclo de sincronización del POS de escritorio, configurable por el cajero y
 * persistido por dispositivo en `localStorage` (mismo criterio que `almacenDePuntoVenta.ts`: un
 * escalar chico, sin necesidad de IndexedDB). Un valor ausente, corrupto o inaccesible nunca rompe
 * la pantalla: cae al valor por defecto.
 */

export const CLAVE_INTERVALO_DE_SINCRONIZACION = 'ways.pos.intervaloDeSincronizacionMinutos'

export const INTERVALO_POR_DEFECTO_MINUTOS = 5
export const INTERVALO_MINIMO_MINUTOS = 1
export const INTERVALO_MAXIMO_MINUTOS = 60

/** Minutos enteros dentro de [mínimo, máximo] — redondea y recorta. `null` cuando el valor no es
 * un número (texto vacío, letras, `NaN`, `Infinity`, cualquier otro tipo). */
export function normalizarIntervaloEnMinutos(valor: unknown): number | null {
  const numero = typeof valor === 'number' ? valor : typeof valor === 'string' && valor.trim() !== '' ? Number(valor) : Number.NaN
  if (!Number.isFinite(numero)) return null
  return Math.min(INTERVALO_MAXIMO_MINUTOS, Math.max(INTERVALO_MINIMO_MINUTOS, Math.round(numero)))
}

export function leerIntervaloDeSincronizacion(): number {
  try {
    return normalizarIntervaloEnMinutos(localStorage.getItem(CLAVE_INTERVALO_DE_SINCRONIZACION)) ?? INTERVALO_POR_DEFECTO_MINUTOS
  } catch {
    return INTERVALO_POR_DEFECTO_MINUTOS
  }
}

/** Persiste el valor normalizado y lo devuelve — el llamador muestra lo que quedó vigente, nunca
 * lo que tipeó. */
export function guardarIntervaloDeSincronizacion(minutosPedidos: number): number {
  const minutos = normalizarIntervaloEnMinutos(minutosPedidos) ?? INTERVALO_POR_DEFECTO_MINUTOS
  try {
    localStorage.setItem(CLAVE_INTERVALO_DE_SINCRONIZACION, String(minutos))
  } catch {
    // Sin almacenamiento el valor rige solo hasta el próximo reinicio.
  }
  return minutos
}

export function minutosAMilisegundos(minutos: number): number {
  return minutos * 60_000
}
