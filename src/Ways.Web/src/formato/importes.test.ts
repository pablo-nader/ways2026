import { describe, expect, it } from 'vitest'
import { formatearImporte, parsearImporte } from './importes'

describe('formatearImporte', () => {
  const casos: Array<[number, string]> = [
    [0, '0,00'],
    [5.5, '5,50'],
    [1234, '1.234,00'],
    [1234.5, '1.234,50'],
    [10000, '10.000,00'],
    [1234567.891, '1.234.567,89'],
    [-1234.5, '-1.234,50'],
    [100000000, '100.000.000,00'],
  ]

  it.each(casos)('formatea %s como "%s"', (valor, esperado) => {
    expect(formatearImporte(valor)).toBe(esperado)
  })

  it('el separador de miles queda visible incluso con 4 dígitos (nunca "1234,00")', () => {
    expect(formatearImporte(1234)).toBe('1.234,00')
    expect(formatearImporte(9999)).toBe('9.999,00')
  })

  it('con simbolo: true antepone "$ "', () => {
    expect(formatearImporte(1234.56, { simbolo: true })).toBe('$ 1.234,56')
    expect(formatearImporte(10000, { simbolo: true })).toBe('$ 10.000,00')
    expect(formatearImporte(5.5, { simbolo: true })).toBe('$ 5,50')
  })

  it('el signo negativo va antes del símbolo, nunca "$ -"', () => {
    expect(formatearImporte(-1234.5, { simbolo: true })).toBe('-$ 1.234,50')
  })

  it('sin simbolo no antepone nada', () => {
    expect(formatearImporte(1234.56)).toBe('1.234,56')
  })

  it('decimales permite otra cantidad de dígitos', () => {
    expect(formatearImporte(1234.5, { decimales: 0 })).toBe('1.235')
    expect(formatearImporte(1234.5678, { decimales: 3 })).toBe('1.234,568')
  })

  it('null, undefined y NaN devuelven "—"', () => {
    expect(formatearImporte(null)).toBe('—')
    expect(formatearImporte(undefined)).toBe('—')
    expect(formatearImporte(Number.NaN)).toBe('—')
  })

  it('un cero genuino no es tratado como "sin dato"', () => {
    expect(formatearImporte(0)).toBe('0,00')
  })

  it('un valor que redondea a cero no muestra signo negativo ("-0,00")', () => {
    expect(formatearImporte(-0.001)).toBe('0,00')
    expect(formatearImporte(-0)).toBe('0,00')
  })

  describe('redondeo half-away-from-zero (evita el bug de punto flotante de toFixed)', () => {
    const casosDeRedondeo: Array<[number, string]> = [
      [1.005, '1,01'],
      [1.015, '1,02'],
      [1.025, '1,03'],
      [0.005, '0,01'],
      [-0.005, '-0,01'],
      [2.675, '2,68'],
      [1234.005, '1.234,01'],
      [9999.995, '10.000,00'],
    ]

    it.each(casosDeRedondeo)('%s redondea a "%s"', (valor, esperado) => {
      expect(formatearImporte(valor)).toBe(esperado)
    })
  })

  it('mutation evidence: sin agrupado determinístico, un número de 4 dígitos perdería el "." — ver nota', () => {
    // Esta prueba documenta la mutación ejecutada manualmente (no se automatiza en CI
    // porque requeriría dos implementaciones paralelas): al reemplazar `agruparMiles`
    // por `Intl.NumberFormat('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })`
    // (sin `useGrouping: 'always''), `formatearImporte(1234)` pasa a devolver "1234,00" en
    // motores ICU cuya locale es-AR define `minimumGroupingDigits >= 2` — este test falla
    // en ese escenario y vuelve a pasar al revertir a `agruparMiles`.
    expect(formatearImporte(1234)).toBe('1.234,00')
  })
})

describe('parsearImporte', () => {
  it.each([
    ['1.234,56', 1234.56],
    ['1234,56', 1234.56],
    ['1234', 1234],
    ['-1.234,56', -1234.56],
    ['12.345.678', 12345678],
    ['1,5', 1.5],
    ['0', 0],
    ['0,00', 0],
    ['$ 1.234,56', 1234.56],
    ['  1.234,56  ', 1234.56],
  ])('parsea "%s" como %s', (texto, esperado) => {
    expect(parsearImporte(texto)).toBe(esperado)
  })

  it('rechaza "." usado como separador decimal (regla: "," es el único decimal)', () => {
    expect(parsearImporte('1234.56')).toBeNull()
  })

  it('rechaza un grupo de miles incompleto', () => {
    expect(parsearImporte('1.23')).toBeNull()
    expect(parsearImporte('12.3456')).toBeNull()
  })

  it('rechaza entrada vacía, solo espacios, o no numérica', () => {
    expect(parsearImporte('')).toBeNull()
    expect(parsearImporte('   ')).toBeNull()
    expect(parsearImporte('abc')).toBeNull()
    expect(parsearImporte(null)).toBeNull()
    expect(parsearImporte(undefined)).toBeNull()
  })

  it('rechaza una coma sin dígitos detrás (entrada incompleta)', () => {
    expect(parsearImporte('1234,')).toBeNull()
  })

  it('rechaza más de una coma', () => {
    expect(parsearImporte('1,234,56')).toBeNull()
  })

  it('"-0,00" normaliza a 0 (nunca -0)', () => {
    const resultado = parsearImporte('-0,00')
    expect(resultado).toBe(0)
    expect(Object.is(resultado, -0)).toBe(false)
  })

  it('formatearImporte(parsearImporte(texto)) es estable para entradas válidas', () => {
    expect(formatearImporte(parsearImporte('1.234,56'))).toBe('1.234,56')
    expect(formatearImporte(parsearImporte('10000'))).toBe('10.000,00')
  })
})
