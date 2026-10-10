import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Ofertas } from './Ofertas'
import type { ArticuloListado, FamiliaListado, GrupoListado, ListaPrecioListado, OfertaListado, PaginaDe } from '../api/tipos'

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

function familiaFixture(sobrescribir: Partial<FamiliaListado> = {}): FamiliaListado {
  return { id: 7, nombre: 'Sabores', activo: true, cantidadArticulos: 3, ...sobrescribir }
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
    costoListaPor: 'Unidad',
    costoListaIncluyeIva: true,
    descuentoProveedor: null,
    costoNominal: null,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    controlaLote: false,
    acumulaEnVenta: true,
    idFamilia: null,
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
    idFamilia: null,
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

const familiaSabores = familiaFixture({ id: 7, nombre: 'Sabores' })

type EscenarioDeOfertas = {
  ofertas?: OfertaListado[]
  /** `null`: el listado de familias falla. */
  familias?: FamiliaListado[] | null
  /** Detalle por id (`GET /api/ofertas/{id}`). */
  detalles?: Record<number, OfertaListado>
}

function mockearApiGet({ ofertas = [], familias = [familiaSabores], detalles = {} }: EscenarioDeOfertas = {}) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/ofertas') return Promise.resolve<OfertaListado[]>(ofertas)
    if (ruta.startsWith('/ofertas/')) return Promise.resolve(detalles[Number(ruta.split('/')[2])])
    if (ruta === '/familias') return familias === null ? Promise.reject(new Error('sin red')) : Promise.resolve(familias)
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
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    expect(screen.getByLabelText('Buscar artículo')).toBeInTheDocument()
    expect(screen.queryByLabelText('Grupo objetivo')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Categoría objetivo')).not.toBeInTheDocument()
  })

  it('cambiar el radio a Grupo hace aparecer el select de grupo y oculta el buscador de artículo', async () => {
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Grupo' }))

    expect(screen.getByLabelText('Grupo objetivo')).toBeInTheDocument()
    expect(screen.queryByPlaceholderText('Buscar por nombre o código interno…')).not.toBeInTheDocument()
  })

  it('cambiar el radio a Categoría hace aparecer el select de categoría', async () => {
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Categoría' }))

    expect(screen.getByLabelText('Categoría objetivo')).toBeInTheDocument()
  })
})

describe('Ofertas — alcance por familia', () => {
  async function abrirNuevaOferta() {
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
    await userEvent.type(screen.getByLabelText('Nombre'), 'Oferta de familia')
    await userEvent.type(screen.getByLabelText('Porcentaje (%)'), '10')
  }

  /** El select existe desde que se elige el radio, pero sus opciones llegan con la carga: se espera a la opción, que es el dato. */
  async function elegirFamilia(nombre: string) {
    await userEvent.click(screen.getByRole('radio', { name: 'Familia' }))
    await screen.findByRole('option', { name: nombre })
    await userEvent.selectOptions(screen.getByLabelText('Familia objetivo'), nombre)
  }

  it('cambiar el radio a Familia hace aparecer el select de familia y oculta los otros alcances', async () => {
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Familia' }))

    expect(screen.getByLabelText('Familia objetivo')).toBeInTheDocument()
    expect(screen.queryByLabelText('Grupo objetivo')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Categoría objetivo')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Buscar artículo')).not.toBeInTheDocument()
  })

  it('crea una oferta de familia: manda idFamilia y deja artículo, grupo y categoría en null', async () => {
    apiPostMock.mockResolvedValue(ofertaFixture({ id: 5, idArticulo: null, idFamilia: 7 }))
    await abrirNuevaOferta()
    await elegirFamilia('Sabores')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Oferta "Oferta de familia" creada.')
    expect(apiPostMock).toHaveBeenCalledExactlyOnceWith(
      '/ofertas',
      expect.objectContaining({ idFamilia: 7, idArticulo: null, idGrupo: null, idCategoria: null, porcentaje: 10 }),
    )
  })

  it('sin familia elegida no guarda y pide elegirla', async () => {
    await abrirNuevaOferta()
    await userEvent.click(screen.getByRole('radio', { name: 'Familia' }))

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Elegí una familia para el alcance.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('cambiar de alcance después de elegir una familia no la manda: solo viaja la columna del radio vigente', async () => {
    apiPostMock.mockResolvedValue(ofertaFixture({ id: 5, idArticulo: null, idGrupo: 1 }))
    await abrirNuevaOferta()
    await elegirFamilia('Sabores')
    await userEvent.click(screen.getByRole('radio', { name: 'Grupo' }))
    await screen.findByRole('option', { name: 'Bebidas' })
    await userEvent.selectOptions(screen.getByLabelText('Grupo objetivo'), 'Bebidas')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Oferta "Oferta de familia" creada.')
    expect(apiPostMock).toHaveBeenCalledExactlyOnceWith(
      '/ofertas',
      expect.objectContaining({ idFamilia: null, idGrupo: 1, idArticulo: null, idCategoria: null }),
    )
  })

  it('rotula la familia inactiva y la vacía, y ofrece todas', async () => {
    mockearApiGet({
      familias: [
        familiaSabores,
        familiaFixture({ id: 8, nombre: 'Talles', activo: false }),
        familiaFixture({ id: 9, nombre: 'Colores', cantidadArticulos: 0 }),
      ],
    })
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
    await userEvent.click(screen.getByRole('radio', { name: 'Familia' }))

    await screen.findByRole('option', { name: 'Colores (sin artículos)' })

    const opciones = within(screen.getByLabelText('Familia objetivo')).getAllByRole('option')
    expect(opciones.map((o) => o.textContent)).toEqual(['Elegir…', 'Sabores', 'Talles (inactiva)', 'Colores (sin artículos)'])
  })

  it('si el listado de familias falla lo avisa y el resto de la pantalla sigue andando', async () => {
    mockearApiGet({ ofertas: [ofertaFixture({ id: 2, nombre: 'Oferta existente' })], familias: null })
    render(<Ofertas />)

    expect(
      await screen.findByText(/No se pudieron cargar las familias: el alcance por familia no va a estar disponible hasta recargar la página\./),
    ).toBeInTheDocument()
    expect(screen.getByText('Oferta existente')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeEnabled()
  })

  it('el listado nombra la familia de la oferta, o su id si la familia no está cargada', async () => {
    mockearApiGet({
      ofertas: [
        ofertaFixture({ id: 2, nombre: 'Con nombre', idArticulo: null, idFamilia: 7 }),
        ofertaFixture({ id: 3, nombre: 'Sin cargar', idArticulo: null, idFamilia: 99 }),
      ],
    })
    render(<Ofertas />)

    const filaConNombre = (await screen.findByText('Con nombre')).closest('tr')!
    await waitFor(() => expect(within(filaConNombre).getByText('Familia: Sabores')).toBeInTheDocument())
    const filaSinCargar = screen.getByText('Sin cargar').closest('tr')!
    expect(within(filaSinCargar).getByText('Familia: 99')).toBeInTheDocument()
  })

  it('editar una oferta de familia abre el radio Familia con su familia elegida y el PUT reenvía idFamilia', async () => {
    const oferta = ofertaFixture({ id: 2, nombre: 'Oferta existente', idArticulo: null, idFamilia: 7 })
    mockearApiGet({ ofertas: [oferta], detalles: { 2: oferta } })
    apiPutMock.mockResolvedValue(oferta)
    render(<Ofertas />)
    await screen.findByText('Oferta existente')
    await screen.findByText('Familia: Sabores')

    await userEvent.click(screen.getByRole('button', { name: 'Editar' }))

    expect(await screen.findByRole('radio', { name: 'Familia' })).toBeChecked()
    expect(screen.getByLabelText('Familia objetivo')).toHaveValue('7')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Oferta "Oferta existente" actualizada.')
    expect(apiPutMock).toHaveBeenCalledExactlyOnceWith(
      '/ofertas/2',
      expect.objectContaining({ idFamilia: 7, idArticulo: null, idGrupo: null, idCategoria: null }),
    )
  })

  /** Con el listado de familias caído el select no tiene la opción de la oferta, pero el estado conserva `idFamilia`: guardar no le
   * quita el alcance a la oferta. */
  it('editar una oferta de familia con el listado de familias caído no le quita la familia al guardar', async () => {
    const oferta = ofertaFixture({ id: 2, nombre: 'Oferta existente', idArticulo: null, idFamilia: 7 })
    mockearApiGet({ ofertas: [oferta], detalles: { 2: oferta }, familias: null })
    apiPutMock.mockResolvedValue(oferta)
    render(<Ofertas />)
    await screen.findByText('Oferta existente')
    await screen.findByText(/No se pudieron cargar las familias/)

    await userEvent.click(screen.getByRole('button', { name: 'Editar' }))
    expect(await screen.findByRole('radio', { name: 'Familia' })).toBeChecked()
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Oferta "Oferta existente" actualizada.')
    expect(apiPutMock).toHaveBeenCalledExactlyOnceWith('/ofertas/2', expect.objectContaining({ idFamilia: 7 }))
  })
})

describe('Ofertas — radio de beneficio (visibleSi análogo)', () => {
  it('con beneficio Porcentaje (default) muestra el input de porcentaje y no los otros dos', async () => {
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    expect(screen.getByLabelText('Porcentaje (%)')).toBeInTheDocument()
    expect(screen.queryByLabelText('Importe fijo por unidad ($)')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Precio unitario ($)')).not.toBeInTheDocument()
  })

  it('cambiar el radio a Importe fijo hace aparecer ese input y oculta el de porcentaje', async () => {
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Importe fijo por unidad' }))

    expect(screen.getByLabelText('Importe fijo por unidad ($)')).toBeInTheDocument()
    expect(screen.queryByLabelText('Porcentaje (%)')).not.toBeInTheDocument()
  })

  it('cambiar el radio a Precio unitario hace aparecer ese input', async () => {
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Precio unitario' }))

    expect(screen.getByLabelText('Precio unitario ($)')).toBeInTheDocument()
  })
})

describe('Ofertas — multi-select de listas', () => {
  it('sin marcar ninguna lista, el guardado envía idsListas vacío (aplica a todas)', async () => {
    apiPostMock.mockResolvedValue(ofertaFixture())
    render(<Ofertas />)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

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
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

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
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

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
      if (ruta === '/familias') return Promise.resolve([familiaSabores])
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

    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
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
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await userEvent.click(screen.getByRole('button', { name: 'Eliminar' }))

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
