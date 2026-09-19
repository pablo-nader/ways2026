import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router'
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
  marcas?: MarcaListado[]
  grupos?: GrupoListado[]
  categorias?: CategoriaListado[]
  proveedores?: ProveedorListado[]
  alicuotas?: AlicuotaIvaListado[]
  condicionesFiscales?: CondicionFiscalListado[]
  /** Override completo del fetch de condiciones fiscales (p.ej. para simular un rechazo seguido
   * de un reintento exitoso) — cuando está presente, gana sobre `condicionesFiscales`. */
  condicionesFiscalesImpl?: () => Promise<CondicionFiscalListado[]>
  /** Override completo del fetch de detalle por id (p.ej. para simular una respuesta lenta que
   * llega tarde) — cuando está presente, gana sobre la resolución por defecto. */
  detalleImpl?: (id: number) => Promise<ArticuloListado>
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
      if (catalogos.detalleImpl) return catalogos.detalleImpl(id)
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
      return catalogos.condicionesFiscalesImpl
        ? catalogos.condicionesFiscalesImpl()
        : Promise.resolve(catalogos.condicionesFiscales ?? [condicionFiscalFixture()])
    if (ruta === '/empresas') return Promise.resolve([])
    if (ruta === '/catalogos/listas-precio') return Promise.resolve([])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

/**
 * `Articulos` deriva el modo del modal de `useLocation().pathname` (ver `articulos/rutaModal.ts`)
 * en vez de con `<Routes>` anidadas — se monta en una única entrada `/articulos/*`, igual que en
 * `App.tsx`, para que React Router no la remonte al abrir/cerrar el modal ni al pasar de
 * `/create` a `/edit/{id}` recién creado.
 */
function renderArticulos(rutaInicial = '/articulos') {
  return render(
    <MemoryRouter initialEntries={[rutaInicial]}>
      <Routes>
        <Route path="/articulos/*" element={<Articulos />} />
      </Routes>
    </MemoryRouter>,
  )
}

async function abrirFormularioNuevo() {
  renderArticulos()
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
    renderArticulos()

    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('link', { name: 'Editar' }))

    await screen.findByText('Editando artículo A0001')
    await userEvent.click(await screen.findByRole('button', { name: 'Calcular sugerencia de precio' }))
    await screen.findByText(/Precio sugerido a partir de costo y margen/)

    // Sin cancelar, se pasa directo a editar el segundo artículo — mismo flujo que protege el
    // `key={formulario.id ?? 'nuevo'}` documentado junto a <FormularioArticulo>.
    const filaDos = screen.getByText('Articulo Dos').closest('tr')
    if (!filaDos) throw new Error('No se encontró la fila del artículo dos')
    await userEvent.click(within(filaDos).getByRole('link', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0002')

    await waitFor(() => {
      expect(screen.queryByText(/Precio sugerido a partir de costo y margen/)).not.toBeInTheDocument()
    })
  })
})

// ---- controlaLote (stage-12-lotes-vencimientos, Slice 15) ---------------------------------------

describe('Articulos — toggle controlaLote (coerción boolean, dto-contract-honesty)', () => {
  it('un artículo con controlaLote:false arranca con el checkbox destildado', async () => {
    renderArticulos()

    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('link', { name: 'Editar' }))

    await screen.findByText('Editando artículo A0001')
    expect(screen.getByLabelText('Controla lote / vencimiento')).not.toBeChecked()
  })

  it('tildar el checkbox y guardar manda controlaLote:true — nunca un string ni "on"', async () => {
    mockearApiGet()
    apiPutMock.mockResolvedValue(articuloFixture({ id: 1, controlaLote: true }))
    renderArticulos()

    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('link', { name: 'Editar' }))

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
    renderArticulos()

    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('link', { name: 'Editar' }))

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
    expect(screen.queryAllByRole('dialog')).toHaveLength(1)

    await userEvent.click(screen.getByRole('button', { name: 'Nueva marca' }))
    dialogo = screen.getByRole('dialog', { name: 'Nueva marca' })
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryAllByRole('dialog')).toHaveLength(1)

    await userEvent.click(screen.getByRole('button', { name: 'Nuevo grupo' }))
    dialogo = screen.getByRole('dialog', { name: 'Nuevo grupo' })
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryAllByRole('dialog')).toHaveLength(1)

    await userEvent.click(screen.getByRole('button', { name: 'Nuevo proveedor' }))
    dialogo = await screen.findByRole('dialog', { name: 'Nuevo proveedor' })
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryAllByRole('dialog')).toHaveLength(1)
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

    await waitFor(() => expect(screen.queryAllByRole('dialog')).toHaveLength(1))

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

    await waitFor(() => expect(screen.queryAllByRole('dialog')).toHaveLength(1))

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

    await waitFor(() => expect(screen.queryAllByRole('dialog')).toHaveLength(1))

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

    await waitFor(() => expect(screen.queryAllByRole('dialog')).toHaveLength(1))

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

// ---- articulos-en-modal: el alta/edición vive en un Modal gobernado por la URL ------------------

describe('Articulos — rutas del modal', () => {
  it('/articulos no muestra ningún modal', async () => {
    renderArticulos('/articulos')
    await screen.findByText('Articulo Uno')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('/articulos/create abre el modal de alta', async () => {
    renderArticulos('/articulos/create')
    await screen.findByText('Articulo Uno')
    expect(await screen.findByRole('dialog', { name: 'Nuevo artículo' })).toBeInTheDocument()
  })

  it('/articulos/edit/1 pide el detalle y abre el modal de edición', async () => {
    renderArticulos('/articulos/edit/1')
    await screen.findByText('Articulo Uno')
    expect(await screen.findByRole('dialog', { name: 'Editando artículo A0001' })).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenCalledWith('/articulos/1')
  })

  it('el link "Editar" de cada fila apunta a /articulos/edit/{id}', async () => {
    renderArticulos()
    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    const filaDos = screen.getByText('Articulo Dos').closest('tr')
    if (!filaUno || !filaDos) throw new Error('No se encontraron las filas')
    expect(within(filaUno).getByRole('link', { name: 'Editar' })).toHaveAttribute('href', '/articulos/edit/1')
    expect(within(filaDos).getByRole('link', { name: 'Editar' })).toHaveAttribute('href', '/articulos/edit/2')
  })

  it('un id no numérico en /articulos/edit/... muestra un error dentro del modal, sin crashear, con vuelta al listado', async () => {
    renderArticulos('/articulos/edit/abc')
    await screen.findByText('Articulo Uno')
    expect(await screen.findByText('No se especificó un artículo válido.')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Volver al listado' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('un id numérico que el servidor rechaza (no encontrado) muestra el error del servidor, con vuelta al listado', async () => {
    mockearApiGet({ detalleImpl: () => Promise.reject(new ErrorApi(404, 'no_encontrado', 'Artículo no encontrado.')) })
    renderArticulos('/articulos/edit/999')
    await screen.findByText('Articulo Uno')
    expect(await screen.findByText('Artículo no encontrado.')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Volver al listado' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

describe('Articulos — alta de un artículo nuevo', () => {
  it('crear reemplaza la URL a /articulos/edit/{id} sin cerrar el modal, muestra el aviso y aparecen códigos de barra y precios', async () => {
    mockearApiGet()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/articulos') return Promise.resolve(articuloFixture({ id: 5, codigoInterno: 'A0005', nombre: 'Nuevo Art' }))
      return Promise.reject(new Error(`POST no esperado en el test: ${ruta}`))
    })

    await abrirFormularioNuevo()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Nuevo Art')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('dialog', { name: 'Editando artículo A0005' })).toBeInTheDocument()
    expect(screen.getByText(/creado con código interno A0005/)).toBeInTheDocument()
    expect(screen.getByText('Códigos de barra')).toBeInTheDocument()
    expect(screen.getByText('Precios por lista')).toBeInTheDocument()
    // El cambio de URL (/articulos/create → /articulos/edit/5) no debe disparar un refetch del
    // detalle recién creado: `formulario.id` ya coincide con el id de la URL.
    expect(apiGetMock).not.toHaveBeenCalledWith('/articulos/5')
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  /**
   * Cláusula bajo prueba: `{ replace: true }` en el `navigate` de `guardar()` (Articulos.tsx) tras
   * un alta exitosa. Mutation-proof-tests: sacar `{ replace: true }` (navegación por `push`) deja
   * `/articulos/create` alcanzable con "atrás" — el segundo `expect` de abajo pasaría a ver el
   * modal de alta en blanco en vez del listado sin modal.
   */
  it('el alta reemplaza la entrada de historial: "atrás" desde la edición recién creada vuelve a /articulos, no a /articulos/create', async () => {
    mockearApiGet()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/articulos') return Promise.resolve(articuloFixture({ id: 5, codigoInterno: 'A0005', nombre: 'Nuevo Art' }))
      return Promise.reject(new Error(`POST no esperado en el test: ${ruta}`))
    })

    function ArnesConHistorial() {
      const navigate = useNavigate()
      return (
        <>
          <button type="button" onClick={() => navigate(-1)}>
            Atrás
          </button>
          <Articulos />
        </>
      )
    }

    render(
      <MemoryRouter initialEntries={['/articulos']}>
        <Routes>
          <Route path="/articulos/*" element={<ArnesConHistorial />} />
        </Routes>
      </MemoryRouter>,
    )

    await screen.findByText('Articulo Uno')
    await userEvent.click(screen.getByRole('button', { name: 'Nuevo' }))
    await screen.findByText('Nuevo artículo')
    await userEvent.type(screen.getByLabelText('Nombre'), 'Nuevo Art')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByRole('dialog', { name: 'Editando artículo A0005' })

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

describe('Articulos — cierre del modal', () => {
  it('cerrar sin cambios vuelve a /articulos sin volver a pedir el listado', async () => {
    await abrirFormularioNuevo()
    const llamadasAlListadoAntes = apiGetMock.mock.calls.filter(([ruta]) => ruta === '/articulos').length

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Articulo Uno')).toBeInTheDocument()
    const llamadasAlListadoDespues = apiGetMock.mock.calls.filter(([ruta]) => ruta === '/articulos').length
    expect(llamadasAlListadoDespues).toBe(llamadasAlListadoAntes)
  })

  /**
   * Cláusula bajo prueba: Escape en `Modal` solo actúa sobre el modal TOPE de la pila
   * (`pilaDeModales`) — con el modal de artículo abajo y uno de alta rápida encima, Escape debe
   * cerrar únicamente el de arriba.
   */
  it('Escape con un modal de alta rápida apilado encima cierra solo ese, el modal de artículo sigue abierto', async () => {
    await abrirFormularioNuevo()
    await userEvent.click(screen.getByRole('button', { name: 'Nueva marca' }))
    expect(screen.getByRole('dialog', { name: 'Nueva marca' })).toBeInTheDocument()

    fireEvent.keyDown(document, { key: 'Escape' })

    expect(screen.queryByRole('dialog', { name: 'Nueva marca' })).not.toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Nuevo artículo' })).toBeInTheDocument()
  })
})

describe('Articulos — confirmación al cerrar con cambios sin guardar', () => {
  it('sin cambios, cerrar no pregunta nada', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    await abrirFormularioNuevo()

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(confirmSpy).not.toHaveBeenCalled()
    confirmSpy.mockRestore()
  })

  it('con cambios sin guardar, cancelar la confirmación mantiene el modal abierto con lo tipeado', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    await abrirFormularioNuevo()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Borrador sin guardar')

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('dialog', { name: 'Nuevo artículo' })).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Borrador sin guardar')
    confirmSpy.mockRestore()
  })

  it('con cambios sin guardar, aceptar la confirmación cierra el modal', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    await abrirFormularioNuevo()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Borrador descartado')

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    confirmSpy.mockRestore()
  })
})

describe('Articulos — restauración de foco al cerrar el modal', () => {
  it('cerrar devuelve el foco al link "Editar" que abrió la edición', async () => {
    renderArticulos()
    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    const linkEditar = within(filaUno).getByRole('link', { name: 'Editar' })

    await userEvent.click(linkEditar)
    await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(linkEditar).toHaveFocus()
  })

  it('abierto por URL directa (sin click previo), cerrar devuelve el foco al botón "Nuevo" de reserva', async () => {
    renderArticulos('/articulos/edit/1')
    await screen.findByRole('dialog', { name: 'Editando artículo A0001' })

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.getByRole('button', { name: 'Nuevo' })).toHaveFocus()
  })
})

describe('Articulos — Editar deshabilitado mientras la pantalla está ocupada', () => {
  /**
   * Cláusula bajo prueba: el `preventDefault` de `alClickearEditar` cuando `ocupado && esClicSimple`
   * en `Articulos.tsx`. Mutation-proof-tests: comentar ese `preventDefault` hace que este test
   * falle (el modal se abre igual con la Baja todavía en vuelo).
   */
  it('con una Baja en vuelo, un click simple en "Editar" no navega', async () => {
    let resolverBaja: () => void = () => {}
    const bajaEnVuelo = new Promise<void>((resolve) => {
      resolverBaja = resolve
    })
    apiDeleteMock.mockReturnValue(bajaEnVuelo)
    vi.spyOn(window, 'confirm').mockReturnValue(true)

    renderArticulos()
    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('button', { name: 'Baja' }))

    await userEvent.click(within(filaUno).getByRole('link', { name: 'Editar' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await act(async () => {
      resolverBaja()
      await bajaEnVuelo
    })
    vi.restoreAllMocks()
  })
})

describe('Articulos — respuesta desactualizada del detalle al cambiar de edición', () => {
  /**
   * Harness mínimo: expone un botón que navega DIRECTO de una URL de edición a otra, sin pasar
   * por /articulos — simula "atrás/adelante" del navegador entre dos ediciones ya abiertas.
   */
  function ArnesConNavegacionDirecta({ destino }: { destino: string }) {
    const navigate = useNavigate()
    return (
      <>
        <button type="button" onClick={() => navigate(destino)}>
          Ir directo
        </button>
        <Articulos />
      </>
    )
  }

  it('al cambiar directamente de /articulos/edit/1 a /articulos/edit/2, una respuesta tardía del primero no pisa al segundo', async () => {
    let resolverLento: (valor: ArticuloListado) => void = () => {}
    const lento = new Promise<ArticuloListado>((resolve) => {
      resolverLento = resolve
    })
    mockearApiGet({
      detalleImpl: (id) => (id === 1 ? lento : Promise.resolve(articuloDos)),
    })

    render(
      <MemoryRouter initialEntries={['/articulos/edit/1']}>
        <Routes>
          <Route path="/articulos/*" element={<ArnesConNavegacionDirecta destino="/articulos/edit/2" />} />
        </Routes>
      </MemoryRouter>,
    )

    await screen.findByText('Articulo Uno')
    await userEvent.click(screen.getByRole('button', { name: 'Ir directo' }))
    await screen.findByRole('dialog', { name: 'Editando artículo A0002' })

    await act(async () => {
      resolverLento(articuloUno)
      await lento
    })

    expect(screen.getByRole('dialog', { name: 'Editando artículo A0002' })).toBeInTheDocument()
    expect(screen.queryByText('Editando artículo A0001')).not.toBeInTheDocument()
  })
})
