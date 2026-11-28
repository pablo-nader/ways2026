import { describe, expect, it } from 'vitest'
import {
  ajustarNombre,
  ANCHO_DE_CARACTER_DEL_NOMBRE,
  anchoDelPrecioEnEm,
  ESCALA_DE_ANCHO_DEL_PRECIO,
  FUENTE_MINIMA_MM,
  fuenteParaUnaLinea,
  INTERLINEADO_DEL_NOMBRE,
  MAXIMO_DE_LINEAS_DEL_NOMBRE,
  partirEnLineas,
} from './ajusteDeTexto'

describe('partirEnLineas', () => {
  it('corta por palabras sin pasarse del ancho', () => {
    expect(partirEnLineas('Aceite de girasol Cocinero 900 ml', 14)).toEqual(['Aceite de', 'girasol', 'Cocinero 900', 'ml'])
  })

  it('una palabra más larga que la línea se parte por caracteres', () => {
    expect(partirEnLineas('Paraclorobenzoico', 6)).toEqual(['Paracl', 'oroben', 'zoico'])
  })

  it('un nombre vacío no genera líneas', () => {
    expect(partirEnLineas('   ', 10)).toEqual([])
  })
})

describe('ajustarNombre', () => {
  const caja = { anchoMm: 60, altoMm: 12 }
  const factor = ANCHO_DE_CARACTER_DEL_NOMBRE.normal

  it('un nombre corto usa la fuente más grande', () => {
    expect(ajustarNombre('Sal', caja, factor, 20).fuenteMm).toBeGreaterThan(ajustarNombre('Aceite de girasol alto oleico Cocinero 900 ml', caja, factor, 20).fuenteMm)
  })

  it('nunca pasa del tope pedido ni del alto de una línea', () => {
    expect(ajustarNombre('Sal', caja, factor, 4).fuenteMm).toBeLessThanOrEqual(4)
    expect(ajustarNombre('Sal', caja, factor, 50).fuenteMm).toBeLessThanOrEqual(caja.altoMm / INTERLINEADO_DEL_NOMBRE)
  })

  it('la fuente nunca crece al alargar el nombre', () => {
    let anterior = Number.POSITIVE_INFINITY
    for (let repeticiones = 1; repeticiones <= 8; repeticiones++) {
      const { fuenteMm } = ajustarNombre('Galletitas surtidas '.repeat(repeticiones).trim(), caja, factor, 10)
      expect(fuenteMm).toBeLessThanOrEqual(anterior)
      anterior = fuenteMm
    }
  })

  it('el resultado entra en dos líneas dentro de la caja', () => {
    const { fuenteMm, lineas } = ajustarNombre('Fideos tallarines al huevo Matarazzo paquete familiar 500 g', caja, factor, 10)
    expect(lineas.length).toBeLessThanOrEqual(MAXIMO_DE_LINEAS_DEL_NOMBRE)
    expect(lineas.length * fuenteMm * INTERLINEADO_DEL_NOMBRE).toBeLessThanOrEqual(caja.altoMm)
    for (const linea of lineas) expect(linea.length * fuenteMm * factor).toBeLessThanOrEqual(caja.anchoMm)
  })

  it('un nombre imposible cae al mínimo en vez de romper', () => {
    expect(ajustarNombre('x'.repeat(400), caja, factor, 10).fuenteMm).toBe(FUENTE_MINIMA_MM)
  })
})

describe('anchoDelPrecioEnEm', () => {
  it('los separadores y el espacio son más angostos que los dígitos', () => {
    expect(anchoDelPrecioEnEm('$ 1.234,50')).toBeCloseTo(7 * 0.6 + 0.26 + 0.26 + 0.21, 6)
  })

  it('un texto vacío no mide nada', () => {
    expect(anchoDelPrecioEnEm('')).toBe(0)
  })
})

describe('fuenteParaUnaLinea', () => {
  const caja = { anchoMm: 90, altoMm: 27 }

  it('un texto corto usa el alto de la caja', () => {
    expect(fuenteParaUnaLinea(anchoDelPrecioEnEm('$ 9'), caja)).toBe(27)
  })

  it('un texto largo se achica para no pasarse del ancho', () => {
    const ancho = anchoDelPrecioEnEm('$ 1.234.567,89')
    const fuente = fuenteParaUnaLinea(ancho, caja)
    expect(fuente).toBeLessThan(27)
    expect(ancho * fuente).toBeLessThanOrEqual(caja.anchoMm)
  })

  it('el condensado entra más grande que el normal', () => {
    const ancho = anchoDelPrecioEnEm('$ 12.345,67')
    expect(fuenteParaUnaLinea(ancho * ESCALA_DE_ANCHO_DEL_PRECIO.condensado, caja)).toBeGreaterThan(fuenteParaUnaLinea(ancho, caja))
  })

  it('el interlineado reduce la fuente que entra a lo alto', () => {
    expect(fuenteParaUnaLinea(1, caja, 1.25)).toBeCloseTo(27 / 1.25, 1)
  })
})
