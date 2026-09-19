import { StrictMode, useEffect } from 'react'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Articulos } from './Articulos'
import { ErrorApi } from '../api/cliente'
import type {
  AlicuotaIvaListado,
  AreaListado,
  ArticuloListado,
  CategoriaListado,
  CondicionFiscalListado,
  EmpresaListado,
  FilaDeGrillaDeArticulos,
  GrupoListado,
  MarcaListado,
  PaginaDeGrillaDeArticulos,
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

const articuloUno = articuloFixture({ id: 1, codigoInterno: 'A0001', nombre: 'Articulo Uno' })
const articuloDos = articuloFixture({ id: 2, codigoInterno: 'A0002', nombre: 'Articulo Dos' })

/** Fila de `GET /api/articulos/grilla` (articulos-grilla-web) — shape distinto del `ArticuloListado`
 * de arriba (que sigue sirviendo el detalle por id, `/articulos/{id}`). */
function filaGrillaFixture(sobrescribir: Partial<FilaDeGrillaDeArticulos> = {}): FilaDeGrillaDeArticulos {
  return {
    id: 1,
    codigoInterno: 'A0001',
    nombre: 'Articulo Uno',
    precio: 100,
    idProveedorHabitual: null,
    proveedor: null,
    activo: true,
    ...sobrescribir,
  }
}

function paginaGrillaFixture(
  items: FilaDeGrillaDeArticulos[],
  sobrescribir: Partial<PaginaDeGrillaDeArticulos> = {},
): PaginaDeGrillaDeArticulos {
  return { items, total: items.length, pagina: 1, tamanio: 25, nombreListaPrecio: 'General', ...sobrescribir }
}

const filaGrillaUno = filaGrillaFixture({ id: 1, codigoInterno: 'A0001', nombre: 'Articulo Uno' })
const filaGrillaDos = filaGrillaFixture({ id: 2, codigoInterno: 'A0002', nombre: 'Articulo Dos' })

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

function empresaFixture(sobrescribir: Partial<EmpresaListado> = {}): EmpresaListado {
  return {
    id: 1,
    idTenant: 1,
    razonSocial: 'Empresa Uno SA',
    nombreFantasia: null,
    cuit: null,
    nombreTenant: null,
    ...sobrescribir,
  }
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
  /** Select del formulario de artículo que el alta rápida debe dejar seleccionado (M8). */
  selectLabel: string
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
      selectLabel: 'Marca',
    },
    {
      padron: 'categoría',
      boton: 'Nueva categoría',
      ruta: '/catalogos/categorias',
      completar: async (dialogo) => {
        await userEvent.type(within(dialogo).getByLabelText('Nombre'), 'Nueva')
      },
      respuesta: () => categoriaFixture({ id: 9, nombre: 'Nueva' }),
      selectLabel: 'Categoría',
    },
    {
      padron: 'grupo',
      boton: 'Nuevo grupo',
      ruta: '/catalogos/grupos',
      completar: async (dialogo) => {
        await userEvent.type(within(dialogo).getByLabelText('Nombre'), 'Nueva')
      },
      respuesta: () => grupoFixture({ id: 9, nombre: 'Nueva' }),
      selectLabel: 'Grupo',
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
      selectLabel: 'Proveedor habitual',
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
  /** Override del `total` de la página de proveedores — default: `proveedores.length` (sin
   * truncar). Un `total` mayor simula que el tenant tiene más proveedores que el tamaño de página
   * pedido, y el `idProveedorHabitual` de un artículo puede caer fuera de esa página. */
  proveedoresTotal?: number
  alicuotas?: AlicuotaIvaListado[]
  condicionesFiscales?: CondicionFiscalListado[]
  empresas?: EmpresaListado[]
  /** Override completo del fetch de condiciones fiscales (p.ej. para simular un rechazo seguido
   * de un reintento exitoso) — cuando está presente, gana sobre `condicionesFiscales`. */
  condicionesFiscalesImpl?: () => Promise<CondicionFiscalListado[]>
  /** Override completo del fetch de detalle por id (p.ej. para simular una respuesta lenta que
   * llega tarde) — cuando está presente, gana sobre la resolución por defecto. */
  detalleImpl?: (id: number) => Promise<ArticuloListado>
  /** Respuesta fija de `GET /api/articulos/grilla` — por defecto, las dos filas de siempre. */
  grilla?: PaginaDeGrillaDeArticulos
  /** Override completo del fetch de áreas (p.ej. para simular un rechazo o una respuesta tardía)
   * — cuando está presente, gana sobre la resolución por defecto. */
  areasImpl?: () => Promise<{ id: number; nombre: string; activo: boolean }[]>
  /** Override completo del fetch de alícuotas de IVA (p.ej. para simular una respuesta tardía) —
   * cuando está presente, gana sobre la resolución por defecto. */
  alicuotasImpl?: () => Promise<AlicuotaIvaListado[]>
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
    if (ruta.startsWith('/articulos/grilla')) {
      return Promise.resolve(catalogos.grilla ?? paginaGrillaFixture([filaGrillaUno, filaGrillaDos]))
    }
    if (/^\/articulos\/\d+$/.test(ruta)) {
      const id = Number(ruta.split('/')[2])
      if (catalogos.detalleImpl) return catalogos.detalleImpl(id)
      return Promise.resolve(articulos.find((a) => a.id === id) ?? articulos[0])
    }
    if (/^\/articulos\/\d+\/codigos-barra$/.test(ruta)) return Promise.resolve([])
    if (/^\/articulos\/\d+\/precios$/.test(ruta)) return Promise.resolve([])
    if (/^\/articulos\/\d+\/sugerencia-precio$/.test(ruta)) return Promise.resolve({ precioSugerido: 55.5 })
    // startsWith, no === : Articulos.tsx pide estos cuatro con `incluirInactivos=true` (fix/
    // articulos-form-catalogos-inactivos) — el mock despacha por recurso sin importar el query
    // string, igual que `/proveedores` ya hacía más abajo. `soloActivos` imita el filtrado real
    // del servidor cuando el query string NO pide inactivas (mutation-proof-tests regla 3: sin
    // esto, un `listar(false)` mutado seguiría devolviendo la lista completa y ningún test lo
    // detectaría — el mock necesita discriminar el mismo query string que la clave bajo prueba).
    const incluirInactivos = ruta.includes('incluirInactivos=true')
    function soloActivos<T extends { activo: boolean }>(items: T[]): T[] {
      return incluirInactivos ? items : items.filter((item) => item.activo)
    }
    if (ruta.startsWith('/catalogos/areas'))
      return catalogos.areasImpl ? catalogos.areasImpl() : Promise.resolve(soloActivos(catalogos.areas ?? [areaFixture()]))
    if (ruta.startsWith('/catalogos/categorias')) return Promise.resolve(soloActivos(catalogos.categorias ?? []))
    if (ruta.startsWith('/catalogos/marcas')) return Promise.resolve(soloActivos(catalogos.marcas ?? []))
    if (ruta.startsWith('/catalogos/grupos')) return Promise.resolve(soloActivos(catalogos.grupos ?? []))
    if (ruta.startsWith('/proveedores')) {
      const items = catalogos.proveedores ?? []
      return Promise.resolve({ items, total: catalogos.proveedoresTotal ?? items.length, pagina: 1, tamanio: 200 })
    }
    if (ruta === '/catalogos-fiscales/alicuotas-iva')
      return catalogos.alicuotasImpl
        ? catalogos.alicuotasImpl()
        : Promise.resolve(catalogos.alicuotas ?? [{ id: 1, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true }])
    if (ruta === '/catalogos-fiscales/condiciones-fiscales')
      return catalogos.condicionesFiscalesImpl
        ? catalogos.condicionesFiscalesImpl()
        : Promise.resolve(catalogos.condicionesFiscales ?? [condicionFiscalFixture()])
    if (ruta === '/empresas') return Promise.resolve(catalogos.empresas ?? [])
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

// ---- defaults de Área/Alícuota de IVA cuando los catálogos resuelven DESPUÉS de abrir el alta ---

describe('Articulos — defaults de Área/Alícuota de IVA cuando los catálogos llegan tarde (M5)', () => {
  /**
   * Cláusula bajo prueba: el efecto de `Articulos.tsx` que completa `idArea`/`idAlicuotaIva`
   * cuando el catálogo respectivo resuelve DESPUÉS de haber entrado a 'crear'. Mutation-proof-
   * tests: sacar ese efecto (o su `useEffect`) hace fallar este test — ambos campos quedarían en
   * '' para siempre pese a que los catálogos ya llegaron.
   */
  it('/articulos/create abierta con los catálogos pendientes recibe los defaults apenas resuelven', async () => {
    let resolverAreas: (valor: { id: number; nombre: string; activo: boolean }[]) => void = () => {}
    let resolverAlicuotas: (valor: AlicuotaIvaListado[]) => void = () => {}
    const areasPendientes = new Promise<{ id: number; nombre: string; activo: boolean }[]>((resolve) => {
      resolverAreas = resolve
    })
    const alicuotasPendientes = new Promise<AlicuotaIvaListado[]>((resolve) => {
      resolverAlicuotas = resolve
    })
    mockearApiGet({ areasImpl: () => areasPendientes, alicuotasImpl: () => alicuotasPendientes })

    renderArticulos('/articulos/create')
    await screen.findByRole('dialog', { name: 'Nuevo artículo' })

    expect(screen.getByLabelText('Área')).toHaveValue('')
    expect(screen.getByLabelText('Alícuota de IVA')).toHaveValue('')

    await act(async () => {
      resolverAreas([{ id: 7, nombre: 'Almacén', activo: true }])
      resolverAlicuotas([{ id: 20, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true }])
      await Promise.all([areasPendientes, alicuotasPendientes])
    })

    expect(screen.getByLabelText('Área')).toHaveValue('7')
    expect(screen.getByLabelText('Alícuota de IVA')).toHaveValue('20')
  })

  /**
   * Cláusula bajo prueba: el guard `formulario.idAlicuotaIva === ''` del mismo efecto — "nunca
   * pisa una elección ya hecha por el usuario". Mutation-proof-tests: sacar ese guard (aplicar
   * siempre el default) hace fallar el último `expect` (la alícuota elegida a mano pasaría a 20).
   */
  it('un valor elegido antes de que el catálogo resuelva no se pisa con su default', async () => {
    let resolverAreas: (valor: { id: number; nombre: string; activo: boolean }[]) => void = () => {}
    const areasPendientes = new Promise<{ id: number; nombre: string; activo: boolean }[]>((resolve) => {
      resolverAreas = resolve
    })
    mockearApiGet({
      areasImpl: () => areasPendientes,
      alicuotas: [
        { id: 10, nombre: 'IVA 27%', porcentaje: 27, codigoAfip: 6, activo: true },
        { id: 20, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true },
      ],
    })

    renderArticulos('/articulos/create')
    await screen.findByRole('dialog', { name: 'Nuevo artículo' })
    // Alícuota ya cargada (no depende de `areasPendientes`): el usuario elige la 27%, no la del
    // default (21%), ANTES de que el área resuelva.
    await userEvent.selectOptions(screen.getByLabelText('Alícuota de IVA'), '10')

    await act(async () => {
      resolverAreas([{ id: 7, nombre: 'Almacén', activo: true }])
      await areasPendientes
    })

    expect(screen.getByLabelText('Área')).toHaveValue('7')
    expect(screen.getByLabelText('Alícuota de IVA')).toHaveValue('10')
  })
})

describe('Articulos — los defaults tardíos de Área/Alícuota solo tocan esos campos de la base limpia (N2)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  /** Abre /articulos/create con áreas y alícuotas pendientes; devuelve cómo resolverlas juntas. */
  async function abrirAltaConCatalogosPendientes() {
    let resolverAreas: (valor: { id: number; nombre: string; activo: boolean }[]) => void = () => {}
    let resolverAlicuotas: (valor: AlicuotaIvaListado[]) => void = () => {}
    const areasPendientes = new Promise<{ id: number; nombre: string; activo: boolean }[]>((resolve) => {
      resolverAreas = resolve
    })
    const alicuotasPendientes = new Promise<AlicuotaIvaListado[]>((resolve) => {
      resolverAlicuotas = resolve
    })
    mockearApiGet({ areasImpl: () => areasPendientes, alicuotasImpl: () => alicuotasPendientes })

    renderArticulos('/articulos/create')
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo artículo' })

    async function resolverCatalogos() {
      await act(async () => {
        resolverAreas([{ id: 7, nombre: 'Almacén', activo: true }])
        resolverAlicuotas([{ id: 20, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true }])
        await Promise.all([areasPendientes, alicuotasPendientes])
      })
      expect(screen.getByLabelText('Área')).toHaveValue('7')
      expect(screen.getByLabelText('Alícuota de IVA')).toHaveValue('20')
    }

    return { dialogo, resolverCatalogos }
  }

  /**
   * Cláusula bajo prueba: la base limpia (`formularioOriginalRef`) recibe SOLO los campos que el
   * efecto de defaults completó, no el formulario entero. Mutation-proof-tests: volver a copiar el
   * formulario completo a la base hace fallar el `expect` del `confirm` — el Nombre tipeado antes de
   * que llegaran los catálogos pasaría a contar como "guardado" y se descartaría sin preguntar.
   */
  it('lo tipeado antes de que lleguen los catálogos sigue contando como cambio sin guardar', async () => {
    const { dialogo, resolverCatalogos } = await abrirAltaConCatalogosPendientes()
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), 'Borrador temprano')
    await resolverCatalogos()
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('dialog', { name: 'Nuevo artículo' })).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Borrador temprano')
  })

  /**
   * Cláusula bajo prueba: el parche de `idArea` e `idAlicuotaIva` sobre la base limpia en el mismo
   * efecto. Mutation-proof-tests: sacar ese parche (o uno solo de sus dos campos) hace fallar el
   * `expect` del `confirm` — el auto-completado dejaría un falso "hay cambios sin guardar".
   */
  it('sin tocar nada, el auto-completado de los defaults no deja cambios sin guardar', async () => {
    const { dialogo, resolverCatalogos } = await abrirAltaConCatalogosPendientes()
    await resolverCatalogos()
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

    expect(confirmSpy).not.toHaveBeenCalled()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

// ---- avisos de catálogos requeridos/listas de precio, también visibles DENTRO del modal --------

describe('Articulos — avisos de catálogos: visibles también dentro del modal', () => {
  /**
   * Cláusula bajo prueba: el bloque `erroresCatalogosRequeridos` renderizado en
   * `ModalDeArticulo.tsx`. Mutation-proof-tests: sacar ese bloque hace fallar este test (el aviso
   * solo quedaría en la grilla, tapada por el backdrop del modal).
   */
  it('un catálogo requerido que falla muestra el aviso DENTRO del diálogo, no solo en la grilla', async () => {
    mockearApiGet({ areasImpl: () => Promise.reject(new Error('sin red')) })

    await abrirFormularioNuevo()
    const dialogo = screen.getByRole('dialog', { name: 'Nuevo artículo' })

    expect(await within(dialogo).findByText(/No se pudieron cargar las áreas\./)).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el bloque `avisoListasPrecio` renderizado en `ModalDeArticulo.tsx`.
   * Mutation-proof-tests: sacar ese bloque hace fallar este test.
   */
  it('un fallo al cargar las listas de precio muestra el aviso DENTRO del diálogo', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/catalogos/listas-precio') return Promise.reject(new Error('sin red'))
      if (ruta.startsWith('/articulos/grilla')) return Promise.resolve(paginaGrillaFixture([filaGrillaUno, filaGrillaDos]))
      if (ruta === '/catalogos/areas') return Promise.resolve([{ id: 1, nombre: 'Almacén', activo: true }])
      if (ruta === '/catalogos/categorias') return Promise.resolve([])
      if (ruta === '/catalogos/marcas') return Promise.resolve([])
      if (ruta === '/catalogos/grupos') return Promise.resolve([])
      if (ruta.startsWith('/proveedores')) return Promise.resolve({ items: [], total: 0, pagina: 1, tamanio: 200 })
      if (ruta === '/catalogos-fiscales/alicuotas-iva')
        return Promise.resolve([{ id: 1, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true }])
      if (ruta === '/empresas') return Promise.resolve([])
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await abrirFormularioNuevo()
    const dialogo = screen.getByRole('dialog', { name: 'Nuevo artículo' })

    expect(
      await within(dialogo).findByText(/No se pudieron cargar las listas de precio/),
    ).toBeInTheDocument()
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

  // fix/articulos-form-catalogos-inactivos: `categorias` en este formulario trae activas e
  // inactivas (incluirInactivos: true, para el select de Categoría del artículo) — una categoría
  // nueva nunca debería poder quedar parentada bajo una ya desactivada.
  it('el select de categoría padre NO ofrece una categoría inactiva', async () => {
    mockearApiGet({
      categorias: [
        categoriaFixture({ id: 1, nombre: 'Bebidas', activo: true }),
        categoriaFixture({ id: 2, nombre: 'Lácteos (de baja)', activo: false }),
      ],
    })
    await abrirFormularioNuevo()

    await userEvent.click(screen.getByRole('button', { name: 'Nueva categoría' }))
    const dialogo = screen.getByRole('dialog', { name: 'Nueva categoría' })
    const selectPadre = within(dialogo).getByLabelText('Categoría padre')
    expect(within(selectPadre).getAllByRole('option').map((o) => o.textContent)).toEqual(['— Ninguna (raíz) —', 'Bebidas'])
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
    const llamadasAlListadoAntes = apiGetMock.mock.calls.filter(([ruta]) => (ruta as string).startsWith('/articulos/grilla')).length

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Articulo Uno')).toBeInTheDocument()
    const llamadasAlListadoDespues = apiGetMock.mock.calls.filter(([ruta]) => (ruta as string).startsWith('/articulos/grilla')).length
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

  /**
   * Cláusula bajo prueba: la normalización (orden) de `idsEmpresas` en `normalizarParaComparar`
   * (Articulos.tsx). Mutation-proof-tests: comparar con `JSON.stringify` sin normalizar hace
   * fallar este test — destildar y volver a tildar reordena el array (filter + append al final)
   * aunque el conjunto final sea idéntico al original, y `confirm` pasaría a llamarse.
   */
  it('destildar y volver a tildar la misma empresa no deja "cambios sin guardar" (reordena idsEmpresas, no lo cambia)', async () => {
    mockearApiGet({
      empresas: [empresaFixture({ id: 1 }), empresaFixture({ id: 2, razonSocial: 'Empresa Dos SA' })],
    })
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/articulos/grilla')) return Promise.resolve(paginaGrillaFixture([filaGrillaUno, filaGrillaDos]))
      if (ruta === '/articulos/1')
        return Promise.resolve(articuloFixture({ id: 1, disponibleParaTodas: false, idsEmpresas: [1, 2] }))
      if (/^\/articulos\/\d+\/codigos-barra$/.test(ruta)) return Promise.resolve([])
      if (/^\/articulos\/\d+\/precios$/.test(ruta)) return Promise.resolve([])
      if (ruta === '/catalogos/areas') return Promise.resolve([{ id: 1, nombre: 'Almacén', activo: true }])
      if (ruta === '/catalogos/categorias') return Promise.resolve([])
      if (ruta === '/catalogos/marcas') return Promise.resolve([])
      if (ruta === '/catalogos/grupos') return Promise.resolve([])
      if (ruta.startsWith('/proveedores')) return Promise.resolve({ items: [], total: 0, pagina: 1, tamanio: 200 })
      if (ruta === '/catalogos-fiscales/alicuotas-iva')
        return Promise.resolve([{ id: 1, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true }])
      if (ruta === '/empresas')
        return Promise.resolve([empresaFixture({ id: 1 }), empresaFixture({ id: 2, razonSocial: 'Empresa Dos SA' })])
      if (ruta === '/catalogos/listas-precio') return Promise.resolve([])
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)

    renderArticulos()
    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('link', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0001')

    // El artículo carga con idsEmpresas = [1, 2] (ambas ya tildadas). Destildar la empresa 1 y
    // volver a tildarla reordena el array a [2, 1] — mismo conjunto, distinta representación.
    await userEvent.click(screen.getByLabelText('Empresa Uno SA'))
    await userEvent.click(screen.getByLabelText('Empresa Uno SA'))

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(confirmSpy).not.toHaveBeenCalled()
    confirmSpy.mockRestore()
  })
})

// ---- Atrás/Adelante del navegador con cambios sin guardar (M2) ---------------------------------

describe('Articulos — Atrás del navegador con cambios sin guardar (M2)', () => {
  /**
   * Cláusula bajo prueba: la compuerta de confirmación del efecto de apertura de `Articulos.tsx`
   * (`destinoModalRef.current !== null && haySinGuardar()`). Mutation-proof-tests: sacarla hace
   * fallar el primer `expect` de cada test de abajo (POP descarta en silencio); revertida, verde.
   */
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

  function renderConHistorial() {
    return render(
      <MemoryRouter initialEntries={['/articulos', '/articulos/edit/1']} initialIndex={1}>
        <Routes>
          <Route path="/articulos/*" element={<ArnesConHistorial />} />
        </Routes>
      </MemoryRouter>,
    )
  }

  it('POP (Atrás) con el formulario sucio pregunta antes de salir; cancelar mantiene lo tipeado y la URL en la edición', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    renderConHistorial()
    await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await userEvent.type(screen.getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0001' })).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Articulo Uno (editado)')
    confirmSpy.mockRestore()
  })

  it('POP (Atrás) con el formulario sucio, aceptando la confirmación, cierra el modal', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderConHistorial()
    await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await userEvent.type(screen.getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    confirmSpy.mockRestore()
  })

  it('POP (Atrás) con el formulario limpio no pregunta nada', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderConHistorial()
    await screen.findByRole('dialog', { name: 'Editando artículo A0001' })

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))

    expect(confirmSpy).not.toHaveBeenCalled()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    confirmSpy.mockRestore()
  })
})

describe('Articulos — beforeunload (recarga/cierre de pestaña) mientras hay cambios sin guardar (M2)', () => {
  /**
   * Cláusula bajo prueba: el `useEffect` de `beforeunload` de `Articulos.tsx` — se registra SOLO
   * mientras `haySinGuardar()` es true. Mutation-proof-tests: registrar el listener siempre (sin el
   * guard `if (!haySinGuardar()) return`) hace fallar el primer `expect`; no registrarlo nunca hace
   * fallar el segundo.
   */
  it('previene la salida solo mientras el formulario tiene cambios sin guardar', async () => {
    await abrirFormularioNuevo()

    const eventoLimpio = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(eventoLimpio)
    expect(eventoLimpio.defaultPrevented).toBe(false)

    await userEvent.type(screen.getByLabelText('Nombre'), 'Borrador')

    const eventoSucio = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(eventoSucio)
    expect(eventoSucio.defaultPrevented).toBe(true)
  })

  it('deja de prevenir la salida una vez que el formulario vuelve a estar limpio (modal cerrado)', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    await abrirFormularioNuevo()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Borrador')
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    const evento = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(evento)
    expect(evento.defaultPrevented).toBe(false)
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

describe('Articulos — Baja de un artículo desde la grilla', () => {
  /**
   * Cláusula bajo prueba: el `setAviso` de `eliminar()` en `Articulos.tsx` y el bump de
   * `pedidoDeRefresco` que le pide a `GrillaDeArticulos` refetchear. Mutation-proof-tests: borrar
   * cualquiera de los dos deja este test sin ver el aviso o sin ver la fila actualizada tras el
   * refresco (la fixture de la grilla cambia DESPUÉS de la Baja, así que solo el refresco real la
   * revela).
   */
  it('una Baja exitosa muestra el aviso "dado de baja" y refresca la grilla con los datos nuevos', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    apiDeleteMock.mockResolvedValue(undefined)
    renderArticulos()

    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')

    // Tras la Baja, el servidor ya no devuelve "Articulo Uno" — la única forma de verlo
    // desaparecer es que la grilla haya vuelto a pedir el listado.
    mockearApiGet({ grilla: paginaGrillaFixture([filaGrillaDos]) })

    await userEvent.click(within(filaUno).getByRole('button', { name: 'Baja' }))

    expect(await screen.findByText('Artículo "Articulo Uno" dado de baja.')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Articulo Uno')).not.toBeInTheDocument())
    expect(screen.getByText('Articulo Dos')).toBeInTheDocument()

    confirmSpy.mockRestore()
  })

  /**
   * Cláusula bajo prueba: el `catch` de `eliminar()` en `Articulos.tsx` (`setError` con el mensaje
   * de `ErrorApi`). Mutation-proof-tests: que ese `catch` no seteara `error` (o mostrara un mensaje
   * genérico) haría fallar el `findByText` de abajo.
   */
  it('una Baja rechazada por el servidor muestra el error, sin aviso de éxito', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    apiDeleteMock.mockRejectedValue(new ErrorApi(409, 'articulo_en_uso', 'El artículo está en uso y no se puede dar de baja.'))
    renderArticulos()

    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')

    await userEvent.click(within(filaUno).getByRole('button', { name: 'Baja' }))

    expect(await screen.findByText('El artículo está en uso y no se puede dar de baja.')).toBeInTheDocument()
    expect(screen.queryByText(/dado de baja/)).not.toBeInTheDocument()
    expect(screen.getByText('Articulo Uno')).toBeInTheDocument()

    confirmSpy.mockRestore()
  })
})

describe('Articulos — un guardado exitoso refresca la grilla', () => {
  /**
   * Cláusula bajo prueba: el bump de `pedidoDeRefresco` en `guardar()` (Articulos.tsx), hermano del
   * de `eliminar()` (mutation-proof-tests regla 15). El nombre que devuelve el PUT y el que devuelve
   * el listado son distintos a propósito: solo un refresco real trae el del listado.
   */
  it('tras guardar una edición, la grilla vuelve a pedir el listado y muestra los datos nuevos', async () => {
    mockearApiGet()
    apiPutMock.mockResolvedValue(articuloFixture({ id: 1, nombre: 'Articulo Uno (editado)' }))
    renderArticulos('/articulos/edit/1')
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    expect(await screen.findByText('Articulo Uno')).toBeInTheDocument()

    mockearApiGet({
      grilla: paginaGrillaFixture([
        filaGrillaFixture({ id: 1, codigoInterno: 'A0001', nombre: 'Articulo Uno Refrescado' }),
        filaGrillaDos,
      ]),
    })

    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Articulo Uno Refrescado')).toBeInTheDocument()
    expect(screen.queryByText('Articulo Uno')).not.toBeInTheDocument()
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

// ---- cierre del modal reemplaza la entrada de historial, también en una edición entrada por URL directa (M6) --

describe('Articulos — cerrar una edición abierta por URL directa reemplaza la entrada de historial (M6)', () => {
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

  /**
   * Cláusula bajo prueba: `{ replace: true }` en el `navigate` de `cerrarModal` (Articulos.tsx).
   * Mutation-proof-tests: sacar `{ replace: true }` (navegación por `push`) hace fallar el
   * `expect` de abajo — con push, "atrás" desde /articulos vuelve a /articulos/edit/1 y resucita
   * el modal.
   */
  it('entrar directo a /articulos/edit/1 y cerrar: "atrás" no resucita el modal', async () => {
    render(
      <MemoryRouter initialEntries={['/articulos', '/articulos/edit/1']} initialIndex={1}>
        <Routes>
          <Route path="/articulos/*" element={<ArnesConHistorial />} />
        </Routes>
      </MemoryRouter>,
    )

    await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

// ---- el token de edición también gatea el catch de abrirEdicion (M7) ---------------------------

describe('Articulos — el catch de abrirEdicion respeta el token de edición en curso (M7)', () => {
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

  /**
   * Cláusula bajo prueba: `if (tokenEdicionRef.current !== token) return` en la rama `catch` de
   * `abrirEdicion` (Articulos.tsx). Mutation-proof-tests: sacar ese guard hace fallar los dos
   * `expect` de abajo — el rechazo tardío del primer detalle pisaría el segundo, ya cargado, con
   * el error "no encontrado".
   */
  it('un 404 tardío del primer edit no pisa al segundo, ya cargado', async () => {
    let rechazarLento: (error: unknown) => void = () => {}
    const lento = new Promise<ArticuloListado>((_resolve, reject) => {
      rechazarLento = reject
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
      rechazarLento(new ErrorApi(404, 'no_encontrado', 'Artículo no encontrado.'))
      await lento.catch(() => {})
    })

    expect(screen.getByRole('dialog', { name: 'Editando artículo A0002' })).toBeInTheDocument()
    expect(screen.queryByText('Artículo no encontrado.')).not.toBeInTheDocument()
  })
})

// ---- el completado del alta rápida es una actualización funcional, no por closure (M8) ---------

describe('Articulos — el alta rápida completa el formulario por actualización funcional, no por closure (M8)', () => {
  /**
   * Cláusula bajo prueba: `actualizarFormulario((previo) => ({ ...previo, idX: nuevo.id }))` en
   * cada `onCreado` de `FormularioArticulo.tsx` (react-async-state regla 1). Mutation-proof-tests:
   * reemplazar por `onCambio({ ...valor, idX: nuevo.id })` (closure del `valor` de render, capturado
   * ANTES del await del alta rápida) hace fallar el primer `expect` de abajo — el Nombre tipeado
   * MIENTRAS el POST estaba pendiente se pierde, pisado por el `valor` viejo.
   */
  it.each(casosAltaRapida())(
    'crear un $padron con el POST pendiente: tipear en Nombre mientras tanto sobrevive al resolver, y el padrón queda seleccionado',
    async ({ boton, ruta, completar, respuesta, selectLabel }) => {
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
      const dialogoArticulo = screen.getByRole('dialog', { name: 'Nuevo artículo' })
      await userEvent.click(screen.getByRole('button', { name: boton }))
      const dialogoAlta = await screen.findByRole('dialog', { name: boton })
      await completar(dialogoAlta)
      await userEvent.click(within(dialogoAlta).getByRole('button', { name: 'Crear' }))

      // El POST queda pendiente: tipear en el Nombre del artículo ANTES de que resuelva.
      await userEvent.type(within(dialogoArticulo).getByLabelText('Nombre'), 'Tocado mientras tanto')

      await act(async () => {
        resolver(respuesta())
        await pendiente
      })
      await waitFor(() => expect(screen.queryAllByRole('dialog')).toHaveLength(1))

      expect(within(dialogoArticulo).getByLabelText('Nombre')).toHaveValue('Tocado mientras tanto')
      expect(within(dialogoArticulo).getByLabelText(selectLabel)).toHaveValue('9')
    },
  )
})

// ---- la decisión de salir se toma ANTES de desmontar el modal (N1) -----------------------------

describe('Articulos — rechazar la salida no remonta el modal ni pierde estado de los hijos (N1)', () => {
  // Restaurar en `afterEach` (no al final de cada test): un `expect` que falla antes del restore
  // dejaría el spy de `confirm` vivo y sus llamadas se sumarían en los tests siguientes.
  afterEach(() => {
    vi.restoreAllMocks()
  })

  // Cada pathname commiteado, en orden: la URL final restaurada no alcanza para probar que un
  // cierre rechazado nunca navegó (ida y vuelta por /articulos deja la misma URL al final).
  let recorrido: string[] = []
  beforeEach(() => {
    recorrido = []
  })

  function Ubicacion() {
    const { pathname } = useLocation()
    useEffect(() => {
      recorrido.push(pathname)
    }, [pathname])
    return <p>Ubicación: {pathname}</p>
  }

  function ArnesConHistorial() {
    const navigate = useNavigate()
    return (
      <>
        <button type="button" onClick={() => navigate(-1)}>
          Atrás
        </button>
        <button type="button" onClick={() => navigate('/articulos/edit/2')}>
          Ir a la edición 2
        </button>
        <Ubicacion />
        <Articulos />
      </>
    )
  }

  function renderConHistorial({ estricto = false, entradas = ['/articulos', '/articulos/edit/1'] } = {}) {
    const arbol = (
      <MemoryRouter initialEntries={entradas} initialIndex={entradas.length - 1}>
        <Routes>
          <Route path="/articulos/*" element={<ArnesConHistorial />} />
        </Routes>
      </MemoryRouter>
    )
    return render(estricto ? <StrictMode>{arbol}</StrictMode> : arbol)
  }

  function llamadasA(ruta: string) {
    return apiGetMock.mock.calls.filter(([r]) => r === ruta).length
  }

  /** Deja la edición 1 sucia en el formulario (Nombre) Y en un hijo con estado propio
   * (`GestorDeCodigosBarra`: el código tipeado vive solo en ese componente, nunca en `Articulos`). */
  async function ensuciarEdicionUno() {
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')
    await userEvent.type(within(dialogo).getByPlaceholderText('Código de barras'), '7790001')
    await waitFor(() => expect(llamadasA('/articulos/1/codigos-barra')).toBe(1))
  }

  /**
   * Cláusula bajo prueba: el `confirm` de `cerrarModal` ANTES del `navigate` (Articulos.tsx).
   * Mutation-proof-tests: devolverle la decisión al efecto de apertura (navegar primero) hace
   * fallar `recorrido` — la URL pasa por /articulos antes de volver. Con el render todavía atado al
   * pathname en vivo, además, el modal se remonta: el código tipeado se pierde y los códigos de
   * barra se piden dos veces.
   */
  it('Cancelar con cambios sin guardar pregunta antes de navegar: rechazar conserva lo tipeado en el formulario y en los hijos', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    renderConHistorial()
    await ensuciarEdicionUno()

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(recorrido).toEqual(['/articulos/edit/1'])
    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Articulo Uno (editado)')
    expect(screen.getByPlaceholderText('Código de barras')).toHaveValue('7790001')
    expect(llamadasA('/articulos/1/codigos-barra')).toBe(1)
  })

  /**
   * Cláusula bajo prueba: el render del modal gobernado por el destino COMPROMETIDO
   * (`destinoMostrado`), no por el pathname en vivo. Mutation-proof-tests: volver a `{modo && ...}`
   * desmonta el modal apenas la URL de POP se commitea — el código tipeado se pierde y los códigos
   * de barra se piden dos veces aunque la URL termine restaurada.
   */
  it('POP (Atrás) con cambios sin guardar: rechazar restaura la URL sin desmontar el modal', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    renderConHistorial()
    await ensuciarEdicionUno()

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Articulo Uno (editado)')
    expect(screen.getByPlaceholderText('Código de barras')).toHaveValue('7790001')
    expect(llamadasA('/articulos/1/codigos-barra')).toBe(1)
  })

  /**
   * Cláusula bajo prueba: `descartarModal()` en `cerrarModal` antes del `navigate` — deja
   * comprometido el destino nulo para que el efecto de apertura no vuelva a preguntar.
   * Mutation-proof-tests: sacarlo (solo navegar) hace que el efecto vea una salida de un modal
   * sucio y pregunte por segunda vez.
   */
  it('Cancelar con cambios sin guardar, aceptando, cierra con una sola pregunta y deja la URL en /articulos', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderConHistorial()
    await ensuciarEdicionUno()

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Ubicación: /articulos')).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: la confirmación de una salida por URL vive en el efecto de apertura (una
   * corrida por cambio de `[modo, idParam]`), nunca en el render. Mutation-proof-tests: evaluar el
   * `confirm` durante el render hace que StrictMode lo duplique (pregunta dos veces); el test sin
   * StrictMode de arriba sigue verde con ese mutante, este no.
   */
  it('bajo StrictMode, una salida por POP pregunta exactamente una vez y rechazarla no vuelve a pedir los hijos', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    renderConHistorial({ estricto: true })
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')
    await userEvent.type(within(dialogo).getByPlaceholderText('Código de barras'), '7790001')
    const llamadasAntes = llamadasA('/articulos/1/codigos-barra')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()
    expect(screen.getByPlaceholderText('Código de barras')).toHaveValue('7790001')
    expect(llamadasA('/articulos/1/codigos-barra')).toBe(llamadasAntes)
  })

  it('alta → guardar pasa a /articulos/edit/{id} sin preguntar ni remontar el modal, y luego Atrás tampoco pregunta', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/articulos') return Promise.resolve(articuloFixture({ id: 5, codigoInterno: 'A0005', nombre: 'Nuevo Art' }))
      return Promise.reject(new Error(`POST no esperado en el test: ${ruta}`))
    })
    renderConHistorial({ entradas: ['/articulos', '/articulos/create'] })
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo artículo' })
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), 'Nuevo Art')

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Ubicación: /articulos/edit/5')).toBeInTheDocument()
    // Vacía los efectos del commit de la URL y el render que disparen ANTES de comparar: la URL
    // nueva aparece un commit antes de que el efecto de apertura comprometa el destino.
    await act(async () => {})
    expect(screen.getByText(/creado con código interno A0005/)).toBeInTheDocument()
    // Mismo nodo de diálogo: el modal nunca se desmontó en el paso de /create a /edit/5.
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0005' })).toBe(dialogo)

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))

    expect(confirmSpy).not.toHaveBeenCalled()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Ubicación: /articulos')).toBeInTheDocument()
  })

  it('de /articulos/edit/1 con cambios a /articulos/edit/2, aceptando, carga la edición 2', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderConHistorial()
    await ensuciarEdicionUno()

    await userEvent.click(screen.getByRole('button', { name: 'Ir a la edición 2' }))

    expect(await screen.findByRole('dialog', { name: 'Editando artículo A0002' })).toBeInTheDocument()
    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.getByLabelText('Nombre')).toHaveValue('Articulo Dos')
    expect(screen.getByText('Ubicación: /articulos/edit/2')).toBeInTheDocument()
  })
})

// ---- rechazar un Atrás/Adelante se deshace moviéndose en el historial, sin pisar entradas (H1) --

describe('Articulos — rechazar una salida por Atrás/Adelante vuelve a la entrada del modal sin pisar la de llegada (H1)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  let recorrido: string[] = []
  beforeEach(() => {
    recorrido = []
  })

  function Ubicacion() {
    const { pathname } = useLocation()
    useEffect(() => {
      recorrido.push(pathname)
    }, [pathname])
    return <p>Ubicación: {pathname}</p>
  }

  function ArnesConHistorial() {
    const navigate = useNavigate()
    return (
      <>
        <button type="button" onClick={() => navigate(-1)}>
          Atrás
        </button>
        <button type="button" onClick={() => navigate(1)}>
          Adelante
        </button>
        <button type="button" onClick={() => navigate(-2)}>
          Atrás dos
        </button>
        <button type="button" onClick={() => navigate('/articulos/edit/2')}>
          Ir a la edición 2
        </button>
        <Ubicacion />
        <Articulos />
      </>
    )
  }

  function renderDesde(ruta: string) {
    return render(
      <MemoryRouter initialEntries={[ruta]}>
        <Routes>
          <Route path="/articulos/*" element={<ArnesConHistorial />} />
        </Routes>
      </MemoryRouter>,
    )
  }

  function llamadasA(ruta: string) {
    return apiGetMock.mock.calls.filter(([r]) => r === ruta).length
  }

  /** Abre la edición 1 con el link "Editar" de la grilla, como el usuario: la pantalla ve pasar la
   * entrada de la grilla y la de la edición. Un `initialIndex` sobre entradas previas montaría la
   * pantalla directo en el modal, con la entrada de llegada sin posición conocida. */
  async function abrirEdicionUnoDesdeLaGrilla() {
    renderDesde('/articulos')
    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('link', { name: 'Editar' }))
    return screen.findByRole('dialog', { name: 'Editando artículo A0001' })
  }

  /**
   * Cláusula bajo prueba: `navigate(desplazamiento)` al rechazar una salida por POP (Articulos.tsx).
   * Mutation-proof-tests: volver al `replace` pisa la entrada de la grilla con la de la edición y el
   * segundo Atrás ya no llega a /articulos; invertir el signo del desplazamiento deja la URL en
   * /articulos con el modal montado.
   */
  it('Atrás rechazado vuelve a la edición con el mismo diálogo y lo tipeado; el siguiente Atrás aceptado llega a la grilla', async () => {
    vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValue(true)
    const dialogo = await abrirEdicionUnoDesdeLaGrilla()
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')
    await userEvent.type(within(dialogo).getByPlaceholderText('Código de barras'), '7790001')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await act(async () => {})

    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0001' })).toBe(dialogo)
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Articulo Uno (editado)')
    expect(within(dialogo).getByPlaceholderText('Código de barras')).toHaveValue('7790001')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await act(async () => {})

    expect(screen.getByText('Ubicación: /articulos')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Articulo Uno')).toBeInTheDocument()
  })

  it('tras rechazar un Atrás, otro Atrás aceptado pregunta una sola vez más y termina en /articulos', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValue(true)
    const dialogo = await abrirEdicionUnoDesdeLaGrilla()
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await act(async () => {})

    // Volver a la entrada del modal no es otra salida: no vuelve a preguntar.
    expect(confirmSpy).toHaveBeenCalledTimes(1)

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await act(async () => {})

    expect(confirmSpy).toHaveBeenCalledTimes(2)
    expect(recorrido).toEqual(['/articulos', '/articulos/edit/1', '/articulos', '/articulos/edit/1', '/articulos'])
    expect(screen.getByText('Ubicación: /articulos')).toBeInTheDocument()
  })

  it('Adelante rechazado vuelve a la edición 1; el siguiente Adelante aceptado carga la edición 2, que sigue en el historial', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValue(true)
    renderDesde('/articulos/edit/1')
    await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await userEvent.click(screen.getByRole('button', { name: 'Ir a la edición 2' }))
    await screen.findByRole('dialog', { name: 'Editando artículo A0002' })
    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    expect(confirmSpy).not.toHaveBeenCalled()
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Adelante' }))
    await act(async () => {})

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0001' })).toBe(dialogo)
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Articulo Uno (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Adelante' }))

    expect(await screen.findByRole('dialog', { name: 'Editando artículo A0002' })).toBeInTheDocument()
    expect(confirmSpy).toHaveBeenCalledTimes(2)
    expect(screen.getByLabelText('Nombre')).toHaveValue('Articulo Dos')
    expect(screen.getByText('Ubicación: /articulos/edit/2')).toBeInTheDocument()
  })

  it('con tres entradas, Atrás rechazado desde la edición 2 no pisa la edición 1: Atrás aceptado la carga y otro Atrás llega a la grilla', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValue(true)
    await abrirEdicionUnoDesdeLaGrilla()
    await userEvent.click(screen.getByRole('button', { name: 'Ir a la edición 2' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0002' })
    expect(confirmSpy).not.toHaveBeenCalled()
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await act(async () => {})

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.getByText('Ubicación: /articulos/edit/2')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0002' })).toBe(dialogo)
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Articulo Dos (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))

    expect(await screen.findByRole('dialog', { name: 'Editando artículo A0001' })).toBeInTheDocument()
    expect(confirmSpy).toHaveBeenCalledTimes(2)
    expect(screen.getByLabelText('Nombre')).toHaveValue('Articulo Uno')
    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await act(async () => {})

    expect(confirmSpy).toHaveBeenCalledTimes(2)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Ubicación: /articulos')).toBeInTheDocument()
    expect(screen.getByText('Articulo Uno')).toBeInTheDocument()
  })

  // Un POP que salta dos entradas (menú de historial del navegador) se deshace con el desplazamiento
  // completo, no con su signo: con ±1 aterrizaría en la edición 1 en vez de volver a la 2.
  it('un Atrás que salta dos entradas, rechazado, vuelve a la edición 2 sin pisar la 1 ni la grilla', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValue(true)
    await abrirEdicionUnoDesdeLaGrilla()
    await userEvent.click(screen.getByRole('button', { name: 'Ir a la edición 2' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0002' })
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás dos' }))
    await act(async () => {})

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.getByText('Ubicación: /articulos/edit/2')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0002' })).toBe(dialogo)
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Articulo Dos (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))

    expect(await screen.findByRole('dialog', { name: 'Editando artículo A0001' })).toBeInTheDocument()
    expect(confirmSpy).toHaveBeenCalledTimes(2)

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await act(async () => {})

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Ubicación: /articulos')).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el `replace` para una salida que NO es POP (Articulos.tsx). Rechazar un
   * PUSH reescribe la entrada que ese PUSH acaba de crear, así que la edición rechazada no queda
   * como entrada "adelante". Mutation-proof-tests: deshacer también el PUSH moviéndose
   * (`navigate(-1)`) la deja alcanzable y Adelante vuelve a preguntar por ella.
   */
  it('rechazar un link a otra edición no deja esa edición alcanzable con Adelante', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    const dialogo = await abrirEdicionUnoDesdeLaGrilla()
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Ir a la edición 2' }))
    await act(async () => {})

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Adelante' }))
    await act(async () => {})

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0001' })).toBe(dialogo)
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Articulo Uno (editado)')
    expect(llamadasA('/articulos/2')).toBe(0)
  })
})

// ---- catálogos inactivos en el formulario (fix/articulos-form-catalogos-inactivos) -------------
// Un catálogo (área/categoría/marca/grupo) o proveedor referenciado por un artículo puede estar
// desactivado (`activo: false`, la salida recomendada cuando la guarda de referencias rechaza el
// borrado) — el select de edición lo ofrece igual, con el sufijo "(inactiva)"/"(inactivo)".

describe('Articulos — catálogos inactivos en el formulario', () => {
  /**
   * Casos de los CINCO selects que ofrecen catálogos con opciones inactivas (Área, Categoría,
   * Marca, Grupo, Proveedor habitual). Comparten `opcionesConValorActual` y el sufijo
   * "(inactiva)"/"(inactivo)" en `FormularioArticulo.tsx`, pero cada uno es un call site propio
   * (mutation-proof-tests regla 15: una cláusula replicada en varios siblings necesita un kill por
   * sibling — sacar el helper o el sufijo de UNO solo no afecta a los demás).
   */
  type CasoCatalogoInactivo = {
    selectLabel: string
    sufijo: string
    opcionVacia: string
    idField: 'idArea' | 'idCategoria' | 'idMarca' | 'idGrupo' | 'idProveedorHabitual'
    etiquetaActiva: string
    etiquetaInactiva: string
    mockCatalogo: () => Partial<CatalogosDeTest>
  }

  function conReferencia(idField: CasoCatalogoInactivo['idField'], id: number): Partial<ArticuloListado> {
    switch (idField) {
      case 'idArea':
        return { idArea: id }
      case 'idCategoria':
        return { idCategoria: id }
      case 'idMarca':
        return { idMarca: id }
      case 'idGrupo':
        return { idGrupo: id }
      case 'idProveedorHabitual':
        return { idProveedorHabitual: id }
      default:
        throw new Error(`idField no soportado: ${idField as string}`)
    }
  }

  function casosCatalogosInactivos(): CasoCatalogoInactivo[] {
    return [
      {
        selectLabel: 'Área',
        sufijo: ' (inactiva)',
        opcionVacia: 'Elegir…',
        idField: 'idArea',
        etiquetaActiva: 'Almacén',
        etiquetaInactiva: 'Depósito',
        mockCatalogo: () => ({
          areas: [areaFixture({ id: 1, nombre: 'Almacén', activo: true }), areaFixture({ id: 2, nombre: 'Depósito', activo: false })],
        }),
      },
      {
        selectLabel: 'Categoría',
        sufijo: ' (inactiva)',
        opcionVacia: 'Sin especificar',
        idField: 'idCategoria',
        etiquetaActiva: 'Bebidas',
        etiquetaInactiva: 'Lácteos',
        mockCatalogo: () => ({
          categorias: [
            categoriaFixture({ id: 1, nombre: 'Bebidas', activo: true }),
            categoriaFixture({ id: 2, nombre: 'Lácteos', activo: false }),
          ],
        }),
      },
      {
        selectLabel: 'Marca',
        sufijo: ' (inactiva)',
        opcionVacia: 'Sin especificar',
        idField: 'idMarca',
        etiquetaActiva: 'Alfa',
        etiquetaInactiva: 'Beta',
        mockCatalogo: () => ({
          marcas: [marcaFixture({ id: 1, nombre: 'Alfa', activo: true }), marcaFixture({ id: 2, nombre: 'Beta', activo: false })],
        }),
      },
      {
        selectLabel: 'Grupo',
        sufijo: ' (inactivo)',
        opcionVacia: 'Sin especificar',
        idField: 'idGrupo',
        etiquetaActiva: 'Almacén',
        etiquetaInactiva: 'Limpieza',
        mockCatalogo: () => ({
          grupos: [grupoFixture({ id: 1, nombre: 'Almacén', activo: true }), grupoFixture({ id: 2, nombre: 'Limpieza', activo: false })],
        }),
      },
      {
        selectLabel: 'Proveedor habitual',
        sufijo: ' (inactivo)',
        opcionVacia: 'Sin especificar',
        idField: 'idProveedorHabitual',
        etiquetaActiva: 'Alfa SA',
        etiquetaInactiva: 'Beta SA',
        mockCatalogo: () => ({
          proveedores: [
            proveedorFixture({ id: 1, razonSocial: 'Alfa SA', activo: true }),
            proveedorFixture({ id: 2, razonSocial: 'Beta SA', activo: false }),
          ],
        }),
      },
    ]
  }

  it.each(casosCatalogosInactivos())(
    'en alta, el select de $selectLabel no ofrece una opción inactiva',
    async ({ selectLabel, opcionVacia, etiquetaActiva, mockCatalogo }) => {
      mockearApiGet(mockCatalogo())

      await abrirFormularioNuevo()

      const opciones = within(screen.getByLabelText(selectLabel))
        .getAllByRole('option')
        .map((o) => o.textContent)
      expect(opciones).toEqual([opcionVacia, etiquetaActiva])
    },
  )

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

  /**
   * Cláusula bajo prueba: el sufijo "(inactiva)"/"(inactivo)" de cada uno de los cinco selects y,
   * de paso (mutation-proof-tests regla 3), `listar(true)` de área/categoría/marca/grupo — si
   * alguno pasara a `listar(false)`, el mock (que discrimina el mismo query string, ver
   * `mockearApiGet`) dejaría de devolver la referencia inactiva y este mismo caso fallaría antes de
   * llegar al assert del sufijo (el select quedaría sin ninguna opción con ese valor).
   */
  it.each(casosCatalogosInactivos())(
    'al editar un artículo cuya referencia de $selectLabel está inactiva, la muestra con el sufijo y la mantiene seleccionada',
    async ({ selectLabel, sufijo, opcionVacia, idField, etiquetaActiva, etiquetaInactiva, mockCatalogo }) => {
      const conReferenciaInactiva = articuloFixture({ id: 1, ...conReferencia(idField, 2) })
      mockearApiGet({
        articulos: [conReferenciaInactiva, articuloDos],
        ...mockCatalogo(),
      })
      apiPutMock.mockResolvedValue(conReferenciaInactiva)
      renderArticulos()

      const fila = (await screen.findByText('Articulo Uno')).closest('tr')
      if (!fila) throw new Error('No se encontró la fila del artículo')
      await userEvent.click(within(fila).getByRole('link', { name: 'Editar' }))
      await screen.findByText('Editando artículo A0001')

      const select = screen.getByLabelText(selectLabel) as HTMLSelectElement
      expect(select).toHaveValue('2')
      expect(within(select).getAllByRole('option').map((o) => o.textContent)).toEqual([
        opcionVacia,
        etiquetaActiva,
        `${etiquetaInactiva}${sufijo}`,
      ])

      await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

      await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
      const [, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
      expect(cuerpo[idField]).toBe(2)
    },
  )
})

// ---- el formulario nunca clasifica una referencia como "colgante" contra el catálogo del cliente
// (F1, judgment-day round 1) ----------------------------------------------------------------------
// `abrirEdicion` clasificaba un id ausente del listado (áreas/categorías/marcas/grupos/
// proveedores) como baja lógica del catálogo y lo normalizaba a "sin asignar" — pero esa lista es
// SIEMPRE estado de cliente: puede estar todavía cargando, haber fallado en silencio (marcas/
// categorías/grupos/proveedores caen a `[]` sin aviso, ver el `useEffect` de carga), o venir
// truncada (proveedores, `tamanio=200`). Cualquiera de los tres clasifica una referencia REAL como
// colgante y el guardado sin tocar la pisa con `null` — pérdida silenciosa de datos válidos. El
// servidor es la única autoridad: cada id viaja intacto y, si de verdad no existe,
// `ServicioDeArticulos` lo rechaza con 400 `referencia_invalida` (`"No existe la marca {id}."`),
// que esta pantalla ya muestra vía `ErrorApi.message` sin cerrar el modal.
describe('Articulos — el formulario nunca clasifica una referencia como colgante contra el catálogo del cliente', () => {
  it('con un idMarca que no está en el catálogo cargado, guardar sin tocar el select reenvía el idMarca ORIGINAL en el PUT', async () => {
    const conMarcaAusenteDelCatalogo = articuloFixture({ id: 1, idMarca: 999 })
    mockearApiGet({
      articulos: [conMarcaAusenteDelCatalogo, articuloDos],
      marcas: [marcaFixture({ id: 1, nombre: 'Alfa', activo: true })],
    })
    apiPutMock.mockResolvedValue(conMarcaAusenteDelCatalogo)
    renderArticulos()

    const fila = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!fila) throw new Error('No se encontró la fila del artículo')
    await userEvent.click(within(fila).getByRole('link', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0001')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.idMarca).toBe(999)
  })

  it('si el servidor rechaza esa marca con 400 referencia_invalida, muestra el mensaje del servidor y el formulario sigue abierto', async () => {
    const conMarcaAusenteDelCatalogo = articuloFixture({ id: 1, idMarca: 999 })
    mockearApiGet({
      articulos: [conMarcaAusenteDelCatalogo, articuloDos],
      marcas: [marcaFixture({ id: 1, nombre: 'Alfa', activo: true })],
    })
    apiPutMock.mockRejectedValue(new ErrorApi(400, 'referencia_invalida', 'No existe la marca 999.'))
    renderArticulos()

    const fila = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!fila) throw new Error('No se encontró la fila del artículo')
    await userEvent.click(within(fila).getByRole('link', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0001')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('No existe la marca 999.')).toBeInTheDocument()
    expect(screen.getByText('Editando artículo A0001')).toBeInTheDocument()
  })

  it('abrir Editar antes de que resuelvan los catálogos (carrera) no descarta ninguna referencia al guardar sin tocar', async () => {
    // Valores todos DISTINTOS entre sí (mutation-proof-tests: una asignación cruzada entre campos
    // tiene que poder detectarse) — ninguno con opción cargada en su select, a propósito: la
    // prueba es que el guardado no depende en absoluto de qué haya (o no) en esas listas.
    const articuloCompleto = articuloFixture({
      id: 1,
      idArea: 1,
      idCategoria: 10,
      idMarca: 20,
      idGrupo: 30,
      idProveedorHabitual: 40,
    })

    let resolverAreas: (v: unknown) => void = () => {}
    let resolverCategorias: (v: unknown) => void = () => {}
    let resolverMarcas: (v: unknown) => void = () => {}
    let resolverGrupos: (v: unknown) => void = () => {}
    let resolverProveedores: (v: unknown) => void = () => {}
    const areasPendiente = new Promise((resolve) => {
      resolverAreas = resolve
    })
    const categoriasPendiente = new Promise((resolve) => {
      resolverCategorias = resolve
    })
    const marcasPendiente = new Promise((resolve) => {
      resolverMarcas = resolve
    })
    const gruposPendiente = new Promise((resolve) => {
      resolverGrupos = resolve
    })
    const proveedoresPendiente = new Promise((resolve) => {
      resolverProveedores = resolve
    })

    apiGetMock.mockImplementation((ruta: string) => {
      const articulos = [articuloCompleto, articuloDos]
      if (ruta.startsWith('/articulos/grilla')) return Promise.resolve(paginaGrillaFixture([filaGrillaUno, filaGrillaDos]))
      if (/^\/articulos\/\d+$/.test(ruta)) {
        const id = Number(ruta.split('/')[2])
        return Promise.resolve(articulos.find((a) => a.id === id) ?? articulos[0])
      }
      if (/^\/articulos\/\d+\/codigos-barra$/.test(ruta)) return Promise.resolve([])
      if (/^\/articulos\/\d+\/precios$/.test(ruta)) return Promise.resolve([])
      if (/^\/articulos\/\d+\/sugerencia-precio$/.test(ruta)) return Promise.resolve({ precioSugerido: 55.5 })
      // Colgados a propósito: no resuelven hasta que el test los libera, DESPUÉS de haber
      // abierto la edición (que ya resolvió el detalle del artículo).
      if (ruta.startsWith('/catalogos/areas')) return areasPendiente
      if (ruta.startsWith('/catalogos/categorias')) return categoriasPendiente
      if (ruta.startsWith('/catalogos/marcas')) return marcasPendiente
      if (ruta.startsWith('/catalogos/grupos')) return gruposPendiente
      if (ruta.startsWith('/proveedores')) return proveedoresPendiente
      if (ruta === '/catalogos-fiscales/alicuotas-iva')
        return Promise.resolve([{ id: 1, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true }])
      if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve([condicionFiscalFixture()])
      if (ruta === '/empresas') return Promise.resolve([])
      if (ruta === '/catalogos/listas-precio') return Promise.resolve([])
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    apiPutMock.mockResolvedValue(articuloCompleto)

    renderArticulos()

    const fila = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!fila) throw new Error('No se encontró la fila del artículo')
    await userEvent.click(within(fila).getByRole('link', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0001')

    // Los catálogos siguen sin resolver acá adentro. mutation-proof-tests regla 7: resolver DENTRO
    // de `act` y esperar la MISMA promesa que el componente espera, para que `setAreas`/etc. (que
    // se engancharon primero, en el mount) ya hayan corrido antes de seguir — un `waitFor` de
    // afuera podría pasar en el primer tick, antes de que el efecto tardío aplique.
    await act(async () => {
      resolverAreas([areaFixture({ id: 1 })])
      resolverCategorias([])
      resolverMarcas([])
      resolverGrupos([])
      resolverProveedores({ items: [], total: 0, pagina: 1, tamanio: 200 })
      await Promise.all([areasPendiente, categoriasPendiente, marcasPendiente, gruposPendiente, proveedoresPendiente])
    })

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.idArea).toBe(1)
    expect(cuerpo.idCategoria).toBe(10)
    expect(cuerpo.idMarca).toBe(20)
    expect(cuerpo.idGrupo).toBe(30)
    expect(cuerpo.idProveedorHabitual).toBe(40)
  })

  it('un proveedor habitual ausente de la página truncada de proveedores (total > items) conserva su id al guardar sin tocar', async () => {
    const conProveedorTruncado = articuloFixture({ id: 1, idProveedorHabitual: 555 })
    mockearApiGet({
      articulos: [conProveedorTruncado, articuloDos],
      proveedores: [proveedorFixture({ id: 1, razonSocial: 'Alfa SA' })],
      proveedoresTotal: 250,
    })
    apiPutMock.mockResolvedValue(conProveedorTruncado)
    renderArticulos()

    const fila = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!fila) throw new Error('No se encontró la fila del artículo')
    await userEvent.click(within(fila).getByRole('link', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0001')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.idProveedorHabitual).toBe(555)
  })
})
