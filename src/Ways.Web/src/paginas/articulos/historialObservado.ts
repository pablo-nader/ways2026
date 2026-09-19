import { NavigationType } from 'react-router'

/**
 * Posiciones relativas de las entradas del historial que la pantalla vio pasar, indexadas por
 * `location.key`. Alcanza con el tipo de cada navegación para ubicarlas sin depender de los
 * internos del router: PUSH queda una posición más adelante que la actual, REPLACE ocupa la misma
 * y POP vuelve a una ya conocida. Las posiciones solo valen entre sí: una entrada que la pantalla
 * nunca vio no tiene posición conocida.
 */
export interface HistorialObservado {
  readonly posiciones: ReadonlyMap<string, number>
  readonly actual: string | null
  readonly anterior: string | null
}

export const HISTORIAL_SIN_OBSERVAR: HistorialObservado = { posiciones: new Map(), actual: null, anterior: null }

export function registrarEntrada(historial: HistorialObservado, clave: string, tipo: NavigationType): HistorialObservado {
  const conocida = historial.posiciones.get(clave)
  if (conocida !== undefined) return { ...historial, actual: clave, anterior: historial.actual }

  const posicionActual = historial.actual === null ? undefined : historial.posiciones.get(historial.actual)
  if (posicionActual !== undefined && tipo !== NavigationType.Pop) {
    const posicion = tipo === NavigationType.Push ? posicionActual + 1 : posicionActual
    return { posiciones: new Map(historial.posiciones).set(clave, posicion), actual: clave, anterior: historial.actual }
  }

  // Primera entrada vista, o POP a una que nunca se vio: su posición respecto de las conocidas es
  // desconocida, así que la numeración vuelve a empezar desde ella.
  return { posiciones: new Map([[clave, 0]]), actual: clave, anterior: null }
}

/**
 * Desplazamiento para `navigate(n)` que lleva de la entrada actual de vuelta a la anterior, o `null`
 * si alguna de las dos no tiene posición conocida. Nunca devuelve 0: `navigate(0)` recarga la página.
 */
export function desplazamientoHaciaLaAnterior(historial: HistorialObservado): number | null {
  if (historial.actual === null || historial.anterior === null) return null
  const desde = historial.posiciones.get(historial.actual)
  const hacia = historial.posiciones.get(historial.anterior)
  if (desde === undefined || hacia === undefined || desde === hacia) return null
  return hacia - desde
}
