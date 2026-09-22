import { afterEach, describe, expect, it, vi } from 'vitest'
import { clienteDeDispositivos } from './dispositivos'
import { establecerTokenDeSesionBearer, tokenDeSesionBearerActual, VENTANA_SESION_OFFLINE_MS } from './entornoTauri'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('./cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: vi.fn(),
    delete: vi.fn(),
  },
}))

type GlobalConTauri = typeof globalThis & { __TAURI__?: { core: { invoke: ReturnType<typeof vi.fn> } } }
const invokeMock = vi.fn()

function instalarPuenteTauri() {
  ;(globalThis as GlobalConTauri).__TAURI__ = { core: { invoke: invokeMock } }
}

function quitarPuenteTauri() {
  delete (globalThis as GlobalConTauri).__TAURI__
}

afterEach(() => {
  quitarPuenteTauri()
  invokeMock.mockReset()
  establecerTokenDeSesionBearer(null)
})

describe('clienteDeDispositivos', () => {
  it('obtenerActual pide GET /dispositivos/actual', () => {
    apiGetMock.mockResolvedValue(undefined)
    void clienteDeDispositivos.obtenerActual()
    expect(apiGetMock).toHaveBeenCalledWith('/dispositivos/actual')
  })

  it('vincular pide POST /dispositivos con idPuntoVenta y nombre', () => {
    apiPostMock.mockResolvedValue(undefined)
    void clienteDeDispositivos.vincular({ idPuntoVenta: 7, nombre: 'Caja 1' })
    expect(apiPostMock).toHaveBeenCalledWith('/dispositivos', { idPuntoVenta: 7, nombre: 'Caja 1' })
  })

  it('iniciarSesion pide POST /auth/login-dispositivo con usuario, password y solicitarBearer', async () => {
    const usuario = { id: 1, usuario: 'jperez', mail: 'jperez@ways.test', rolId: 4, rol: 'Vendedor', ultimaConexion: null, idTenant: 1 }
    apiPostMock.mockResolvedValue(usuario)
    await clienteDeDispositivos.iniciarSesion({ usuario: 'jperez', password: 'secreta' })
    // Fuera de Tauri (este test corre en jsdom, sin window.__TAURI__) solicitarBearer es
    // siempre false — el navegador normal sigue con la cookie, sin cambio de contrato.
    expect(apiPostMock).toHaveBeenCalledWith('/auth/login-dispositivo', {
      usuario: 'jperez',
      password: 'secreta',
      solicitarBearer: false,
    })
  })

  it('iniciarSesion devuelve el usuario directo cuando la respuesta no trae token (contrato sin cambios)', async () => {
    const usuario = { id: 1, usuario: 'jperez', mail: 'jperez@ways.test', rolId: 4, rol: 'Vendedor', ultimaConexion: null, idTenant: 1 }
    apiPostMock.mockResolvedValue(usuario)
    const resultado = await clienteDeDispositivos.iniciarSesion({ usuario: 'jperez', password: 'secreta' })
    expect(resultado).toBe(usuario)
  })

  // stage-pos-sesion-offline: bajo Tauri, la respuesta SÍ trae token — estos tests instalan el
  // puente real (`window.__TAURI__`) para ejercitar `corriendoEnTauri() === true` de punta a
  // punta, cosa que ningún test de este archivo hacía hasta ahora.
  it('bajo Tauri, con respuesta con bearer, instala el token en memoria Y lo persiste con su vencimiento', async () => {
    instalarPuenteTauri()
    invokeMock.mockResolvedValue(undefined)
    const usuario = { id: 1, usuario: 'jperez', mail: 'jperez@ways.test', rolId: 4, rol: 'Vendedor', ultimaConexion: null, idTenant: 1 }
    apiPostMock.mockResolvedValue({ usuario, token: 'token-de-sesion', expiraEl: '2026-06-01T00:00:00Z' })

    const resultado = await clienteDeDispositivos.iniciarSesion({ usuario: 'jperez', password: 'secreta' })

    expect(resultado).toBe(usuario)
    expect(tokenDeSesionBearerActual()).toBe('token-de-sesion')
    expect(invokeMock).toHaveBeenCalledWith('guardar_sesion_de_cajero', {
      sesion: { token: 'token-de-sesion', expira_el: '2026-06-01T00:00:00Z' },
    })
  })

  it('bajo Tauri, iniciarSesion pide solicitarBearer: true', async () => {
    instalarPuenteTauri()
    invokeMock.mockResolvedValue(undefined)
    apiPostMock.mockResolvedValue({
      usuario: { id: 1, usuario: 'jperez', mail: 'jperez@ways.test', rolId: 4, rol: 'Vendedor', ultimaConexion: null, idTenant: 1 },
      token: 'token-de-sesion',
      expiraEl: '2026-06-01T00:00:00Z',
    })

    await clienteDeDispositivos.iniciarSesion({ usuario: 'jperez', password: 'secreta' })

    expect(apiPostMock).toHaveBeenCalledWith('/auth/login-dispositivo', {
      usuario: 'jperez',
      password: 'secreta',
      solicitarBearer: true,
    })
  })

  // judgment-day ronda 1 (FIX 2b, BLOCKER): el `expiraEl` REAL del servidor son 365 días —
  // persistirlo tal cual dejaría un archivo filtrado vigente por casi un año. Este test usa un
  // vencimiento realista (lejano) para probar que SÍ se acota, a diferencia del test de arriba
  // ('instala el token en memoria Y lo persiste con su vencimiento'), que usa uno YA más corto
  // que la ventana a propósito (para seguir probando el passthrough sin acoplarse a esta ventana).
  it('bajo Tauri, con un expiraEl de servidor realista (lejano), persiste una expiración ACOTADA a la ventana local, nunca el valor crudo de 365 días', async () => {
    instalarPuenteTauri()
    invokeMock.mockResolvedValue(undefined)
    const ahora = new Date('2026-01-01T00:00:00Z')
    vi.setSystemTime(ahora)
    const expiraElServidor = new Date(ahora.getTime() + 365 * 24 * 60 * 60 * 1000).toISOString()
    const usuario = { id: 1, usuario: 'jperez', mail: 'jperez@ways.test', rolId: 4, rol: 'Vendedor', ultimaConexion: null, idTenant: 1 }
    apiPostMock.mockResolvedValue({ usuario, token: 'token-de-sesion', expiraEl: expiraElServidor })

    await clienteDeDispositivos.iniciarSesion({ usuario: 'jperez', password: 'secreta' })

    const expiracionEsperada = new Date(ahora.getTime() + VENTANA_SESION_OFFLINE_MS).toISOString()
    expect(invokeMock).toHaveBeenCalledWith('guardar_sesion_de_cajero', {
      sesion: { token: 'token-de-sesion', expira_el: expiracionEsperada },
    })
    expect(expiracionEsperada).not.toBe(expiraElServidor)
    vi.useRealTimers()
  })

  it('un fallo al persistir la sesión (IPC) nunca convierte un login exitoso en uno fallido', async () => {
    instalarPuenteTauri()
    invokeMock.mockRejectedValue(new Error('IPC falló'))
    const usuario = { id: 1, usuario: 'jperez', mail: 'jperez@ways.test', rolId: 4, rol: 'Vendedor', ultimaConexion: null, idTenant: 1 }
    apiPostMock.mockResolvedValue({ usuario, token: 'token-de-sesion', expiraEl: '2026-06-01T00:00:00Z' })

    const resultado = await clienteDeDispositivos.iniciarSesion({ usuario: 'jperez', password: 'secreta' })

    expect(resultado).toBe(usuario)
    expect(tokenDeSesionBearerActual()).toBe('token-de-sesion')
  })
})
