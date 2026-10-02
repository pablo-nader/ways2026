/**
 * Tope de espera para las requests de fondo del POS de escritorio (envío de una venta encolada,
 * reserva de numeración, rendición de la cola). Con una red lenta o colgada, `fetch` puede no
 * resolver nunca; sin un tope, el drenado y la reserva quedarían trabados indefinidamente.
 */

export const TIEMPO_LIMITE_DE_RED_MS = 5_000

/** El tope venció antes de que la request resolviera. NO significa que el servidor no la haya
 * recibido: una venta enviada puede haberse registrado igual, así que quien la reciba tiene que
 * tratarla como resultado incierto (reintentar con el mismo número y contenido), nunca como
 * rechazo. */
export class ErrorDeTiempoAgotado extends Error {
  constructor(ms: number) {
    super(`El servidor no respondió en ${ms / 1000} s.`)
    this.name = 'ErrorDeTiempoAgotado'
  }
}

/** Corre `operacion` con una señal que se aborta al vencer `ms`, y rechaza con
 * `ErrorDeTiempoAgotado` en ese momento aunque la operación no respete la señal. */
export function conTiempoLimite<T>(operacion: (senal: AbortSignal) => Promise<T>, ms: number = TIEMPO_LIMITE_DE_RED_MS): Promise<T> {
  const controlador = new AbortController()
  return new Promise<T>((resolver, rechazar) => {
    const idTope = setTimeout(() => {
      controlador.abort()
      rechazar(new ErrorDeTiempoAgotado(ms))
    }, ms)
    operacion(controlador.signal).then(
      (valor) => {
        clearTimeout(idTope)
        resolver(valor)
      },
      (error: unknown) => {
        clearTimeout(idTope)
        rechazar(error)
      },
    )
  })
}
