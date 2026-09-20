import { describe, expect, it, vi } from 'vitest'
import { clienteDeDispositivos } from './dispositivos'

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
})
