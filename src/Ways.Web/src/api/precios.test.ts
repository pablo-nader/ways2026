import { beforeEach, describe, expect, it, vi } from 'vitest'

const apiPostMock = vi.fn()

vi.mock('./cliente', () => ({
  api: {
    get: vi.fn(),
    post: (...args: unknown[]) => apiPostMock(...args),
  },
}))

import { clienteDePrecios } from './precios'

beforeEach(() => {
  apiPostMock.mockReset()
})

describe('clienteDePrecios — alcance de familia (doc 10 §3)', () => {
  it('establecer manda el alcance en el cuerpo del POST /articulos/{id}/precios', () => {
    clienteDePrecios.establecer(31, { idListaPrecio: 2, precio: 1500, confirmarReemplazo: false, alcance: 'Familia' })

    expect(apiPostMock).toHaveBeenCalledExactlyOnceWith('/articulos/31/precios', {
      idListaPrecio: 2,
      precio: 1500,
      confirmarReemplazo: false,
      alcance: 'Familia',
    })
  })

  it('programar manda el alcance en el cuerpo del POST /articulos/{id}/precios/programados', () => {
    clienteDePrecios.programar(31, {
      idListaPrecio: 2,
      precio: 1500,
      vigenteDesde: '2026-10-12T15:00:00.000Z',
      confirmarReemplazo: true,
      alcance: 'SoloEste',
    })

    expect(apiPostMock).toHaveBeenCalledExactlyOnceWith('/articulos/31/precios/programados', {
      idListaPrecio: 2,
      precio: 1500,
      vigenteDesde: '2026-10-12T15:00:00.000Z',
      confirmarReemplazo: true,
      alcance: 'SoloEste',
    })
  })

  it('sin alcance, el cuerpo no lo inventa', () => {
    clienteDePrecios.establecer(31, { idListaPrecio: 2, precio: 1500 })

    expect(apiPostMock.mock.calls[0][1]).toEqual({ idListaPrecio: 2, precio: 1500 })
  })
})
