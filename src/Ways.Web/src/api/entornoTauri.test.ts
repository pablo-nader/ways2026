import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  corriendoEnTauri,
  establecerTokenDeSesionBearer,
  guardarCredencialDeDispositivo,
  guardarSesionDeCajeroPersistida,
  inicializarUrlServidor,
  leerCredencialDeDispositivo,
  limpiarSesionDeCajeroPersistida,
  restaurarSesionDeCajeroPersistida,
  sesionPersistidaEsValida,
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
  // Por si algún test de restaurarSesionDeCajeroPersistida (más abajo) usa vi.setSystemTime y
  // falla antes de su propio vi.useRealTimers() — nunca debe filtrarse un reloj congelado a otro
  // test de este archivo.
  vi.useRealTimers()
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

describe('sesionPersistidaEsValida (stage-pos-sesion-offline)', () => {
  it('es true con un vencimiento futuro', () => {
    expect(sesionPersistidaEsValida('2026-06-01T00:00:00Z', new Date('2026-01-01T00:00:00Z'))).toBe(true)
  })

  it('es false con un vencimiento pasado', () => {
    expect(sesionPersistidaEsValida('2025-01-01T00:00:00Z', new Date('2026-01-01T00:00:00Z'))).toBe(false)
  })

  it('es false exactamente en el instante del vencimiento (estricto, nunca inclusive)', () => {
    expect(sesionPersistidaEsValida('2026-01-01T00:00:00.000Z', new Date('2026-01-01T00:00:00.000Z'))).toBe(false)
  })

  it('es false con una fecha vacía o no parseable, nunca se asume vigente', () => {
    expect(sesionPersistidaEsValida('', new Date())).toBe(false)
    expect(sesionPersistidaEsValida('esto-no-es-una-fecha', new Date())).toBe(false)
  })
})

describe('guardarSesionDeCajeroPersistida', () => {
  it('no hace nada fuera de Tauri', async () => {
    await guardarSesionDeCajeroPersistida('un-token', '2026-06-01T00:00:00Z')
    expect(invokeMock).not.toHaveBeenCalled()
  })

  it('invoca guardar_sesion_de_cajero con token y expira_el bajo Tauri', async () => {
    instalarPuenteTauri()
    invokeMock.mockResolvedValue(undefined)
    await guardarSesionDeCajeroPersistida('un-token', '2026-06-01T00:00:00Z')
    expect(invokeMock).toHaveBeenCalledWith('guardar_sesion_de_cajero', {
      sesion: { token: 'un-token', expira_el: '2026-06-01T00:00:00Z' },
    })
  })

  it('nunca lanza aunque el comando falle (un login ya autenticado en memoria no puede reportarse como fallido)', async () => {
    instalarPuenteTauri()
    invokeMock.mockRejectedValue(new Error('IPC falló'))
    await expect(guardarSesionDeCajeroPersistida('un-token', '2026-06-01T00:00:00Z')).resolves.toBeUndefined()
  })
})

describe('limpiarSesionDeCajeroPersistida', () => {
  it('no hace nada fuera de Tauri', async () => {
    await limpiarSesionDeCajeroPersistida()
    expect(invokeMock).not.toHaveBeenCalled()
  })

  it('reusa guardar_sesion_de_cajero con token/expira_el vacíos (sin comando de limpieza separado)', async () => {
    instalarPuenteTauri()
    invokeMock.mockResolvedValue(undefined)
    await limpiarSesionDeCajeroPersistida()
    expect(invokeMock).toHaveBeenCalledWith('guardar_sesion_de_cajero', { sesion: { token: '', expira_el: '' } })
  })

  it('nunca lanza aunque el comando falle', async () => {
    instalarPuenteTauri()
    invokeMock.mockRejectedValue(new Error('IPC falló'))
    await expect(limpiarSesionDeCajeroPersistida()).resolves.toBeUndefined()
  })
})

describe('restaurarSesionDeCajeroPersistida', () => {
  it('no hace nada (ni invoca) fuera de Tauri', async () => {
    await restaurarSesionDeCajeroPersistida()
    expect(invokeMock).not.toHaveBeenCalled()
    expect(tokenDeSesionBearerActual()).toBeNull()
  })

  it('instala el token en memoria cuando la sesión persistida todavía no venció', async () => {
    instalarPuenteTauri()
    vi.setSystemTime(new Date('2026-01-01T00:00:00Z'))
    invokeMock.mockResolvedValue({ token: 'token-restaurado', expira_el: '2026-06-01T00:00:00Z' })

    await restaurarSesionDeCajeroPersistida()

    expect(invokeMock).toHaveBeenCalledWith('leer_sesion_de_cajero')
    expect(tokenDeSesionBearerActual()).toBe('token-restaurado')
    vi.useRealTimers()
  })

  it('NO instala el token cuando la sesión persistida ya venció (cae al login, mismo camino que hoy)', async () => {
    instalarPuenteTauri()
    vi.setSystemTime(new Date('2026-06-02T00:00:00Z'))
    invokeMock.mockResolvedValue({ token: 'token-vencido', expira_el: '2026-06-01T00:00:00Z' })

    await restaurarSesionDeCajeroPersistida()

    expect(tokenDeSesionBearerActual()).toBeNull()
    vi.useRealTimers()
  })

  it('no hace nada si no hay ninguna sesión persistida (comando devuelve null)', async () => {
    instalarPuenteTauri()
    invokeMock.mockResolvedValue(null)
    await restaurarSesionDeCajeroPersistida()
    expect(tokenDeSesionBearerActual()).toBeNull()
  })

  /** Mutation target: el guard `resultado.token === ''` en `restaurarSesionDeCajeroPersistida` —
   * un token vacío con un `expira_el` futuro (válido) no debe instalarse nunca, aunque
   * `sesionPersistidaEsValida` por sí sola diría que la fecha es válida. El backend real
   * (`sesion::analizar`, "vacío es None") nunca produce esta forma, pero la función de este lado
   * no debe confiar ciegamente en la forma exacta de lo que cruza el IPC. */
  it('no instala un token vacío aunque expira_el sea una fecha futura válida', async () => {
    instalarPuenteTauri()
    vi.setSystemTime(new Date('2026-01-01T00:00:00Z'))
    invokeMock.mockResolvedValue({ token: '', expira_el: '2026-06-01T00:00:00Z' })

    await restaurarSesionDeCajeroPersistida()

    expect(tokenDeSesionBearerActual()).toBeNull()
    vi.useRealTimers()
  })

  it('no hace nada (nunca lanza) si el comando falla', async () => {
    instalarPuenteTauri()
    invokeMock.mockRejectedValue(new Error('IPC falló'))
    await expect(restaurarSesionDeCajeroPersistida()).resolves.toBeUndefined()
    expect(tokenDeSesionBearerActual()).toBeNull()
  })

  it('no hace nada si la forma devuelta no tiene token/expira_el como string', async () => {
    instalarPuenteTauri()
    invokeMock.mockResolvedValue({ token: 123, expira_el: '2026-06-01T00:00:00Z' })
    await restaurarSesionDeCajeroPersistida()
    expect(tokenDeSesionBearerActual()).toBeNull()
  })
})
