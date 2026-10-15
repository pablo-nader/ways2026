import { describe, expect, it } from 'vitest'
import { atributosDeCampoDecimal, atributosDeCampoEntero, formatearNumeroDecimal, parsearEnteroTipeado, parsearNumeroDecimal } from './numeros'

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

describe('atributosDeCampoDecimal / atributosDeCampoEntero', () => {
  const cumple = (pattern: string, texto: string) => new RegExp(`^(?:${pattern})$`).test(texto)

  it('el decimal acepta coma o punto y acota los decimales', () => {
    const { pattern, inputMode, type } = atributosDeCampoDecimal({ decimales: 2 })
    expect(type).toBe('text')
    expect(inputMode).toBe('decimal')
    expect(['10', '10,5', '10.55', ',5', '0,25'].every((t) => cumple(pattern, t))).toBe(true)
    expect(['10,555', '1,2,3', '1.234,5', '-1', 'abc', ''].some((t) => cumple(pattern, t))).toBe(false)
  })

  it('el decimal sin tope de decimales y con negativo opcional', () => {
    expect(cumple(atributosDeCampoDecimal().pattern, '1,23456')).toBe(true)
    expect(cumple(atributosDeCampoDecimal().pattern, '-1')).toBe(false)
    expect(cumple(atributosDeCampoDecimal({ permitirNegativo: true }).pattern, '-1,5')).toBe(true)
  })

  it('el entero rechaza separadores decimales', () => {
    const { pattern, inputMode } = atributosDeCampoEntero()
    expect(inputMode).toBe('numeric')
    expect(cumple(pattern, '12')).toBe(true)
    expect(['1,5', '1.5', '-1', ''].some((t) => cumple(pattern, t))).toBe(false)
    expect(cumple(atributosDeCampoEntero({ permitirNegativo: true }).pattern, '-3')).toBe(true)
  })
})

describe('formatearNumeroDecimal', () => {
  it('muestra la coma', () => {
    expect(formatearNumeroDecimal(0.25)).toBe('0,25')
    expect(formatearNumeroDecimal(3)).toBe('3')
  })
})
