import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  CLAVE_INTERVALO_DE_SINCRONIZACION,
  guardarIntervaloDeSincronizacion,
  INTERVALO_POR_DEFECTO_MINUTOS,
  leerIntervaloDeSincronizacion,
  minutosAMilisegundos,
  normalizarIntervaloEnMinutos,
} from './intervaloDeSincronizacion'

beforeEach(() => {
  localStorage.clear()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('normalizarIntervaloEnMinutos', () => {
  it.each([
    [15, 15],
    ['15', 15],
    [' 7 ', 7],
    [1, 1],
    [60, 60],
    [2.4, 2],
    ['2.6', 3],
  ])('%j dentro del rango → %d', (valor, esperado) => {
    expect(normalizarIntervaloEnMinutos(valor)).toBe(esperado)
  })

  it.each([
    [0, 1],
    [-5, 1],
    ['0.2', 1],
    [61, 60],
    ['1000', 60],
  ])('%j fuera del rango se recorta a %d', (valor, esperado) => {
    expect(normalizarIntervaloEnMinutos(valor)).toBe(esperado)
  })

  it.each([[''], ['   '], ['abc'], [Number.NaN], [Number.POSITIVE_INFINITY], [null], [undefined], [{}]])('%j no es un número → null', (valor) => {
    expect(normalizarIntervaloEnMinutos(valor)).toBeNull()
  })
})

describe('leerIntervaloDeSincronizacion', () => {
  it('sin nada guardado devuelve el valor por defecto (5 minutos)', () => {
    expect(INTERVALO_POR_DEFECTO_MINUTOS).toBe(5)
    expect(leerIntervaloDeSincronizacion()).toBe(5)
  })

  it('devuelve el valor guardado', () => {
    localStorage.setItem(CLAVE_INTERVALO_DE_SINCRONIZACION, '12')
    expect(leerIntervaloDeSincronizacion()).toBe(12)
  })

  it('un valor guardado corrupto cae al valor por defecto', () => {
    localStorage.setItem(CLAVE_INTERVALO_DE_SINCRONIZACION, 'basura')
    expect(leerIntervaloDeSincronizacion()).toBe(INTERVALO_POR_DEFECTO_MINUTOS)
  })

  it('un valor guardado fuera de rango se recorta', () => {
    localStorage.setItem(CLAVE_INTERVALO_DE_SINCRONIZACION, '500')
    expect(leerIntervaloDeSincronizacion()).toBe(60)
  })

  it('si localStorage tira, devuelve el valor por defecto en vez de romper', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('SecurityError')
    })
    expect(leerIntervaloDeSincronizacion()).toBe(INTERVALO_POR_DEFECTO_MINUTOS)
  })
})

describe('guardarIntervaloDeSincronizacion', () => {
  it('persiste el valor normalizado y lo devuelve', () => {
    expect(guardarIntervaloDeSincronizacion(90)).toBe(60)
    expect(localStorage.getItem(CLAVE_INTERVALO_DE_SINCRONIZACION)).toBe('60')
    expect(leerIntervaloDeSincronizacion()).toBe(60)
  })

  it('si localStorage tira al escribir, devuelve igual el valor vigente sin romper', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('QuotaExceededError')
    })
    expect(guardarIntervaloDeSincronizacion(10)).toBe(10)
  })
})

describe('minutosAMilisegundos', () => {
  it('convierte minutos a milisegundos', () => {
    expect(minutosAMilisegundos(5)).toBe(300_000)
  })
})
