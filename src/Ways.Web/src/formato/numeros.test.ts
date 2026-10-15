import { describe, expect, it } from 'vitest'
import { formatearNumeroDecimal, parsearEnteroTipeado, parsearNumeroDecimal } from './numeros'

describe('parsearNumeroDecimal', () => {
  it.each([
    ['0,25', 0.25],
    ['0.25', 0.25],
    ['1', 1],
    ['1,', 1],
    ['1.', 1],
    [',5', 0.5],
    ['.5', 0.5],
    ['  2,5 ', 2.5],
    ['007', 7],
  ])('%j → %j', (texto, esperado) => {
    expect(parsearNumeroDecimal(texto)).toBe(esperado)
  })

  it.each(['', '  ', '.', ',', '1.234,5', '1,2,3', '1.2.3', 'abc', '1e3', '-1', '--1', '1 2', '+1'])('%j → null', (texto) => {
    expect(parsearNumeroDecimal(texto)).toBeNull()
  })

  it('el negativo solo se admite a pedido', () => {
    expect(parsearNumeroDecimal('-1,5', { permitirNegativo: true })).toBe(-1.5)
    expect(parsearNumeroDecimal('-', { permitirNegativo: true })).toBeNull()
    expect(parsearNumeroDecimal('1,5', { permitirNegativo: true })).toBe(1.5)
  })
})

describe('parsearEnteroTipeado', () => {
  it('acepta dígitos y rechaza separadores', () => {
    expect(parsearEnteroTipeado(' 12 ')).toBe(12)
    expect(parsearEnteroTipeado('1,5')).toBeNull()
    expect(parsearEnteroTipeado('1.5')).toBeNull()
    expect(parsearEnteroTipeado('')).toBeNull()
    expect(parsearEnteroTipeado('-1')).toBeNull()
  })
})

describe('formatearNumeroDecimal', () => {
  it('muestra la coma', () => {
    expect(formatearNumeroDecimal(0.25)).toBe('0,25')
    expect(formatearNumeroDecimal(3)).toBe('3')
  })
})
