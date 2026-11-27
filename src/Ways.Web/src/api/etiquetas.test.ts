import { beforeEach, describe, expect, it, vi } from 'vitest'
import { clienteDeEtiquetas, MAXIMO_DE_ARTICULOS_POR_HOJA } from './etiquetas'

const apiPostMock = vi.fn()
const apiGetMock = vi.fn()

vi.mock('./cliente', async (importarOriginal) => ({
  ...(await importarOriginal<typeof import('./cliente')>()),
  api: { post: (...args: unknown[]) => apiPostMock(...args), get: (...args: unknown[]) => apiGetMock(...args) },
}))

describe('clienteDeEtiquetas', () => {
  beforeEach(() => {
    apiPostMock.mockReset()
    apiGetMock.mockReset()
  })

  it('el tope de la selección es el del servidor', () => {
    expect(MAXIMO_DE_ARTICULOS_POR_HOJA).toBe(200)
  })

  it('por selección manda idsArticulo y nunca filtro', async () => {
    apiPostMock.mockResolvedValue({})
    await clienteDeEtiquetas.datosDeArticulos(7, 3, [1, 2])
    expect(apiPostMock).toHaveBeenCalledWith('/etiquetas/datos', { idPuntoVenta: 7, idListaPrecio: 3, idsArticulo: [1, 2] })
    expect(apiPostMock.mock.calls[0][1]).not.toHaveProperty('filtro')
  })

  it('por filtro manda filtro y nunca idsArticulo', async () => {
    apiPostMock.mockResolvedValue({})
    await clienteDeEtiquetas.datosPorFiltro(7, 3, { busqueda: 'coca', soloConOfertaVigente: true })
    expect(apiPostMock).toHaveBeenCalledWith('/etiquetas/datos', {
      idPuntoVenta: 7,
      idListaPrecio: 3,
      filtro: { busqueda: 'coca', soloConOfertaVigente: true },
    })
    expect(apiPostMock.mock.calls[0][1]).not.toHaveProperty('idsArticulo')
  })

  it('buscarArticulos pide solo vendibles, con página y tamaño', async () => {
    apiGetMock.mockResolvedValue({ items: [], total: 0, pagina: 2, tamanio: 25 })
    await clienteDeEtiquetas.buscarArticulos('  coca ', 2, 25)
    expect(apiGetMock).toHaveBeenCalledWith('/articulos?busqueda=coca&soloVendibles=true&pagina=2&tamanio=25')
  })

  it('buscarArticulos omite la búsqueda vacía', async () => {
    apiGetMock.mockResolvedValue({ items: [], total: 0, pagina: 1, tamanio: 25 })
    await clienteDeEtiquetas.buscarArticulos('   ', 1, 25)
    expect(apiGetMock).toHaveBeenCalledWith('/articulos?soloVendibles=true&pagina=1&tamanio=25')
  })
})
