import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Ofertas } from './Ofertas'
import type { ArticuloListado, GrupoListado, ListaPrecioListado, OfertaListado, PaginaDe } from '../api/tipos'

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

function grupoFixture(sobrescribir: Partial<GrupoListado> = {}): GrupoListado {
  return { id: 1, nombre: 'Bebidas', activo: true, idEmpresa: null, margen: null, ...sobrescribir }
}

function listaFixture(sobrescribir: Partial<ListaPrecioListado> = {}): ListaPrecioListado {
  return {
    id: 1,
    nombre: 'Lista mayorista',
    activo: true,
    idEmpresa: null,
    esDefault: false,
    modo: 'Fija',
    idListaBase: null,
    porcentaje: null,
    ...sobrescribir,
  }
}

function articuloFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    id: 1,
    codigoInterno: 'A0001',
    nombre: 'Coca Cola 1L',
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

function ofertaFixture(sobrescribir: Partial<OfertaListado> = {}): OfertaListado {
  return {
    id: 1,
    nombre: 'Oferta A',
    idEmpresa: null,
    idArticulo: 1,
    idGrupo: null,
    idCategoria: null,
    fechaDesde: null,
    fechaHasta: null,
    horaDesde: null,
    horaHasta: null,
    diasSemana: [],
    cantidadMinima: null,
    precioUnitario: null,
    porcentaje: 10,
    importeFijo: null,
    prioridad: 0,
    acumulable: false,
    activo: true,
    idsListas: [],
    ...sobrescribir,
  }
}

const grupoBebidas = grupoFixture({ id: 1, nombre: 'Bebidas' })
const listaMayorista = listaFixture({ id: 3, nombre: 'Lista mayorista' })
const articuloCoca = articuloFixture({ id: 1, nombre: 'Coca Cola 1L' })

function mockearApiGet() {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/ofertas') return Promise.resolve<OfertaListado[]>([])
    if (ruta === '/catalogos/grupos') return Promise.resolve([grupoBebidas])
    if (ruta === '/catalogos/categorias') return Promise.resolve([])
    if (ruta === '/empresas') return Promise.resolve([])
    if (ruta === '/catalogos/listas-precio') return Promise.resolve([listaMayorista])
    if (ruta.startsWith('/articulos?')) {
      const pagina: PaginaDe<ArticuloListado> = { items: [articuloCoca], total: 1, pagina: 1, tamanio: 20 }
      return Promise.resolve(pagina)
    }
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

describe('Ofertas — radio de alcance (visibleSi análogo)', () => {
  it('con alcance Artículo (default) muestra el buscador de artículo y no los selects de grupo/categoría', async () => {
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo' }))

    expect(screen.getByLabelText('Buscar artículo')).toBeInTheDocument()
    expect(screen.queryByLabelText('Grupo objetivo')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Categoría objetivo')).not.toBeInTheDocument()
  })

  it('cambiar el radio a Grupo hace aparecer el select de grupo y oculta el buscador de artículo', async () => {
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Grupo' }))

    expect(screen.getByLabelText('Grupo objetivo')).toBeInTheDocument()
    expect(screen.queryByPlaceholderText('Buscar por nombre o código interno…')).not.toBeInTheDocument()
  })

  it('cambiar el radio a Categoría hace aparecer el select de categoría', async () => {
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Categoría' }))

    expect(screen.getByLabelText('Categoría objetivo')).toBeInTheDocument()
  })
})

describe('Ofertas — radio de beneficio (visibleSi análogo)', () => {
  it('con beneficio Porcentaje (default) muestra el input de porcentaje y no los otros dos', async () => {
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo' }))

    expect(screen.getByLabelText('Porcentaje (%)')).toBeInTheDocument()
    expect(screen.queryByLabelText('Importe fijo por unidad ($)')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Precio unitario ($)')).not.toBeInTheDocument()
  })

  it('cambiar el radio a Importe fijo hace aparecer ese input y oculta el de porcentaje', async () => {
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Importe fijo por unidad' }))

    expect(screen.getByLabelText('Importe fijo por unidad ($)')).toBeInTheDocument()
    expect(screen.queryByLabelText('Porcentaje (%)')).not.toBeInTheDocument()
  })

  it('cambiar el radio a Precio unitario hace aparecer ese input', async () => {
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Precio unitario' }))

    expect(screen.getByLabelText('Precio unitario ($)')).toBeInTheDocument()
  })
})

describe('Ofertas — multi-select de listas', () => {
  it('sin marcar ninguna lista, el guardado envía idsListas vacío (aplica a todas)', async () => {
    apiPostMock.mockResolvedValue(ofertaFixture())
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo' }))

    await userEvent.type(screen.getByLabelText('Nombre'), 'Oferta nueva')
    await userEvent.type(screen.getByLabelText('Porcentaje (%)'), '10')

    // Alcance por defecto es Artículo: se busca y selecciona uno para poder enviar el form.
    await userEvent.type(screen.getByPlaceholderText('Buscar por nombre o código interno…'), 'coca')
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    await userEvent.selectOptions(await screen.findByRole('listbox'), '1')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(apiPostMock).toHaveBeenCalledWith('/ofertas', expect.objectContaining({ idsListas: [] }))
  })

  it('marcar una lista la incluye en idsListas al guardar', async () => {
    apiPostMock.mockResolvedValue(ofertaFixture())
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo' }))

    await userEvent.type(screen.getByLabelText('Nombre'), 'Oferta nueva')
    await userEvent.type(screen.getByLabelText('Porcentaje (%)'), '10')
    await userEvent.click(screen.getByLabelText('Lista mayorista'))

    await userEvent.type(screen.getByPlaceholderText('Buscar por nombre o código interno…'), 'coca')
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    await userEvent.selectOptions(await screen.findByRole('listbox'), '1')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(apiPostMock).toHaveBeenCalledWith('/ofertas', expect.objectContaining({ idsListas: [3] }))
  })
})

describe('Ofertas — ventana deshabilitada durante el guardado (react-async-state rule 5/9)', () => {
  it('mientras el POST está en vuelo, el campo nombre queda deshabilitado', async () => {
    let resolverCreacion: (valor: OfertaListado) => void = () => {}
    apiPostMock.mockImplementation(
      () =>
        new Promise<OfertaListado>((resolve) => {
          resolverCreacion = resolve
        }),
    )
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo' }))

    await userEvent.type(screen.getByLabelText('Nombre'), 'Oferta nueva')
    await userEvent.type(screen.getByLabelText('Porcentaje (%)'), '10')
    await userEvent.type(screen.getByPlaceholderText('Buscar por nombre o código interno…'), 'coca')
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    await userEvent.selectOptions(await screen.findByRole('listbox'), '1')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(screen.getByLabelText('Nombre')).toBeDisabled()

    resolverCreacion(ofertaFixture())
    await screen.findByText('Oferta "Oferta nueva" creada.')
  })

  it('mientras el POST está en vuelo, Editar/Nuevo/Baja no disparan fetch ni pisan el formulario en guardado', async () => {
    const ofertaExistente = ofertaFixture({ id: 2, nombre: 'Oferta existente' })
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas') return Promise.resolve<OfertaListado[]>([ofertaExistente])
      if (ruta === '/catalogos/grupos') return Promise.resolve([grupoBebidas])
      if (ruta === '/catalogos/categorias') return Promise.resolve([])
      if (ruta === '/empresas') return Promise.resolve([])
      if (ruta === '/catalogos/listas-precio') return Promise.resolve([listaMayorista])
      if (ruta.startsWith('/articulos?')) {
        const pagina: PaginaDe<ArticuloListado> = { items: [articuloCoca], total: 1, pagina: 1, tamanio: 20 }
        return Promise.resolve(pagina)
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    let resolverCreacion: (valor: OfertaListado) => void = () => {}
    apiPostMock.mockImplementation(
      () =>
        new Promise<OfertaListado>((resolve) => {
          resolverCreacion = resolve
        }),
    )
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)

    render(<Ofertas />)
    await screen.findByText('Oferta existente')

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo' }))
    await userEvent.type(screen.getByLabelText('Nombre'), 'Oferta nueva')
    await userEvent.type(screen.getByLabelText('Porcentaje (%)'), '10')
    await userEvent.type(screen.getByPlaceholderText('Buscar por nombre o código interno…'), 'coca')
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    await userEvent.selectOptions(await screen.findByRole('listbox'), '1')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    apiGetMock.mockClear()

    // Con el guardado en vuelo, intentamos las tres acciones que podrían pisar la edición en curso.
    await userEvent.click(screen.getByRole('button', { name: 'Editar' }))
    await userEvent.click(screen.getByRole('button', { name: 'Nuevo' }))
    await userEvent.click(screen.getByRole('button', { name: 'Baja' }))

    expect(apiGetMock).not.toHaveBeenCalledWith(expect.stringMatching(/^\/ofertas\/\d+$/))
    expect(confirmSpy).not.toHaveBeenCalled()
    expect(screen.getByText('Nueva oferta')).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Oferta nueva')
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPutMock).not.toHaveBeenCalled()
    expect(apiDeleteMock).not.toHaveBeenCalled()

    resolverCreacion(ofertaFixture())
    await screen.findByText('Oferta "Oferta nueva" creada.')

    confirmSpy.mockRestore()
  })
})
