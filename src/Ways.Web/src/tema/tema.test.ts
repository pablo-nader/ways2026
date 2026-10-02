import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { aplicarTema, CLAVE_DE_TEMA, guardarTema, leerTemaGuardado } from './tema'

beforeEach(() => {
  localStorage.clear()
  delete document.documentElement.dataset.bsTheme
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('tema', () => {
  it('sin valor guardado el tema es oscuro', () => {
    expect(leerTemaGuardado()).toBe('oscuro')
  })

  it('un valor guardado inválido cae en oscuro', () => {
    localStorage.setItem(CLAVE_DE_TEMA, 'solarizado')

    expect(leerTemaGuardado()).toBe('oscuro')
  })

  it('guardar y leer devuelve el mismo tema', () => {
    guardarTema('claro')

    expect(localStorage.getItem(CLAVE_DE_TEMA)).toBe('claro')
    expect(leerTemaGuardado()).toBe('claro')
  })

  it('aplicar el tema fija data-bs-theme en el html', () => {
    aplicarTema('claro')
    expect(document.documentElement.dataset.bsTheme).toBe('light')

    aplicarTema('oscuro')
    expect(document.documentElement.dataset.bsTheme).toBe('dark')
  })

  it('si localStorage falla al leer, devuelve oscuro sin romper', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('bloqueado')
    })

    expect(leerTemaGuardado()).toBe('oscuro')
  })

  it('si localStorage falla al guardar, no lanza', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('cuota')
    })

    expect(() => guardarTema('claro')).not.toThrow()
  })
})
