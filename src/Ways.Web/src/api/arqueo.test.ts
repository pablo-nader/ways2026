import { describe, expect, it } from 'vitest'
import { aSolicitudDeCierre, conteoValido, conteosCompletos, diferenciaPrevia } from './arqueo'
import type { LineaDeResumen } from './tipos'

describe('diferenciaPrevia', () => {
  it('positivo cuando lo declarado es menor a lo esperado (faltante)', () => {
    expect(diferenciaPrevia(1000, 900)).toBe(100)
  })

  it('negativo cuando lo declarado supera lo esperado (sobrante)', () => {
    expect(diferenciaPrevia(1000, 1100)).toBe(-100)
  })

  it('cero cuando coinciden exactamente', () => {
    expect(diferenciaPrevia(500, 500)).toBe(0)
  })
})

describe('conteoValido', () => {
  it('rechaza un valor vacío', () => {
    expect(conteoValido('')).toBe(false)
  })

  it('rechaza un valor no numérico', () => {
    expect(conteoValido('abc')).toBe(false)
  })

  it('rechaza un número negativo', () => {
    expect(conteoValido('-1')).toBe(false)
  })

  it('acepta 0 (declarar nada es un acto deliberado, no un default)', () => {
    expect(conteoValido('0')).toBe(true)
  })

  it('acepta un número positivo', () => {
    expect(conteoValido('640')).toBe(true)
  })
})

describe('conteosCompletos', () => {
  const medios: LineaDeResumen[] = [
    { idMedioPago: 1, importeEsperado: 640 },
    { idMedioPago: 2, importeEsperado: 300 },
  ]

  it('false sin ningún medio arqueable (todavía no hay resumen cargado)', () => {
    expect(conteosCompletos([], {})).toBe(false)
  })

  it('false si falta el conteo de un medio', () => {
    expect(conteosCompletos(medios, { 1: '640' })).toBe(false)
  })

  it('false si el conteo de un medio es inválido', () => {
    expect(conteosCompletos(medios, { 1: '640', 2: 'abc' })).toBe(false)
  })

  it('true cuando todos los medios arqueables tienen un conteo válido', () => {
    expect(conteosCompletos(medios, { 1: '640', 2: '0' })).toBe(true)
  })
})

describe('aSolicitudDeCierre', () => {
  const medios: LineaDeResumen[] = [
    { idMedioPago: 1, importeEsperado: 640 },
    { idMedioPago: 2, importeEsperado: 300 },
  ]

  it('arma exactamente un conteo por medio arqueable, en el mismo orden', () => {
    const solicitud = aSolicitudDeCierre(medios, { 1: '635', 2: '300' }, '')

    expect(solicitud.conteos).toEqual([
      { idMedioPago: 1, importeDeclarado: 635 },
      { idMedioPago: 2, importeDeclarado: 300 },
    ])
  })

  it('recorta observaciones y las convierte a null si quedan vacías', () => {
    expect(aSolicitudDeCierre(medios, { 1: '0', 2: '0' }, '   ').observaciones).toBeNull()
    expect(aSolicitudDeCierre(medios, { 1: '0', 2: '0' }, '  turno tranquilo  ').observaciones).toBe(
      'turno tranquilo',
    )
  })
})
