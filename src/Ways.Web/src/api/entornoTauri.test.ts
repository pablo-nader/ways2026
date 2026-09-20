import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  corriendoEnTauri,
  establecerTokenDeSesionBearer,
  guardarCredencialDeDispositivo,
  inicializarUrlServidor,
  leerCredencialDeDispositivo,
  tokenDeSesionBearerActual,
  urlBaseApi,
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

describe('inicializarUrlServidor/urlBaseApi', () => {
  it('urlBaseApi es "" fuera de Tauri, aunque haya quedado una URL cacheada de un llamado previo bajo Tauri', async () => {
    // Cachea una URL real primero (bajo Tauri) para que el gate de `corriendoEnTauri()` sea lo
    // único que puede explicar la diferencia — con el caché en null desde el arranque, esta
    // prueba pasaría igual sin ese gate (confound, ver skill mutation-proof-tests regla 3).
    instalarPuenteTauri()
    invokeMock.mockResolvedValue({ url_servidor: 'https://empresa.aipos.site' })
    await inicializarUrlServidor()
    expect(urlBaseApi()).toBe('https://empresa.aipos.site')

    quitarPuenteTauri()
    expect(urlBaseApi()).toBe('')
  })

  it('inicializarUrlServidor no invoca nada fuera de Tauri', async () => {
    await inicializarUrlServidor()
    expect(invokeMock).not.toHaveBeenCalled()
  })

  it('cachea el url_servidor que devuelve info_app, y urlBaseApi lo antepone bajo Tauri', async () => {
    instalarPuenteTauri()
    invokeMock.mockResolvedValue({ version: '1.0.0', impresora: null, url_servidor: 'https://empresa.aipos.site' })
    await inicializarUrlServidor()
    expect(invokeMock).toHaveBeenCalledWith('info_app')
    expect(urlBaseApi()).toBe('https://empresa.aipos.site')
  })

  it('cachea null si info_app no devuelve url_servidor', async () => {
    instalarPuenteTauri()
    invokeMock.mockResolvedValue({ version: '1.0.0', impresora: null })
    await inicializarUrlServidor()
    expect(urlBaseApi()).toBe('')
  })

  it('deja el cache en null (nunca lanza) si el comando falla, aunque antes hubiera una URL cacheada', async () => {
    instalarPuenteTauri()
    invokeMock.mockResolvedValue({ url_servidor: 'https://empresa.aipos.site' })
    await inicializarUrlServidor()
    expect(urlBaseApi()).toBe('https://empresa.aipos.site')

    invokeMock.mockRejectedValue(new Error('IPC falló'))
    await expect(inicializarUrlServidor()).resolves.toBeUndefined()
    expect(urlBaseApi()).toBe('')
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
