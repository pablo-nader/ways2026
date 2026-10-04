import { describe, expect, it } from 'vitest'
import {
  esCantidadValida,
  esFraccionDeArticuloPorUnidad,
  esTextoDeCantidadValido,
  fraccionaUnaUnidad,
  respetaGranularidad,
  restriccionDeCantidad,
} from './cantidadPorUnidad'

describe('restriccionDeCantidad', () => {
  it('un artículo por unidad se mueve de a uno y arranca en uno', () => {
    expect(restriccionDeCantidad('Unidad')).toEqual({ step: 1, min: 1 })
  })

  it('un artículo por peso mantiene el paso y el mínimo de milésimas', () => {
    expect(restriccionDeCantidad('Peso')).toEqual({ step: 0.001, min: 0.001 })
  })

  it('sin unidad conocida se comporta como peso', () => {
    expect(restriccionDeCantidad(undefined)).toEqual({ step: 0.001, min: 0.001 })
    expect(restriccionDeCantidad(null)).toEqual({ step: 0.001, min: 0.001 })
  })

  it('permiteCero baja el mínimo a cero sin tocar el paso', () => {
    expect(restriccionDeCantidad('Unidad', { permiteCero: true })).toEqual({ step: 1, min: 0 })
    expect(restriccionDeCantidad('Peso', { permiteCero: true })).toEqual({ step: 0.001, min: 0 })
  })
})

describe('respetaGranularidad', () => {
  it.each([1, 2, 12, 0])('una unidad admite el entero %s', (cantidad) => {
    expect(respetaGranularidad('Unidad', cantidad)).toBe(true)
  })

  it.each([0.5, 1.001, 2.999, 0.0001])('una unidad rechaza la fracción %s', (cantidad) => {
    expect(respetaGranularidad('Unidad', cantidad)).toBe(false)
  })

  it.each([1, 0.001, 12.3, 12.345])('un peso admite hasta tres decimales: %s', (cantidad) => {
    expect(respetaGranularidad('Peso', cantidad)).toBe(true)
  })

  it.each([0.0001, 12.3456])('un peso rechaza más de tres decimales: %s', (cantidad) => {
    expect(respetaGranularidad('Peso', cantidad)).toBe(false)
  })

  it('un decimal con ruido de punto flotante cuenta como tres decimales', () => {
    expect(respetaGranularidad('Peso', 0.1 + 0.2)).toBe(true)
    expect(respetaGranularidad(undefined, 1.005)).toBe(true)
  })

  it('un valor que no es número finito se rechaza', () => {
    expect(respetaGranularidad('Peso', Number.NaN)).toBe(false)
    expect(respetaGranularidad('Unidad', Number.POSITIVE_INFINITY)).toBe(false)
  })

  it('sin unidad conocida admite las fracciones que admitía siempre', () => {
    expect(respetaGranularidad(undefined, 1.5)).toBe(true)
    expect(respetaGranularidad(null, 0.25)).toBe(true)
  })
})

describe('esCantidadValida', () => {
  it('exige que la cantidad sea positiva además de la granularidad', () => {
    expect(esCantidadValida('Unidad', 0)).toBe(false)
    expect(esCantidadValida('Unidad', -1)).toBe(false)
    expect(esCantidadValida('Peso', 0)).toBe(false)
    expect(esCantidadValida('Peso', -0.5)).toBe(false)
    expect(esCantidadValida(undefined, 0)).toBe(false)
  })

  it('una unidad acepta enteros positivos y rechaza fracciones', () => {
    expect(esCantidadValida('Unidad', 3)).toBe(true)
    expect(esCantidadValida('Unidad', 1.5)).toBe(false)
  })

  it('un peso o una unidad ausente aceptan 0,001 y fracciones', () => {
    expect(esCantidadValida('Peso', 0.001)).toBe(true)
    expect(esCantidadValida(undefined, 0.5)).toBe(true)
  })
})

describe('esTextoDeCantidadValido', () => {
  it('vacío o no numérico es inválido', () => {
    expect(esTextoDeCantidadValido('Peso', '')).toBe(false)
    expect(esTextoDeCantidadValido('Peso', '   ')).toBe(false)
    expect(esTextoDeCantidadValido('Peso', 'abc')).toBe(false)
  })

  it('aplica la regla de la unidad al número tipeado', () => {
    expect(esTextoDeCantidadValido('Unidad', '2')).toBe(true)
    expect(esTextoDeCantidadValido('Unidad', '2.5')).toBe(false)
    expect(esTextoDeCantidadValido('Peso', '2.5')).toBe(true)
    expect(esTextoDeCantidadValido(undefined, '2.5')).toBe(true)
  })
})

describe('fraccionaUnaUnidad', () => {
  it('una fracción positiva de un artículo por unidad', () => {
    expect(fraccionaUnaUnidad('Unidad', 1.5)).toBe(true)
  })

  it('no es una fracción un entero, un cero, un negativo ni un número inválido', () => {
    expect(fraccionaUnaUnidad('Unidad', 2)).toBe(false)
    expect(fraccionaUnaUnidad('Unidad', 0)).toBe(false)
    expect(fraccionaUnaUnidad('Unidad', -1.5)).toBe(false)
    expect(fraccionaUnaUnidad('Unidad', Number.NaN)).toBe(false)
  })

  it('un peso o una unidad ausente nunca fraccionan una unidad', () => {
    expect(fraccionaUnaUnidad('Peso', 1.5)).toBe(false)
    expect(fraccionaUnaUnidad(undefined, 1.5)).toBe(false)
    expect(fraccionaUnaUnidad(null, 1.5)).toBe(false)
  })
})

describe('esFraccionDeArticuloPorUnidad', () => {
  it('solo es verdadero para un positivo fraccionario de un artículo por unidad', () => {
    expect(esFraccionDeArticuloPorUnidad('Unidad', '1.5')).toBe(true)
    expect(esFraccionDeArticuloPorUnidad('Unidad', '2')).toBe(false)
    expect(esFraccionDeArticuloPorUnidad('Unidad', '')).toBe(false)
    expect(esFraccionDeArticuloPorUnidad('Unidad', '0')).toBe(false)
    expect(esFraccionDeArticuloPorUnidad('Peso', '1.5')).toBe(false)
    expect(esFraccionDeArticuloPorUnidad(undefined, '1.5')).toBe(false)
  })
})
