import { fireEvent, render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ModalDeBusquedaDeArticulos } from './ModalDeBusquedaDeArticulos'
import type { ArticuloListado, PaginaDe, ResultadoDeResolucion } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: vi.fn(),
    delete: vi.fn(),
  },
  ErrorApi: class ErrorApiMock extends Error {
    estado: number
    codigo: string
    constructor(estado: number, codigo: string, mensaje: string) {
      super(mensaje)
      this.estado = estado
      this.codigo = codigo
    }
  },
}))

function articuloListadoFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    id: 9,
    codigoInterno: 'A0009',
    nombre: 'Fanta 1.5L',
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
    costoLista: null,
    descuentoProveedor: null,
    costoNominal: null,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    controlaLote: false,
    ...sobrescribir,
  }
}

function paginaDe(items: ArticuloListado[]): PaginaDe<ArticuloListado> {
  return { items, total: items.length, pagina: 1, tamanio: 25 }
}

function propsDe(sobrescribir: Partial<Parameters<typeof ModalDeBusquedaDeArticulos>[0]> = {}) {
  return {
    idListaPrecio: 1,
    idEmpresa: 1,
    onAgregar: vi.fn(),
    onCerrar: vi.fn(),
    ...sobrescribir,
  }
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
})

describe('ModalDeBusquedaDeArticulos — contexto de precio', () => {
  it('con idListaPrecio null no resuelve precio (queda inerte) aunque la búsqueda encuentre resultados', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=fa') ? Promise.resolve(paginaDe([articuloListadoFixture()])) : Promise.reject(new Error(ruta)),
    )

    render(<ModalDeBusquedaDeArticulos {...propsDe({ idListaPrecio: null })} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    expect(await screen.findByText('Fanta 1.5L')).toBeInTheDocument()
    expect(screen.getByText('—')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('la resolución de precio viaja con idEmpresa/idListaPrecio de las props y cantidad = 1 por artículo', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=fa') ? Promise.resolve(paginaDe([articuloListadoFixture()])) : Promise.reject(new Error(ruta)),
    )
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') return Promise.resolve<ResultadoDeResolucion[]>([])
      return Promise.reject(new Error(ruta))
    })

    render(<ModalDeBusquedaDeArticulos {...propsDe({ idListaPrecio: 3, idEmpresa: 7 })} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    await screen.findByText('Fanta 1.5L')
    expect(apiPostMock).toHaveBeenCalledWith('/ofertas/resolver', {
      lineas: [{ idArticulo: 9, idEmpresa: 7, idListaPrecio: 3, cantidad: 1 }],
    })
  })
})

describe('ModalDeBusquedaDeArticulos — errores y reentrancia', () => {
  it('una búsqueda rechazada muestra un error y limpia los resultados previos', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/articulos?busqueda=fa')) return Promise.resolve(paginaDe([articuloListadoFixture()]))
      if (ruta.startsWith('/articulos?busqueda=zz')) return Promise.reject(new Error('boom'))
      return Promise.reject(new Error(ruta))
    })
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/ofertas/resolver' ? Promise.resolve<ResultadoDeResolucion[]>([]) : Promise.reject(new Error(ruta)),
    )

    render(<ModalDeBusquedaDeArticulos {...propsDe()} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')

    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    await screen.findByText('Fanta 1.5L')

    fireEvent.change(input, { target: { value: 'zz' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    expect(await screen.findByText('No se pudo buscar artículos.')).toBeInTheDocument()
    expect(screen.queryByText('Fanta 1.5L')).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el guard `agregadoRef.current` al tope de `agregar()`. Evidencia de
   * mutación (mutation-proof-tests regla 2): con el guard comentado, este test falla
   * (`onAgregar` se llama 2 veces); restaurado, vuelve a verde — ver el reporte de la tarea.
   */
  it('dos clicks sincrónicos en "Agregar" de la misma fila llaman a onAgregar una sola vez', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=fa') ? Promise.resolve(paginaDe([articuloListadoFixture()])) : Promise.reject(new Error(ruta)),
    )
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/ofertas/resolver' ? Promise.resolve<ResultadoDeResolucion[]>([]) : Promise.reject(new Error(ruta)),
    )
    const onAgregar = vi.fn()

    render(<ModalDeBusquedaDeArticulos {...propsDe({ onAgregar })} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    const boton = await screen.findByRole('button', { name: 'Agregar' })

    fireEvent.click(boton)
    fireEvent.click(boton)

    expect(onAgregar).toHaveBeenCalledTimes(1)
    expect(onAgregar).toHaveBeenCalledWith({ idArticulo: 9, codigoInterno: 'A0009', nombre: 'Fanta 1.5L', codigoBarra: null }, 1)
  })

  it('Escape invoca a onCerrar', () => {
    const onCerrar = vi.fn()
    render(<ModalDeBusquedaDeArticulos {...propsDe({ onCerrar })} />)

    fireEvent.keyDown(document, { key: 'Escape' })

    expect(onCerrar).toHaveBeenCalledTimes(1)
  })
})
