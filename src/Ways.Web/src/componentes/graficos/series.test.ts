import { describe, expect, it } from 'vitest'
import { aSerieDeGrafico } from './series'

describe('aSerieDeGrafico', () => {
  it('pasa etiqueta y valor sin transformar cuando el valor no es null', () => {
    const serie = aSerieDeGrafico([
      { etiqueta: 'lun', valor: 100 },
      { etiqueta: 'mar', valor: 250.5 },
    ])

    expect(serie).toEqual([
      { etiqueta: 'lun', valor: 100 },
      { etiqueta: 'mar', valor: 250.5 },
    ])
  })

  it('mapea un valor null a 0 para el eje del gráfico', () => {
    const serie = aSerieDeGrafico([{ etiqueta: 'mie', valor: null }])

    expect(serie).toEqual([{ etiqueta: 'mie', valor: 0 }])
  })

  it('mapea un valor 0 real a 0 (no lo confunde con null)', () => {
    const serie = aSerieDeGrafico([{ etiqueta: 'jue', valor: 0 }])

    expect(serie).toEqual([{ etiqueta: 'jue', valor: 0 }])
  })

  it('devuelve un array vacío para una serie vacía', () => {
    expect(aSerieDeGrafico([])).toEqual([])
  })

  it('preserva el orden de los buckets de entrada', () => {
    const serie = aSerieDeGrafico([
      { etiqueta: 'c', valor: 3 },
      { etiqueta: 'a', valor: 1 },
      { etiqueta: 'b', valor: 2 },
    ])

    expect(serie.map((punto) => punto.etiqueta)).toEqual(['c', 'a', 'b'])
  })
})
