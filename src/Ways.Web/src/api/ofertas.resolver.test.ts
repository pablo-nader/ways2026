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
