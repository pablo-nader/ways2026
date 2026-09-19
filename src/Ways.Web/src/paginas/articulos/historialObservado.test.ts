import { NavigationType } from 'react-router'
import { describe, expect, it } from 'vitest'
import { desplazamientoHaciaLaAnterior, HISTORIAL_SIN_OBSERVAR, registrarEntrada, type HistorialObservado } from './historialObservado'

function recorrer(...pasos: [string, NavigationType][]): HistorialObservado {
  return pasos.reduce((historial, [clave, tipo]) => registrarEntrada(historial, clave, tipo), HISTORIAL_SIN_OBSERVAR)
}

describe('historialObservado', () => {
  it('la primera entrada vista no tiene a dónde volver', () => {
    expect(desplazamientoHaciaLaAnterior(recorrer(['a', NavigationType.Pop]))).toBeNull()
  })

  it('un PUSH queda una posición adelante: volver desde él es retroceder uno', () => {
    expect(desplazamientoHaciaLaAnterior(recorrer(['a', NavigationType.Pop], ['b', NavigationType.Push]))).toBe(-1)
  })

  it('un POP hacia atrás a una entrada conocida se deshace avanzando lo mismo que retrocedió', () => {
    const historial = recorrer(['a', NavigationType.Pop], ['b', NavigationType.Push], ['c', NavigationType.Push], ['a', NavigationType.Pop])

    expect(desplazamientoHaciaLaAnterior(historial)).toBe(2)
  })

  it('un POP hacia adelante a una entrada conocida se deshace retrocediendo', () => {
    const historial = recorrer(
      ['a', NavigationType.Pop],
      ['b', NavigationType.Push],
      ['a', NavigationType.Pop],
      ['b', NavigationType.Pop],
    )

    expect(desplazamientoHaciaLaAnterior(historial)).toBe(-1)
  })

  it('un REPLACE ocupa la posición de la entrada que reemplaza', () => {
    const historial = recorrer(
      ['a', NavigationType.Pop],
      ['b', NavigationType.Push],
      ['c', NavigationType.Replace],
      ['a', NavigationType.Pop],
    )

    expect(desplazamientoHaciaLaAnterior(historial)).toBe(1)
  })

  it('un POP a una entrada nunca vista no tiene desplazamiento conocido', () => {
    const historial = recorrer(['a', NavigationType.Pop], ['b', NavigationType.Push], ['x', NavigationType.Pop])

    expect(desplazamientoHaciaLaAnterior(historial)).toBeNull()
  })

  it('tras un POP a una entrada nunca vista, las posiciones previas se olvidan en vez de mezclarse con las nuevas', () => {
    const historial = recorrer(
      ['a', NavigationType.Pop],
      ['b', NavigationType.Push],
      ['x', NavigationType.Pop],
      ['a', NavigationType.Pop],
    )

    expect(desplazamientoHaciaLaAnterior(historial)).toBeNull()
  })

  it('la numeración que vuelve a empezar desde una entrada nunca vista sigue ubicando los PUSH siguientes', () => {
    const historial = recorrer(
      ['a', NavigationType.Pop],
      ['x', NavigationType.Pop],
      ['y', NavigationType.Push],
      ['x', NavigationType.Pop],
    )

    expect(desplazamientoHaciaLaAnterior(historial)).toBe(1)
  })

  it('nunca devuelve 0 (navigate(0) recarga la página), aunque dos claves compartan posición', () => {
    const historial = recorrer(['a', NavigationType.Pop], ['b', NavigationType.Replace], ['a', NavigationType.Pop])

    expect(desplazamientoHaciaLaAnterior(historial)).toBeNull()
  })
})
