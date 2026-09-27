import { afterEach, describe, expect, it, vi } from 'vitest'
import { clienteDeDispositivos } from './dispositivos'
import { establecerTokenDeSesionBearer, tokenDeSesionBearerActual, VENTANA_SESION_OFFLINE_MS } from './entornoTauri'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('./cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: vi.fn(),
    delete: (...args: unknown[]) => apiDeleteMock(...(args as [string])),
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
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  establecerTokenDeSesionBearer(null)
})

/** `leer_credencial_de_dispositivo` devuelve `secreto`; cualquier otro comando resuelve `undefined`. */
function conSecretoGuardado(secreto: string | null) {
  invokeMock.mockImplementation((comando: string) =>
    Promise.resolve(comando === 'leer_credencial_de_dispositivo' ? secreto : undefined),
  )
}

const usuarioCajero = { id: 1, usuario: 'jperez', mail: 'jperez@ways.test', rolId: 4, rol: 'Vendedor', ultimaConexion: null, idTenant: 1 }

describe('header Authorization: Dispositivo', () => {
  it('fuera de Tauri, obtenerActual e iniciarSesion salen sin header propio', async () => {
    apiGetMock.mockResolvedValue(undefined)
    apiPostMock.mockResolvedValue(usuarioCajero)

    await clienteDeDispositivos.obtenerActual()
    await clienteDeDispositivos.iniciarSesion({ usuario: 'jperez', password: 'secreta' })

    expect(apiGetMock.mock.calls).toEqual([['/dispositivos/actual']])
    expect(apiPostMock.mock.calls).toEqual([
      ['/auth/login-dispositivo', { usuario: 'jperez', password: 'secreta', solicitarBearer: false }],
    ])
  })

  it('bajo Tauri con secreto guardado, obtenerActual manda Authorization: Dispositivo <secreto>', async () => {
    instalarPuenteTauri()
    conSecretoGuardado('secreto-del-equipo')
    apiGetMock.mockResolvedValue(undefined)

    await clienteDeDispositivos.obtenerActual()

    expect(invokeMock).toHaveBeenCalledWith('leer_credencial_de_dispositivo')
    expect(apiGetMock.mock.calls).toEqual([['/dispositivos/actual', { Authorization: 'Dispositivo secreto-del-equipo' }]])
  })

  it('bajo Tauri con secreto guardado, iniciarSesion manda Authorization: Dispositivo <secreto>', async () => {
    instalarPuenteTauri()
    conSecretoGuardado('secreto-del-equipo')
    apiPostMock.mockResolvedValue({ usuario: usuarioCajero, token: 'token-de-sesion', expiraEl: '2026-06-01T00:00:00Z' })

    await clienteDeDispositivos.iniciarSesion({ usuario: 'jperez', password: 'secreta' })

    expect(apiPostMock.mock.calls).toEqual([
      [
        '/auth/login-dispositivo',
        { usuario: 'jperez', password: 'secreta', solicitarBearer: true },
        { Authorization: 'Dispositivo secreto-del-equipo' },
      ],
    ])
  })

  it('bajo Tauri sin secreto guardado, ninguna de las dos manda header propio', async () => {
    instalarPuenteTauri()
    conSecretoGuardado(null)
    apiGetMock.mockResolvedValue(undefined)
    apiPostMock.mockResolvedValue({ usuario: usuarioCajero, token: 'token-de-sesion', expiraEl: '2026-06-01T00:00:00Z' })

    await clienteDeDispositivos.obtenerActual()
    await clienteDeDispositivos.iniciarSesion({ usuario: 'jperez', password: 'secreta' })

    expect(apiGetMock.mock.calls).toEqual([['/dispositivos/actual']])
    expect(apiPostMock.mock.calls).toEqual([
      ['/auth/login-dispositivo', { usuario: 'jperez', password: 'secreta', solicitarBearer: true }],
    ])
  })
})

describe('clienteDeDispositivos', () => {
  it('obtenerActual pide GET /dispositivos/actual', async () => {
    apiGetMock.mockResolvedValue(undefined)
    await clienteDeDispositivos.obtenerActual()
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
    // `snapshot: null`: `iniciarSesion` todavía no conoce el punto de venta en este momento (se
    // resuelve un instante después, ver `LoginDeDispositivo.tsx`) — ver el doc-comment de
    // `guardarSesionDeCajeroPersistida` en `entornoTauri.ts`.
    expect(invokeMock).toHaveBeenCalledWith('guardar_sesion_de_cajero', {
      sesion: { token: 'token-de-sesion', expira_el: '2026-06-01T00:00:00Z', snapshot: null },
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
      sesion: { token: 'token-de-sesion', expira_el: expiracionEsperada, snapshot: null },
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

  it('listar pide GET /dispositivos', () => {
    apiGetMock.mockResolvedValue([])
    void clienteDeDispositivos.listar()
    expect(apiGetMock).toHaveBeenCalledWith('/dispositivos')
  })

  it('revocar pide DELETE /dispositivos/{id}', () => {
    apiDeleteMock.mockResolvedValue(undefined)
    void clienteDeDispositivos.revocar(42)
    expect(apiDeleteMock).toHaveBeenCalledWith('/dispositivos/42')
  })
})
