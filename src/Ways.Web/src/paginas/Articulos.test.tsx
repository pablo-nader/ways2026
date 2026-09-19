import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Articulos } from './Articulos'
import { ErrorApi } from '../api/cliente'
import type {
  AlicuotaIvaListado,
  AreaListado,
  ArticuloListado,
  CategoriaListado,
  CondicionFiscalListado,
  GrupoListado,
  MarcaListado,
  PaginaDe,
  ProveedorListado,
} from '../api/tipos'

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
    controlaLote: false,
    ...sobrescribir,
  }
}

function paginaFixture(items: ArticuloListado[]): PaginaDe<ArticuloListado> {
  return { items, total: items.length, pagina: 1, tamanio: 20 }
}

const articuloUno = articuloFixture({ id: 1, codigoInterno: 'A0001', nombre: 'Articulo Uno' })
const articuloDos = articuloFixture({ id: 2, codigoInterno: 'A0002', nombre: 'Articulo Dos' })

function areaFixture(sobrescribir: Partial<AreaListado> = {}): AreaListado {
  return { id: 1, nombre: 'Almacén', activo: true, idEmpresa: null, orden: 1, ...sobrescribir }
}

function marcaFixture(sobrescribir: Partial<MarcaListado> = {}): MarcaListado {
  return { id: 1, nombre: 'Alfa', activo: true, idEmpresa: null, ...sobrescribir }
}

function grupoFixture(sobrescribir: Partial<GrupoListado> = {}): GrupoListado {
  return { id: 1, nombre: 'Alfa', activo: true, idEmpresa: null, margen: null, ...sobrescribir }
}

function categoriaFixture(sobrescribir: Partial<CategoriaListado> = {}): CategoriaListado {
  return { id: 1, nombre: 'Alfa', activo: true, idEmpresa: null, orden: 1, idCategoriaPadre: null, ...sobrescribir }
}

function proveedorFixture(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 1,
    razonSocial: 'Alfa SA',
    nombreFantasia: null,
    cuit: null,
    idCondicionFiscal: 1,
    domicilio: null,
    telefono: null,
    email: null,
    vendedor: null,
    celularVendedor: null,
    supervisor: null,
    celularSupervisor: null,
    margen: null,
    observaciones: null,
    activo: true,
    idEmpresa: null,
    ...sobrescribir,
  }
}

function condicionFiscalFixture(sobrescribir: Partial<CondicionFiscalListado> = {}): CondicionFiscalListado {
  return { id: 1, codigo: 'RI', nombre: 'Responsable Inscripto', codigoAfip: 1, activo: true, ...sobrescribir }
}

/**
 * Un caso por padrón (Marca/Categoría/Grupo/Proveedor habitual) para las pruebas que ejercitan
 * los CUATRO por igual (stopPropagation y bloqueadoRef, mutation-proof-tests) — cada uno completa
 * los campos mínimos que su propio `AltaRapida*` exige y declara la ruta real de su POST.
 */
type CasoAltaRapida = {
  padron: string
  boton: string
  ruta: string
  completar: (dialogo: HTMLElement) => Promise<void>
  respuesta: () => unknown
}

function casosAltaRapida(): CasoAltaRapida[] {
  return [
    {
      padron: 'marca',
      boton: 'Nueva marca',
      ruta: '/catalogos/marcas',
      completar: async (dialogo) => {
        await userEvent.type(within(dialogo).getByLabelText('Nombre'), 'Nueva')
      },
      respuesta: () => marcaFixture({ id: 9, nombre: 'Nueva' }),
    },
    {
      padron: 'categoría',
      boton: 'Nueva categoría',
      ruta: '/catalogos/categorias',
      completar: async (dialogo) => {
        await userEvent.type(within(dialogo).getByLabelText('Nombre'), 'Nueva')
      },
      respuesta: () => categoriaFixture({ id: 9, nombre: 'Nueva' }),
    },
    {
      padron: 'grupo',
      boton: 'Nuevo grupo',
      ruta: '/catalogos/grupos',
      completar: async (dialogo) => {
        await userEvent.type(within(dialogo).getByLabelText('Nombre'), 'Nueva')
      },
      respuesta: () => grupoFixture({ id: 9, nombre: 'Nueva' }),
    },
    {
      padron: 'proveedor',
      boton: 'Nuevo proveedor',
      ruta: '/proveedores',
      completar: async (dialogo) => {
        await userEvent.type(within(dialogo).getByLabelText('Razón social'), 'Nueva')
        await userEvent.selectOptions(await within(dialogo).findByLabelText('Condición fiscal'), '1')
      },
      respuesta: () => proveedorFixture({ id: 9, razonSocial: 'Nueva' }),
    },
  ]
}

type CatalogosDeTest = {
  /** Override del listado y del detalle de artículos — default: [articuloUno, articuloDos]. */
  articulos?: ArticuloListado[]
  areas?: AreaListado[]
  marcas?: MarcaListado[]
  grupos?: GrupoListado[]
  categorias?: CategoriaListado[]
  proveedores?: ProveedorListado[]
  alicuotas?: AlicuotaIvaListado[]
  condicionesFiscales?: CondicionFiscalListado[]
  /** Override completo del fetch de condiciones fiscales (p.ej. para simular un rechazo seguido
   * de un reintento exitoso) — cuando está presente, gana sobre `condicionesFiscales`. */
  condicionesFiscalesImpl?: () => Promise<CondicionFiscalListado[]>
}

/**
 * Despacha por ruta, igual que el mock de `../api/cliente` en `PaginaCatalogo.test.tsx`:
 * `clienteDeArticulos`/`clienteDePrecios`/`clienteDeCatalogo`/`clienteDeOrganizacion` son todos
 * envoltorios finos sobre `api.get`, así que interceptar acá alcanza para toda la pantalla sin
 * mockear cada módulo de API por separado. Los catálogos de padrones (marcas/grupos/categorías/
 * proveedores/alícuotas/condiciones fiscales) son parametrizables para las pruebas de alta rápida.
 */
function mockearApiGet(catalogos: CatalogosDeTest = {}) {
  apiGetMock.mockImplementation((ruta: string) => {
    const articulos = catalogos.articulos ?? [articuloUno, articuloDos]
    if (ruta === '/articulos') return Promise.resolve(paginaFixture(articulos))
    if (/^\/articulos\/\d+$/.test(ruta)) {
      const id = Number(ruta.split('/')[2])
      return Promise.resolve(articulos.find((a) => a.id === id) ?? articulos[0])
    }
    if (/^\/articulos\/\d+\/codigos-barra$/.test(ruta)) return Promise.resolve([])
    if (/^\/articulos\/\d+\/precios$/.test(ruta)) return Promise.resolve([])
    if (/^\/articulos\/\d+\/sugerencia-precio$/.test(ruta)) return Promise.resolve({ precioSugerido: 55.5 })
    // startsWith, no === : Articulos.tsx pide estos cuatro con `incluirInactivos=true` (fix/
    // articulos-form-catalogos-inactivos) — el mock despacha por recurso sin importar el query
    // string, igual que `/proveedores` ya hacía más abajo.
    if (ruta.startsWith('/catalogos/areas')) return Promise.resolve(catalogos.areas ?? [areaFixture()])
    if (ruta.startsWith('/catalogos/categorias')) return Promise.resolve(catalogos.categorias ?? [])
    if (ruta.startsWith('/catalogos/marcas')) return Promise.resolve(catalogos.marcas ?? [])
    if (ruta.startsWith('/catalogos/grupos')) return Promise.resolve(catalogos.grupos ?? [])
    if (ruta.startsWith('/proveedores')) {
      const items = catalogos.proveedores ?? []
      return Promise.resolve({ items, total: items.length, pagina: 1, tamanio: 200 })
    }
    if (ruta === '/catalogos-fiscales/alicuotas-iva')
      return Promise.resolve(catalogos.alicuotas ?? [{ id: 1, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true }])
    if (ruta === '/catalogos-fiscales/condiciones-fiscales')
      return catalogos.condicionesFiscalesImpl
        ? catalogos.condicionesFiscalesImpl()
        : Promise.resolve(catalogos.condicionesFiscales ?? [condicionFiscalFixture()])
    if (ruta === '/empresas') return Promise.resolve([])
    if (ruta === '/catalogos/listas-precio') return Promise.resolve([])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

async function abrirFormularioNuevo() {
  render(<Articulos />)
  await screen.findByText('Articulo Uno')
  await userEvent.click(screen.getByRole('button', { name: 'Nuevo' }))
  await screen.findByText('Nuevo artículo')
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

// ---- controlaLote (stage-12-lotes-vencimientos, Slice 15) ---------------------------------------

describe('Articulos — toggle controlaLote (coerción boolean, dto-contract-honesty)', () => {
  it('un artículo con controlaLote:false arranca con el checkbox destildado', async () => {
    render(<Articulos />)

    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('button', { name: 'Editar' }))

    await screen.findByText('Editando artículo A0001')
    expect(screen.getByLabelText('Controla lote / vencimiento')).not.toBeChecked()
  })

  it('tildar el checkbox y guardar manda controlaLote:true — nunca un string ni "on"', async () => {
    mockearApiGet()
    apiPutMock.mockResolvedValue(articuloFixture({ id: 1, controlaLote: true }))
    render(<Articulos />)

    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('button', { name: 'Editar' }))

    await screen.findByText('Editando artículo A0001')
    await userEvent.click(screen.getByLabelText('Controla lote / vencimiento'))
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.controlaLote).toBe(true)
  })

  it('sin tocar el checkbox, guarda controlaLote:false — el valor previo del artículo, no un default inventado', async () => {
    mockearApiGet()
    apiPutMock.mockResolvedValue(articuloFixture({ id: 1, controlaLote: false }))
    render(<Articulos />)

    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('button', { name: 'Editar' }))

    await screen.findByText('Editando artículo A0001')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.controlaLote).toBe(false)
  })
})

// ---- alícuota de IVA por defecto en un artículo nuevo (fix: elegirAlicuotaPorDefecto) ----------

describe('Articulos — alícuota de IVA por defecto de un artículo nuevo', () => {
  it('con el 21% en la posición 1 (no la 0, como lo devuelve el servidor real ordenado por porcentaje DESC), arranca con el 21% seleccionado', async () => {
    mockearApiGet({
      alicuotas: [
        { id: 10, nombre: 'IVA 27%', porcentaje: 27, codigoAfip: 6, activo: true },
        { id: 20, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true },
      ],
    })

    await abrirFormularioNuevo()

    expect(screen.getByLabelText('Alícuota de IVA')).toHaveValue('20')
  })
})

// ---- alta rápida de padrones desde el formulario de artículo ------------------------------------

describe('Articulos — alta rápida: cada botón "+" abre el modal correcto', () => {
  it('Categoría, Marca, Grupo y Proveedor habitual abren su propio modal de alta rápida', async () => {
    mockearApiGet()
    await abrirFormularioNuevo()

    await userEvent.click(screen.getByRole('button', { name: 'Nueva categoría' }))
    let dialogo = screen.getByRole('dialog', { name: 'Nueva categoría' })
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Nueva marca' }))
    dialogo = screen.getByRole('dialog', { name: 'Nueva marca' })
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Nuevo grupo' }))
    dialogo = screen.getByRole('dialog', { name: 'Nuevo grupo' })
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Nuevo proveedor' }))
    dialogo = await screen.findByRole('dialog', { name: 'Nuevo proveedor' })
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

describe('Articulos — alta rápida: alta exitosa inserta ordenado, selecciona y devuelve el foco', () => {
  it('crear una marca la inserta en la posición alfabética (no al final), la selecciona en el formulario y le devuelve el foco al select', async () => {
    mockearApiGet({ marcas: [marcaFixture({ id: 1, nombre: 'Alfa' }), marcaFixture({ id: 2, nombre: 'Zeta' })] })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/catalogos/marcas') return Promise.resolve(marcaFixture({ id: 3, nombre: 'Mango' }))
      return Promise.reject(new Error(`POST no esperado en el test: ${ruta}`))
    })

    await abrirFormularioNuevo()
    const selectMarca = screen.getByLabelText('Marca') as HTMLSelectElement

    await userEvent.click(screen.getByRole('button', { name: 'Nueva marca' }))
    const dialogo = screen.getByRole('dialog', { name: 'Nueva marca' })
    // Espacios a los costados a propósito: prueban que el POST manda el nombre recortado.
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), '  Mango  ')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Crear' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    expect(apiPostMock).toHaveBeenCalledWith('/catalogos/marcas', { nombre: 'Mango', idEmpresa: null, activo: true })

    const opciones = within(selectMarca)
      .getAllByRole('option')
      .map((o) => o.textContent)
    expect(opciones).toEqual(['Sin especificar', 'Alfa', 'Mango', 'Zeta'])
    expect(selectMarca).toHaveValue('3')
    expect(selectMarca).toHaveFocus()
  })

  it('crear una categoría con padre la inserta en la posición alfabética, la selecciona en el formulario, cierra el modal y le devuelve el foco al select', async () => {
    mockearApiGet({
      categorias: [categoriaFixture({ id: 1, nombre: 'Bebidas' }), categoriaFixture({ id: 2, nombre: 'Panificados' })],
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/catalogos/categorias') {
        return Promise.resolve(categoriaFixture({ id: 3, nombre: 'Lácteos', idCategoriaPadre: 1 }))
      }
      return Promise.reject(new Error(`POST no esperado en el test: ${ruta}`))
    })

    await abrirFormularioNuevo()
    const selectCategoria = screen.getByLabelText('Categoría') as HTMLSelectElement

    await userEvent.click(screen.getByRole('button', { name: 'Nueva categoría' }))
    const dialogo = screen.getByRole('dialog', { name: 'Nueva categoría' })
    // Espacios a los costados a propósito: prueban que el POST manda el nombre recortado.
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), '  Lácteos  ')
    await userEvent.selectOptions(within(dialogo).getByLabelText('Categoría padre'), '1')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Crear' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    expect(apiPostMock).toHaveBeenCalledWith('/catalogos/categorias', {
      nombre: 'Lácteos',
      idEmpresa: null,
      orden: 1,
      idCategoriaPadre: 1,
      activo: true,
    })

    const opciones = within(selectCategoria)
      .getAllByRole('option')
      .map((o) => o.textContent)
    expect(opciones).toEqual(['Sin especificar', 'Bebidas', 'Lácteos', 'Panificados'])
    expect(selectCategoria).toHaveValue('3')
    expect(selectCategoria).toHaveFocus()
  })

  it('crear un grupo con margen lo inserta en la posición alfabética, lo selecciona en el formulario, cierra el modal y le devuelve el foco al select', async () => {
    mockearApiGet({
      grupos: [grupoFixture({ id: 1, nombre: 'Almacén' }), grupoFixture({ id: 2, nombre: 'Limpieza' })],
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/catalogos/grupos') {
        return Promise.resolve(grupoFixture({ id: 3, nombre: 'Bebidas', margen: 15.5 }))
      }
      return Promise.reject(new Error(`POST no esperado en el test: ${ruta}`))
    })

    await abrirFormularioNuevo()
    const selectGrupo = screen.getByLabelText('Grupo') as HTMLSelectElement

    await userEvent.click(screen.getByRole('button', { name: 'Nuevo grupo' }))
    const dialogo = screen.getByRole('dialog', { name: 'Nuevo grupo' })
    // Espacios a los costados a propósito: prueban que el POST manda el nombre recortado.
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), '  Bebidas  ')
    await userEvent.type(within(dialogo).getByLabelText('Margen sugerido (%)'), '15.5')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Crear' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    expect(apiPostMock).toHaveBeenCalledWith('/catalogos/grupos', {
      nombre: 'Bebidas',
      idEmpresa: null,
      activo: true,
      margen: 15.5,
    })

    const opciones = within(selectGrupo)
      .getAllByRole('option')
      .map((o) => o.textContent)
    expect(opciones).toEqual(['Sin especificar', 'Almacén', 'Bebidas (margen 15.5%)', 'Limpieza'])
    expect(selectGrupo).toHaveValue('3')
    expect(selectGrupo).toHaveFocus()
  })
})

describe('Articulos — alta rápida: error del servidor mantiene el modal abierto', () => {
  it('si el alta de grupo falla, el modal sigue abierto con el mensaje de ErrorApi y los valores tipeados', async () => {
    mockearApiGet({ grupos: [grupoFixture({ id: 1, nombre: 'Existente' })] })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/catalogos/grupos') {
        return Promise.reject(new ErrorApi(409, 'nombre_duplicado', 'Ya existe un grupo con ese nombre.'))
      }
      return Promise.reject(new Error(`POST no esperado en el test: ${ruta}`))
    })

    await abrirFormularioNuevo()
    await userEvent.click(screen.getByRole('button', { name: 'Nuevo grupo' }))
    const dialogo = screen.getByRole('dialog', { name: 'Nuevo grupo' })

    await userEvent.type(within(dialogo).getByLabelText('Nombre'), 'Duplicado')
    await userEvent.type(within(dialogo).getByLabelText('Margen sugerido (%)'), '15')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Crear' }))

    expect(await within(dialogo).findByText('Ya existe un grupo con ese nombre.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Duplicado')
    expect(within(dialogo).getByLabelText('Margen sugerido (%)')).toHaveValue(15)
    expect(screen.getByRole('dialog', { name: 'Nuevo grupo' })).toBeInTheDocument()
  })
})

describe('Articulos — alta rápida de proveedor: falla el catálogo de condiciones fiscales', () => {
  it('un fetch rechazado no deja un spinner infinito, muestra el error y deshabilita "Crear"; "Reintentar" recarga y habilita el alta', async () => {
    let intentos = 0
    mockearApiGet({
      condicionesFiscalesImpl: () => {
        intentos += 1
        return intentos === 1
          ? Promise.reject(new Error('falla de red'))
          : Promise.resolve([condicionFiscalFixture({ id: 7, nombre: 'Responsable Inscripto' })])
      },
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/proveedores') return Promise.resolve(proveedorFixture({ id: 9, razonSocial: 'Nueva SA' }))
      return Promise.reject(new Error(`POST no esperado en el test: ${ruta}`))
    })

    await abrirFormularioNuevo()
    await userEvent.click(screen.getByRole('button', { name: 'Nuevo proveedor' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo proveedor' })

    await within(dialogo).findByText('No se pudieron cargar las condiciones fiscales.')
    expect(within(dialogo).queryByText('Cargando condiciones fiscales…')).not.toBeInTheDocument()
    expect(within(dialogo).getByRole('button', { name: 'Crear' })).toBeDisabled()

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Reintentar' }))

    expect(within(dialogo).queryByText('No se pudieron cargar las condiciones fiscales.')).not.toBeInTheDocument()
    const selectCondicion = await within(dialogo).findByLabelText('Condición fiscal')
    expect(within(dialogo).getByRole('button', { name: 'Crear' })).toBeEnabled()

    await userEvent.type(within(dialogo).getByLabelText('Razón social'), 'Nueva SA')
    await userEvent.selectOptions(selectCondicion, '7')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Crear' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/proveedores', expect.objectContaining({ idCondicionFiscal: 7 })))
  })
})

describe('Articulos — alta rápida: el submit de cada mini-formulario no dispara el guardado del artículo', () => {
  // Cláusula bajo prueba: `evento.stopPropagation()` en cada `AltaRapida*.guardar`. El modal sale
  // del DOM físico vía `createPortal`, pero React sigue propagando eventos sintéticos por el árbol
  // de COMPONENTES — sin `stopPropagation` el submit del mini-formulario burbujea hasta el <form>
  // del artículo y dispara su propio guardado (POST /articulos).
  it.each(casosAltaRapida())(
    'crear un $padron desde el modal no llama a POST /articulos',
    async ({ boton, ruta, completar, respuesta }) => {
      mockearApiGet()
      apiPostMock.mockImplementation((r: string) => {
        if (r === ruta) return Promise.resolve(respuesta())
        return Promise.reject(new Error(`POST no esperado en el test: ${r}`))
      })

      await abrirFormularioNuevo()
      await userEvent.click(screen.getByRole('button', { name: boton }))
      const dialogo = await screen.findByRole('dialog', { name: boton })
      await completar(dialogo)
      await userEvent.click(within(dialogo).getByRole('button', { name: 'Crear' }))

      await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith(ruta, expect.anything()))
      expect(apiPostMock).not.toHaveBeenCalledWith('/articulos', expect.anything())
    },
  )
})

describe('Articulos — alta rápida: dos submits sincrónicos en "Crear" disparan un solo POST', () => {
  // Cláusula bajo prueba: el `bloqueadoRef` de cada `AltaRapida*` (react-async-state regla 11) —
  // sin la guarda de reentrancia sincrónica, dos clicks en "Crear" dentro del mismo tick (antes de
  // que React re-renderice el botón deshabilitado) disparan dos POST.
  it.each(casosAltaRapida())(
    'crear un $padron con dos clicks sincrónicos en "Crear" dispara un solo POST',
    async ({ boton, ruta, completar, respuesta }) => {
      mockearApiGet()
      let resolver: (valor: unknown) => void = () => {}
      const pendiente = new Promise((resolve) => {
        resolver = resolve
      })
      apiPostMock.mockImplementation((r: string) => {
        if (r === ruta) return pendiente
        return Promise.reject(new Error(`POST no esperado en el test: ${r}`))
      })

      await abrirFormularioNuevo()
      await userEvent.click(screen.getByRole('button', { name: boton }))
      const dialogo = await screen.findByRole('dialog', { name: boton })
      await completar(dialogo)

      const botonCrear = within(dialogo).getByRole('button', { name: 'Crear' })
      act(() => {
        fireEvent.click(botonCrear)
        fireEvent.click(botonCrear)
      })

      expect(apiPostMock).toHaveBeenCalledTimes(1)

      await act(async () => {
        resolver(respuesta())
        await pendiente
      })
    },
  )
})

describe('Articulos — alta rápida de categoría ofrece las categorías ya cargadas como padre', () => {
  it('el select de categoría padre lista las categorías existentes, además de la opción "raíz"', async () => {
    mockearApiGet({
      categorias: [categoriaFixture({ id: 1, nombre: 'Bebidas' }), categoriaFixture({ id: 2, nombre: 'Lácteos' })],
    })
    await abrirFormularioNuevo()

    await userEvent.click(screen.getByRole('button', { name: 'Nueva categoría' }))
    const dialogo = screen.getByRole('dialog', { name: 'Nueva categoría' })
    const selectPadre = within(dialogo).getByLabelText('Categoría padre')
    expect(within(selectPadre).getAllByRole('option').map((o) => o.textContent)).toEqual([
      '— Ninguna (raíz) —',
      'Bebidas',
      'Lácteos',
    ])
  })
})

describe('Articulos — alta rápida de proveedor: etiqueta y orden en el select', () => {
  it('el proveedor recién creado se muestra por su etiqueta (nombre de fantasía o razón social) y queda en la posición alfabética', async () => {
    mockearApiGet({
      proveedores: [
        proveedorFixture({ id: 1, razonSocial: 'Alfa SA', nombreFantasia: null }),
        proveedorFixture({ id: 2, razonSocial: 'Zeta SA', nombreFantasia: null }),
      ],
      condicionesFiscales: [condicionFiscalFixture({ id: 7, nombre: 'Responsable Inscripto' })],
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/proveedores') {
        return Promise.resolve(proveedorFixture({ id: 3, razonSocial: 'Manzana Distribuciones', nombreFantasia: null }))
      }
      return Promise.reject(new Error(`POST no esperado en el test: ${ruta}`))
    })

    await abrirFormularioNuevo()
    const selectProveedor = screen.getByLabelText('Proveedor habitual') as HTMLSelectElement

    // Orden inicial ya viene por etiqueta, no por como llegó del servidor.
    expect(within(selectProveedor).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'Sin especificar',
      'Alfa SA',
      'Zeta SA',
    ])

    await userEvent.click(screen.getByRole('button', { name: 'Nuevo proveedor' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo proveedor' })
    // Espacios a los costados a propósito: prueban que el POST manda la razón social recortada.
    await userEvent.type(within(dialogo).getByLabelText('Razón social'), '  Manzana Distribuciones  ')
    await userEvent.selectOptions(await within(dialogo).findByLabelText('Condición fiscal'), '7')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Crear' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    expect(apiPostMock).toHaveBeenCalledWith('/proveedores', {
      razonSocial: 'Manzana Distribuciones',
      nombreFantasia: null,
      cuit: null,
      idCondicionFiscal: 7,
      domicilio: null,
      telefono: null,
      email: null,
      vendedor: null,
      celularVendedor: null,
      supervisor: null,
      celularSupervisor: null,
      margen: null,
      observaciones: null,
      idEmpresa: null,
      activo: true,
    })

    expect(within(selectProveedor).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'Sin especificar',
      'Alfa SA',
      'Manzana Distribuciones',
      'Zeta SA',
    ])
    expect(selectProveedor).toHaveValue('3')
  })
})

// ---- catálogos inactivos y FK colgantes en el formulario (fix/articulos-form-catalogos-inactivos) ----
// Un catálogo (área/categoría/marca/grupo) o proveedor referenciado por un artículo puede estar
// desactivado (`activo: false`, la salida recomendada cuando la guarda de referencias rechaza el
// borrado) o, en datos legado de antes de esa guarda, dado de baja lógica (ya no aparece en el
// listado en absoluto — dangling-fk-read-models). El formulario tiene que distinguir los dos casos:
// inactivo pero visible se ofrece igual (con sufijo) en edición; colgante se trata como "sin
// asignar".

describe('Articulos — catálogos inactivos en el formulario', () => {
  it('en alta, el select de Marca no ofrece una marca inactiva', async () => {
    mockearApiGet({
      marcas: [marcaFixture({ id: 1, nombre: 'Alfa', activo: true }), marcaFixture({ id: 2, nombre: 'Beta', activo: false })],
    })

    await abrirFormularioNuevo()

    const opciones = within(screen.getByLabelText('Marca'))
      .getAllByRole('option')
      .map((o) => o.textContent)
    expect(opciones).toEqual(['Sin especificar', 'Alfa'])
  })

  it('en alta, el área por defecto es la primera ACTIVA — no la primera del arreglo, que puede ser inactiva', async () => {
    mockearApiGet({
      areas: [
        areaFixture({ id: 1, nombre: 'Almacén viejo', activo: false }),
        areaFixture({ id: 2, nombre: 'Depósito', activo: true }),
      ],
    })

    await abrirFormularioNuevo()

    expect(screen.getByLabelText('Área')).toHaveValue('2')
  })

  it('al editar un artículo con marca inactiva, la muestra seleccionada con el sufijo "(inactiva)" y guarda sin tocarla', async () => {
    const conMarcaInactiva = articuloFixture({ id: 1, idMarca: 2 })
    mockearApiGet({
      articulos: [conMarcaInactiva, articuloDos],
      marcas: [marcaFixture({ id: 1, nombre: 'Alfa', activo: true }), marcaFixture({ id: 2, nombre: 'Beta', activo: false })],
    })
    apiPutMock.mockResolvedValue(conMarcaInactiva)
    render(<Articulos />)

    const fila = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!fila) throw new Error('No se encontró la fila del artículo')
    await userEvent.click(within(fila).getByRole('button', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0001')

    const selectMarca = screen.getByLabelText('Marca') as HTMLSelectElement
    expect(selectMarca).toHaveValue('2')
    expect(within(selectMarca).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'Sin especificar',
      'Alfa',
      'Beta (inactiva)',
    ])

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.idMarca).toBe(2)
  })

  it('al editar un artículo con área inactiva (obligatoria), la muestra seleccionada con el sufijo y permite guardar sin cambiarla', async () => {
    const conAreaInactiva = articuloFixture({ id: 1, idArea: 2 })
    mockearApiGet({
      articulos: [conAreaInactiva, articuloDos],
      areas: [
        areaFixture({ id: 1, nombre: 'Almacén', activo: true }),
        areaFixture({ id: 2, nombre: 'Depósito viejo', activo: false }),
      ],
    })
    apiPutMock.mockResolvedValue(conAreaInactiva)
    render(<Articulos />)

    const fila = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!fila) throw new Error('No se encontró la fila del artículo')
    await userEvent.click(within(fila).getByRole('button', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0001')

    const selectArea = screen.getByLabelText('Área') as HTMLSelectElement
    expect(selectArea).toHaveValue('2')
    expect(within(selectArea).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'Elegir…',
      'Almacén',
      'Depósito viejo (inactiva)',
    ])

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.idArea).toBe(2)
  })

  it('la grilla resuelve el nombre del área aunque esté inactiva (nombreDe ya no se limita a las activas)', async () => {
    mockearApiGet({ areas: [areaFixture({ id: 1, nombre: 'Depósito viejo', activo: false })] })

    render(<Articulos />)

    const fila = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!fila) throw new Error('No se encontró la fila del artículo')
    expect(within(fila).getByText('Depósito viejo')).toBeInTheDocument()
  })
})

describe('Articulos — FK colgante hacia un catálogo dado de baja lógica (legado, dangling-fk-read-models)', () => {
  it('con un idMarca que ya no existe en el catálogo, el formulario la trata como "sin asignar" y guarda idMarca: null', async () => {
    const conMarcaColgante = articuloFixture({ id: 1, idMarca: 999 })
    mockearApiGet({
      articulos: [conMarcaColgante, articuloDos],
      marcas: [marcaFixture({ id: 1, nombre: 'Alfa', activo: true })],
    })
    apiPutMock.mockResolvedValue(conMarcaColgante)
    render(<Articulos />)

    const fila = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!fila) throw new Error('No se encontró la fila del artículo')
    await userEvent.click(within(fila).getByRole('button', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0001')

    const selectMarca = screen.getByLabelText('Marca') as HTMLSelectElement
    expect(selectMarca).toHaveValue('')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.idMarca).toBeNull()
  })

  it('con un idArea que ya no existe en el catálogo, el formulario deja el select sin elegir — sin crashear', async () => {
    const conAreaColgante = articuloFixture({ id: 1, idArea: 999 })
    mockearApiGet({
      articulos: [conAreaColgante, articuloDos],
      areas: [areaFixture({ id: 1, nombre: 'Almacén', activo: true })],
    })
    render(<Articulos />)

    const fila = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!fila) throw new Error('No se encontró la fila del artículo')
    await userEvent.click(within(fila).getByRole('button', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0001')

    const selectArea = screen.getByLabelText('Área') as HTMLSelectElement
    expect(selectArea).toHaveValue('')
    expect(within(selectArea).getAllByRole('option').map((o) => o.textContent)).toEqual(['Elegir…', 'Almacén'])

    // required + value === '' (placeholder disabled): el navegador bloquea el submit nativo antes
    // de que llegue al handler — no debería dispararse ningún POST/PUT igual.
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    expect(apiPutMock).not.toHaveBeenCalled()
  })
})
