import { StrictMode, useEffect } from 'react'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Articulos } from './Articulos'
import { Toaster } from '../toasts/Toaster'
import { ErrorApi } from '../api/cliente'
import { AuthContext } from '../auth/AuthContext'
import {
  ROL,
  type AlicuotaIvaListado,
  type AreaListado,
  type ArticuloListado,
  type CategoriaListado,
  type CondicionFiscalListado,
  type EmpresaListado,
  type FilaDeGrillaDeArticulos,
  type GrupoListado,
  type MarcaListado,
  type PaginaDeGrillaDeArticulos,
  type ProveedorListado,
  type UsuarioAutenticado,
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
    disponibleOnline: true,
    disponibleVenta: true,
    tieneImagen: false,
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

const articuloUno = articuloFixture({ id: 1, codigoInterno: 'A0001', nombre: 'Articulo Uno' })
const articuloDos = articuloFixture({ id: 2, codigoInterno: 'A0002', nombre: 'Articulo Dos' })

/** Fila de `GET /api/articulos/grilla` (articulos-grilla-web) — shape distinto del `ArticuloListado`
 * de arriba (que sigue sirviendo el detalle por id, `/articulos/{id}`). */
function filaGrillaFixture(sobrescribir: Partial<FilaDeGrillaDeArticulos> = {}): FilaDeGrillaDeArticulos {
  return {
    disponibleOnline: true,
    disponibleVenta: true,
    tieneImagen: false,
    id: 1,
    codigoInterno: 'A0001',
    nombre: 'Articulo Uno',
    precio: 100,
    costoNominal: 60,
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
    percibeIibb: false,
    percibeIva: false,
    preciosIncluyenIva: false,
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
    alicuotaPercepcionIibb: null,
    alicuotaPercepcionIva: null,
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
    // Familias (doc 10 §3): estos tests no usan familias, y sin familias el alta no ofrece el selector. Las
    // pruebas del selector, de la pertenencia y del alcance viven en `ArticulosFamilias.test.tsx`.
    if (ruta === '/familias') return Promise.resolve([])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

/**
 * `Articulos` deriva el modo del modal de `useLocation().pathname` (ver `articulos/rutaModal.ts`)
 * en vez de con `<Routes>` anidadas — se monta en una única entrada `/articulos/*`, igual que en
 * `App.tsx`, para que React Router no la remonte al abrir/cerrar el modal ni al pasar de
 * `/create` a `/edit/{id}` recién creado.
 */
function renderArticulos(rutaInicial = '/articulos', rolId?: number) {
  const pantalla = (
    <MemoryRouter initialEntries={[rutaInicial]}>
      <Routes>
        <Route path="/articulos/*" element={<Articulos />} />
      </Routes>
      <Toaster />
    </MemoryRouter>
  )
  if (rolId === undefined) return render(pantalla)

  const usuario: UsuarioAutenticado = { id: 1, usuario: 'ana', mail: 'ana@comercio.test', rolId, rol: 'Rol', ultimaConexion: null, idTenant: 1 }

  return render(
    <AuthContext.Provider value={{ usuario, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }}>
      {pantalla}
    </AuthContext.Provider>,
  )
}

async function abrirFormularioNuevo() {
  renderArticulos()
  await screen.findByText('Articulo Uno')
  await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
  await screen.findByText('Nuevo artículo')
}

/** Ninguna confirmación de esta pantalla usa `window.confirm`: devuelve `true` para que un regreso a él se note. */
let confirmSpy: ReturnType<typeof vi.spyOn>

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
  mockearApiGet()
  confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
})

afterEach(() => {
  expect(confirmSpy).not.toHaveBeenCalled()
  confirmSpy.mockRestore()
})

/** La pregunta de descarte abierta: un `alertdialog` apilado sobre el modal del artículo. */
async function preguntaDeDescarte() {
  return screen.findByRole('alertdialog', { name: 'Descartar cambios' })
}

async function descartarLosCambios() {
  await userEvent.click(within(await preguntaDeDescarte()).getByRole('button', { name: 'Descartar cambios' }))
}

async function seguirEditando() {
  await userEvent.click(within(await preguntaDeDescarte()).getByRole('button', { name: 'Cancelar' }))
}

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

describe('Articulos — toggle acumulaEnVenta', () => {
  async function abrirEdicionDelUno() {
    renderArticulos()
    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('link', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0001')
  }

  it('un artículo nuevo arranca acumulando (checkbox tildado)', async () => {
    await abrirFormularioNuevo()

    expect(screen.getByLabelText('Acumula en una sola línea al vender')).toBeChecked()
  })

  it('un artículo con acumulaEnVenta:false arranca con el checkbox destildado', async () => {
    mockearApiGet({ articulos: [articuloFixture({ id: 1, acumulaEnVenta: false })] })
    await abrirEdicionDelUno()

    expect(screen.getByLabelText('Acumula en una sola línea al vender')).not.toBeChecked()
  })

  it('destildar el checkbox y guardar manda acumulaEnVenta:false (booleano)', async () => {
    apiPutMock.mockResolvedValue(articuloFixture({ id: 1, acumulaEnVenta: false }))
    await abrirEdicionDelUno()

    await userEvent.click(screen.getByLabelText('Acumula en una sola línea al vender'))
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.acumulaEnVenta).toBe(false)
  })
})

describe('Articulos — artículo por importe', () => {
  async function abrirEdicionDelUno() {
    renderArticulos()
    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('link', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0001')
  }

  async function guardarYLeerCuerpo() {
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    return apiPutMock.mock.calls[0][1] as Record<string, unknown>
  }

  it('la unidad ofrece "Por importe"', async () => {
    await abrirFormularioNuevo()

    expect(within(screen.getByLabelText('Unidad de venta')).getByRole('option', { name: 'Por importe' })).toBeInTheDocument()
  })

  it('al elegir "Por importe" el checkbox queda deshabilitado y destildado, y aparece el selector de atajo', async () => {
    await abrirFormularioNuevo()
    expect(screen.queryByLabelText('Tecla de atajo del POS')).not.toBeInTheDocument()

    await userEvent.selectOptions(screen.getByLabelText('Unidad de venta'), 'Importe')

    const acumula = screen.getByLabelText('Acumula en una sola línea al vender')
    expect(acumula).toBeDisabled()
    expect(acumula).not.toBeChecked()
    const tecla = screen.getByLabelText('Tecla de atajo del POS')
    expect(within(tecla).getAllByRole('option').map((o) => o.textContent)).toEqual(['Sin atajo', 'F5', 'F6', 'F7', 'F8'])
  })

  it('guardar un artículo editado como "Por importe" con F6 manda acumulaEnVenta false y la tecla', async () => {
    apiPutMock.mockResolvedValue(articuloFixture({ id: 1, unidadVenta: 'Importe', acumulaEnVenta: false, teclaAtajo: 'F6' }))
    await abrirEdicionDelUno()

    await userEvent.selectOptions(screen.getByLabelText('Unidad de venta'), 'Importe')
    await userEvent.selectOptions(screen.getByLabelText('Tecla de atajo del POS'), 'F6')
    const cuerpo = await guardarYLeerCuerpo()

    expect(cuerpo).toMatchObject({ unidadVenta: 'Importe', acumulaEnVenta: false, teclaAtajo: 'F6' })
  })

  it('volver a "Por unidad" devuelve el checkbox tildado, quita el selector y manda teclaAtajo null', async () => {
    apiPutMock.mockResolvedValue(articuloFixture({ id: 1 }))
    await abrirEdicionDelUno()

    await userEvent.selectOptions(screen.getByLabelText('Unidad de venta'), 'Importe')
    await userEvent.selectOptions(screen.getByLabelText('Tecla de atajo del POS'), 'F5')
    await userEvent.selectOptions(screen.getByLabelText('Unidad de venta'), 'Unidad')

    const acumula = screen.getByLabelText('Acumula en una sola línea al vender')
    expect(acumula).toBeEnabled()
    expect(acumula).toBeChecked()
    expect(screen.queryByLabelText('Tecla de atajo del POS')).not.toBeInTheDocument()
    const cuerpo = await guardarYLeerCuerpo()
    expect(cuerpo).toMatchObject({ unidadVenta: 'Unidad', acumulaEnVenta: true, teclaAtajo: null })
  })

  it('guardar sin tocar un artículo por importe con tecla reenvía la tecla guardada', async () => {
    mockearApiGet({ articulos: [articuloFixture({ id: 1, unidadVenta: 'Importe', acumulaEnVenta: false, teclaAtajo: 'F7' })] })
    apiPutMock.mockResolvedValue(articuloFixture({ id: 1, unidadVenta: 'Importe', acumulaEnVenta: false, teclaAtajo: 'F7' }))
    await abrirEdicionDelUno()

    expect(screen.getByLabelText('Tecla de atajo del POS')).toHaveValue('F7')
    expect(screen.getByLabelText('Acumula en una sola línea al vender')).toBeDisabled()
    const cuerpo = await guardarYLeerCuerpo()

    expect(cuerpo).toMatchObject({ unidadVenta: 'Importe', acumulaEnVenta: false, teclaAtajo: 'F7' })
  })

  it('una tecla ya tomada por otro artículo se avisa junto al selector y no en el banner', async () => {
    apiPutMock.mockRejectedValue(new ErrorApi(409, 'tecla_atajo_duplicada', 'Ya hay un artículo con la tecla de atajo F8 en este tenant.'))
    await abrirEdicionDelUno()

    await userEvent.selectOptions(screen.getByLabelText('Unidad de venta'), 'Importe')
    await userEvent.selectOptions(screen.getByLabelText('Tecla de atajo del POS'), 'F8')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('La tecla F8 ya la usa otro artículo. Elegí otra o dejá este sin atajo.')).toBeInTheDocument()
    expect(screen.queryByText(/en este tenant/)).not.toBeInTheDocument()

    await userEvent.selectOptions(screen.getByLabelText('Tecla de atajo del POS'), 'F5')
    expect(screen.queryByText(/ya la usa otro artículo/)).not.toBeInTheDocument()
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

    // El select de alícuota se renderiza SIEMPRE y sin `disabled`, con solo `Elegir…` hasta que
    // llega el catálogo, así que esperar al diálogo no prueba que las opciones estén y
    // `toBeEnabled()` pasaría en el primer tick sin probar nada. La opción concreta es la única
    // señal de que el dato aterrizó (web-test-data-gates): sin esto, `selectOptions` corre contra
    // un select vacío y tira "Value 10 not found in options" en cuanto la máquina está cargada.
    await screen.findByRole('option', { name: 'IVA 27% (27%)' })

    // El usuario elige la 27%, no la del default (21%), ANTES de que el área resuelva.
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
   * formulario completo a la base hace fallar la pregunta de descarte — el Nombre tipeado antes de
   * que llegaran los catálogos pasaría a contar como "guardado" y se descartaría sin preguntar.
   */
  it('lo tipeado antes de que lleguen los catálogos sigue contando como cambio sin guardar', async () => {
    const { dialogo, resolverCatalogos } = await abrirAltaConCatalogosPendientes()
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), 'Borrador temprano')
    await resolverCatalogos()

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))
    await seguirEditando()

    expect(screen.getByRole('dialog', { name: 'Nuevo artículo' })).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Borrador temprano')
  })

  /**
   * Cláusula bajo prueba: el parche de `idArea` e `idAlicuotaIva` sobre la base limpia en el mismo
   * efecto. Mutation-proof-tests: sacar ese parche (o uno solo de sus dos campos) hace fallar el
   * `expect` de la pregunta de descarte — el auto-completado dejaría un falso "hay cambios sin guardar".
   */
  it('sin tocar nada, el auto-completado de los defaults no deja cambios sin guardar', async () => {
    const { dialogo, resolverCatalogos } = await abrirAltaConCatalogosPendientes()
    await resolverCatalogos()

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
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

  it('en la pantalla de artículos el consejo para reintentar sigue siendo recargar la página', async () => {
    mockearApiGet({ areasImpl: () => Promise.reject(new Error('sin red')) })

    await abrirFormularioNuevo()
    const dialogo = screen.getByRole('dialog', { name: 'Nuevo artículo' })

    expect(await within(dialogo).findByText(/hasta que se puedan cargar — recargá la página para reintentar\./)).toBeInTheDocument()
    expect(within(dialogo).queryByText(/cerrá el alta/i)).not.toBeInTheDocument()
  })

  it('un fallo de las listas de precio en la pantalla de artículos también aconseja recargar la página', async () => {
    mockearApiGet()
    const base = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/catalogos/listas-precio' ? Promise.reject(new Error('sin red')) : base(ruta),
    )

    await abrirFormularioNuevo()
    const dialogo = screen.getByRole('dialog', { name: 'Nuevo artículo' })

    expect(await within(dialogo).findByText(/el editor de precios no está disponible\. Recargá la página para reintentar\./)).toBeInTheDocument()
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

    // Misma FORMA que el select de alícuota — `categorias` le llega a AltaRapidaCategoria por prop
    // desde Articulos.tsx, el select se renderiza sin esperarla y `abrirFormularioNuevo` solo
    // prueba que llegó el listado de artículos, que es otro fetch — pero acá el gate NO está
    // probado: con las categorías demoradas 400ms el test pasa igual, porque el `type` de once
    // caracteres de arriba ya da tiempo de sobra. Solo muere si nunca resuelven. Queda como
    // resguardo por la regla de web-test-data-gates (nunca actuar sobre el dato habiendo esperado
    // solo al elemento), no como arreglo de una falla observada: si ese `type` se acorta o se va,
    // esta es la línea que evita la carrera.
    await within(dialogo).findByRole('option', { name: 'Bebidas' })

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
    expect(within(dialogo).getByLabelText('Margen sugerido (%)')).toHaveValue('15')
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
    // Lectura de OPCIONES, no del elemento: el select se renderiza siempre y sin `disabled`, así que
    // nada garantiza que `categorias` (otro fetch, prop desde Articulos.tsx) ya esté.
    //
    // Este gate NO está probado, y es la referencia para los otros dos de abajo: el `click`
    // awaiteado de arriba descarga suficiente trabajo de React como para que el fixture aterrice,
    // y con las categorías demoradas 400ms el `expect` pelado pasaba igual — solo muere si nunca
    // resuelven. Va como resguardo por la regla de web-test-data-gates (nunca actuar sobre el dato
    // habiendo esperado solo al elemento), no como arreglo de una falla observada: esa protección
    // es incidental y depende de cuántos flushes dispare ese `click`. El único sitio de este
    // archivo con falla REAL y probada es el select de alícuota, que no tiene nada awaiteado en el
    // medio.
    await waitFor(() =>
      expect(within(selectPadre).getAllByRole('option').map((o) => o.textContent)).toEqual([
        '— Ninguna (raíz) —',
        'Bebidas',
        'Lácteos',
      ]),
    )
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
    // Misma lectura de opciones que el test de arriba, y gate igualmente NO probado por el mismo
    // `click` awaiteado — ver el detalle allá.
    await waitFor(() =>
      expect(within(selectPadre).getAllByRole('option').map((o) => o.textContent)).toEqual(['— Ninguna (raíz) —', 'Bebidas']),
    )
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

    // Orden inicial ya viene por etiqueta, no por como llegó del servidor. La espera es por el
    // mismo motivo que los dos selects de categoría padre: `abrirFormularioNuevo` solo prueba que
    // llegó el listado de artículos, y `/proveedores` es otro fetch. Gate igualmente NO probado —
    // el `click` awaiteado que hay dentro de `abrirFormularioNuevo` alcanza para que el fixture
    // aterrice; ver el detalle en el primer select de categoría padre.
    await waitFor(() =>
      expect(within(selectProveedor).getAllByRole('option').map((o) => o.textContent)).toEqual([
        'Sin especificar',
        'Alfa SA',
        'Zeta SA',
      ]),
    )

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
      percibeIibb: false,
      percibeIva: false,
      preciosIncluyenIva: false,
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
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
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
    await abrirFormularioNuevo()

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('con cambios sin guardar, cerrar abre una confirmación en un modal apilado sin cerrar todavía el del artículo', async () => {
    await abrirFormularioNuevo()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Borrador sin guardar')

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    const pregunta = await preguntaDeDescarte()
    expect(pregunta).toHaveTextContent('¿Descartar los cambios sin guardar del artículo?')
    expect(screen.getByRole('dialog', { name: 'Nuevo artículo', hidden: true })).toBeInTheDocument()
  })

  it('con cambios sin guardar, cancelar la confirmación mantiene el modal abierto con lo tipeado', async () => {
    await abrirFormularioNuevo()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Borrador sin guardar')

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    await seguirEditando()

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Nuevo artículo' })).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Borrador sin guardar')
  })

  it('con cambios sin guardar, aceptar la confirmación cierra el modal', async () => {
    await abrirFormularioNuevo()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Borrador descartado')

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    await descartarLosCambios()

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('un doble clic sincrónico en Descartar cambios cierra el modal y la confirmación y deja la grilla', async () => {
    await abrirFormularioNuevo()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Borrador descartado')
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    const descartar = within(await preguntaDeDescarte()).getByRole('button', { name: 'Descartar cambios' })

    await act(async () => {
      descartar.click()
      descartar.click()
      await Promise.resolve()
    })

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Articulo Uno')).toBeInTheDocument()
  })

  it('Escape sobre la confirmación la cierra y deja el modal del artículo abierto con lo tipeado', async () => {
    await abrirFormularioNuevo()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Borrador sin guardar')
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    await preguntaDeDescarte()

    fireEvent.keyDown(document, { key: 'Escape' })

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Nuevo artículo' })).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Borrador sin guardar')
  })

  /**
   * Cláusula bajo prueba: la normalización (orden) de `idsEmpresas` en `normalizarParaComparar`
   * (Articulos.tsx). Mutation-proof-tests: comparar con `JSON.stringify` sin normalizar hace
   * fallar este test — destildar y volver a tildar reordena el array (filter + append al final)
   * aunque el conjunto final sea idéntico al original, y la pregunta de descarte se abriría.
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

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
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
    renderConHistorial()
    await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await userEvent.type(screen.getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await seguirEditando()

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0001' })).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Articulo Uno (editado)')
  })

  it('POP (Atrás) con el formulario sucio, aceptando la confirmación, cierra el modal', async () => {
    renderConHistorial()
    await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await userEvent.type(screen.getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await descartarLosCambios()

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('POP (Atrás) con el formulario limpio no pregunta nada', async () => {
    renderConHistorial()
    await screen.findByRole('dialog', { name: 'Editando artículo A0001' })

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
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
    await abrirFormularioNuevo()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Borrador')
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    await descartarLosCambios()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    const evento = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(evento)
    expect(evento.defaultPrevented).toBe(false)
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

    expect(screen.getByRole('button', { name: 'Agregar' })).toHaveFocus()
  })
})

describe('Articulos — Baja de un artículo desde la grilla', () => {
  /**
   * Cláusula bajo prueba: el `toast.exito` de `confirmarBaja()` en `Articulos.tsx` y el bump de
   * `pedidoDeRefresco` que le pide a `GrillaDeArticulos` refetchear. Mutation-proof-tests: borrar
   * cualquiera de los dos deja este test sin ver el aviso o sin ver la fila actualizada tras el
   * refresco (la fixture de la grilla cambia DESPUÉS de la Baja, así que solo el refresco real la
   * revela).
   */
  it('una Baja exitosa muestra el aviso "dado de baja" y refresca la grilla con los datos nuevos', async () => {
    apiDeleteMock.mockResolvedValue(undefined)
    renderArticulos()

    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')

    // Tras la Baja, el servidor ya no devuelve "Articulo Uno" — la única forma de verlo
    // desaparecer es que la grilla haya vuelto a pedir el listado.
    mockearApiGet({ grilla: paginaGrillaFixture([filaGrillaDos]) })

    await userEvent.click(within(filaUno).getByRole('button', { name: 'Eliminar' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmar baja' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Artículo "Articulo Uno" dado de baja.')
    expect(document.body.querySelector('.alert-success')).toBeNull()
    await waitFor(() => expect(screen.queryByText('Articulo Uno')).not.toBeInTheDocument())
    expect(screen.getByText('Articulo Dos')).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  it('Eliminar abre una confirmación que nombra el artículo y no escribe hasta confirmar', async () => {
    renderArticulos()
    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')

    await userEvent.click(within(filaUno).getByRole('button', { name: 'Eliminar' }))

    expect(await screen.findByRole('alertdialog')).toHaveTextContent('¿Dar de baja el artículo "Articulo Uno"?')
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  it('cancelar la confirmación no da de baja y devuelve el foco al botón Eliminar', async () => {
    renderArticulos()
    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    const eliminar = within(filaUno).getByRole('button', { name: 'Eliminar' })

    await userEvent.click(eliminar)
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(eliminar).toHaveFocus()
  })

  /**
   * Cláusula bajo prueba: el disparador que `GrillaDeArticulos` captura en el `onClick` y `pedirBaja` guarda. Con
   * `fireEvent.click` el botón no recibe foco: leer `document.activeElement` daría `<body>` y el foco no volvería.
   */
  it('el foco vuelve al botón pulsado al cancelar la confirmación aunque ese botón no hubiera recibido el foco', async () => {
    renderArticulos()
    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    const eliminar = within(filaUno).getByRole('button', { name: 'Eliminar' })

    fireEvent.click(eliminar)
    expect(eliminar).not.toHaveFocus()
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cancelar' }))

    expect(eliminar).toHaveFocus()
  })

  /**
   * Cláusula bajo prueba: `eliminandoRef` de `confirmarBaja()`. La guarda de `ConfirmacionDeBaja` frena el segundo
   * clic en el mismo tick por su cuenta; este test prueba el conjunto, no el ref solo.
   */
  it('un doble clic sincrónico en Confirmar baja emite un solo DELETE y la confirmación queda inerte mientras escribe', async () => {
    let resolver: () => void = () => {}
    apiDeleteMock.mockReturnValue(new Promise<void>((r) => (resolver = r)))
    renderArticulos()
    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('button', { name: 'Eliminar' }))
    const confirmacion = await screen.findByRole('alertdialog')
    const confirmar = within(confirmacion).getByRole('button', { name: 'Confirmar baja' })

    act(() => {
      confirmar.click()
      confirmar.click()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    await waitFor(() => expect(within(confirmacion).getByRole('button', { name: 'Dando de baja…' })).toBeDisabled())
    expect(within(confirmacion).getByRole('button', { name: 'Cancelar' })).toBeDisabled()

    await act(async () => resolver())
    await screen.findByText('Artículo "Articulo Uno" dado de baja.')
  })

  /**
   * Cláusula bajo prueba: el `catch` de `confirmarBaja()` en `Articulos.tsx` (`setErrorDeBaja` con el mensaje de
   * `ErrorApi`, mostrado dentro de la confirmación, que sigue abierta). Mutation-proof-tests: que ese `catch` no
   * seteara el error (o cerrara la confirmación) haría fallar los `expect` de abajo.
   */
  it('una Baja rechazada por el servidor muestra el error dentro de la confirmación, que sigue abierta, sin aviso de éxito', async () => {
    apiDeleteMock.mockRejectedValue(new ErrorApi(409, 'articulo_en_uso', 'El artículo está en uso y no se puede dar de baja.'))
    renderArticulos()

    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')

    await userEvent.click(within(filaUno).getByRole('button', { name: 'Eliminar' }))
    const confirmacion = await screen.findByRole('alertdialog')
    await userEvent.click(within(confirmacion).getByRole('button', { name: 'Confirmar baja' }))

    expect(await within(confirmacion).findByRole('alert')).toHaveTextContent('El artículo está en uso y no se puede dar de baja.')
    expect(screen.getAllByText('El artículo está en uso y no se puede dar de baja.')).toHaveLength(1)
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
    expect(screen.queryByText(/dado de baja\./)).not.toBeInTheDocument()
    expect(screen.getByText('Articulo Uno')).toBeInTheDocument()
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

    renderArticulos()
    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('button', { name: 'Eliminar' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmar baja' }))

    await userEvent.click(within(filaUno).getByRole('link', { name: 'Editar' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await act(async () => {
      resolverBaja()
      await bajaEnVuelo
    })
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
   * Cláusula bajo prueba: la pregunta de descarte de `cerrarModal` ANTES del `navigate` (Articulos.tsx).
   * Mutation-proof-tests: devolverle la decisión al efecto de apertura (navegar primero) hace
   * fallar `recorrido` — la URL pasa por /articulos antes de volver. Con el render todavía atado al
   * pathname en vivo, además, el modal se remonta: el código tipeado se pierde y los códigos de
   * barra se piden dos veces.
   */
  it('Cancelar con cambios sin guardar pregunta antes de navegar: rechazar conserva lo tipeado en el formulario y en los hijos', async () => {
    renderConHistorial()
    await ensuciarEdicionUno()

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    await preguntaDeDescarte()
    expect(recorrido).toEqual(['/articulos/edit/1'])
    await seguirEditando()

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
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
    renderConHistorial()
    await ensuciarEdicionUno()

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await seguirEditando()
    await act(async () => {})

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
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
    renderConHistorial()
    await ensuciarEdicionUno()

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    await descartarLosCambios()

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Ubicación: /articulos')).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: la pregunta de una salida por URL vive en el efecto de apertura (una
   * corrida por cambio de `[modo, idParam]`), nunca en el render. Mutation-proof-tests: abrirla
   * durante el render haría que StrictMode la duplique (dos diálogos de descarte, y `findByRole`
   * falla por ambigüedad); el test sin StrictMode de arriba sigue verde con ese mutante, este no.
   */
  it('bajo StrictMode, una salida por POP abre una sola pregunta y rechazarla no vuelve a pedir los hijos', async () => {
    renderConHistorial({ estricto: true })
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')
    await userEvent.type(within(dialogo).getByPlaceholderText('Código de barras'), '7790001')
    const llamadasAntes = llamadasA('/articulos/1/codigos-barra')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    expect(await screen.findAllByRole('alertdialog')).toHaveLength(1)
    await seguirEditando()
    await act(async () => {})

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()
    expect(screen.getByPlaceholderText('Código de barras')).toHaveValue('7790001')
    expect(llamadasA('/articulos/1/codigos-barra')).toBe(llamadasAntes)
  })

  /**
   * Cláusula bajo prueba: `if (articulo.ocupado) { deshacerSalida(...) }` del efecto de apertura. Mutation-proof-tests:
   * sacarla abre la pregunta de descarte con el alta guardándose; la respuesta reemplaza la URL a /edit/{id} y la
   * pregunta queda colgada (el `expect` de `alertdialog` de abajo falla).
   */
  it('Atrás con el alta guardándose no abre ninguna pregunta: deshace la salida y la respuesta deja el modal en /articulos/edit/{id}', async () => {
    let resolver: () => void = () => {}
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta !== '/articulos') return Promise.reject(new Error(`POST no esperado en el test: ${ruta}`))
      return new Promise((resolve) => {
        resolver = () => resolve(articuloFixture({ id: 5, codigoInterno: 'A0005', nombre: 'Nuevo Art' }))
      })
    })
    renderConHistorial({ entradas: ['/articulos', '/articulos/create'] })
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo artículo' })
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), 'Nuevo Art')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await act(async () => {})

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByText('Ubicación: /articulos/create')).toBeInTheDocument()

    await act(async () => resolver())

    expect(await screen.findByText('Ubicación: /articulos/edit/5')).toBeInTheDocument()
    await act(async () => {})
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0005' })).toBe(dialogo)
  })

  it('alta → guardar pasa a /articulos/edit/{id} sin preguntar ni remontar el modal, y luego Atrás tampoco pregunta', async () => {
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

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Ubicación: /articulos')).toBeInTheDocument()
  })

  it('de /articulos/edit/1 con cambios a /articulos/edit/2, aceptando, carga la edición 2', async () => {
    renderConHistorial()
    await ensuciarEdicionUno()

    await userEvent.click(screen.getByRole('button', { name: 'Ir a la edición 2' }))
    await descartarLosCambios()

    expect(await screen.findByRole('dialog', { name: 'Editando artículo A0002' })).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Articulo Dos')
    expect(screen.getByText('Ubicación: /articulos/edit/2')).toBeInTheDocument()
  })
})

// ---- rechazar un Atrás/Adelante se deshace moviéndose en el historial, sin pisar entradas (H1) --

describe('Articulos — rechazar una salida por Atrás/Adelante vuelve a la entrada del modal sin pisar la de llegada (H1)', () => {
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
    const dialogo = await abrirEdicionUnoDesdeLaGrilla()
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')
    await userEvent.type(within(dialogo).getByPlaceholderText('Código de barras'), '7790001')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await seguirEditando()
    await act(async () => {})

    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0001' })).toBe(dialogo)
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Articulo Uno (editado)')
    expect(within(dialogo).getByPlaceholderText('Código de barras')).toHaveValue('7790001')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await descartarLosCambios()
    await act(async () => {})

    expect(screen.getByText('Ubicación: /articulos')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Articulo Uno')).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el `setDescarte` de la rama `destino === destinoModalRef.current` del efecto de apertura
   * (Articulos.tsx). Mutation-proof-tests: sacarlo deja abierta la pregunta de una salida que la URL ya deshizo, y
   * confirmarla aplicaría un destino viejo.
   */
  it('Adelante mientras se pregunta por un Atrás cierra la pregunta: la URL volvió al modal y no queda nada que decidir', async () => {
    const dialogo = await abrirEdicionUnoDesdeLaGrilla()
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')
    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await preguntaDeDescarte()

    await userEvent.click(screen.getByText('Adelante'))

    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument())
    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0001' })).toBe(dialogo)
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Articulo Uno (editado)')
  })

  it('tras rechazar un Atrás, otro Atrás aceptado pregunta una sola vez más y termina en /articulos', async () => {
    const dialogo = await abrirEdicionUnoDesdeLaGrilla()
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await seguirEditando()
    await act(async () => {})

    // Volver a la entrada del modal no es otra salida: no vuelve a preguntar.
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await descartarLosCambios()
    await act(async () => {})

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(recorrido).toEqual(['/articulos', '/articulos/edit/1', '/articulos', '/articulos/edit/1', '/articulos'])
    expect(screen.getByText('Ubicación: /articulos')).toBeInTheDocument()
  })

  it('Adelante rechazado vuelve a la edición 1; el siguiente Adelante aceptado carga la edición 2, que sigue en el historial', async () => {
    renderDesde('/articulos/edit/1')
    await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await userEvent.click(screen.getByRole('button', { name: 'Ir a la edición 2' }))
    await screen.findByRole('dialog', { name: 'Editando artículo A0002' })
    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Adelante' }))
    await seguirEditando()
    await act(async () => {})

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0001' })).toBe(dialogo)
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Articulo Uno (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Adelante' }))
    await descartarLosCambios()

    expect(await screen.findByRole('dialog', { name: 'Editando artículo A0002' })).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Articulo Dos')
    expect(screen.getByText('Ubicación: /articulos/edit/2')).toBeInTheDocument()
  })

  it('con tres entradas, Atrás rechazado desde la edición 2 no pisa la edición 1: Atrás aceptado la carga y otro Atrás llega a la grilla', async () => {
    await abrirEdicionUnoDesdeLaGrilla()
    await userEvent.click(screen.getByRole('button', { name: 'Ir a la edición 2' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0002' })
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await seguirEditando()
    await act(async () => {})

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByText('Ubicación: /articulos/edit/2')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0002' })).toBe(dialogo)
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Articulo Dos (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await descartarLosCambios()

    expect(await screen.findByRole('dialog', { name: 'Editando artículo A0001' })).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Articulo Uno')
    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await act(async () => {})

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Ubicación: /articulos')).toBeInTheDocument()
    expect(screen.getByText('Articulo Uno')).toBeInTheDocument()
  })

  // Un POP que salta dos entradas (menú de historial del navegador) se deshace con el desplazamiento
  // completo, no con su signo: con ±1 aterrizaría en la edición 1 en vez de volver a la 2.
  it('un Atrás que salta dos entradas, rechazado, vuelve a la edición 2 sin pisar la 1 ni la grilla', async () => {
    await abrirEdicionUnoDesdeLaGrilla()
    await userEvent.click(screen.getByRole('button', { name: 'Ir a la edición 2' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0002' })
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás dos' }))
    await seguirEditando()
    await act(async () => {})

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByText('Ubicación: /articulos/edit/2')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0002' })).toBe(dialogo)
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Articulo Dos (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Atrás' }))
    await descartarLosCambios()

    expect(await screen.findByRole('dialog', { name: 'Editando artículo A0001' })).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()

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
    const dialogo = await abrirEdicionUnoDesdeLaGrilla()
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')

    await userEvent.click(screen.getByRole('button', { name: 'Ir a la edición 2' }))
    await seguirEditando()
    await act(async () => {})

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByText('Ubicación: /articulos/edit/1')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Adelante' }))
    await act(async () => {})

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
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

describe('Articulos — advertencia de código de barras que no parece un GTIN (GestorDeCodigosBarra)', () => {
  const RUTA_CODIGOS = '/articulos/1/codigos-barra'
  // El dígito verificador correcto de 779000123456x es 8: este código es un tipeo típico.
  const CODIGO_CON_VERIFICADOR_MALO = '7790001234567'
  const CODIGO_VALIDO = '7790001234568'
  const MOTIVO_VERIFICADOR = 'El dígito verificador no coincide: puede haber un error de tipeo.'

  function llamadasDeAlta() {
    return apiPostMock.mock.calls.filter(([ruta]) => ruta === RUTA_CODIGOS)
  }

  /** Abre la edición 1 y espera a que `GestorDeCodigosBarra` termine de hidratar: el input se
   * renderiza antes que el dato, y "Sin códigos…" solo aparece cuando la lista ya llegó. */
  async function abrirGestor() {
    apiPostMock.mockImplementation((ruta: string, cuerpo: { codigo: string }) => {
      if (ruta === RUTA_CODIGOS) return Promise.resolve({ id: 70, idArticulo: 1, codigo: cuerpo.codigo, activo: true })
      return Promise.reject(new Error(`POST no esperado en el test: ${ruta}`))
    })
    renderArticulos('/articulos/edit/1')
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await within(dialogo).findByText('Sin códigos de barra cargados.')
    return {
      entrada: within(dialogo).getByPlaceholderText('Código de barras'),
      botonAgregar: within(dialogo).getByRole('button', { name: 'Agregar' }),
      dialogo,
    }
  }

  /**
   * Cláusula bajo prueba: la llamada a `advertenciaDeCodigoBarra` en `intentarAgregar`. Mutation-
   * proof-tests: enviar siempre (sin consultar al helper) deja el POST emitido y esta prueba falla.
   */
  it('un código con el verificador equivocado muestra la advertencia y no llama a la API', async () => {
    const { entrada, botonAgregar, dialogo } = await abrirGestor()

    await userEvent.type(entrada, CODIGO_CON_VERIFICADOR_MALO)
    await userEvent.click(botonAgregar)

    expect(within(dialogo).getByText(MOTIVO_VERIFICADOR)).toBeInTheDocument()
    expect(within(dialogo).getByRole('button', { name: 'Agregar igual' })).toBeEnabled()
    expect(llamadasDeAlta()).toHaveLength(0)
    expect(entrada).toHaveValue(CODIGO_CON_VERIFICADOR_MALO)
  })

  it('"Agregar igual" envía el código recortado, lo agrega a la lista y retira la advertencia', async () => {
    const { entrada, botonAgregar, dialogo } = await abrirGestor()

    await userEvent.type(entrada, '  ABC-123  ')
    await userEvent.click(botonAgregar)
    expect(within(dialogo).getByText(/no son dígitos/)).toBeInTheDocument()
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Agregar igual' }))

    expect(await within(dialogo).findByText('ABC-123')).toBeInTheDocument()
    expect(llamadasDeAlta()).toEqual([[RUTA_CODIGOS, { codigo: 'ABC-123' }]])
    expect(within(dialogo).queryByRole('button', { name: 'Agregar igual' })).not.toBeInTheDocument()
    expect(entrada).toHaveValue('')
  })

  /**
   * Cláusula bajo prueba: `setPendiente(null)` en el `onChange` del input. Mutation-proof-tests:
   * sacarlo deja la advertencia (y "Agregar igual") visible sobre un código ya distinto.
   */
  it('editar el código retira la advertencia pendiente', async () => {
    const { entrada, botonAgregar, dialogo } = await abrirGestor()
    await userEvent.type(entrada, CODIGO_CON_VERIFICADOR_MALO)
    await userEvent.click(botonAgregar)
    expect(within(dialogo).getByRole('button', { name: 'Agregar igual' })).toBeInTheDocument()

    await userEvent.type(entrada, '0')

    expect(within(dialogo).queryByRole('button', { name: 'Agregar igual' })).not.toBeInTheDocument()
    expect(within(dialogo).queryByText(MOTIVO_VERIFICADOR)).not.toBeInTheDocument()
    expect(llamadasDeAlta()).toHaveLength(0)
  })

  /**
   * Cláusula bajo prueba: Enter solo llama a `intentarAgregar` y nunca a `enviar` (onKeyDown del
   * input). Mutation-proof-tests: hacer que, con una advertencia pendiente, Enter llame a
   * `enviar(pendiente.codigo)` emite el POST en el segundo Enter y esta prueba falla.
   */
  it('Enter con un código dudoso solo advierte, y un segundo Enter no lo confirma', async () => {
    const { entrada, dialogo } = await abrirGestor()

    await userEvent.type(entrada, `${CODIGO_CON_VERIFICADOR_MALO}{Enter}`)
    expect(within(dialogo).getByText(MOTIVO_VERIFICADOR)).toBeInTheDocument()
    expect(llamadasDeAlta()).toHaveLength(0)

    await userEvent.type(entrada, '{Enter}')
    await userEvent.type(entrada, '{Enter}')

    expect(within(dialogo).getByText(MOTIVO_VERIFICADOR)).toBeInTheDocument()
    expect(llamadasDeAlta()).toHaveLength(0)
  })

  it('un código válido se envía directo con el botón, sin advertencia', async () => {
    const { entrada, botonAgregar, dialogo } = await abrirGestor()

    await userEvent.type(entrada, CODIGO_VALIDO)
    await userEvent.click(botonAgregar)

    expect(await within(dialogo).findByText(CODIGO_VALIDO)).toBeInTheDocument()
    expect(llamadasDeAlta()).toEqual([[RUTA_CODIGOS, { codigo: CODIGO_VALIDO }]])
    expect(within(dialogo).queryByRole('button', { name: 'Agregar igual' })).not.toBeInTheDocument()
    expect(within(dialogo).queryByRole('alert')).not.toBeInTheDocument()
  })

  it('un código válido se envía directo con Enter, sin advertencia', async () => {
    const { entrada, dialogo } = await abrirGestor()

    await userEvent.type(entrada, `${CODIGO_VALIDO}{Enter}`)

    expect(await within(dialogo).findByText(CODIGO_VALIDO)).toBeInTheDocument()
    expect(llamadasDeAlta()).toHaveLength(1)
    expect(within(dialogo).queryByRole('button', { name: 'Agregar igual' })).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: `setError('')` en `intentarAgregar`, antes de evaluar el código. Mutation-proof-tests: sacarlo
   * deja el aviso rojo del POST fallido junto a la advertencia del código nuevo.
   */
  it('un intento nuevo que termina en advertencia retira el error de un POST anterior', async () => {
    const { entrada, botonAgregar, dialogo } = await abrirGestor()
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'codigo_duplicado', 'El código ya existe.'))
    await userEvent.type(entrada, CODIGO_VALIDO)
    await userEvent.click(botonAgregar)
    expect(await within(dialogo).findByText('El código ya existe.')).toBeInTheDocument()

    await userEvent.clear(entrada)
    await userEvent.type(entrada, CODIGO_CON_VERIFICADOR_MALO)
    await userEvent.click(botonAgregar)

    expect(within(dialogo).getByText(MOTIVO_VERIFICADOR)).toBeInTheDocument()
    expect(within(dialogo).queryByText('El código ya existe.')).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el `trim()` previo a `advertenciaDeCodigoBarra` en `intentarAgregar`.
   * Mutation-proof-tests: validar el valor sin recortar hace que los espacios cuenten como
   * caracteres no numéricos y aparezca la advertencia.
   */
  it('un GTIN válido con espacios alrededor se envía recortado y sin advertencia', async () => {
    const { entrada, botonAgregar, dialogo } = await abrirGestor()

    await userEvent.type(entrada, `  ${CODIGO_VALIDO}  `)
    await userEvent.click(botonAgregar)

    expect(await within(dialogo).findByText(CODIGO_VALIDO)).toBeInTheDocument()
    expect(llamadasDeAlta()).toEqual([[RUTA_CODIGOS, { codigo: CODIGO_VALIDO }]])
    expect(within(dialogo).queryByRole('alert')).not.toBeInTheDocument()
  })

  it('si el POST de "Agregar igual" falla: muestra el error, conserva el código, retira la advertencia y un intento posterior funciona', async () => {
    const { entrada, botonAgregar, dialogo } = await abrirGestor()
    apiPostMock.mockRejectedValueOnce(new ErrorApi(500, 'error', 'No se pudo guardar.'))
    await userEvent.type(entrada, CODIGO_CON_VERIFICADOR_MALO)
    await userEvent.click(botonAgregar)

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Agregar igual' }))

    expect(await within(dialogo).findByText('No se pudo guardar.')).toBeInTheDocument()
    expect(entrada).toHaveValue(CODIGO_CON_VERIFICADOR_MALO)
    expect(within(dialogo).queryByRole('button', { name: 'Agregar igual' })).not.toBeInTheDocument()
    expect(within(dialogo).queryByText(MOTIVO_VERIFICADOR)).not.toBeInTheDocument()

    await userEvent.click(botonAgregar)
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Agregar igual' }))

    expect(await within(dialogo).findByText(CODIGO_CON_VERIFICADOR_MALO)).toBeInTheDocument()
    expect(llamadasDeAlta()).toHaveLength(2)
    expect(within(dialogo).queryByText('No se pudo guardar.')).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: `bloqueadoPorPadre` en el `disabled` de "Agregar igual". Se bloquea al
   * padre con un PUT de guardado que no resuelve.
   */
  it('"Agregar igual" queda deshabilitado mientras el formulario padre está guardando', async () => {
    const { entrada, botonAgregar, dialogo } = await abrirGestor()
    apiPutMock.mockReturnValue(new Promise(() => {}))
    await userEvent.type(entrada, CODIGO_CON_VERIFICADOR_MALO)
    await userEvent.click(botonAgregar)
    const confirmar = within(dialogo).getByRole('button', { name: 'Agregar igual' })
    expect(confirmar).toBeEnabled()

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(confirmar).toBeDisabled())
    expect(llamadasDeAlta()).toHaveLength(0)
  })

  it('dos clics sincrónicos en "Agregar igual" emiten un solo POST', async () => {
    const { entrada, botonAgregar, dialogo } = await abrirGestor()
    await userEvent.type(entrada, CODIGO_CON_VERIFICADOR_MALO)
    await userEvent.click(botonAgregar)
    const confirmar = within(dialogo).getByRole('button', { name: 'Agregar igual' })

    await act(async () => {
      confirmar.click()
      confirmar.click()
    })

    expect(llamadasDeAlta()).toHaveLength(1)
  })
})

// ---- sección Costos: costo de lista con y sin IVA, base Unidad/Bulto y costo nominal de solo lectura --------

describe('Articulos — sección Costos', () => {
  const dosAlicuotas: AlicuotaIvaListado[] = [
    { id: 1, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true },
    { id: 2, nombre: 'IVA 10,5%', porcentaje: 10.5, codigoAfip: 4, activo: true },
  ]

  async function abrirAltaConAlicuota() {
    mockearApiGet({ alicuotas: dosAlicuotas })
    apiPostMock.mockResolvedValue(articuloFixture({ id: 5, codigoInterno: 'A0005', nombre: 'Nuevo' }))
    await abrirFormularioNuevo()
    await waitFor(() => expect(screen.getByLabelText('Alícuota de IVA')).toHaveValue('1'))
  }

  async function abrirEdicion(articulo: ArticuloListado) {
    mockearApiGet({ alicuotas: dosAlicuotas, articulos: [articulo] })
    apiPutMock.mockResolvedValue(articulo)
    renderArticulos()
    const fila = (await screen.findByText(articulo.nombre)).closest('tr')
    if (!fila) throw new Error('No se encontró la fila del artículo')
    await userEvent.click(within(fila).getByRole('link', { name: 'Editar' }))
    await screen.findByText(`Editando artículo ${articulo.codigoInterno}`)
    await waitFor(() => expect(screen.getByLabelText('Alícuota de IVA')).toHaveValue(String(articulo.idAlicuotaIva)))
  }

  function cuerpoDelPedido(mock: typeof apiPostMock) {
    return mock.mock.calls[0][1] as Record<string, unknown>
  }

  it('lo tipeado sin IVA deriva el costo con IVA y viaja como costo de lista sin IVA', async () => {
    await abrirAltaConAlicuota()

    await userEvent.type(screen.getByLabelText('Costo de lista sin IVA'), '100')

    expect(screen.getByLabelText('Costo de lista con IVA')).toHaveValue('121,00')
    expect(screen.getByLabelText('Costo nominal')).toHaveValue('121,00')

    await userEvent.type(screen.getByLabelText('Nombre'), 'Nuevo')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(cuerpoDelPedido(apiPostMock)).toMatchObject({ costoLista: 100, costoListaPor: 'Unidad', costoListaIncluyeIva: false })
    expect(cuerpoDelPedido(apiPostMock)).not.toHaveProperty('costoNominal')
  })

  it('lo tipeado con IVA deriva el costo sin IVA y viaja como costo de lista con IVA', async () => {
    await abrirAltaConAlicuota()

    await userEvent.type(screen.getByLabelText('Costo de lista con IVA'), '121')

    expect(screen.getByLabelText('Costo de lista sin IVA')).toHaveValue('100,00')

    await userEvent.type(screen.getByLabelText('Nombre'), 'Nuevo')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(cuerpoDelPedido(apiPostMock)).toMatchObject({ costoLista: 121, costoListaIncluyeIva: true })
  })

  it('pasar por el campo derivado sin tipear no lo vuelve la fuente del costo de lista', async () => {
    await abrirAltaConAlicuota()
    await userEvent.type(screen.getByLabelText('Costo de lista sin IVA'), '100')

    await userEvent.click(screen.getByLabelText('Costo de lista con IVA'))
    await userEvent.tab()

    await userEvent.type(screen.getByLabelText('Nombre'), 'Nuevo')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(cuerpoDelPedido(apiPostMock)).toMatchObject({ costoLista: 100, costoListaIncluyeIva: false })
  })

  it('un artículo nuevo arranca con descuento 0 y lo manda así', async () => {
    await abrirAltaConAlicuota()
    expect(screen.getByLabelText('Descuento (%)')).toHaveValue('0')
    await userEvent.type(screen.getByLabelText('Nombre'), 'Nuevo')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(cuerpoDelPedido(apiPostMock)).toMatchObject({ descuentoProveedor: 0 })
  })

  it('vaciar el descuento manda 0 y no null', async () => {
    await abrirAltaConAlicuota()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Nuevo')
    await userEvent.clear(screen.getByLabelText('Descuento (%)'))

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(cuerpoDelPedido(apiPostMock)).toMatchObject({ descuentoProveedor: 0 })
  })

  it('cambiar la alícuota recalcula el campo derivado y deja intacto el que se tipeó', async () => {
    await abrirAltaConAlicuota()
    await userEvent.type(screen.getByLabelText('Costo de lista sin IVA'), '100')

    await userEvent.selectOptions(screen.getByLabelText('Alícuota de IVA'), '2')

    expect(screen.getByLabelText('Costo de lista sin IVA')).toHaveValue('100,00')
    expect(screen.getByLabelText('Costo de lista con IVA')).toHaveValue('110,50')
    expect(screen.getByLabelText('Costo nominal')).toHaveValue('110,50')
  })

  it('el costo nominal es de solo lectura y aplica el descuento de proveedor', async () => {
    await abrirAltaConAlicuota()
    await userEvent.type(screen.getByLabelText('Costo de lista con IVA'), '100')

    await userEvent.clear(screen.getByLabelText('Descuento (%)'))
    await userEvent.type(screen.getByLabelText('Descuento (%)'), '10')

    expect(screen.getByLabelText('Costo nominal')).toHaveAttribute('readonly')
    expect(screen.getByLabelText('Costo nominal')).toHaveValue('90,00')
  })

  it('un costo por bulto sin unidades por bulto bloquea el guardado con un aviso junto a los costos', async () => {
    await abrirAltaConAlicuota()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Nuevo')
    await userEvent.type(screen.getByLabelText('Costo de lista con IVA'), '300')

    await userEvent.selectOptions(screen.getByLabelText('Costo por'), 'Bulto')

    expect(screen.getByRole('alert')).toHaveTextContent('Un costo por bulto necesita las unidades por bulto')
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
    expect(screen.getByLabelText('Costo nominal')).toHaveValue('—')

    await userEvent.type(screen.getByLabelText('Unidades por bulto'), '12')

    expect(screen.queryByText(/Un costo por bulto necesita/)).not.toBeInTheDocument()
    expect(screen.getByLabelText('Costo nominal')).toHaveValue('25,00')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(cuerpoDelPedido(apiPostMock)).toMatchObject({ costoListaPor: 'Bulto', unidadesPorBulto: 12, costoLista: 300 })
  })

  /** Cláusula bajo prueba: `|| bultoSinUnidadesPorBulto` en el `disabled` de "Guardar". Enter en un campo envía el
   * formulario haciendo clic en su botón por defecto, y con ese botón deshabilitado no se envía nada. Evidencia de
   * mutación (mutation-proof-tests): sacar esa cláusula del `disabled` emite el POST con Enter y hace fallar esta prueba. */
  it('Enter en un campo con un costo por bulto sin unidades por bulto tampoco guarda', async () => {
    await abrirAltaConAlicuota()
    await userEvent.type(screen.getByLabelText('Costo de lista con IVA'), '300')
    await userEvent.selectOptions(screen.getByLabelText('Costo por'), 'Bulto')
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()

    await userEvent.type(screen.getByLabelText('Nombre'), 'Nuevo{Enter}')

    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('la base por unidad no exige unidades por bulto', async () => {
    await abrirAltaConAlicuota()

    expect(screen.getByLabelText('Costo por')).toHaveValue('Unidad')
    expect(screen.queryByText(/Un costo por bulto necesita/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  it('un artículo cargado sin cambios muestra el costo nominal guardado, que puede ser el de una compra', async () => {
    await abrirEdicion(articuloFixture({ costoLista: 100, costoNominal: 73.5 }))

    expect(screen.getByLabelText('Costo de lista con IVA')).toHaveValue('100,00')
    expect(screen.getByLabelText('Costo de lista sin IVA')).toHaveValue('82,64')
    expect(screen.getByLabelText('Costo nominal')).toHaveValue('73,50')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(cuerpoDelPedido(apiPutMock)).toMatchObject({ costoLista: 100, costoListaPor: 'Unidad', costoListaIncluyeIva: true })
    expect(cuerpoDelPedido(apiPutMock)).not.toHaveProperty('costoNominal')
  })

  it('al cambiar una entrada del cálculo el costo nominal pasa a ser la vista previa', async () => {
    await abrirEdicion(articuloFixture({ costoLista: 100, costoNominal: 73.5 }))

    await userEvent.clear(screen.getByLabelText('Descuento (%)'))
    await userEvent.type(screen.getByLabelText('Descuento (%)'), '10')

    expect(screen.getByLabelText('Costo nominal')).toHaveValue('90,00')
  })

  it('un artículo guardado sin IVA abre con ese campo como fuente y el otro derivado', async () => {
    await abrirEdicion(articuloFixture({ costoLista: 100, costoListaIncluyeIva: false, costoNominal: 121 }))

    expect(screen.getByLabelText('Costo de lista sin IVA')).toHaveValue('100,00')
    expect(screen.getByLabelText('Costo de lista con IVA')).toHaveValue('121,00')
    expect(screen.getByLabelText('Costo nominal')).toHaveValue('121,00')
  })

  const CONTROLES_DE_COSTO = [
    'Costo de lista sin IVA',
    'Costo de lista con IVA',
    'Costo por',
    'Descuento (%)',
    'Costo nominal',
  ]
  const sinCostos = () =>
    articuloFixture({ costoLista: null, costoListaPor: null, costoListaIncluyeIva: null, descuentoProveedor: null, costoNominal: null })

  /** Cláusula bajo prueba: `!valor.costosOcultos` que envuelve la sección Costos. Quien no recibe costos no ve ningún
   * control de costo y guardar manda esos campos en `null`.
   * Evidencia de mutación (mutation-proof-tests): sacar el envoltorio vuelve a mostrar los cinco controles y hace fallar la prueba. */
  it('quien no ve los costos no tiene ningún control de costo y su edición conserva la base y el IVA guardados', async () => {
    await abrirEdicion(sinCostos())

    for (const etiqueta of CONTROLES_DE_COSTO) {
      expect(screen.queryByLabelText(etiqueta), etiqueta).not.toBeInTheDocument()
    }
    expect(screen.queryByText('Costos', { selector: 'strong' })).not.toBeInTheDocument()
    expect(screen.queryByText(/El costo nominal es el costo por unidad/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(cuerpoDelPedido(apiPutMock)).toMatchObject({
      costoLista: null,
      costoListaPor: null,
      costoListaIncluyeIva: null,
      descuentoProveedor: null,
    })
  })

  it('quien recibe los costos ve la sección completa', async () => {
    await abrirEdicion(articuloFixture({ costoLista: 100, costoNominal: 73.5 }))

    for (const etiqueta of CONTROLES_DE_COSTO) {
      expect(screen.getByLabelText(etiqueta), etiqueta).toBeInTheDocument()
    }
    expect(screen.getByText('Costos', { selector: 'strong' })).toBeInTheDocument()
  })
})

describe('Articulos — disponibilidad del artículo', () => {
  it('al editar como admin muestra la sección, y cambiar la disponibilidad usa su propio PUT sin tocar el formulario', async () => {
    apiPutMock.mockResolvedValue({ idArticulo: 1, disponibleVenta: true, disponibleOnline: false })
    renderArticulos('/articulos/edit/1', ROL.Admin)
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    const interruptor = await within(dialogo).findByRole('switch', { name: 'Disponible online' })

    await userEvent.click(interruptor)

    expect(await within(dialogo).findByText('El artículo quedó fuera de la tienda online.')).toBeInTheDocument()
    expect(apiPutMock).toHaveBeenCalledTimes(1)
    expect(apiPutMock).toHaveBeenCalledWith('/articulos/1/disponibilidad', { disponibleVenta: true, disponibleOnline: false })

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('una escritura de la tienda refresca la grilla con el estado nuevo', async () => {
    apiPutMock.mockResolvedValue({ idArticulo: 1, disponibleVenta: false, disponibleOnline: true })
    renderArticulos('/articulos/edit/1', ROL.Admin)
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    const interruptor = await within(dialogo).findByRole('switch', { name: 'Disponible a la venta' })
    expect(screen.queryByText('No se vende', { selector: '.badge' })).not.toBeInTheDocument()
    mockearApiGet({
      grilla: paginaGrillaFixture([filaGrillaFixture({ id: 1, nombre: 'Articulo Uno', disponibleVenta: false }), filaGrillaDos]),
    })

    await userEvent.click(interruptor)

    expect(await screen.findByText('No se vende', { selector: '.badge' })).toBeInTheDocument()
  })

  it('el PUT de la edición del artículo no lleva la disponibilidad', async () => {
    apiPutMock.mockResolvedValue(articuloFixture({ id: 1 }))
    renderArticulos('/articulos/edit/1', ROL.Admin)
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await within(dialogo).findByRole('switch', { name: 'Disponible online' })

    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' (editado)')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][0]).toBe('/articulos/1')
    const campos = Object.keys(apiPutMock.mock.calls[0][1] as object)
    expect(campos).not.toContain('disponibleOnline')
    expect(campos).not.toContain('disponibleVenta')
    expect(campos).not.toContain('tieneImagen')
  })

  it('la sección arranca con lo que trajo el detalle del artículo', async () => {
    mockearApiGet({
      articulos: [articuloFixture({ id: 1, disponibleVenta: false, disponibleOnline: true, tieneImagen: true }), articuloDos],
    })
    renderArticulos('/articulos/edit/1', ROL.Admin)
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })

    expect(await within(dialogo).findByRole('switch', { name: 'Disponible online' })).toBeChecked()
    expect(within(dialogo).getByRole('switch', { name: 'Disponible a la venta' })).not.toBeChecked()
    expect(within(dialogo).getByRole('img', { name: 'Imagen del artículo' })).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: la sección avisa a `alDeEscribir` mientras la puerta de "Quitar imagen" está abierta. Sin
   * eso el resto del formulario seguía operable y se podía apilar una alta rápida encima de la puerta, cuyo Escape
   * le impedía cerrarse. Mutación: quitar `alDeEscribir(true)` de la apertura deja "Nueva marca" habilitado.
   */
  it('con la puerta de "Quitar imagen" abierta el resto del formulario queda inerte y Escape solo cierra la puerta', async () => {
    mockearApiGet({ articulos: [articuloFixture({ id: 1, tieneImagen: true }), articuloDos] })
    renderArticulos('/articulos/edit/1', ROL.Admin)
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    const nuevaMarca = await within(dialogo).findByRole('button', { name: 'Nueva marca' })
    await waitFor(() => expect(nuevaMarca).toBeEnabled())

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Quitar imagen' }))

    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
    expect(nuevaMarca).toBeDisabled()
    expect(within(dialogo).getByRole('button', { name: 'Nueva categoría' })).toBeDisabled()

    await userEvent.keyboard('{Escape}')

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0001' })).toBeInTheDocument()
    await waitFor(() => expect(nuevaMarca).toBeEnabled())
  })

  it('confirmar quitar la imagen devuelve el formulario a la normalidad', async () => {
    apiDeleteMock.mockResolvedValue(undefined)
    mockearApiGet({ articulos: [articuloFixture({ id: 1, tieneImagen: true }), articuloDos] })
    renderArticulos('/articulos/edit/1', ROL.Admin)
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    const nuevaMarca = await within(dialogo).findByRole('button', { name: 'Nueva marca' })
    await waitFor(() => expect(nuevaMarca).toBeEnabled())
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Quitar imagen' }))
    expect(nuevaMarca).toBeDisabled()

    await userEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Confirmar quitar imagen' }))

    expect(await within(dialogo).findByText('Se quitó la imagen.')).toBeInTheDocument()
    await waitFor(() => expect(nuevaMarca).toBeEnabled())
  })

  it('en un alta no hay sección: primero hay que guardar el artículo', async () => {
    renderArticulos('/articulos/create', ROL.Admin)
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo artículo' })

    expect(within(dialogo).getByText('Guardá el artículo para poder cargar códigos de barra y precios.')).toBeInTheDocument()
    expect(within(dialogo).queryByText('Venta y tienda online')).not.toBeInTheDocument()
    expect(within(dialogo).queryByRole('switch')).not.toBeInTheDocument()
  })

  it('quien no administra el catálogo no ve la sección', async () => {
    renderArticulos('/articulos/edit/1', ROL.Vendedor)
    const dialogo = await screen.findByRole('dialog', { name: 'Editando artículo A0001' })
    await within(dialogo).findByText('Códigos de barra')

    expect(within(dialogo).queryByText('Venta y tienda online')).not.toBeInTheDocument()
    expect(within(dialogo).queryByRole('switch', { name: 'Disponible online' })).not.toBeInTheDocument()
    expect(within(dialogo).queryByRole('switch', { name: 'Disponible a la venta' })).not.toBeInTheDocument()
  })
})
