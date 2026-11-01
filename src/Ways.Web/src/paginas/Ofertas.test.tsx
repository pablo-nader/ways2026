import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Ofertas } from './Ofertas'
import { Toaster } from '../toasts/Toaster'
import { ErrorApi } from '../api/cliente'
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
    disponibleOnline: true,
    disponibleVenta: true,
    tieneImagen: false,
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
    soloMultiplos: false,
    precioUnitario: null,
    porcentaje: 10,
    importeFijo: null,
    prioridad: 0,
    acumulable: false,
    activo: true,
    disponibleOnline: true,
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
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    expect(screen.getByLabelText('Buscar artículo')).toBeInTheDocument()
    expect(screen.queryByLabelText('Grupo objetivo')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Categoría objetivo')).not.toBeInTheDocument()
  })

  it('cambiar el radio a Grupo hace aparecer el select de grupo y oculta el buscador de artículo', async () => {
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Grupo' }))

    expect(screen.getByLabelText('Grupo objetivo')).toBeInTheDocument()
    expect(screen.queryByPlaceholderText('Buscar por nombre o código interno…')).not.toBeInTheDocument()
  })

  it('cambiar el radio a Categoría hace aparecer el select de categoría', async () => {
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Categoría' }))

    expect(screen.getByLabelText('Categoría objetivo')).toBeInTheDocument()
  })
})

describe('Ofertas — alcance por familia', () => {
  async function abrirNuevaOferta() {
    render(<><Ofertas /><Toaster /></>)
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
    render(<><Ofertas /><Toaster /></>)
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
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
    await userEvent.click(screen.getByRole('radio', { name: 'Familia' }))

    await screen.findByRole('option', { name: 'Colores (sin artículos)' })

    const opciones = within(screen.getByLabelText('Familia objetivo')).getAllByRole('option')
    expect(opciones.map((o) => o.textContent)).toEqual(['Elegir…', 'Sabores', 'Talles (inactiva)', 'Colores (sin artículos)'])
  })

  it('si el listado de familias falla lo avisa y el resto de la pantalla sigue andando', async () => {
    mockearApiGet({ ofertas: [ofertaFixture({ id: 2, nombre: 'Oferta existente' })], familias: null })
    render(<><Ofertas /><Toaster /></>)

    expect(
      await screen.findByText(/No se pudieron cargar las familias: no hay familias para elegir hasta recargar la página\. Una oferta que ya apunta a una familia se muestra por su número y se puede guardar sin cambiarla\./),
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
    render(<><Ofertas /><Toaster /></>)

    const filaConNombre = (await screen.findByText('Con nombre')).closest('tr')!
    await waitFor(() => expect(within(filaConNombre).getByText('Familia: Sabores')).toBeInTheDocument())
    const filaSinCargar = screen.getByText('Sin cargar').closest('tr')!
    expect(within(filaSinCargar).getByText('Familia: 99')).toBeInTheDocument()
  })

  it('el listado nombra el artículo de la oferta, o su id si el servidor no trae el nombre', async () => {
    mockearApiGet({
      ofertas: [
        ofertaFixture({ id: 2, nombre: 'Con nombre', idArticulo: 357, nombreArticulo: 'Alfajor maizena' }),
        ofertaFixture({ id: 3, nombre: 'Sin nombre', idArticulo: 358, nombreArticulo: null }),
      ],
    })
    render(<><Ofertas /><Toaster /></>)

    const filaConNombre = (await screen.findByText('Con nombre')).closest('tr')!
    expect(within(filaConNombre).getByText('Artículo: Alfajor maizena')).toBeInTheDocument()
    const filaSinNombre = screen.getByText('Sin nombre').closest('tr')!
    expect(within(filaSinNombre).getByText('Artículo #358')).toBeInTheDocument()
  })

  it('editar una oferta de familia abre el radio Familia con su familia elegida y el PUT reenvía idFamilia', async () => {
    const oferta = ofertaFixture({ id: 2, nombre: 'Oferta existente', idArticulo: null, idFamilia: 7 })
    mockearApiGet({ ofertas: [oferta], detalles: { 2: oferta } })
    apiPutMock.mockResolvedValue(oferta)
    render(<><Ofertas /><Toaster /></>)
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

  /** Con el listado de familias caído la oferta no tiene su familia entre las opciones: el select muestra una opción de respaldo
   * con el id, seleccionada, y el estado conserva `idFamilia`, así que guardar no le quita el alcance a la oferta. */
  it('editar una oferta de familia con el listado de familias caído no le quita la familia al guardar', async () => {
    const oferta = ofertaFixture({ id: 2, nombre: 'Oferta existente', idArticulo: null, idFamilia: 7 })
    mockearApiGet({ ofertas: [oferta], detalles: { 2: oferta }, familias: null })
    apiPutMock.mockResolvedValue(oferta)
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Oferta existente')
    await screen.findByText(/No se pudieron cargar las familias/)

    await userEvent.click(screen.getByRole('button', { name: 'Editar' }))
    expect(await screen.findByRole('radio', { name: 'Familia' })).toBeChecked()
    const select = screen.getByLabelText('Familia objetivo')
    expect(select).toHaveValue('7')
    expect(within(select).getByRole('option', { name: 'Familia #7 (no disponible)' })).toHaveProperty('selected', true)
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Oferta "Oferta existente" actualizada.')
    expect(apiPutMock).toHaveBeenCalledExactlyOnceWith('/ofertas/2', expect.objectContaining({ idFamilia: 7 }))
  })
})

describe('Ofertas — radio de beneficio (visibleSi análogo)', () => {
  it('con beneficio Porcentaje (default) muestra el input de porcentaje y no los otros dos', async () => {
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    expect(screen.getByLabelText('Porcentaje (%)')).toBeInTheDocument()
    expect(screen.queryByLabelText('Importe fijo por unidad ($)')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Precio unitario ($)')).not.toBeInTheDocument()
  })

  it('cambiar el radio a Importe fijo hace aparecer ese input y oculta el de porcentaje', async () => {
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Importe fijo por unidad' }))

    expect(screen.getByLabelText('Importe fijo por unidad ($)')).toBeInTheDocument()
    expect(screen.queryByLabelText('Porcentaje (%)')).not.toBeInTheDocument()
  })

  it('cambiar el radio a Precio unitario hace aparecer ese input', async () => {
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Precio unitario' }))

    expect(screen.getByLabelText('Precio unitario ($)')).toBeInTheDocument()
  })
})

describe('Ofertas — multi-select de listas', () => {
  it('sin marcar ninguna lista, el guardado envía idsListas vacío (aplica a todas)', async () => {
    apiPostMock.mockResolvedValue(ofertaFixture())
    render(<><Ofertas /><Toaster /></>)
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
    render(<><Ofertas /><Toaster /></>)
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

describe('Ofertas — solo múltiplos de la cantidad mínima', () => {
  it('la casilla está deshabilitada sin cantidad mínima, se habilita con ella y se desmarca al vaciarla', async () => {
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
    const casilla = screen.getByLabelText('Solo múltiplos de la cantidad')

    expect(casilla).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Cantidad mínima'), '2')
    expect(casilla).toBeEnabled()
    await userEvent.click(casilla)
    expect(casilla).toBeChecked()

    await userEvent.clear(screen.getByLabelText('Cantidad mínima'))
    expect(casilla).toBeDisabled()
    expect(casilla).not.toBeChecked()
  })

  it('al guardar manda soloMultiplos true junto con la cantidad mínima', async () => {
    apiPostMock.mockResolvedValue(ofertaFixture())
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await userEvent.type(screen.getByLabelText('Nombre'), 'Oferta nueva')
    await userEvent.type(screen.getByLabelText('Porcentaje (%)'), '10')
    await userEvent.type(screen.getByLabelText('Cantidad mínima'), '2')
    await userEvent.click(screen.getByLabelText('Solo múltiplos de la cantidad'))
    await userEvent.type(screen.getByPlaceholderText('Buscar por nombre o código interno…'), 'coca')
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    await userEvent.selectOptions(await screen.findByRole('listbox'), '1')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(apiPostMock).toHaveBeenCalledWith('/ofertas', expect.objectContaining({ cantidadMinima: 2, soloMultiplos: true }))
  })

  it('sin tocar la casilla manda soloMultiplos false', async () => {
    apiPostMock.mockResolvedValue(ofertaFixture())
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await userEvent.type(screen.getByLabelText('Nombre'), 'Oferta nueva')
    await userEvent.type(screen.getByLabelText('Porcentaje (%)'), '10')
    await userEvent.type(screen.getByPlaceholderText('Buscar por nombre o código interno…'), 'coca')
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    await userEvent.selectOptions(await screen.findByRole('listbox'), '1')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(apiPostMock).toHaveBeenCalledWith('/ofertas', expect.objectContaining({ soloMultiplos: false }))
  })

  it('editar una oferta con soloMultiplos abre la casilla marcada y el PUT lo reenvía', async () => {
    const oferta = ofertaFixture({ id: 2, nombre: 'Oferta existente', cantidadMinima: 2, soloMultiplos: true })
    mockearApiGet({ ofertas: [oferta], detalles: { 2: oferta } })
    apiPutMock.mockResolvedValue(oferta)
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Oferta existente')

    await userEvent.click(screen.getByRole('button', { name: 'Editar' }))

    await waitFor(() => expect(screen.getByLabelText('Cantidad mínima')).toHaveValue('2'))
    expect(screen.getByLabelText('Solo múltiplos de la cantidad')).toBeChecked()
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Oferta "Oferta existente" actualizada.')
    expect(apiPutMock).toHaveBeenCalledExactlyOnceWith('/ofertas/2', expect.objectContaining({ cantidadMinima: 2, soloMultiplos: true }))
  })

  it('al vaciar la cantidad mínima en la edición la casilla se desmarca y el PUT manda soloMultiplos false', async () => {
    const oferta = ofertaFixture({ id: 2, nombre: 'Oferta existente', cantidadMinima: 2, soloMultiplos: true })
    mockearApiGet({ ofertas: [oferta], detalles: { 2: oferta } })
    apiPutMock.mockResolvedValue(oferta)
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Oferta existente')
    await userEvent.click(screen.getByRole('button', { name: 'Editar' }))
    await waitFor(() => expect(screen.getByLabelText('Cantidad mínima')).toHaveValue('2'))

    await userEvent.clear(screen.getByLabelText('Cantidad mínima'))

    const casilla = screen.getByLabelText('Solo múltiplos de la cantidad')
    expect(casilla).toBeDisabled()
    expect(casilla).not.toBeChecked()
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Oferta "Oferta existente" actualizada.')
    expect(apiPutMock).toHaveBeenCalledExactlyOnceWith('/ofertas/2', expect.objectContaining({ cantidadMinima: null, soloMultiplos: false }))
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
    render(<><Ofertas /><Toaster /></>)
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
    render(<><Ofertas /><Toaster /></>)
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
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByText('Nueva oferta')).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Oferta nueva')
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPutMock).not.toHaveBeenCalled()
    expect(apiDeleteMock).not.toHaveBeenCalled()

    resolverCreacion(ofertaFixture())
    await screen.findByText('Oferta "Oferta nueva" creada.')
  })
})

describe('Ofertas — la baja se confirma en un modal', () => {
  async function pedirLaBaja() {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    mockearApiGet({ ofertas: [ofertaFixture({ id: 2, nombre: 'Oferta existente' })] })
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Oferta existente')
    const eliminar = screen.getByRole('button', { name: 'Eliminar' })
    await userEvent.click(eliminar)
    return { confirmSpy, eliminar, confirmacion: await screen.findByRole('alertdialog') }
  }

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('abre una confirmación que nombra la oferta y no escribe hasta confirmar', async () => {
    const { confirmSpy, confirmacion } = await pedirLaBaja()

    expect(confirmacion).toHaveTextContent('¿Dar de baja la oferta "Oferta existente"?')
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(confirmSpy).not.toHaveBeenCalled()
  })

  it('cancelar cierra la confirmación sin dar de baja y devuelve el foco al botón', async () => {
    const { eliminar, confirmacion } = await pedirLaBaja()

    await userEvent.click(within(confirmacion).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(eliminar).toHaveFocus()
  })

  it('un doble clic sincrónico en Confirmar baja emite un solo DELETE y la puerta queda inerte mientras escribe', async () => {
    const { confirmacion } = await pedirLaBaja()
    let resolver: () => void = () => {}
    apiDeleteMock.mockReturnValue(new Promise<void>((r) => (resolver = r)))
    const confirmar = within(confirmacion).getByRole('button', { name: 'Confirmar baja' })

    act(() => {
      confirmar.click()
      confirmar.click()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    expect(apiDeleteMock).toHaveBeenCalledWith('/ofertas/2')
    await waitFor(() => expect(within(confirmacion).getByRole('button', { name: 'Dando de baja…' })).toBeDisabled())
    expect(within(confirmacion).getByRole('button', { name: 'Cancelar' })).toBeDisabled()

    await act(async () => resolver())
    await screen.findByText('Oferta "Oferta existente" dada de baja.')
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `invalidarEdicionEnCurso()` de `pedirBaja`. Sin él, la edición que llega tarde monta su modal encima de la confirmación. */
  it('abrir la baja descarta una apertura de edición todavía en vuelo: su modal no se monta encima de la confirmación', async () => {
    let resolverDetalle: (o: OfertaListado) => void = () => {}
    const existente = ofertaFixture({ id: 2, nombre: 'Oferta existente' })
    mockearApiGet({ ofertas: [existente] })
    const base = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/ofertas/2' ? new Promise<OfertaListado>((r) => (resolverDetalle = r)) : base(ruta),
    )
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Oferta existente')
    await userEvent.click(screen.getByRole('button', { name: 'Editar' }))
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/ofertas/2'))

    await userEvent.click(screen.getByRole('button', { name: 'Eliminar' }))
    await screen.findByRole('alertdialog')
    await act(async () => resolverDetalle(existente))

    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('una baja rechazada muestra el motivo dentro de la confirmación, que sigue abierta, sin aviso de éxito', async () => {
    const { confirmacion } = await pedirLaBaja()
    apiDeleteMock.mockRejectedValue(new ErrorApi(409, 'oferta_en_uso', 'La oferta está en uso.'))

    await userEvent.click(within(confirmacion).getByRole('button', { name: 'Confirmar baja' }))

    expect(await within(confirmacion).findByRole('alert')).toHaveTextContent('La oferta está en uso.')
    expect(screen.getAllByText('La oferta está en uso.')).toHaveLength(1)
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
    expect(screen.queryByText('Oferta "Oferta existente" dada de baja.')).not.toBeInTheDocument()
  })
})

describe('Ofertas — alta y edición en modal', () => {
  async function completarOfertaDeGrupo() {
    await userEvent.type(screen.getByLabelText('Nombre'), 'Oferta nueva')
    await userEvent.type(screen.getByLabelText('Porcentaje (%)'), '10')
    await userEvent.click(screen.getByRole('radio', { name: 'Grupo' }))
    await screen.findByRole('option', { name: 'Bebidas' })
    await userEvent.selectOptions(screen.getByLabelText('Grupo objetivo'), 'Bebidas')
  }

  it('al entrar muestra solo la grilla: sin formulario ni diálogo', async () => {
    mockearApiGet({ ofertas: [ofertaFixture({ id: 2, nombre: 'Oferta existente' })] })
    render(<><Ofertas /><Toaster /></>)

    expect(await screen.findByText('Oferta existente')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Nombre')).not.toBeInTheDocument()
  })

  it('Agregar abre el modal "Nueva oferta" con el formulario vacío', async () => {
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    const dialogo = screen.getByRole('dialog', { name: 'Nueva oferta' })
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('')
  })

  it('Editar abre el modal "Editar oferta" con la oferta cargada', async () => {
    const oferta = ofertaFixture({ id: 2, nombre: 'Oferta existente', idArticulo: null, idGrupo: 1 })
    mockearApiGet({ ofertas: [oferta], detalles: { 2: oferta } })
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Oferta existente')

    await userEvent.click(screen.getByRole('button', { name: 'Editar' }))

    const dialogo = await screen.findByRole('dialog', { name: 'Editar oferta' })
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Oferta existente')
    expect(within(dialogo).getByRole('radio', { name: 'Grupo' })).toBeChecked()
  })

  it('Cancelar cierra el modal sin escribir nada', async () => {
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('un guardado exitoso cierra el modal, avisa en la pantalla y refresca el listado', async () => {
    apiPostMock.mockResolvedValue(ofertaFixture({ id: 5, nombre: 'Oferta nueva', idArticulo: null, idGrupo: 1 }))
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
    await completarOfertaDeGrupo()
    const listadosAntes = apiGetMock.mock.calls.filter((c) => c[0] === '/ofertas').length

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Oferta "Oferta nueva" creada.')
    expect(document.body.querySelector('.alert-success')).toBeNull()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiGetMock.mock.calls.filter((c) => c[0] === '/ofertas').length).toBe(listadosAntes + 1)
  })

  it('un error de validación se muestra dentro del modal, que sigue abierto', async () => {
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
    await userEvent.type(screen.getByLabelText('Nombre'), 'Oferta nueva')
    await userEvent.type(screen.getByLabelText('Porcentaje (%)'), '10')

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Guardar' }))

    const dialogo = screen.getByRole('dialog', { name: 'Nueva oferta' })
    expect(within(dialogo).getByText('Elegí un artículo para el alcance.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un guardado que falla muestra el error dentro del modal, lo deja abierto y conserva lo escrito', async () => {
    apiPostMock.mockRejectedValue(new Error('boom'))
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
    await completarOfertaDeGrupo()

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Guardar' }))

    const dialogo = screen.getByRole('dialog', { name: 'Nueva oferta' })
    expect(await within(dialogo).findByText('No se pudo guardar la oferta.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Oferta nueva')
    expect(within(dialogo).getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  it('si el guardado salió bien pero el refresco falla, cierra el modal y lo avisa con un toast', async () => {
    apiPostMock.mockResolvedValue(ofertaFixture({ id: 5, nombre: 'Oferta nueva', idArticulo: null, idGrupo: 1 }))
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
    await completarOfertaDeGrupo()
    const listadoOk = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/ofertas' ? Promise.reject(new Error('sin red')) : listadoOk(ruta),
    )

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Guardar' }))

    const advertencia = await screen.findByText('La oferta se guardó, pero no se pudo actualizar el listado. Volvé a entrar para verla.')
    expect(advertencia.closest('[role="status"]')).not.toBeNull()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('un error al abrir la oferta se muestra en la pantalla y no abre el modal', async () => {
    const oferta = ofertaFixture({ id: 2, nombre: 'Oferta existente' })
    mockearApiGet({ ofertas: [oferta] })
    const base = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/ofertas/2' ? Promise.reject(new Error('boom')) : base(ruta),
    )
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Oferta existente')

    await userEvent.click(screen.getByRole('button', { name: 'Editar' }))

    expect(await screen.findByText('No se pudo abrir la oferta.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

describe('Ofertas — error y foco del modal', () => {
  async function completarOfertaDeGrupo() {
    await userEvent.type(screen.getByLabelText('Nombre'), 'Oferta nueva')
    await userEvent.type(screen.getByLabelText('Porcentaje (%)'), '10')
    await userEvent.click(screen.getByRole('radio', { name: 'Grupo' }))
    await screen.findByRole('option', { name: 'Bebidas' })
    await userEvent.selectOptions(screen.getByLabelText('Grupo objetivo'), 'Bebidas')
  }

  it('un guardado rechazado expone el error como alerta adentro del diálogo', async () => {
    apiPostMock.mockRejectedValue(new Error('boom'))
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
    await completarOfertaDeGrupo()

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Guardar' }))

    const alerta = await within(screen.getByRole('dialog')).findByRole('alert')
    expect(alerta).toHaveTextContent('No se pudo guardar la oferta.')
  })

  it('dos validaciones idénticas seguidas traen la alerta a la vista cada una', async () => {
    const scrollIntoViewMock = vi.fn()
    Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', {
      configurable: true,
      writable: true,
      value: scrollIntoViewMock,
    })
    try {
      render(<><Ofertas /><Toaster /></>)
      await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
      await userEvent.type(screen.getByLabelText('Nombre'), 'Oferta nueva')
      await userEvent.type(screen.getByLabelText('Porcentaje (%)'), '10')
      const guardar = within(screen.getByRole('dialog')).getByRole('button', { name: 'Guardar' })

      await userEvent.click(guardar)
      const alerta = await within(screen.getByRole('dialog')).findByRole('alert')
      expect(alerta).toHaveTextContent('Elegí un artículo para el alcance.')
      expect(scrollIntoViewMock).toHaveBeenCalledTimes(1)

      await userEvent.click(guardar)
      expect(scrollIntoViewMock).toHaveBeenCalledTimes(2)
    } finally {
      Reflect.deleteProperty(HTMLElement.prototype, 'scrollIntoView')
    }
  })

  it('al abrir, el foco queda en el primer campo y no en la ×', async () => {
    render(<><Ofertas /><Toaster /></>)

    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await waitFor(() => expect(within(screen.getByRole('dialog')).getByLabelText('Nombre')).toHaveFocus())
  })

  it('con el refresco en vuelo tras guardar, el foco queda en el contenedor de la pantalla y no en <body>', async () => {
    const respuestaBase = apiGetMock.getMockImplementation() as (ruta: string) => Promise<unknown>
    let liberarRefresco: (ofertas: OfertaListado[]) => void = () => {}
    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta !== '/ofertas') return respuestaBase(ruta)
      cargas += 1
      return cargas === 1
        ? respuestaBase(ruta)
        : new Promise<OfertaListado[]>((resolver) => {
            liberarRefresco = resolver
          })
    })
    apiPostMock.mockResolvedValue(ofertaFixture({ id: 5, nombre: 'Oferta nueva', idArticulo: null, idGrupo: 1 }))
    const { container } = render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
    await completarOfertaDeGrupo()

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Oferta "Oferta nueva" creada.')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeDisabled()
    expect(document.activeElement).not.toBe(document.body)
    expect(document.activeElement).toBe(container.firstElementChild)

    await act(async () => liberarRefresco([]))
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeEnabled()
    expect(document.activeElement).toBe(container.firstElementChild)
  })

  it('cancelar devuelve el foco al botón que abrió el modal', async () => {
    render(<><Ofertas /><Toaster /></>)

    const abrir = await screen.findByRole('button', { name: 'Agregar' })
    await userEvent.click(abrir)
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))

    expect(abrir).toHaveFocus()
  })
})

describe('Ofertas — disponible online', () => {
  async function completarOfertaDeGrupo() {
    await userEvent.type(screen.getByLabelText('Nombre'), 'Oferta nueva')
    await userEvent.type(screen.getByLabelText('Porcentaje (%)'), '10')
    await userEvent.click(screen.getByRole('radio', { name: 'Grupo' }))
    await screen.findByRole('option', { name: 'Bebidas' })
    await userEvent.selectOptions(screen.getByLabelText('Grupo objetivo'), 'Bebidas')
  }

  /**
   * Cláusula bajo prueba: una oferta nueva nace disponible online y el alta lo manda. Mutación: arrancar el formulario
   * con `disponibleOnline: false` hace fallar las dos aserciones.
   */
  it('una oferta nueva arranca con "Disponible online" marcado y el alta manda disponibleOnline: true', async () => {
    apiPostMock.mockResolvedValue(ofertaFixture({ id: 5, idArticulo: null, idGrupo: 1 }))
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
    expect(screen.getByRole('checkbox', { name: 'Disponible online' })).toBeChecked()
    await completarOfertaDeGrupo()

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Oferta "Oferta nueva" creada.')
    expect(apiPostMock).toHaveBeenCalledExactlyOnceWith('/ofertas', expect.objectContaining({ disponibleOnline: true, activo: true }))
  })

  it('desmarcarlo en el alta manda disponibleOnline: false sin tocar "Activa"', async () => {
    apiPostMock.mockResolvedValue(ofertaFixture({ id: 5, idArticulo: null, idGrupo: 1 }))
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
    await completarOfertaDeGrupo()
    await userEvent.click(screen.getByRole('checkbox', { name: 'Disponible online' }))

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Oferta "Oferta nueva" creada.')
    expect(apiPostMock).toHaveBeenCalledExactlyOnceWith('/ofertas', expect.objectContaining({ disponibleOnline: false, activo: true }))
  })

  it('editar una oferta no disponible online la abre desmarcada y el PUT reenvía el valor', async () => {
    const oferta = ofertaFixture({ id: 2, nombre: 'Oferta existente', idArticulo: null, idGrupo: 1, disponibleOnline: false })
    mockearApiGet({ ofertas: [oferta], detalles: { 2: oferta } })
    apiPutMock.mockResolvedValue(oferta)
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Oferta existente')
    await userEvent.click(screen.getByRole('button', { name: 'Editar' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Editar oferta' })
    expect(within(dialogo).getByRole('checkbox', { name: 'Disponible online' })).not.toBeChecked()

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Oferta "Oferta existente" actualizada.')
    expect(apiPutMock).toHaveBeenCalledExactlyOnceWith('/ofertas/2', expect.objectContaining({ disponibleOnline: false }))
  })

  it('en la edición, marcarlo manda disponibleOnline: true', async () => {
    const oferta = ofertaFixture({ id: 2, nombre: 'Oferta existente', idArticulo: null, idGrupo: 1, disponibleOnline: false })
    mockearApiGet({ ofertas: [oferta], detalles: { 2: oferta } })
    apiPutMock.mockResolvedValue(oferta)
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Oferta existente')
    await userEvent.click(screen.getByRole('button', { name: 'Editar' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Editar oferta' })
    await userEvent.click(within(dialogo).getByRole('checkbox', { name: 'Disponible online' }))

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Oferta "Oferta existente" actualizada.')
    expect(apiPutMock).toHaveBeenCalledExactlyOnceWith('/ofertas/2', expect.objectContaining({ disponibleOnline: true }))
  })

  /**
   * Cláusula bajo prueba: la columna "Online" dice Sí solo si la oferta está disponible online Y activa. Mutación: quitar
   * `&& o.activo` hace fallar la aserción de la oferta inactiva.
   */
  it('la grilla muestra la columna "Online" con Sí solo si la oferta está disponible online y activa', async () => {
    mockearApiGet({
      ofertas: [
        ofertaFixture({ id: 1, nombre: 'En la tienda', disponibleOnline: true }),
        ofertaFixture({ id: 2, nombre: 'Solo en el local', disponibleOnline: false }),
        ofertaFixture({ id: 3, nombre: 'Dada de baja', disponibleOnline: true, activo: false }),
      ],
    })
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('En la tienda')
    const columnas = screen
      .getAllByRole('columnheader')
      .slice(0, 8)
      .map((c) => c.textContent?.replace('↕', ''))
    const posicion = columnas.indexOf('Online')
    expect(posicion).toBeGreaterThan(-1)

    const celdaDe = (nombre: string) => {
      const fila = screen.getByText(nombre).closest('tr')
      if (!fila) throw new Error('No se encontró la fila')

      return within(fila).getAllByRole('cell')[posicion]
    }

    expect(celdaDe('En la tienda')).toHaveTextContent('Sí')
    expect(celdaDe('Solo en el local')).toHaveTextContent('No')
    expect(celdaDe('Dada de baja')).toHaveTextContent('No')
  })

  it('un 400 disponible_online_requerido se explica dentro del modal, que sigue abierto', async () => {
    const rechazo = Object.assign(new (ErrorApi as unknown as new (mensaje: string) => Error)('texto del servidor'), {
      estado: 400,
      codigo: 'disponible_online_requerido',
    })
    apiPostMock.mockRejectedValue(rechazo)
    render(<><Ofertas /><Toaster /></>)
    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
    await completarOfertaDeGrupo()

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Guardar' }))

    const dialogo = screen.getByRole('dialog', { name: 'Nueva oferta' })
    expect(
      await within(dialogo).findByText('Falta indicar si la oferta está disponible online. Recargá la pantalla e intentá de nuevo.'),
    ).toBeInTheDocument()
  })
})

// Filtros por columna y orden por encabezado (grillas, slice S5b2).

const ofertaZeta = ofertaFixture({
  id: 1,
  nombre: 'Zeta oferta',
  nombreArticulo: 'Pancho',
  porcentaje: 15,
  prioridad: 100,
  acumulable: false,
  activo: true,
  disponibleOnline: true,
})
const ofertaAlfa = ofertaFixture({
  id: 2,
  nombre: 'Alfa oferta',
  idArticulo: null,
  idGrupo: 1,
  porcentaje: null,
  importeFijo: 900,
  prioridad: 9,
  acumulable: true,
  activo: false,
  disponibleOnline: true,
})
const ofertaMedio = ofertaFixture({
  id: 3,
  nombre: 'Medio oferta',
  idArticulo: null,
  idFamilia: 7,
  porcentaje: null,
  importeFijo: 1500,
  prioridad: 20,
  acumulable: true,
  activo: true,
  disponibleOnline: false,
})
/** El orden del servidor (Medio, Zeta, Alfa) no coincide con ningún orden por columna. */
const ENTREGADAS = [ofertaMedio, ofertaZeta, ofertaAlfa]

function columnaVisible(indice: number) {
  return screen
    .getAllByRole('row')
    .slice(2)
    .map((f) => within(f).getAllByRole('cell')[indice]?.textContent ?? '')
}

describe('Ofertas — filtros por columna y orden', () => {
  /** Mutación registrada: pasar `filtradas` sin ordenar a la tabla deja el orden del servidor y rompe las aserciones. */
  it('ordena cada columna por su valor: prioridad como número, beneficio por tipo y monto, y el tercer clic vuelve al original', async () => {
    mockearApiGet({ ofertas: ENTREGADAS })
    const usuario = userEvent.setup()
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Zeta oferta')
    // El alcance se rotula con el catálogo: se espera al dato (el grupo) y no a la fila, que se rinde antes.
    await screen.findByText('Grupo: Bebidas')

    expect(columnaVisible(0)).toEqual(['Medio oferta', 'Zeta oferta', 'Alfa oferta'])

    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    expect(columnaVisible(0)).toEqual(['Alfa oferta', 'Medio oferta', 'Zeta oferta'])
    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    expect(columnaVisible(0)).toEqual(['Zeta oferta', 'Medio oferta', 'Alfa oferta'])
    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    expect(columnaVisible(0)).toEqual(['Medio oferta', 'Zeta oferta', 'Alfa oferta'])

    // Como texto "100" < "20" < "9"; como número, 9 < 20 < 100.
    await usuario.click(screen.getByRole('button', { name: 'Prioridad' }))
    expect(columnaVisible(3)).toEqual(['9', '20', '100'])
    await usuario.click(screen.getByRole('button', { name: 'Prioridad' }))
    expect(columnaVisible(3)).toEqual(['100', '20', '9'])

    // El porcentaje va antes que los importes y entre importes manda el monto: 900 antes que 1.500.
    await usuario.click(screen.getByRole('button', { name: 'Beneficio' }))
    expect(columnaVisible(0)).toEqual(['Zeta oferta', 'Alfa oferta', 'Medio oferta'])
    await usuario.click(screen.getByRole('button', { name: 'Beneficio' }))
    expect(columnaVisible(0)).toEqual(['Medio oferta', 'Alfa oferta', 'Zeta oferta'])

    await usuario.click(screen.getByRole('button', { name: 'Alcance' }))
    expect(columnaVisible(1)).toEqual(['Artículo: Pancho', 'Familia: Sabores', 'Grupo: Bebidas'])

    await usuario.click(screen.getByRole('button', { name: 'Acumulable' }))
    expect(columnaVisible(0)).toEqual(['Zeta oferta', 'Medio oferta', 'Alfa oferta'])

    await usuario.click(screen.getByRole('button', { name: 'Estado' }))
    expect(columnaVisible(5)).toEqual(['Activa', 'Activa', 'Inactiva'])

    await usuario.click(screen.getByRole('button', { name: 'Online' }))
    expect(columnaVisible(6)).toEqual(['No', 'No', 'Sí'])
    expect(columnaVisible(0)[2]).toBe('Zeta oferta')
  })

  /** Mutación registrada: pasar `ofertas` en vez de `filtradas` a `useOrdenamiento` deja todas las filas con un filtro puesto. */
  it('filtra cada columna de datos, con selectores para acumulable, estado y online, y combina los filtros con Y', async () => {
    mockearApiGet({ ofertas: ENTREGADAS })
    const usuario = userEvent.setup()
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Zeta oferta')

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'ALF')
    expect(columnaVisible(0)).toEqual(['Alfa oferta'])
    await usuario.clear(screen.getByLabelText('Filtrar por nombre'))

    await usuario.type(screen.getByLabelText('Filtrar por alcance'), 'sabores')
    expect(columnaVisible(0)).toEqual(['Medio oferta'])
    await usuario.clear(screen.getByLabelText('Filtrar por alcance'))

    await usuario.type(screen.getByLabelText('Filtrar por beneficio'), '15%')
    expect(columnaVisible(0)).toEqual(['Zeta oferta'])
    await usuario.clear(screen.getByLabelText('Filtrar por beneficio'))

    await usuario.type(screen.getByLabelText('Filtrar por prioridad'), '100')
    expect(columnaVisible(0)).toEqual(['Zeta oferta'])
    await usuario.clear(screen.getByLabelText('Filtrar por prioridad'))

    await usuario.selectOptions(screen.getByLabelText('Filtrar por acumulable'), 'Sí')
    expect(columnaVisible(0)).toEqual(['Medio oferta', 'Alfa oferta'])
    await usuario.selectOptions(screen.getByLabelText('Filtrar por acumulable'), '')

    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Inactiva')
    expect(columnaVisible(0)).toEqual(['Alfa oferta'])
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), '')

    // "Online" es Sí solo si está disponible online Y activa: la inactiva figura como No.
    await usuario.selectOptions(screen.getByLabelText('Filtrar por online'), 'No')
    expect(columnaVisible(0)).toEqual(['Medio oferta', 'Alfa oferta'])

    await usuario.selectOptions(screen.getByLabelText('Filtrar por acumulable'), 'Sí')
    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'medio')
    expect(columnaVisible(0)).toEqual(['Medio oferta'])
  })

  it('"Limpiar filtros" restaura las filas; sin coincidencias el estado vacío lo dice y sin ofertas dice otra cosa', async () => {
    mockearApiGet({ ofertas: ENTREGADAS })
    const usuario = userEvent.setup()
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Zeta oferta')

    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()
    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'no existe')
    expect(screen.getByText('Ningún registro coincide con los filtros.')).toBeInTheDocument()
    expect(screen.queryByText('No hay ofertas cargadas.')).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))

    expect(columnaVisible(0)).toEqual(['Medio oferta', 'Zeta oferta', 'Alfa oferta'])
    expect(screen.getByLabelText('Filtrar por nombre')).toHaveValue('')
  })

  it('sin ofertas dice que no hay', async () => {
    mockearApiGet({ ofertas: [] })
    render(<><Ofertas /><Toaster /></>)

    expect(await screen.findByText('No hay ofertas cargadas.')).toBeInTheDocument()
  })

  it('la fila de filtros tiene una celda por encabezado', async () => {
    mockearApiGet({ ofertas: ENTREGADAS })
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Zeta oferta')

    const filas = screen.getAllByRole('row')
    expect(within(filas[0]).getAllByRole('columnheader')).toHaveLength(8)
    expect(within(filas[1]).getAllByRole('columnheader')).toHaveLength(8)
  })

  /** El alcance se rotula con el catálogo cargado: cuando el catálogo llega después, la columna se vuelve a calcular. */
  it('el filtro por alcance usa el nombre del grupo del catálogo, no su id', async () => {
    mockearApiGet({ ofertas: ENTREGADAS })
    const usuario = userEvent.setup()
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Zeta oferta')
    await screen.findByText('Grupo: Bebidas')

    await usuario.type(screen.getByLabelText('Filtrar por alcance'), 'bebidas')
    expect(columnaVisible(0)).toEqual(['Alfa oferta'])
  })

  /** Mutación registrada: reemplazar el render por `cargando ? <Cargando /> : <table>` desmonta la fila de filtros durante el refresco. */
  it('los filtros siguen montados, con lo escrito, mientras el refresco está en vuelo y quedan inertes', async () => {
    mockearApiGet({ ofertas: ENTREGADAS })
    const respuestaBase = apiGetMock.getMockImplementation()!
    let resolverRefresco!: (ofertas: OfertaListado[]) => void
    let cargasDeOfertas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta !== '/ofertas') return respuestaBase(ruta)
      cargasDeOfertas += 1
      if (cargasDeOfertas === 1) return Promise.resolve(ENTREGADAS)

      return new Promise<OfertaListado[]>((resolver) => {
        resolverRefresco = resolver
      })
    })
    apiDeleteMock.mockResolvedValue(undefined)
    const usuario = userEvent.setup()
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Zeta oferta')

    const filtro = screen.getByLabelText('Filtrar por nombre')
    await usuario.type(filtro, 'zet')
    await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))
    await usuario.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText('Oferta "Zeta oferta" dada de baja.')
    expect(screen.getByLabelText('Filtrar por nombre')).toBe(filtro)
    expect(filtro.isConnected).toBe(true)
    expect(filtro).toHaveValue('zet')
    expect(filtro).toBeDisabled()
    expect(screen.getByLabelText('Filtrar por estado')).toBeDisabled()

    await act(async () => {
      resolverRefresco(ENTREGADAS)
      await Promise.resolve()
    })
    await waitFor(() => expect(screen.getByLabelText('Filtrar por nombre')).toBeEnabled())
    expect(screen.getByLabelText('Filtrar por nombre')).toBe(filtro)
    expect(columnaVisible(0)).toEqual(['Zeta oferta'])
  })

  /** Mutación registrada: quitar `limpiarFiltros()` tras `crear` deja el filtro por nombre puesto y la oferta nueva oculta; falla. */
  it('tras crear una oferta se limpian los filtros por columna para que la fila nueva se vea', async () => {
    const nueva = ofertaFixture({ id: 9, nombre: 'Oferta nueva', idArticulo: null, idGrupo: 1 })
    mockearApiGet({ ofertas: ENTREGADAS })
    const respuestaBase = apiGetMock.getMockImplementation()!
    let cargasDeOfertas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta !== '/ofertas') return respuestaBase(ruta)
      cargasDeOfertas += 1

      return Promise.resolve(cargasDeOfertas === 1 ? ENTREGADAS : [...ENTREGADAS, nueva])
    })
    apiPostMock.mockResolvedValue(nueva)
    const usuario = userEvent.setup()
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Zeta oferta')

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'zet')
    expect(screen.queryByText('Alfa oferta')).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Agregar' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Oferta nueva')
    await usuario.type(screen.getByLabelText('Porcentaje (%)'), '10')
    await usuario.click(screen.getByRole('radio', { name: 'Grupo' }))
    await screen.findByRole('option', { name: 'Bebidas' })
    await usuario.selectOptions(screen.getByLabelText('Grupo objetivo'), 'Bebidas')
    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Oferta "Oferta nueva" creada.')
    await waitFor(() => expect(columnaVisible(0)).toEqual(['Medio oferta', 'Zeta oferta', 'Alfa oferta', 'Oferta nueva']))
    expect(screen.getByLabelText('Filtrar por nombre')).toHaveValue('')
  })

  it('los filtros quedan inertes mientras la baja está en vuelo y con la puerta de baja abierta', async () => {
    mockearApiGet({ ofertas: ENTREGADAS })
    let resolverBaja!: () => void
    apiDeleteMock.mockReturnValue(new Promise<void>((resolver) => (resolverBaja = resolver)))
    const usuario = userEvent.setup()
    render(<><Ofertas /><Toaster /></>)
    await screen.findByText('Zeta oferta')

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'zet')
    await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))
    const puerta = await screen.findByRole('alertdialog')
    expect(screen.getByLabelText('Filtrar por nombre')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()

    await usuario.click(within(puerta).getByRole('button', { name: 'Confirmar baja' }))
    expect(screen.getByLabelText('Filtrar por prioridad')).toBeDisabled()

    await act(async () => {
      resolverBaja()
      await Promise.resolve()
    })
    await waitFor(() => expect(screen.getByLabelText('Filtrar por nombre')).toBeEnabled())
  })
})
