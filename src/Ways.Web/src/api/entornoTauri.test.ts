import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  corriendoEnTauri,
  establecerTokenDeSesionBearer,
  guardarCredencialDeDispositivo,
  leerCredencialDeDispositivo,
  tokenDeSesionBearerActual,
} from './entornoTauri'

type GlobalConTauri = typeof globalThis & { __TAURI__?: { core: { invoke: ReturnType<typeof vi.fn> } } }

const invokeMock = vi.fn()

function instalarPuenteTauri() {
  ;(globalThis as GlobalConTauri).__TAURI__ = { core: { invoke: invokeMock } }
}

function quitarPuenteTauri() {
  delete (globalThis as GlobalConTauri).__TAURI__
}

beforeEach(() => {
  invokeMock.mockReset()
  quitarPuenteTauri()
  establecerTokenDeSesionBearer(null)
})

afterEach(() => {
  quitarPuenteTauri()
})

describe('corriendoEnTauri', () => {
  it('es false sin window.__TAURI__ (navegador normal)', () => {
    expect(corriendoEnTauri()).toBe(false)
  })

  it('es true con window.__TAURI__ presente', () => {
    instalarPuenteTauri()
    expect(corriendoEnTauri()).toBe(true)
  })
})

describe('guardarCredencialDeDispositivo', () => {
  it('no hace nada fuera de Tauri', async () => {
    await guardarCredencialDeDispositivo('un-secreto')
    expect(invokeMock).not.toHaveBeenCalled()
  })

  it('invoca guardar_credencial_de_dispositivo con el secreto bajo Tauri', async () => {
    instalarPuenteTauri()
    invokeMock.mockResolvedValue(undefined)
    await guardarCredencialDeDispositivo('un-secreto')
    expect(invokeMock).toHaveBeenCalledWith('guardar_credencial_de_dispositivo', { secreto: 'un-secreto' })
  })
})

describe('leerCredencialDeDispositivo', () => {
  it('devuelve null fuera de Tauri', async () => {
    expect(await leerCredencialDeDispositivo()).toBeNull()
  })

  it('devuelve el secreto que responde el comando bajo Tauri', async () => {
    instalarPuenteTauri()
    invokeMock.mockResolvedValue('un-secreto-guardado')
    expect(await leerCredencialDeDispositivo()).toBe('un-secreto-guardado')
    expect(invokeMock).toHaveBeenCalledWith('leer_credencial_de_dispositivo')
  })

  it('devuelve null (nunca lanza) si el comando falla', async () => {
    instalarPuenteTauri()
    invokeMock.mockRejectedValue(new Error('IPC falló'))
    expect(await leerCredencialDeDispositivo()).toBeNull()
  })
})

describe('tokenDeSesionBearerActual/establecerTokenDeSesionBearer', () => {
  it('arranca en null y refleja lo último que se estableció', () => {
    expect(tokenDeSesionBearerActual()).toBeNull()
    establecerTokenDeSesionBearer('un-token')
    expect(tokenDeSesionBearerActual()).toBe('un-token')
    establecerTokenDeSesionBearer(null)
    expect(tokenDeSesionBearerActual()).toBeNull()
  })
})
