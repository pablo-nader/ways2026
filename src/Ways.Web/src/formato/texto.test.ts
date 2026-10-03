import { describe, expect, it } from 'vitest'
import { contieneSinAcentos, normalizarParaBuscar } from './texto'

describe('normalizarParaBuscar', () => {
  it.each([
    ['José', 'jose'],
    ['JOSÉ', 'jose'],
    ['Ñandú', 'nandu'],
    ['ÁCIDO', 'acido'],
    ['Pingüino', 'pinguino'],
    ['Straße', 'strasse'],
    ['Æsir', 'aesir'],
    ['Søren', 'soren'],
    ['Œuvre', 'oeuvre'],
    ['sin cambios 123', 'sin cambios 123'],
    ['', ''],
  ])('%s pasa a %s', (entrada, esperado) => {
    expect(normalizarParaBuscar(entrada)).toBe(esperado)
  })

  it('una forma ya descompuesta (NFD) queda igual que la compuesta', () => {
    expect(normalizarParaBuscar('José')).toBe(normalizarParaBuscar('José'))
  })
})

describe('contieneSinAcentos', () => {
  it.each(['jose', 'José', 'JOSE', 'JOSÉ', 'ñandu', 'Nandú'])('"%s" encuentra "José Ñandú"', (buscado) => {
    expect(contieneSinAcentos('José Ñandú', buscado)).toBe(true)
  })

  it('un término vacío o solo espacios no filtra', () => {
    expect(contieneSinAcentos('José', '')).toBe(true)
    expect(contieneSinAcentos('José', '   ')).toBe(true)
  })

  it('un término ausente no coincide', () => {
    expect(contieneSinAcentos('José', 'pedro')).toBe(false)
  })
})
