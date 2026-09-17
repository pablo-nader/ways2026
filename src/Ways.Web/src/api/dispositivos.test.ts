import { describe, expect, it, vi } from 'vitest'
import { clienteDeDispositivos } from './dispositivos'

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

  it('iniciarSesion pide POST /auth/login-dispositivo con usuario y password', () => {
    apiPostMock.mockResolvedValue(undefined)
    void clienteDeDispositivos.iniciarSesion({ usuario: 'jperez', password: 'secreta' })
    expect(apiPostMock).toHaveBeenCalledWith('/auth/login-dispositivo', { usuario: 'jperez', password: 'secreta' })
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
