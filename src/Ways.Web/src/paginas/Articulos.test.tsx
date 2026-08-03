import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Articulos } from './Articulos'
import type { ArticuloListado, PaginaDe } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
    delete: (...args: unknown[]) => apiDeleteMock(...(args as [string])),
  },
  ErrorApi: class ErrorApiMock extends Error {},
}))

function articuloFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    id: 1,
    codigoInterno: 'A0001',
    nombre: 'Articulo Uno',
    descripcion: null,
    idArea: 1,
    idCategoria: null,
    idMarca: null,
    idGrupo: null,
    idProveedorHabitual: null,
    idAlicuotaIva: 1,
    unidadVenta: 'Unidad',
    unidadesPorBulto: null,
    esProducto: true,
    costoLista: 100,
    descuentoProveedor: null,
    costoNominal: null,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    ...sobrescribir,
  }
}

function paginaFixture(items: ArticuloListado[]): PaginaDe<ArticuloListado> {
  return { items, total: items.length, pagina: 1, tamanio: 20 }
}

const articuloUno = articuloFixture({ id: 1, codigoInterno: 'A0001', nombre: 'Articulo Uno' })
const articuloDos = articuloFixture({ id: 2, codigoInterno: 'A0002', nombre: 'Articulo Dos' })

/**
 * Despacha por ruta, igual que el mock de `../api/cliente` en `PaginaCatalogo.test.tsx`:
 * `clienteDeArticulos`/`clienteDePrecios`/`clienteDeCatalogo`/`clienteDeOrganizacion` son todos
 * envoltorios finos sobre `api.get`, así que interceptar acá alcanza para toda la pantalla sin
 * mockear cada módulo de API por separado.
 */
function mockearApiGet() {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/articulos') return Promise.resolve(paginaFixture([articuloUno, articuloDos]))
    if (/^\/articulos\/\d+$/.test(ruta)) {
      const id = Number(ruta.split('/')[2])
      return Promise.resolve([articuloUno, articuloDos].find((a) => a.id === id) ?? articuloUno)
    }
    if (/^\/articulos\/\d+\/codigos-barra$/.test(ruta)) return Promise.resolve([])
    if (/^\/articulos\/\d+\/precios$/.test(ruta)) return Promise.resolve([])
    if (/^\/articulos\/\d+\/sugerencia-precio$/.test(ruta)) return Promise.resolve({ precioSugerido: 55.5 })
    if (ruta === '/catalogos/areas') return Promise.resolve([])
    if (ruta === '/catalogos/categorias') return Promise.resolve([])
    if (ruta === '/catalogos/marcas') return Promise.resolve([])
    if (ruta === '/catalogos/grupos') return Promise.resolve([])
    if (ruta.startsWith('/proveedores')) return Promise.resolve({ items: [], total: 0, pagina: 1, tamanio: 200 })
    if (ruta === '/catalogos-fiscales/alicuotas-iva') return Promise.resolve([])
    if (ruta === '/empresas') return Promise.resolve([])
    if (ruta === '/catalogos/listas-precio') return Promise.resolve([])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
  mockearApiGet()
})

describe('Articulos — reseteo de estado por artículo (key de FormularioArticulo)', () => {
  it('la sugerencia de precio calculada para un artículo no persiste al pasar a editar otro', async () => {
    render(<Articulos />)

    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('button', { name: 'Editar' }))

    await screen.findByText('Editando artículo A0001')
    await userEvent.click(await screen.findByRole('button', { name: 'Calcular sugerencia de precio' }))
    await screen.findByText(/Precio sugerido a partir de costo y margen/)

    // Sin cancelar, se pasa directo a editar el segundo artículo — mismo flujo que protege el
    // `key={formulario.id ?? 'nuevo'}` documentado junto a <FormularioArticulo>.
    const filaDos = screen.getByText('Articulo Dos').closest('tr')
    if (!filaDos) throw new Error('No se encontró la fila del artículo dos')
    await userEvent.click(within(filaDos).getByRole('button', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0002')

    await waitFor(() => {
      expect(screen.queryByText(/Precio sugerido a partir de costo y margen/)).not.toBeInTheDocument()
    })
  })
})
