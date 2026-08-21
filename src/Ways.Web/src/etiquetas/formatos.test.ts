import { describe, expect, it } from 'vitest'
import { A4_2X7, A4_3X8, CARTEL_A4, CARTEL_A5, FORMATOS, celdasPorHoja, contarHojas } from './formatos'
import type { DescriptorDeFormato } from './formatos'

// mutation target 3: cada número de geometría de cada uno de los cuatro descriptores — el mm
// emitido y el conteo por hoja declarado deben coincidir con la tupla (design.md:143-148).
describe('formatos — geometría de los cuatro descriptores (mutation target 3)', () => {
  const casos: Array<{ descriptor: DescriptorDeFormato; porHoja: number }> = [
    { descriptor: A4_3X8, porHoja: 24 },
    { descriptor: A4_2X7, porHoja: 14 },
    { descriptor: CARTEL_A4, porHoja: 1 },
    { descriptor: CARTEL_A5, porHoja: 2 },
  ]

  it.each(casos)('$descriptor.id: columnas × filas == el conteo declarado por hoja', ({ descriptor, porHoja }) => {
    expect(descriptor.columnas * descriptor.filas).toBe(porHoja)
    expect(celdasPorHoja(descriptor)).toBe(porHoja)
  })

  it('A4-3x8: celda 70.0×37.0 mm, offset 0.5/0, sin medianiles, cierra la página A4', () => {
    expect(A4_3X8.celdaMm).toEqual({ ancho: 70.0, alto: 37.0 })
    expect(A4_3X8.margenSuperiorMm).toBe(0.5)
    expect(A4_3X8.margenIzquierdoMm).toBe(0)
    expect(A4_3X8.medianilHorizontalMm).toBe(0)
    expect(A4_3X8.medianilVerticalMm).toBe(0)
    expect(A4_3X8.padExternoMm).toBe(5)
    expect(A4_3X8.columnas * A4_3X8.celdaMm.ancho + 2 * A4_3X8.margenIzquierdoMm).toBe(210)
    expect(A4_3X8.filas * A4_3X8.celdaMm.alto + 2 * A4_3X8.margenSuperiorMm).toBe(297)
  })

  it('A4-2x7: celda 99.1×38.1 mm sharpeada (Reconciliación T2), offset 15.15/4.65, gutter 2.5, cierra exacto', () => {
    expect(A4_2X7.celdaMm).toEqual({ ancho: 99.1, alto: 38.1 })
    expect(A4_2X7.margenSuperiorMm).toBeCloseTo(15.15)
    expect(A4_2X7.margenIzquierdoMm).toBeCloseTo(4.65)
    expect(A4_2X7.medianilHorizontalMm).toBe(2.5)
    expect(A4_2X7.medianilVerticalMm).toBe(0)
    expect(A4_2X7.padExternoMm).toBe(3)
    expect(A4_2X7.columnas * A4_2X7.celdaMm.ancho + 2 * A4_2X7.margenIzquierdoMm + A4_2X7.medianilHorizontalMm).toBeCloseTo(210)
    expect(A4_2X7.filas * A4_2X7.celdaMm.alto + 2 * A4_2X7.margenSuperiorMm).toBeCloseTo(297)
  })

  it('CARTEL-A4: hoja completa 190×277 mm, margen 10/10, 1×1', () => {
    expect(CARTEL_A4.celdaMm).toEqual({ ancho: 190.0, alto: 277.0 })
    expect(CARTEL_A4.margenSuperiorMm).toBe(10.0)
    expect(CARTEL_A4.margenIzquierdoMm).toBe(10.0)
    expect(CARTEL_A4.columnas).toBe(1)
    expect(CARTEL_A4.filas).toBe(1)
    expect(CARTEL_A4.celdaMm.ancho + 2 * CARTEL_A4.margenIzquierdoMm).toBe(210)
    expect(CARTEL_A4.celdaMm.alto + 2 * CARTEL_A4.margenSuperiorMm).toBe(297)
  })

  it('CARTEL-A5: media hoja 190×133.5 mm, 1×2, gutter vertical 10', () => {
    expect(CARTEL_A5.celdaMm).toEqual({ ancho: 190.0, alto: 133.5 })
    expect(CARTEL_A5.columnas).toBe(1)
    expect(CARTEL_A5.filas).toBe(2)
    expect(CARTEL_A5.medianilVerticalMm).toBe(10.0)
    expect(CARTEL_A5.filas * CARTEL_A5.celdaMm.alto + 2 * CARTEL_A5.margenSuperiorMm + CARTEL_A5.medianilVerticalMm).toBe(297)
  })

  it('FORMATOS contiene exactamente los cuatro descriptores, ningún literal-id atajo', () => {
    expect(FORMATOS).toHaveLength(4)
    expect(FORMATOS.map((d) => d.id)).toEqual(['A4-3x8', 'A4-2x7', 'CARTEL-A4', 'CARTEL-A5'])
  })
})

// mutation targets 4/5: celdasPorHoja/contarHojas SIEMPRE derivados — nunca un campo
// almacenado — y contarHojas redondea hacia ARRIBA (ceil), nunca floor/división entera.
describe('formatos — celdasPorHoja / contarHojas derivados (mutation targets 4, 5)', () => {
  it('celdasPorHoja: 24 en A4-3x8 (3×8), 14 en A4-2x7 (2×7)', () => {
    expect(celdasPorHoja(A4_3X8)).toBe(24)
    expect(celdasPorHoja(A4_2X7)).toBe(14)
  })

  it('contarHojas: 24 etiquetas en A4-3x8 ⇒ 1 hoja exacta (límite inferior)', () => {
    expect(contarHojas(24, A4_3X8)).toBe(1)
  })

  it('contarHojas: 25 etiquetas en A4-3x8 ⇒ 2 hojas (ceil, nunca floor/división entera)', () => {
    expect(contarHojas(25, A4_3X8)).toBe(2)
  })

  it('contarHojas: 0 etiquetas ⇒ 0 hojas', () => {
    expect(contarHojas(0, A4_3X8)).toBe(0)
  })

  it('un descriptor mutado (celdaPorHoja distinto) mueve el conteo derivado — no hay un campo propio que se quede atrás', () => {
    // Prueba anti-regresión de la regla "nunca almacenado": si celdasPorHoja se leyera de un
    // campo cacheado en vez de columnas*filas, este descriptor con geometría alterada seguiría
    // reportando el valor viejo. Construye un descriptor con la MISMA forma pero otra grilla y
    // confirma que el derivado sigue la tupla, no una copia.
    const otraGrilla: DescriptorDeFormato = { ...A4_3X8, columnas: 2, filas: 5 }
    expect(celdasPorHoja(otraGrilla)).toBe(10)
    expect(contarHojas(11, otraGrilla)).toBe(2)
  })
})
