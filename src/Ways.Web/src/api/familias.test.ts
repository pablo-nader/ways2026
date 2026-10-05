import { beforeEach, describe, expect, it, vi } from 'vitest'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('./cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...args),
    post: (...args: unknown[]) => apiPostMock(...args),
    put: (...args: unknown[]) => apiPutMock(...args),
    delete: (...args: unknown[]) => apiDeleteMock(...args),
  },
}))

import { clienteDeFamilias } from './familias'

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
})

describe('clienteDeFamilias — lecturas', () => {
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
})

describe('clienteDeFamilias — escrituras', () => {
  it('previsualizar hace POST /familias/previsualizacion con la referencia y los artículos, tal cual', async () => {
    apiPostMock.mockResolvedValue({ idArticuloReferencia: 31, idFamilia: null, articulos: [], problemas: [] })

    await clienteDeFamilias.previsualizar({ idArticuloReferencia: 31, idsArticulos: [32, 33] })

    expect(apiPostMock).toHaveBeenCalledExactlyOnceWith('/familias/previsualizacion', { idArticuloReferencia: 31, idsArticulos: [32, 33] })
  })

  it('crear hace POST /familias con el nombre, la referencia y los artículos', async () => {
    apiPostMock.mockResolvedValue({ idFamilia: 7, nombre: 'Sabores', idArticuloReferencia: 31, articulos: [] })

    await clienteDeFamilias.crear({ nombre: 'Sabores', idArticuloReferencia: 31, idsArticulos: [32] })

    expect(apiPostMock).toHaveBeenCalledExactlyOnceWith('/familias', { nombre: 'Sabores', idArticuloReferencia: 31, idsArticulos: [32] })
  })

  it('agregarArticulos hace POST /familias/{id}/articulos con los ids', async () => {
    apiPostMock.mockResolvedValue({ idFamilia: 7, nombre: 'Sabores', idArticuloReferencia: 31, articulos: [] })

    await clienteDeFamilias.agregarArticulos(7, { idsArticulos: [40, 41] })

    expect(apiPostMock).toHaveBeenCalledExactlyOnceWith('/familias/7/articulos', { idsArticulos: [40, 41] })
  })

  it('actualizar hace PUT /familias/{id} con el nombre y el estado', async () => {
    apiPutMock.mockResolvedValue({ id: 7, nombre: 'Sabores', activo: false, cantidadArticulos: 3 })

    await clienteDeFamilias.actualizar(7, { nombre: 'Sabores', activo: false })

    expect(apiPutMock).toHaveBeenCalledExactlyOnceWith('/familias/7', { nombre: 'Sabores', activo: false })
  })

  it('sacarArticulo pide DELETE /familias/{idFamilia}/articulos/{idArticulo}, la familia primero', async () => {
    apiDeleteMock.mockResolvedValue(undefined)

    await clienteDeFamilias.sacarArticulo(7, 31)

    expect(apiDeleteMock).toHaveBeenCalledExactlyOnceWith('/familias/7/articulos/31')
  })

  it('disolver pide DELETE /familias/{id}', async () => {
    apiDeleteMock.mockResolvedValue(undefined)

    await clienteDeFamilias.disolver(7)

    expect(apiDeleteMock).toHaveBeenCalledExactlyOnceWith('/familias/7')
  })
})
