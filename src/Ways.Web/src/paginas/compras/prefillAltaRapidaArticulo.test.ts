import { describe, expect, it } from 'vitest'
import { prefillDeAltaRapidaDeArticulo } from './prefillAltaRapidaArticulo'

describe('prefillDeAltaRapidaDeArticulo', () => {
  it('un texto con espacios es un nombre', () => {
    expect(prefillDeAltaRapidaDeArticulo('  Leche 1L  ')).toEqual({ nombre: 'Leche 1L', codigoProveedor: '' })
  })

  it('una palabra solo de letras de 4 o más caracteres es un nombre', () => {
    expect(prefillDeAltaRapidaDeArticulo('Yerba')).toEqual({ nombre: 'Yerba', codigoProveedor: '' })
    expect(prefillDeAltaRapidaDeArticulo('Ñoquis')).toEqual({ nombre: 'Ñoquis', codigoProveedor: '' })
  })

  it('una palabra de letras de menos de 4 caracteres es un código', () => {
    expect(prefillDeAltaRapidaDeArticulo('ABC')).toEqual({ nombre: '', codigoProveedor: 'ABC' })
  })

  it('un código con dígitos o guiones es un código aunque tenga letras', () => {
    expect(prefillDeAltaRapidaDeArticulo('AB-1234')).toEqual({ nombre: '', codigoProveedor: 'AB-1234' })
    expect(prefillDeAltaRapidaDeArticulo('LECHE123')).toEqual({ nombre: '', codigoProveedor: 'LECHE123' })
    expect(prefillDeAltaRapidaDeArticulo('7790001')).toEqual({ nombre: '', codigoProveedor: '7790001' })
  })

  it('el texto vacío no precarga nada', () => {
    expect(prefillDeAltaRapidaDeArticulo('   ')).toEqual({ nombre: '', codigoProveedor: '' })
  })
})
