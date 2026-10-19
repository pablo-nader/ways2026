import { describe, expect, it } from 'vitest'
import {
  armarMatriz,
  colorDeCelda,
  DIAS_DE_LA_SEMANA,
  intensidadDeCelda,
  maximoDeLaMatriz,
  rotuloDeCelda,
  totalesPorColumna,
  totalesPorFila,
} from './mapaDeCalorEscala'

type Celda = { diaDeLaSemana: number; hora: number; valor: number }

describe('armarMatriz', () => {
  it('arma 7 filas de 24 columnas, ceros donde no hay celda', () => {
    const matriz = armarMatriz<Celda>([], (c) => c.valor)

    expect(matriz).toHaveLength(7)
    expect(matriz.every((fila) => fila.length === 24 && fila.every((v) => v === 0))).toBe(true)
  })

  it('ubica cada celda por día (1 = lunes) y hora, sin depender del orden de entrada', () => {
    const matriz = armarMatriz<Celda>(
      [
        { diaDeLaSemana: 7, hora: 23, valor: 9 },
        { diaDeLaSemana: 1, hora: 0, valor: 3 },
        { diaDeLaSemana: 1, hora: 10, valor: 200 },
      ],
      (c) => c.valor,
    )

    expect(matriz[0][0]).toBe(3)
    expect(matriz[0][10]).toBe(200)
    expect(matriz[6][23]).toBe(9)
    expect(matriz[1][10]).toBe(0)
  })

  it('ignora celdas fuera de rango y valores no finitos', () => {
    const matriz = armarMatriz<Celda>(
      [
        { diaDeLaSemana: 0, hora: 5, valor: 1 },
        { diaDeLaSemana: 8, hora: 5, valor: 1 },
        { diaDeLaSemana: 2, hora: 24, valor: 1 },
        { diaDeLaSemana: 2, hora: 3, valor: Number.NaN },
      ],
      (c) => c.valor,
    )

    expect(matriz.flat().every((v) => v === 0)).toBe(true)
  })
})

describe('totales', () => {
  const matriz = armarMatriz<Celda>(
    [
      { diaDeLaSemana: 1, hora: 10, valor: 100.1 },
      { diaDeLaSemana: 1, hora: 11, valor: 50.2 },
      { diaDeLaSemana: 3, hora: 10, valor: 20 },
    ],
    (c) => c.valor,
  )

  it('suma por día de la semana', () => {
    const filas = totalesPorFila(matriz)
    expect(filas).toHaveLength(7)
    expect(filas[0]).toBe(150.3)
    expect(filas[2]).toBe(20)
    expect(filas[1]).toBe(0)
  })

  it('suma por hora', () => {
    const columnas = totalesPorColumna(matriz)
    expect(columnas).toHaveLength(24)
    expect(columnas[10]).toBe(120.1)
    expect(columnas[11]).toBe(50.2)
    expect(columnas[0]).toBe(0)
  })
})

describe('escala de color', () => {
  it('el máximo ignora ceros y negativos', () => {
    expect(maximoDeLaMatriz([[0, -5, 3], [7, 0, -1]])).toBe(7)
    expect(maximoDeLaMatriz([[0, 0], [-2, 0]])).toBe(0)
  })

  it('la intensidad es relativa al máximo y está acotada entre 0 y 1', () => {
    expect(intensidadDeCelda(50, 100)).toBe(0.5)
    expect(intensidadDeCelda(100, 100)).toBe(1)
    expect(intensidadDeCelda(150, 100)).toBe(1)
  })

  it('cero, negativos, máximo no positivo y no finitos no tienen intensidad', () => {
    expect(intensidadDeCelda(0, 100)).toBe(0)
    expect(intensidadDeCelda(-40, 100)).toBe(0)
    expect(intensidadDeCelda(10, 0)).toBe(0)
    expect(intensidadDeCelda(10, -5)).toBe(0)
    expect(intensidadDeCelda(Number.NaN, 100)).toBe(0)
    expect(intensidadDeCelda(10, Number.POSITIVE_INFINITY)).toBe(0)
  })

  it('sin intensidad la celda queda transparente, también si todo es cero', () => {
    expect(colorDeCelda(0, 0)).toBe('transparent')
    expect(colorDeCelda(-40, 100)).toBe('transparent')
  })

  it('el color más fuerte es el del máximo y crece con el valor', () => {
    expect(colorDeCelda(100, 100)).toBe('rgba(247, 148, 29, 0.600)')
    expect(colorDeCelda(50, 100)).toBe('rgba(247, 148, 29, 0.350)')
    expect(colorDeCelda(0.001, 100)).toMatch(/^rgba\(247, 148, 29, 0\.10\d\)$/)
  })
})

describe('rotuloDeCelda', () => {
  it('arma día, franja horaria y valor', () => {
    expect(rotuloDeCelda(0, 10, '$ 200,00')).toBe('Lunes 10:00–11:00: $ 200,00')
  })

  it('la última franja cierra a las 00:00', () => {
    expect(rotuloDeCelda(6, 23, '3')).toBe('Domingo 23:00–00:00: 3')
  })

  it('los días van de lunes a domingo', () => {
    expect(DIAS_DE_LA_SEMANA[0]).toBe('Lunes')
    expect(DIAS_DE_LA_SEMANA[6]).toBe('Domingo')
  })
})
