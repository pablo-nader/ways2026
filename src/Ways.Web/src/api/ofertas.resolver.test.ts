import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi, ErrorDeRed } from './cliente'
import { clienteDeOfertas } from './ofertas'
import type { LineaDeResolucion } from './tipos'

const apiPostMock = vi.fn()

vi.mock('./cliente', async (importarOriginal) => ({
  ...(await importarOriginal<typeof import('./cliente')>()),
  api: { post: (...args: unknown[]) => apiPostMock(...args) },
}))

const lineas: LineaDeResolucion[] = [{ idArticulo: 1, idEmpresa: null, idListaPrecio: 3, cantidad: 1 }]

describe('clienteDeOfertas.resolver — agregación', () => {
  beforeEach(() => apiPostMock.mockReset())

  it('por omisión pide la resolución como documento (el carrito del POS)', async () => {
    apiPostMock.mockResolvedValue([])
    await clienteDeOfertas.resolver(lineas)
    expect(apiPostMock).toHaveBeenCalledWith('/ofertas/resolver', { lineas, agregacion: 'Documento' })
  })

  it('manda la agregación que pide el llamador', async () => {
    apiPostMock.mockResolvedValue([])
    await clienteDeOfertas.resolver(lineas, 'PorLinea')
    expect(apiPostMock).toHaveBeenCalledWith('/ofertas/resolver', { lineas, agregacion: 'PorLinea' })
  })
})

describe('clienteDeOfertas.resolverConCombos — carrito del POS', () => {
  beforeEach(() => apiPostMock.mockReset())

  it('pega a la ruta hermana con el cuerpo del resolver, siempre como documento', async () => {
    apiPostMock.mockResolvedValue({ lineas: [], combos: [] })
    await clienteDeOfertas.resolverConCombos(lineas)
    expect(apiPostMock).toHaveBeenCalledWith('/ofertas/resolver-con-combos', { lineas, agregacion: 'Documento' })
  })

  it('el resolver de siempre no cambia de ruta: lo usan las búsquedas y la consulta de precio', async () => {
    apiPostMock.mockResolvedValue([])
    await clienteDeOfertas.resolver(lineas, 'PorLinea')
    expect(apiPostMock).toHaveBeenCalledWith('/ofertas/resolver', { lineas, agregacion: 'PorLinea' })
  })

  it('una respuesta sin `combos` se entrega con una lista vacía, y las líneas intactas', async () => {
    const resueltas = [{ idArticulo: 1, idListaPrecio: 3, precioOriginal: 10, precioFinal: 10, descuentoUnitario: 0, aplicadas: [] }]
    apiPostMock.mockResolvedValue({ lineas: resueltas })

    await expect(clienteDeOfertas.resolverConCombos(lineas)).resolves.toEqual({ lineas: resueltas, combos: [] })
  })

  it('una respuesta con `combos` los conserva tal cual', async () => {
    const combos = [{ idCombo: 7, codigo: 7, nombre: 'Pancho + Coca', veces: 1, ahorro: 500 }]
    apiPostMock.mockResolvedValue({ lineas: [], combos })

    await expect((await clienteDeOfertas.resolverConCombos(lineas)).combos).toEqual(combos)
  })

  describe.each([404, 405])('un servidor sin la ruta (%s): cae al resolver de siempre', (estado) => {
    it('pide /ofertas/resolver como documento, devuelve sus líneas y ningún combo', async () => {
      const resueltas = [{ idArticulo: 1, idListaPrecio: 3, precioOriginal: 10, precioFinal: 10, descuentoUnitario: 0, aplicadas: [] }]
      apiPostMock.mockRejectedValueOnce(new ErrorApi(estado, 'no_encontrado', 'x')).mockResolvedValueOnce(resueltas)

      await expect(clienteDeOfertas.resolverConCombos(lineas)).resolves.toEqual({ lineas: resueltas, combos: [] })

      expect(apiPostMock.mock.calls).toEqual([
        ['/ofertas/resolver-con-combos', { lineas, agregacion: 'Documento' }],
        ['/ofertas/resolver', { lineas, agregacion: 'Documento' }],
      ])
    })

    it('si el resolver de respaldo también falla, ese error es el que sale', async () => {
      const respaldo = new ErrorApi(500, 'error', 'respaldo')
      apiPostMock.mockRejectedValueOnce(new ErrorApi(estado, 'no_encontrado', 'x')).mockRejectedValueOnce(respaldo)

      await expect(clienteDeOfertas.resolverConCombos(lineas)).rejects.toBe(respaldo)
    })
  })

  it.each([
    ['un 400', () => new ErrorApi(400, 'solicitud_invalida', 'x')],
    ['un 500', () => new ErrorApi(500, 'error', 'x')],
    ['un 401', () => new ErrorApi(401, 'no_autenticado', 'x')],
    ['una caída de red', () => new ErrorDeRed('sin red')],
  ])('%s NO cae al resolver de siempre: se propaga y no hay segundo pedido', async (_descripcion, crearError) => {
    const error = crearError()
    apiPostMock.mockRejectedValueOnce(error)

    await expect(clienteDeOfertas.resolverConCombos(lineas)).rejects.toBe(error)
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })
})
