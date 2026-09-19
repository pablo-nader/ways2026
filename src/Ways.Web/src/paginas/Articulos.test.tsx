import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Articulos } from './Articulos'
import { ErrorApi } from '../api/cliente'
import type {
  AlicuotaIvaListado,
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

type CatalogosDeTest = {
  marcas?: MarcaListado[]
  grupos?: GrupoListado[]
  categorias?: CategoriaListado[]
  proveedores?: ProveedorListado[]
  alicuotas?: AlicuotaIvaListado[]
  condicionesFiscales?: CondicionFiscalListado[]
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
    if (ruta === '/articulos') return Promise.resolve(paginaFixture([articuloUno, articuloDos]))
    if (/^\/articulos\/\d+$/.test(ruta)) {
      const id = Number(ruta.split('/')[2])
      return Promise.resolve([articuloUno, articuloDos].find((a) => a.id === id) ?? articuloUno)
    }
    if (/^\/articulos\/\d+\/codigos-barra$/.test(ruta)) return Promise.resolve([])
    if (/^\/articulos\/\d+\/precios$/.test(ruta)) return Promise.resolve([])
    if (/^\/articulos\/\d+\/sugerencia-precio$/.test(ruta)) return Promise.resolve({ precioSugerido: 55.5 })
    if (ruta === '/catalogos/areas') return Promise.resolve([{ id: 1, nombre: 'Almacén', activo: true }])
    if (ruta === '/catalogos/categorias') return Promise.resolve(catalogos.categorias ?? [])
    if (ruta === '/catalogos/marcas') return Promise.resolve(catalogos.marcas ?? [])
    if (ruta === '/catalogos/grupos') return Promise.resolve(catalogos.grupos ?? [])
    if (ruta.startsWith('/proveedores')) {
      const items = catalogos.proveedores ?? []
      return Promise.resolve({ items, total: items.length, pagina: 1, tamanio: 200 })
    }
    if (ruta === '/catalogos-fiscales/alicuotas-iva')
      return Promise.resolve(catalogos.alicuotas ?? [{ id: 1, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true }])
    if (ruta === '/catalogos-fiscales/condiciones-fiscales')
      return Promise.resolve(catalogos.condicionesFiscales ?? [condicionFiscalFixture()])
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
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), 'Mango')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Crear' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    const opciones = within(selectMarca)
      .getAllByRole('option')
      .map((o) => o.textContent)
    expect(opciones).toEqual(['Sin especificar', 'Alfa', 'Mango', 'Zeta'])
    expect(selectMarca).toHaveValue('3')
    expect(selectMarca).toHaveFocus()
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

describe('Articulos — alta rápida: el submit del mini-formulario no dispara el guardado del artículo', () => {
  it('crear una marca desde el modal no llama a POST /articulos (stopPropagation)', async () => {
    mockearApiGet({ marcas: [] })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/catalogos/marcas') return Promise.resolve(marcaFixture({ id: 5, nombre: 'Nueva' }))
      return Promise.reject(new Error(`POST no esperado en el test: ${ruta}`))
    })

    await abrirFormularioNuevo()
    await userEvent.click(screen.getByRole('button', { name: 'Nueva marca' }))
    const dialogo = screen.getByRole('dialog', { name: 'Nueva marca' })
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), 'Nueva')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Crear' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/catalogos/marcas', expect.anything()))
    expect(apiPostMock).not.toHaveBeenCalledWith('/articulos', expect.anything())
  })
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
    await userEvent.type(within(dialogo).getByLabelText('Razón social'), 'Manzana Distribuciones')
    await userEvent.selectOptions(await within(dialogo).findByLabelText('Condición fiscal'), '7')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Crear' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    expect(within(selectProveedor).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'Sin especificar',
      'Alfa SA',
      'Manzana Distribuciones',
      'Zeta SA',
    ])
    expect(selectProveedor).toHaveValue('3')
  })
})
