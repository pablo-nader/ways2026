import { afterEach, describe, expect, it, vi } from 'vitest'
import { abrirConfiguracion, enEscritorio, imprimir } from './impresora'

afterEach(() => {
  delete window.__TAURI__
})

describe('enEscritorio', () => {
  it('es false sin window.__TAURI__ (la web normal, o los tests)', () => {
    expect(enEscritorio()).toBe(false)
  })

  it('es true con window.__TAURI__.core.invoke presente', () => {
    window.__TAURI__ = { core: { invoke: vi.fn() } }
    expect(enEscritorio()).toBe(true)
  })
})

describe('imprimir', () => {
  it('sin Tauri devuelve no_disponible sin lanzar', async () => {
    const resultado = await imprimir(new Uint8Array([1, 2, 3]))
    expect(resultado).toEqual({ ok: false, motivo: 'no_disponible', mensaje: expect.any(String) })
  })

  it('con Tauri invoca imprimir_raw con los bytes como array plano', async () => {
    const invoke = vi.fn().mockResolvedValue(undefined)
    window.__TAURI__ = { core: { invoke } }

    const resultado = await imprimir(new Uint8Array([9, 8, 7]))

    expect(invoke).toHaveBeenCalledWith('imprimir_raw', { bytes: [9, 8, 7] })
    expect(resultado).toEqual({ ok: true })
  })

  it('si invoke rechaza, devuelve el error tipado en vez de lanzar', async () => {
    const invoke = vi.fn().mockRejectedValue(new Error('impresora desconectada'))
    window.__TAURI__ = { core: { invoke } }

    const resultado = await imprimir(new Uint8Array([1]))

    expect(resultado).toEqual({ ok: false, motivo: 'error', mensaje: 'impresora desconectada' })
  })

  it('si invoke rechaza con algo que no es Error, usa un mensaje genérico', async () => {
    const invoke = vi.fn().mockRejectedValue('boom')
    window.__TAURI__ = { core: { invoke } }

    const resultado = await imprimir(new Uint8Array([1]))

    expect(resultado).toEqual({ ok: false, motivo: 'error', mensaje: 'No se pudo imprimir.' })
  })
})

describe('abrirConfiguracion', () => {
  it('sin Tauri no hace nada y no lanza', async () => {
    await expect(abrirConfiguracion()).resolves.toBeUndefined()
  })

  it('con Tauri invoca abrir_configuracion', async () => {
    const invoke = vi.fn().mockResolvedValue(undefined)
    window.__TAURI__ = { core: { invoke } }

    await abrirConfiguracion()

    expect(invoke).toHaveBeenCalledWith('abrir_configuracion')
  })

  it('si invoke rechaza, no propaga el error', async () => {
    const invoke = vi.fn().mockRejectedValue(new Error('boom'))
    window.__TAURI__ = { core: { invoke } }

    await expect(abrirConfiguracion()).resolves.toBeUndefined()
  })
})
