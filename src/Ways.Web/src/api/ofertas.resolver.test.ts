import { beforeEach, describe, expect, it, vi } from 'vitest'
import { clienteDeOfertas } from './ofertas'
import type { LineaDeResolucion } from './tipos'

const apiPostMock = vi.fn()

vi.mock('./cliente', () => ({ api: { post: (...args: unknown[]) => apiPostMock(...args) } }))

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
})
