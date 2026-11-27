import { describe, expect, it } from 'vitest'
import {
  dividirEnHojas,
  disposicionDeHoja,
  esEtiquetasPorHoja,
  ETIQUETAS_POR_HOJA,
  HOJA_APAISADA_MM,
  MARGEN_DE_HOJA_MM,
  SEPARACION_DE_CELDAS_MM,
} from './disposicionDeHojas'

describe('disposicionDeHoja', () => {
  it.each([
    [2, 2, 1],
    [4, 2, 2],
    [8, 4, 2],
    [16, 4, 4],
  ] as const)('%i por hoja: %i columnas × %i filas', (porHoja, columnas, filas) => {
    const d = disposicionDeHoja(porHoja)
    expect(d.columnas).toBe(columnas)
    expect(d.filas).toBe(filas)
    expect(d.columnas * d.filas).toBe(porHoja)
  })

  it('la grilla cierra exacto dentro del área útil apaisada', () => {
    for (const porHoja of ETIQUETAS_POR_HOJA) {
      const d = disposicionDeHoja(porHoja)
      const ancho = d.columnas * d.celdaAnchoMm + (d.columnas - 1) * SEPARACION_DE_CELDAS_MM
      const alto = d.filas * d.celdaAltoMm + (d.filas - 1) * SEPARACION_DE_CELDAS_MM
      // 280x193 mm: el tamaño de .gondola-hoja en etiquetas-gondola.css.
      expect(ancho).toBeCloseTo(280, 6)
      expect(alto).toBeCloseTo(193, 6)
      expect(ancho).toBeLessThan(HOJA_APAISADA_MM.ancho - 2 * MARGEN_DE_HOJA_MM)
      expect(alto).toBeLessThan(HOJA_APAISADA_MM.alto - 2 * MARGEN_DE_HOJA_MM)
    }
  })

  it('la hoja es apaisada', () => {
    expect(HOJA_APAISADA_MM.ancho).toBeGreaterThan(HOJA_APAISADA_MM.alto)
  })
})

describe('esEtiquetasPorHoja', () => {
  it('acepta solo 2, 4, 8 y 16', () => {
    expect([1, 2, 3, 4, 8, 12, 16, 24].filter(esEtiquetasPorHoja)).toEqual([2, 4, 8, 16])
  })
})

describe('dividirEnHojas', () => {
  const items = Array.from({ length: 10 }, (_, i) => i + 1)

  it('reparte en orden, cada elemento una vez, ceil(N / porHoja) hojas', () => {
    const hojas = dividirEnHojas(items, 4)
    expect(hojas).toEqual([[1, 2, 3, 4], [5, 6, 7, 8], [9, 10]])
    expect(hojas.flat()).toEqual(items)
  })

  it('una hoja exacta no deja una hoja vacía de más', () => {
    expect(dividirEnHojas([1, 2], 2)).toEqual([[1, 2]])
  })

  it('sin elementos no hay hojas', () => {
    expect(dividirEnHojas([], 8)).toEqual([])
  })

  it('el tamaño de la última hoja es el resto', () => {
    expect(dividirEnHojas(Array.from({ length: 17 }, (_, i) => i), 16).map((h) => h.length)).toEqual([16, 1])
  })
})
