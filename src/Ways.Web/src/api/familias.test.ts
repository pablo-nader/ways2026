import { beforeEach, describe, expect, it, vi } from 'vitest'

const apiGetMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('./cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...args),
    delete: (...args: unknown[]) => apiDeleteMock(...args),
  },
}))

import { clienteDeFamilias } from './familias'

beforeEach(() => {
  apiGetMock.mockReset()
  apiDeleteMock.mockReset()
})

describe('clienteDeFamilias', () => {
  it('listar pide GET /familias y devuelve las filas tal cual', async () => {
    const filas = [{ id: 1, nombre: 'Sabores', activo: true, cantidadArticulos: 3 }]
    apiGetMock.mockResolvedValue(filas)

    await expect(clienteDeFamilias.listar()).resolves.toBe(filas)
    expect(apiGetMock).toHaveBeenCalledExactlyOnceWith('/familias')
  })

  it('obtener pide GET /familias/{id} con el id en la ruta', async () => {
    apiGetMock.mockResolvedValue({ id: 7 })

    await clienteDeFamilias.obtener(7)

    expect(apiGetMock).toHaveBeenCalledExactlyOnceWith('/familias/7')
  })

  it('sacarArticulo pide DELETE /familias/{idFamilia}/articulos/{idArticulo}, la familia primero', async () => {
    apiDeleteMock.mockResolvedValue(undefined)

    await clienteDeFamilias.sacarArticulo(7, 31)

    expect(apiDeleteMock).toHaveBeenCalledExactlyOnceWith('/familias/7/articulos/31')
  })
})
