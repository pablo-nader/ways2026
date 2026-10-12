import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useEffect } from 'react'
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Articulos } from './Articulos'
import { ErrorApi } from '../api/cliente'
import type {
  AlicuotaIvaListado,
  ArticuloListado,
  AreaListado,
  CategoriaListado,
  FamiliaDetalle,
  FamiliaListado,
  FilaDeGrillaDeArticulos,
  GrupoListado,
  ListaPrecioListado,
  PaginaDeGrillaDeArticulos,
  PrevisualizacionDeAgrupacion,
  ProveedorListado,
  ValoresCompartidosDeLaFamilia,
} from '../api/tipos'

/**
 * Familias de artículos en la pantalla de artículos (doc 10 §3): el alta dentro de una familia, la edición de
 * un miembro con su alcance y sacar un artículo de su familia. Las pruebas del resto de la pantalla viven en
 * `Articulos.test.tsx`; el editor de precios, en `articulos/EditorDePrecios.test.tsx`.
 */

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

// ---- fixtures ----------------------------------------------------------------------------------------------

/** Los valores de la referencia de "Sabores": todos distintos de los que un alta en blanco trae por defecto,
 * así que un prellenado que no ocurrió se nota. Los ids son los de los catálogos de `mockearApi`. */
const referencia: ValoresCompartidosDeLaFamilia = {
  idArea: 1,
  idCategoria: 2,
  idGrupo: 3,
  idProveedorHabitual: 4,
  idAlicuotaIva: 5,
  unidadVenta: 'Peso',
  unidadesPorBulto: 12,
  esProducto: true,
  controlaLote: true,
  acumulaEnVenta: false,
  costoLista: 150,
  costoListaPor: 'Unidad',
  costoListaIncluyeIva: true,
  descuentoProveedor: 5,
  costoNominal: 120,
}

/** Los quince valores como los guarda un artículo: el área es obligatoria en él, aunque el detalle de una familia la
 * presente como `null` cuando está dada de baja. */
const compartidosDelArticulo = { ...referencia, idArea: 1 }

/** Lo que un alta manda de la referencia: el costo nominal lo calcula el servidor y no viaja. */
const referenciaEnviada = Object.fromEntries(Object.entries(referencia).filter(([clave]) => clave !== 'costoNominal'))

function miembros(cantidad: number) {
  const nombres = ['Vainilla', 'Frutilla', 'Chocolate', 'Limón', 'Menta']
  return Array.from({ length: cantidad }, (_, i) => ({
    id: 31 + i,
    codigoInterno: `A00${31 + i}`,
    nombre: nombres[i] ?? `Sabor ${i}`,
    idMarca: null,
    activo: true,
  }))
}

function detalleSabores(sobrescribir: Partial<FamiliaDetalle> = {}): FamiliaDetalle {
  return {
    id: 7,
    nombre: 'Sabores',
    activo: true,
    articulos: miembros(3),
    valores: referencia,
    precios: [
      { idListaPrecio: 2, estado: { vigente: 1200, pendiente: null } },
      { idListaPrecio: 3, estado: { vigente: null, pendiente: null } },
    ],
    ...sobrescribir,
  }
}

function familiaListado(sobrescribir: Partial<FamiliaListado> = {}): FamiliaListado {
  return { id: 7, nombre: 'Sabores', activo: true, cantidadArticulos: 3, ...sobrescribir }
}

function articuloFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    publicadoWeb: false,
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
    idAlicuotaIva: 5,
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

/** Vainilla, el miembro que se edita: sus quince campos compartidos son los de la referencia. */
function miembro(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return articuloFixture({
    id: 31,
    codigoInterno: 'A0031',
    nombre: 'Vainilla',
    idFamilia: 7,
    ...compartidosDelArticulo,
    ...sobrescribir,
  })
}

function filaGrilla(sobrescribir: Partial<FilaDeGrillaDeArticulos> = {}): FilaDeGrillaDeArticulos {
  return {
    publicadoWeb: false,
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

const listaGeneral: ListaPrecioListado = {
  id: 2,
  nombre: 'General',
  activo: true,
  idEmpresa: null,
  esDefault: true,
  modo: 'Fija',
  idListaBase: null,
  porcentaje: null,
}
const listaMayorista: ListaPrecioListado = { ...listaGeneral, id: 3, nombre: 'Mayorista', esDefault: false }

const areaAlmacen: AreaListado = { id: 1, nombre: 'Almacén', activo: true, idEmpresa: null, orden: 1 }
const categoriaBebidas: CategoriaListado = { id: 2, nombre: 'Bebidas', activo: true, idEmpresa: null, orden: 1, idCategoriaPadre: null }
const grupoLacteos: GrupoListado = { id: 3, nombre: 'Lácteos', activo: true, idEmpresa: null, margen: null }
const alicuota21: AlicuotaIvaListado = { id: 5, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true }

function proveedorAlfa(): ProveedorListado {
  return {
    id: 4,
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
  }
}

// ---- API simulada ------------------------------------------------------------------------------------------

type Escenario = {
  /** Detalle de cada artículo por id (`GET /api/articulos/{id}`); con `articuloImpl` gana esta, que recibe el
   * número de lectura de ese artículo (1 para la primera). */
  articulos?: Record<number, ArticuloListado>
  articuloImpl?: (id: number, llamada: number) => ArticuloListado | Promise<ArticuloListado>
  /** Respuesta de `GET /api/familias`; con `familiasImpl` gana esta. */
  familias?: FamiliaListado[]
  familiasImpl?: () => Promise<FamiliaListado[]>
  /** Detalle de cada familia por id; con `detalleDeFamiliaImpl` gana esta. */
  detalles?: Record<number, FamiliaDetalle>
  detalleDeFamiliaImpl?: (id: number, llamada: number) => Promise<FamiliaDetalle>
  /** Los artículos de la grilla: por defecto, solo "Articulo Uno". */
  grilla?: PaginaDeGrillaDeArticulos
}

/**
 * Despacha por ruta, igual que el mock de `Articulos.test.tsx`: los clientes son envoltorios finos sobre
 * `api.get`, así que interceptar acá alcanza para toda la pantalla.
 */
function mockearApi(escenario: Escenario = {}) {
  const llamadasDeFamilia: Record<number, number> = {}
  const llamadasDeArticulo: Record<number, number> = {}

  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/articulos/grilla')) {
      return Promise.resolve(
        escenario.grilla ?? { items: [filaGrilla()], total: 1, pagina: 1, tamanio: 25, nombreListaPrecio: 'General' },
      )
    }

    if (/^\/articulos\/\d+$/.test(ruta)) {
      const id = Number(ruta.split('/')[2])
      llamadasDeArticulo[id] = (llamadasDeArticulo[id] ?? 0) + 1
      if (escenario.articuloImpl) return Promise.resolve(escenario.articuloImpl(id, llamadasDeArticulo[id]))
      return Promise.resolve((escenario.articulos ?? {})[id] ?? articuloFixture({ id }))
    }
    if (/^\/articulos\/\d+\/codigos-barra$/.test(ruta)) return Promise.resolve([])
    if (/^\/articulos\/\d+\/precios$/.test(ruta)) return Promise.resolve([])
    if (/^\/articulos\/\d+\/precios\/\d+\/historial$/.test(ruta)) return Promise.resolve([])

    if (ruta === '/familias') {
      return escenario.familiasImpl ? escenario.familiasImpl() : Promise.resolve(escenario.familias ?? [])
    }
    if (/^\/familias\/\d+$/.test(ruta)) {
      const id = Number(ruta.split('/')[2])
      llamadasDeFamilia[id] = (llamadasDeFamilia[id] ?? 0) + 1
      if (escenario.detalleDeFamiliaImpl) return escenario.detalleDeFamiliaImpl(id, llamadasDeFamilia[id])
      const detalle = (escenario.detalles ?? {})[id]
      return detalle ? Promise.resolve(detalle) : Promise.reject(new ErrorApi(404, 'no_encontrado', `No existe la familia ${id}.`))
    }

    if (ruta.startsWith('/catalogos/areas')) return Promise.resolve([areaAlmacen])
    if (ruta.startsWith('/catalogos/categorias')) return Promise.resolve([categoriaBebidas])
    if (ruta.startsWith('/catalogos/marcas')) return Promise.resolve([])
    if (ruta.startsWith('/catalogos/grupos')) return Promise.resolve([grupoLacteos])
    if (ruta.startsWith('/proveedores')) return Promise.resolve({ items: [proveedorAlfa()], total: 1, pagina: 1, tamanio: 200 })
    if (ruta === '/catalogos-fiscales/alicuotas-iva') return Promise.resolve([alicuota21])
    if (ruta === '/empresas') return Promise.resolve([])
    if (ruta === '/catalogos/listas-precio') return Promise.resolve([listaGeneral, listaMayorista])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderArticulos(rutaInicial = '/articulos') {
  return render(
    <MemoryRouter initialEntries={[rutaInicial]}>
      <Routes>
        <Route path="/articulos/*" element={<Articulos />} />
      </Routes>
    </MemoryRouter>,
  )
}

/** Como `renderArticulos`, con varias entradas de historial y una forma de moverse por ellas (el Atrás del
 * navegador): `navegar(-1)`. */
let navegar: (delta: number) => void = () => {}

function EspiaDeNavegacion() {
  const navigate = useNavigate()
  useEffect(() => {
    navegar = (delta) => navigate(delta)
  }, [navigate])
  return null
}

function renderArticulosConHistorial(entradas: string[], indiceInicial: number) {
  return render(
    <MemoryRouter initialEntries={entradas} initialIndex={indiceInicial}>
      <EspiaDeNavegacion />
      <Routes>
        <Route path="/articulos/*" element={<Articulos />} />
      </Routes>
    </MemoryRouter>,
  )
}

/** Una promesa que el test resuelve o rechaza cuando quiere. */
function diferida<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((resolve, reject) => {
    resolver = resolve
    rechazar = reject
  })
  return { promesa, resolver, rechazar }
}

async function abrirAlta() {
  renderArticulos()
  await screen.findByText('Articulo Uno')
  await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
  await screen.findByText('Nuevo artículo')
}

/** Elige una familia en el selector. El selector está desde el primer render con solo "Sin familia": esperar al elemento no
 * prueba nada, la opción es lo único que prueba que las familias llegaron (web-test-data-gates, variante 1b). */
async function elegirFamiliaEnElSelector(valor: string) {
  const selector = await screen.findByLabelText('Familia (opcional)')
  await waitFor(() =>
    expect(within(selector).getAllByRole('option').some((o) => (o as HTMLOptionElement).value === valor)).toBe(true),
  )
  await userEvent.selectOptions(selector, valor)
}

/** Abre un alta y elige la familia: espera a que su detalle ya esté prellenado en el formulario (el dato, no el
 * elemento). */
async function abrirAltaEnLaFamilia(idFamilia = '7') {
  await abrirAlta()
  await elegirFamiliaEnElSelector(idFamilia)
  await waitFor(() => expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('150,00'))
}

async function abrirEdicion(id = 31, codigo = 'A0031') {
  renderArticulos(`/articulos/edit/${id}`)
  await screen.findByText(`Editando artículo ${codigo}`)
}

/** El "Agregar" de la pantalla, el de la grilla: dentro del formulario de un artículo hay otro "Agregar" (el
 * de los códigos de barra), así que se lo busca en la cabecera de la caja. */
function botonNuevo() {
  return within(screen.getByRole('banner')).getByRole('button', { name: 'Agregar' })
}

function unaFamiliaPidioSuDetalle() {
  return apiGetMock.mock.calls.filter((c) => /^\/familias\/\d+$/.test(c[0] as string))
}

function llamadasAGrilla() {
  return apiGetMock.mock.calls.filter((c) => (c[0] as string).startsWith('/articulos/grilla')).length
}

const CAMPOS_COMPARTIDOS_DEL_FORMULARIO = [
  /^Unidad de venta/,
  /^Unidades por bulto/,
  /^Es producto/,
  /^Área/,
  /^Categoría/,
  /^Grupo/,
  /^Proveedor habitual/,
  /^Alícuota de IVA/,
  /^Costo de lista sin IVA/,
  /^Costo de lista con IVA/,
  /^Costo por/,
  /^Descuento/,
  /^Controla lote/,
  /^Acumula en una sola línea/,
]

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
  mockearApi()
})

afterEach(() => {
  vi.restoreAllMocks()
})

// ---- alta dentro de una familia ----------------------------------------------------------------------------

describe('Articulos — alta: el selector de familia', () => {
  it('ofrece "Sin familia" y las familias activas con miembros vivos, con su cantidad; no ofrece la inactiva ni la vacía', async () => {
    mockearApi({
      familias: [
        familiaListado({ id: 7, nombre: 'Sabores', cantidadArticulos: 3 }),
        familiaListado({ id: 8, nombre: 'Talles', cantidadArticulos: 1 }),
        familiaListado({ id: 9, nombre: 'Apagada', activo: false }),
        familiaListado({ id: 10, nombre: 'Vacía', cantidadArticulos: 0 }),
      ],
    })

    await abrirAlta()

    const selector = await screen.findByLabelText('Familia (opcional)')
    await waitFor(() =>
      expect(within(selector).getAllByRole('option').map((o) => o.textContent)).toEqual([
        'Sin familia',
        'Sabores (3 artículos)',
        'Talles (1 artículo)',
      ]),
    )
    expect(selector).toHaveValue('')
  })

  function opcionesDeSelector() {
    return within(screen.getByLabelText('Familia (opcional)'))
      .getAllByRole('option')
      .map((o) => o.textContent)
  }

  it('sin familias para elegir el selector está igual, solo con "Sin familia", y trae el "+"', async () => {
    mockearApi({ familias: [familiaListado({ activo: false })] })

    await abrirAlta()
    await waitFor(() => expect(apiGetMock.mock.calls.some((c) => c[0] === '/familias')).toBe(true))
    await act(async () => {})

    expect(opcionesDeSelector()).toEqual(['Sin familia'])
    expect(screen.getByRole('button', { name: 'Nueva familia' })).toBeEnabled()
    expect(screen.queryByText('Cargando la familia…')).not.toBeInTheDocument()
  })

  it('con cero familias en el tenant el selector tampoco desaparece', async () => {
    await abrirAlta()
    await waitFor(() => expect(apiGetMock.mock.calls.some((c) => c[0] === '/familias')).toBe(true))

    expect(opcionesDeSelector()).toEqual(['Sin familia'])
  })

  /** Cláusula bajo prueba: el aviso del fallo de carga de las familias, que dice lo que la pantalla de verdad
   * impone: sin familias cargadas no se puede elegir una existente y el nombre de una nueva no se contrasta
   * (react-async-state regla 7). */
  it('si no se pudieron cargar las familias lo avisa; el selector queda sin opciones, el "+" y el alta siguen disponibles', async () => {
    mockearApi({ familiasImpl: () => Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.')) })

    await abrirAlta()

    expect(
      await screen.findByText('No se pudieron cargar las familias: no se puede elegir una existente y no se comprueba si el nombre de una nueva ya está en uso.'),
    ).toBeInTheDocument()
    expect(opcionesDeSelector()).toEqual(['Sin familia'])
    expect(screen.getByRole('button', { name: 'Nueva familia' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  it('al editar un artículo que no es miembro el selector está, también cuando las familias no cargaron, y lo avisa', async () => {
    mockearApi({
      articulos: { 1: articuloFixture() },
      familiasImpl: () => Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.')),
    })

    await abrirEdicion(1, 'A0001')

    expect(opcionesDeSelector()).toEqual(['Sin familia'])
    expect(await screen.findByText(/No se pudieron cargar las familias/)).toBeInTheDocument()
  })

  it('al editar un miembro no hay selector: se muestra su pertenencia', async () => {
    escenarioDelMiembro()

    await abrirEdicion()

    expect(screen.queryByLabelText('Familia (opcional)')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Nueva familia' })).not.toBeInTheDocument()
  })
})

describe('Articulos — alta: elegir una familia', () => {
  beforeEach(() => {
    mockearApi({ familias: [familiaListado()], detalles: { 7: detalleSabores() } })
  })

  it('pide el detalle de la familia y prellena los quince campos compartidos con sus valores', async () => {
    await abrirAltaEnLaFamilia()

    expect(apiGetMock).toHaveBeenCalledWith('/familias/7')
    await waitFor(() => expect(screen.getByLabelText(/^Área/)).toHaveValue('1'))
    await waitFor(() => expect(screen.getByLabelText(/^Categoría/)).toHaveValue('2'))
    await waitFor(() => expect(screen.getByLabelText(/^Grupo/)).toHaveValue('3'))
    await waitFor(() => expect(screen.getByLabelText(/^Proveedor habitual/)).toHaveValue('4'))
    await waitFor(() => expect(screen.getByLabelText(/^Alícuota de IVA/)).toHaveValue('5'))
    expect(screen.getByLabelText(/^Unidad de venta/)).toHaveValue('Peso')
    expect(screen.getByLabelText(/^Unidades por bulto/)).toHaveValue(12)
    expect(screen.getByLabelText(/^Es producto/)).toBeChecked()
    expect(screen.getByLabelText(/^Controla lote/)).toBeChecked()
    expect(screen.getByLabelText(/^Acumula en una sola línea/)).not.toBeChecked()
    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('150,00')
    expect(screen.getByLabelText(/^Descuento/)).toHaveValue(5)
    expect(screen.getByLabelText(/^Costo por/)).toHaveValue('Unidad')
    expect(screen.getByLabelText(/^Costo de lista sin IVA/)).toHaveValue('123,97')
  })

  it('los quince campos compartidos quedan bloqueados y su etiqueta lo dice: "(de la familia)"', async () => {
    await abrirAltaEnLaFamilia()

    for (const etiqueta of CAMPOS_COMPARTIDOS_DEL_FORMULARIO) {
      const control = screen.getByLabelText(etiqueta)
      expect(control, String(etiqueta)).toBeDisabled()
      const label = document.querySelector(`label[for="${control.id}"]`)
      expect(label?.textContent, String(etiqueta)).toContain('(de la familia)')
    }
  })

  /** Cláusula bajo prueba: `bloqueoDeFamilia` como tercer argumento de `costoNominalDelFormulario`. La referencia trae
   * 120 de nominal y su fórmula (150 con IVA y 5 % de descuento) da 142,50: solo el guardado de la familia explica 120. */
  it('el costo nominal de solo lectura es el guardado de la familia mientras el alta está bloqueada, y la vista previa al dejarla', async () => {
    const lectura = diferida<FamiliaDetalle>()
    mockearApi({ familias: [familiaListado()], detalleDeFamiliaImpl: () => lectura.promesa })
    await abrirAlta()
    await userEvent.selectOptions(await screen.findByLabelText('Familia (opcional)'), '7')
    await screen.findByText('Cargando la familia…')
    expect(screen.getByLabelText('Costo nominal')).toHaveValue('—')

    await act(async () => {
      lectura.resolver(detalleSabores())
    })
    await waitFor(() => expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('150,00'))
    expect(screen.getByLabelText('Costo nominal')).toHaveValue('120,00')

    await userEvent.selectOptions(screen.getByLabelText('Familia (opcional)'), '')

    expect(screen.getByLabelText('Costo nominal')).toHaveValue('142,50')
  })

  it('los botones "+" de los padrones compartidos quedan bloqueados; el de marca, que es propia, no', async () => {
    await abrirAltaEnLaFamilia()

    expect(screen.getByRole('button', { name: 'Nueva categoría' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Nuevo grupo' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Nuevo proveedor' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Nueva marca' })).toBeEnabled()
  })

  it('los campos propios del artículo siguen editables', async () => {
    await abrirAltaEnLaFamilia()

    for (const etiqueta of ['Código interno', 'Nombre', 'Descripción', 'Marca', 'Activo', 'Disponible para todas las empresas del tenant']) {
      expect(screen.getByLabelText(etiqueta), etiqueta).toBeEnabled()
    }
  })

  it('muestra los precios de la familia por lista, de solo lectura, con lo que es propio del artículo', async () => {
    await abrirAltaEnLaFamilia()

    // Los nombres de las listas vienen de otra lectura (`/catalogos/listas-precio`): la tabla ya existe cuando llega la
    // familia, pero con "Lista 2" y "Lista 3" hasta que llegan, así que se espera el dato y no la tabla.
    const tabla = await screen.findByRole('table', { name: 'Precios que se copian de la familia' })
    await waitFor(() =>
      expect(within(tabla).getAllByRole('row').map((fila) => within(fila).queryAllByRole('cell').map((c) => c.textContent))).toEqual([
        [],
        ['General', '$ 1.200,00'],
        ['Mayorista', '—'],
      ]),
    )
    expect(within(tabla).queryAllByRole('textbox')).toHaveLength(0)
    expect(screen.getByText(/Son propios del artículo: nombre, descripción, códigos, marca, activo y disponibilidad por empresa/)).toBeInTheDocument()
  })

  it('un precio programado de la referencia se muestra con su fecha', async () => {
    const iso = '2026-12-01T10:00:00+00:00'
    mockearApi({
      familias: [familiaListado()],
      detalles: {
        7: detalleSabores({ precios: [{ idListaPrecio: 2, estado: { vigente: 1200, pendiente: { monto: 1300, vigenteDesde: iso } } }] }),
      },
    })

    await abrirAltaEnLaFamilia()

    expect(await screen.findByText(`$ 1.200,00 · programado $ 1.300,00 desde ${new Date(iso).toLocaleString('es-AR')}`)).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `altaConFamiliaLista` en el `disabled` de "Guardar": el aviso "Cargando la familia…"
   * promete que no se guarda mientras no llegó (react-async-state regla 7). */
  it('mientras la familia carga, sus campos ya están bloqueados y no se puede guardar', async () => {
    const lectura = diferida<FamiliaDetalle>()
    mockearApi({ familias: [familiaListado()], detalleDeFamiliaImpl: () => lectura.promesa })
    await abrirAlta()

    await elegirFamiliaEnElSelector('7')

    expect(await screen.findByText('Cargando la familia…')).toBeInTheDocument()
    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()

    await act(async () => {
      lectura.resolver(detalleSabores())
    })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled())
    expect(screen.queryByText('Cargando la familia…')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `!altaLista` en el `disabled` de "Guardar". Enter en un campo envía el formulario haciendo
   * clic en su botón por defecto, y con ese botón deshabilitado no se envía nada: el teclado no llega a ninguna guarda
   * del submit, así que la que cuenta es la del botón. Evidencia de mutación (mutation-proof-tests): sacar
   * `|| !altaLista` del `disabled` de "Guardar" hace fallar este test (se emite el POST sin los valores de la
   * familia); revertido, vuelve a verde. */
  it('Enter en un campo con la familia todavía cargando tampoco guarda', async () => {
    const lectura = diferida<FamiliaDetalle>()
    mockearApi({ familias: [familiaListado()], detalleDeFamiliaImpl: () => lectura.promesa })
    await abrirAlta()
    await elegirFamiliaEnElSelector('7')
    await screen.findByText('Cargando la familia…')

    await userEvent.type(screen.getByLabelText('Nombre'), 'Frutilla{Enter}')

    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('si la carga de la familia falla: muestra el motivo, no deja guardar y "Reintentar" la vuelve a pedir', async () => {
    mockearApi({
      familias: [familiaListado()],
      detalleDeFamiliaImpl: (_id, llamada) =>
        llamada === 1 ? Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó la lectura.')) : Promise.resolve(detalleSabores()),
    })
    await abrirAlta()

    await elegirFamiliaEnElSelector('7')

    expect(await screen.findByText(/Se cayó la lectura\. El artículo no se puede guardar con esta familia hasta que cargue/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()

    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

    await waitFor(() => expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('150,00'))
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
    expect(screen.queryByText(/Se cayó la lectura/)).not.toBeInTheDocument()
    expect(unaFamiliaPidioSuDetalle()).toHaveLength(2)
  })

  it('con la carga fallida, elegir "Sin familia" deja guardar de nuevo', async () => {
    mockearApi({
      familias: [familiaListado()],
      detalleDeFamiliaImpl: () => Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó la lectura.')),
    })
    await abrirAlta()
    await elegirFamiliaEnElSelector('7')
    await screen.findByText(/Se cayó la lectura/)

    await userEvent.selectOptions(screen.getByLabelText('Familia (opcional)'), '')

    expect(screen.queryByText(/Se cayó la lectura/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  it('una familia sin ningún miembro vivo no se puede usar: avisa que no hay valores que copiar y no deja guardar', async () => {
    mockearApi({ familias: [familiaListado()], detalles: { 7: detalleSabores({ articulos: [], valores: null, precios: [] }) } })
    await abrirAlta()

    await elegirFamiliaEnElSelector('7')

    expect(
      await screen.findByText(/La familia "Sabores" no tiene artículos vivos: no hay valores ni precios que copiar\. El artículo no se puede guardar en ella\./),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
  })

  it('dejar la familia en "Sin familia" desbloquea los campos y conserva lo que se había prellenado', async () => {
    await abrirAltaEnLaFamilia()

    await userEvent.selectOptions(screen.getByLabelText('Familia (opcional)'), '')

    for (const etiqueta of CAMPOS_COMPARTIDOS_DEL_FORMULARIO) {
      expect(screen.getByLabelText(etiqueta), String(etiqueta)).toBeEnabled()
    }
    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('150,00')
    expect(screen.getByLabelText(/^Unidad de venta/)).toHaveValue('Peso')
    expect(screen.queryByRole('table', { name: 'Precios que se copian de la familia' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Nueva categoría' })).toBeEnabled()
  })

  /** Cláusula bajo prueba: la generación de `cargarDetalle` del hook, vista desde el alta (react-async-state
   * regla 2): la respuesta tardía de la familia que se dejó de elegir no pisa los valores de la elegida después. */
  it('la respuesta tardía de una familia que se dejó de elegir no pisa a la elegida después', async () => {
    const primera = diferida<FamiliaDetalle>()
    const segunda = diferida<FamiliaDetalle>()
    mockearApi({
      familias: [familiaListado({ id: 7, nombre: 'Sabores' }), familiaListado({ id: 8, nombre: 'Talles' })],
      detalleDeFamiliaImpl: (id) => (id === 7 ? primera.promesa : segunda.promesa),
    })
    await abrirAlta()
    const selector = await screen.findByLabelText('Familia (opcional)')
    await screen.findByRole('option', { name: 'Talles (3 artículos)' })

    await userEvent.selectOptions(selector, '7')
    await userEvent.selectOptions(selector, '8')
    await act(async () => {
      segunda.resolver(detalleSabores({ id: 8, nombre: 'Talles', valores: { ...referencia, costoLista: 999 } }))
    })
    await waitFor(() => expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('999,00'))

    await act(async () => {
      primera.resolver(detalleSabores({ id: 7, nombre: 'Sabores', valores: { ...referencia, costoLista: 111 } }))
    })

    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('999,00')
    expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('8')
  })

  /** Cláusula bajo prueba: `familias.descartarDetalle()` en `descartarModal` y en la apertura del alta: la familia
   * que llega tarde, con el modal ya cerrado, no pisa el alta siguiente. */
  it('una familia que llega después de cerrar el modal no se aplica al alta que se abre luego', async () => {
    const lectura = diferida<FamiliaDetalle>()
    mockearApi({ familias: [familiaListado()], detalleDeFamiliaImpl: () => lectura.promesa })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    await abrirAlta()
    await elegirFamiliaEnElSelector('7')
    await screen.findByText('Cargando la familia…')

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    await waitFor(() => expect(screen.queryByText('Nuevo artículo')).not.toBeInTheDocument())
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Nuevo artículo')

    await act(async () => {
      lectura.resolver(detalleSabores())
    })

    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('')
    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toBeEnabled()
    expect(screen.queryByText('Cargando la familia…')).not.toBeInTheDocument()
  })
})

describe('Articulos — alta: guardar dentro de una familia', () => {
  const creado = articuloFixture({ id: 40, codigoInterno: 'A0040', nombre: 'Frutilla', idFamilia: 7, ...compartidosDelArticulo })

  beforeEach(() => {
    mockearApi({
      familias: [familiaListado()],
      detalleDeFamiliaImpl: (_id, llamada) =>
        Promise.resolve(llamada === 1 ? detalleSabores() : detalleSabores({ articulos: miembros(4) })),
      articulos: { 40: creado },
    })
    apiPostMock.mockResolvedValue(creado)
  })

  it('manda idFamilia y los quince valores de la familia, junto con lo propio del artículo', async () => {
    await abrirAltaEnLaFamilia()

    await userEvent.type(screen.getByLabelText('Nombre'), 'Frutilla')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [ruta, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(ruta).toBe('/articulos')
    expect(cuerpo).toMatchObject({ idFamilia: 7, nombre: 'Frutilla', codigoInterno: null, ...referenciaEnviada })
    expect(cuerpo).not.toHaveProperty('costoNominal')
  })

  it('avisa que tomó los valores y los precios de la familia, y pasa a mostrarla como su familia con un miembro más', async () => {
    await abrirAltaEnLaFamilia()

    await userEvent.type(screen.getByLabelText('Nombre'), 'Frutilla')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText(
        'Artículo "Frutilla" creado con código interno A0040. Es parte de la familia "Sabores": tomó sus valores compartidos y sus precios.',
      ),
    ).toBeInTheDocument()
    expect(await screen.findByText('Editando artículo A0040')).toBeInTheDocument()
    expect(await screen.findByText('Familia "Sabores" (4 artículos)')).toBeInTheDocument()
    expect(unaFamiliaPidioSuDetalle()).toHaveLength(2)
  })

  it('creado el artículo, sus campos compartidos dejan de estar bloqueados: ahora se editan con alcance', async () => {
    await abrirAltaEnLaFamilia()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Frutilla')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Editando artículo A0040')

    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toBeEnabled()
    expect(screen.queryByText('(de la familia)')).not.toBeInTheDocument()
  })

  it('sin familia elegida manda idFamilia: null', async () => {
    mockearApi({ familias: [familiaListado()], detalles: { 7: detalleSabores() } })
    await abrirAlta()
    await screen.findByLabelText('Familia (opcional)')

    await userEvent.type(screen.getByLabelText('Nombre'), 'Suelto')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect((apiPostMock.mock.calls[0][1] as Record<string, unknown>).idFamilia).toBeNull()
  })

  it('elegir la familia y volver a "Sin familia" manda idFamilia: null, con los valores que quedaron', async () => {
    await abrirAltaEnLaFamilia()
    await userEvent.selectOptions(screen.getByLabelText('Familia (opcional)'), '')

    await userEvent.type(screen.getByLabelText('Nombre'), 'Suelto')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(apiPostMock.mock.calls[0][1]).toMatchObject({ idFamilia: null, costoLista: 150 })
  })

  /** Cláusula bajo prueba: `escrituraEnCursoRef` (react-async-state regla 11). La guarda de estado (`ocupado`)
   * sola no alcanza: dos submits en el mismo tick la pasan los dos. */
  it('dos clics sincrónicos en "Guardar" emiten un solo POST', async () => {
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    await abrirAltaEnLaFamilia()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Frutilla')

    const guardar = screen.getByRole('button', { name: 'Guardar' })
    await act(async () => {
      guardar.click()
      guardar.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })
})

describe('Articulos — alta: lo que el servidor rechaza de la familia', () => {
  const MENSAJE_DISTINTOS =
    'Los campos compartidos del artículo tienen que ser idénticos a los de la familia "Sabores": difieren id_area, costo_lista.'

  async function guardarAltaEnLaFamilia() {
    await abrirAltaEnLaFamilia()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Frutilla')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
  }

  it('409 familia_valores_distintos: vuelve a pedir la familia, deja sus valores nuevos en el formulario y dice qué campos diferían', async () => {
    mockearApi({
      familias: [familiaListado()],
      detalleDeFamiliaImpl: (_id, llamada) =>
        Promise.resolve(detalleSabores({ valores: { ...referencia, costoLista: llamada === 1 ? 150 : 200 } })),
    })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_valores_distintos', MENSAJE_DISTINTOS))

    await guardarAltaEnLaFamilia()

    expect(
      await screen.findByText(
        'Los valores del artículo no coinciden con los de la familia en: Área, Costo de lista. Se volvieron a cargar los de la familia: revisalos y guardá de nuevo.',
      ),
    ).toBeInTheDocument()
    await waitFor(() => expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('200,00'))
    expect(unaFamiliaPidioSuDetalle()).toHaveLength(2)
    // La familia sigue elegida y sus campos bloqueados: el artículo todavía quiere entrar a ella.
    expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('7')
    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  it('después de un familia_valores_distintos, guardar de nuevo manda los valores recargados', async () => {
    mockearApi({
      familias: [familiaListado()],
      detalleDeFamiliaImpl: (_id, llamada) =>
        Promise.resolve(detalleSabores({ valores: { ...referencia, costoLista: llamada === 1 ? 150 : 200 } })),
    })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_valores_distintos', MENSAJE_DISTINTOS))
    apiPostMock.mockResolvedValueOnce(articuloFixture({ id: 40, codigoInterno: 'A0040', nombre: 'Frutilla', idFamilia: 7 }))
    await guardarAltaEnLaFamilia()
    await waitFor(() => expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('200,00'))

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(2))
    expect(apiPostMock.mock.calls[1][1]).toMatchObject({ costoLista: 200, idFamilia: 7 })
  })

  /** Cláusula bajo prueba: `invalidarEdicionEnCurso()` en `elegirFamilia`: elegir otra familia supera el
   * seguimiento de un guardado rechazado que todavía relee la anterior. Evidencia de mutación (mutation-proof-tests):
   * sacar esa llamada hace fallar este test (aparece el aviso de la familia que ya no está elegida); revertido,
   * vuelve a verde. */
  it('si mientras se relee la familia rechazada se elige otra, el aviso de la anterior no aparece ni pisa a la nueva', async () => {
    const relectura = diferida<FamiliaDetalle>()
    mockearApi({
      familias: [familiaListado(), familiaListado({ id: 8, nombre: 'Talles' })],
      detalleDeFamiliaImpl: (id, llamada) => {
        if (id === 7 && llamada === 2) return relectura.promesa
        return Promise.resolve(id === 8 ? detalleSabores({ id: 8, nombre: 'Talles', valores: { ...referencia, costoLista: 999 } }) : detalleSabores())
      },
    })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_valores_distintos', MENSAJE_DISTINTOS))
    await guardarAltaEnLaFamilia()
    await waitFor(() => expect(unaFamiliaPidioSuDetalle()).toHaveLength(2))

    await userEvent.selectOptions(screen.getByLabelText('Familia (opcional)'), '8')
    await waitFor(() => expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('999,00'))
    await act(async () => {
      relectura.resolver(detalleSabores({ valores: { ...referencia, costoLista: 111 } }))
    })

    expect(screen.queryByText(/Los valores del artículo no coinciden/)).not.toBeInTheDocument()
    expect(screen.queryByText(MENSAJE_DISTINTOS)).not.toBeInTheDocument()
    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('999,00')
    expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('8')
  })

  it('familia_valores_distintos con la recarga fallida: queda el rechazo del servidor y el motivo de la recarga', async () => {
    mockearApi({
      familias: [familiaListado()],
      detalleDeFamiliaImpl: (_id, llamada) =>
        llamada === 1 ? Promise.resolve(detalleSabores()) : Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó la relectura.')),
    })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_valores_distintos', MENSAJE_DISTINTOS))

    await guardarAltaEnLaFamilia()

    expect(await screen.findByText(MENSAJE_DISTINTOS)).toBeInTheDocument()
    expect(await screen.findByText(/Se cayó la relectura/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
  })

  /** Cláusula bajo prueba: `setErrorGuardado('')` de `elegirFamilia`: el rechazo del guardado anterior no se queda en
   * pantalla mientras se carga la familia que se acaba de elegir. Evidencia de mutación (mutation-proof-tests): sacar esa
   * línea hace fallar este test; revertido, vuelve a verde. */
  it('elegir otra familia saca de pantalla el rechazo del guardado anterior mientras se la carga', async () => {
    const lectura = diferida<FamiliaDetalle>()
    mockearApi({
      familias: [familiaListado(), familiaListado({ id: 8, nombre: 'Talles' })],
      detalleDeFamiliaImpl: (id) => (id === 8 ? lectura.promesa : Promise.resolve(detalleSabores())),
    })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(400, 'referencia_invalida', 'No existe el área 1.'))
    await guardarAltaEnLaFamilia()
    await screen.findByText('No existe el área 1.')

    await userEvent.selectOptions(screen.getByLabelText('Familia (opcional)'), '8')

    expect(await screen.findByText('Cargando la familia…')).toBeInTheDocument()
    expect(screen.queryByText('No existe el área 1.')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `setErrorGuardado('')` de `reintentarFamilia`: el rechazo del guardado anterior no se queda
   * en pantalla mientras se vuelve a pedir la familia. Evidencia de mutación (mutation-proof-tests): sacar esa línea
   * hace fallar este test; revertido, vuelve a verde. */
  it('"Reintentar" la carga de la familia saca de pantalla el rechazo del guardado anterior mientras se la vuelve a pedir', async () => {
    const tercera = diferida<FamiliaDetalle>()
    mockearApi({
      familias: [familiaListado()],
      detalleDeFamiliaImpl: (_id, llamada) => {
        if (llamada === 1) return Promise.resolve(detalleSabores())
        if (llamada === 2) return Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó la relectura.'))
        return tercera.promesa
      },
    })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_valores_distintos', MENSAJE_DISTINTOS))
    await guardarAltaEnLaFamilia()
    await screen.findByText(MENSAJE_DISTINTOS)

    await userEvent.click(await screen.findByRole('button', { name: 'Reintentar' }))

    expect(await screen.findByText('Cargando la familia…')).toBeInTheDocument()
    expect(screen.queryByText(MENSAJE_DISTINTOS)).not.toBeInTheDocument()
  })

  it('familia_valores_distintos cuando la familia ya no admite artículos: sale de la selección y lo dice', async () => {
    mockearApi({
      familias: [familiaListado()],
      detalleDeFamiliaImpl: (_id, llamada) =>
        Promise.resolve(llamada === 1 ? detalleSabores() : detalleSabores({ activo: false })),
    })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_valores_distintos', MENSAJE_DISTINTOS))

    await guardarAltaEnLaFamilia()

    expect(
      await screen.findByText('La familia "Sabores" está inactiva: no se le pueden agregar artículos. Elegí otra familia o creá el artículo sin familia.'),
    ).toBeInTheDocument()
    expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('')
    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toBeEnabled()
  })

  it.each([
    ['familia_inactiva', 'La familia "Sabores" está inactiva: no se le pueden agregar artículos.'],
    ['familia_sin_articulos', 'La familia "Sabores" no tiene artículos vivos: no hay valores de referencia ni precios que copiar.'],
  ])('409 %s: avisa con el texto del servidor, saca la familia de la selección y actualiza las que se ofrecen', async (codigo, mensaje) => {
    let lecturasDeFamilias = 0
    mockearApi({
      familiasImpl: () => Promise.resolve(++lecturasDeFamilias === 1 ? [familiaListado()] : []),
      detalles: { 7: detalleSabores() },
    })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, codigo, mensaje))

    await guardarAltaEnLaFamilia()

    expect(await screen.findByText(`${mensaje} Elegí otra familia o creá el artículo sin familia.`)).toBeInTheDocument()
    await waitFor(() => expect(lecturasDeFamilias).toBe(2))
    // Ya no queda ninguna familia para elegir (solo "Sin familia"), y la que se había elegido dejó de bloquear los campos.
    await waitFor(() => expect(within(screen.getByLabelText('Familia (opcional)')).getAllByRole('option').map((o) => o.textContent)).toEqual(['Sin familia']))
    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  it('409 de una familia que se desactivó: la familia desaparece del selector y el artículo queda sin familia', async () => {
    let lecturasDeFamilias = 0
    mockearApi({
      familiasImpl: () =>
        Promise.resolve(++lecturasDeFamilias === 1 ? [familiaListado(), familiaListado({ id: 8, nombre: 'Talles' })] : [familiaListado({ id: 8, nombre: 'Talles' })]),
      detalles: { 7: detalleSabores() },
    })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_inactiva', 'La familia "Sabores" está inactiva: no se le pueden agregar artículos.'))

    await guardarAltaEnLaFamilia()

    await waitFor(() => {
      const selector = screen.getByLabelText('Familia (opcional)')
      expect(within(selector).getAllByRole('option').map((o) => o.textContent)).toEqual(['Sin familia', 'Talles (3 artículos)'])
      expect(selector).toHaveValue('')
    })
  })

  /** Cláusula bajo prueba: un fallo de `recargarOpciones` no vacía el listado. Evidencia de mutación (mutation-proof-tests):
   * volver a `setOpciones([])` en el `catch` del hook hace fallar este test (el selector desaparece y el aviso dice que
   * no se puede elegir una familia); revertido, vuelve a verde. */
  it('409 de una familia con la relectura de las familias fallida: conserva las que se ofrecían y avisa que pueden estar desactualizadas', async () => {
    let lecturasDeFamilias = 0
    mockearApi({
      familiasImpl: () =>
        ++lecturasDeFamilias === 1
          ? Promise.resolve([familiaListado(), familiaListado({ id: 8, nombre: 'Talles' })])
          : Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.')),
      detalles: { 7: detalleSabores() },
    })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_inactiva', 'La familia "Sabores" está inactiva: no se le pueden agregar artículos.'))

    await guardarAltaEnLaFamilia()

    expect(
      await screen.findByText('No se pudieron actualizar las familias: las que se ofrecen pueden estar desactualizadas.'),
    ).toBeInTheDocument()
    const selector = screen.getByLabelText('Familia (opcional)')
    expect(within(selector).getAllByRole('option').map((o) => o.textContent)).toEqual(['Sin familia', 'Sabores (3 artículos)', 'Talles (3 artículos)'])
    expect(selector).toHaveValue('')
    expect(screen.queryByText(/No se puede elegir una familia/)).not.toBeInTheDocument()
  })

  it('404 de la familia: la saca de la selección con el mensaje del servidor', async () => {
    let lecturasDeFamilias = 0
    mockearApi({
      familiasImpl: () => Promise.resolve(++lecturasDeFamilias === 1 ? [familiaListado()] : []),
      detalles: { 7: detalleSabores() },
    })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.'))

    await guardarAltaEnLaFamilia()

    expect(await screen.findByText('No existe la familia 7. Elegí otra familia o creá el artículo sin familia.')).toBeInTheDocument()
    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toBeEnabled()
  })

  it('cualquier otro rechazo se muestra tal cual y la familia sigue elegida', async () => {
    mockearApi({ familias: [familiaListado()], detalles: { 7: detalleSabores() } })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(400, 'referencia_invalida', 'No existe el área 1.'))

    await guardarAltaEnLaFamilia()

    expect(await screen.findByText('No existe el área 1.')).toBeInTheDocument()
    expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('7')
    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toBeDisabled()
    expect(unaFamiliaPidioSuDetalle()).toHaveLength(1)
  })

  it('un 409 de una familia que no es ninguno de los tres de familia se muestra tal cual, sin tocar la selección', async () => {
    mockearApi({ familias: [familiaListado()], detalles: { 7: detalleSabores() } })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'codigo_interno_duplicado', 'Ya existe un artículo con ese código.'))

    await guardarAltaEnLaFamilia()

    expect(await screen.findByText('Ya existe un artículo con ese código.')).toBeInTheDocument()
    expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('7')
  })
})

// ---- edición de un miembro ---------------------------------------------------------------------------------

const PREGUNTA_DE_ALCANCE_SABORES =
  'Este artículo es parte de la familia "Sabores" (3 artículos). ¿Aplicar el cambio a toda la familia?'

function lecturasDelArticulo(id: number) {
  return apiGetMock.mock.calls.filter((c) => c[0] === `/articulos/${id}`).length
}

function escenarioDelMiembro(extra: Escenario = {}) {
  mockearApi({ articulos: { 31: miembro() }, familias: [familiaListado()], detalles: { 7: detalleSabores() }, ...extra })
}

async function cambiarCostoDeLista(texto: string) {
  const campo = screen.getByLabelText('Costo de lista con IVA')
  await userEvent.clear(campo)
  await userEvent.type(campo, texto)
}

async function guardar() {
  await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
}

function dialogoDeAlcance() {
  return screen.getByRole('dialog', { name: 'Cambio en una familia' })
}

function pedidosDeEdicion() {
  return apiPutMock.mock.calls as [string, Record<string, unknown>][]
}

describe('Articulos — edición: la pertenencia a una familia', () => {
  it('un miembro muestra el nombre de su familia y cuántos artículos tiene, y pide su detalle', async () => {
    escenarioDelMiembro()

    await abrirEdicion()

    expect(screen.getByText('Familia "Sabores" (3 artículos)')).toBeInTheDocument()
    expect(unaFamiliaPidioSuDetalle()).toEqual([['/familias/7']])
    expect(screen.getByRole('button', { name: 'Sacar de la familia' })).toBeEnabled()
    expect(screen.queryByLabelText('Familia (opcional)')).not.toBeInTheDocument()
  })

  it('sus campos compartidos NO están bloqueados: se editan y el alcance se pregunta al guardar', async () => {
    escenarioDelMiembro()

    await abrirEdicion()

    for (const etiqueta of CAMPOS_COMPARTIDOS_DEL_FORMULARIO) {
      expect(screen.getByLabelText(etiqueta), String(etiqueta)).toBeEnabled()
    }
    expect(screen.queryByText('(de la familia)')).not.toBeInTheDocument()
  })

  it('un artículo sin familia no muestra nada de familia ni pide ninguna', async () => {
    escenarioDelMiembro({ articulos: { 31: miembro({ idFamilia: null }) } })

    await abrirEdicion()

    expect(screen.queryByText(/^Familia "/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sacar de la familia' })).not.toBeInTheDocument()
    expect(unaFamiliaPidioSuDetalle()).toEqual([])
  })

  it('el editor de precios recuerda que los precios son de la familia', async () => {
    escenarioDelMiembro()

    await abrirEdicion()

    expect(
      await screen.findByText(/Este artículo es parte de la familia "Sabores" \(3 artículos\): al cambiar un precio se pregunta/),
    ).toBeInTheDocument()
  })

  it('si el detalle de la familia no carga, el formulario abre igual: rótulo sin nombre y el motivo', async () => {
    escenarioDelMiembro({ detalleDeFamiliaImpl: () => Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó la familia.')) })

    await abrirEdicion()

    expect(screen.getByText('Pertenece a una familia')).toBeInTheDocument()
    expect(screen.getByText('Se cayó la familia.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  it('sin el detalle de la familia, un cambio compartido igual pregunta, sin nombrar la familia', async () => {
    escenarioDelMiembro({ detalleDeFamiliaImpl: () => Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó la familia.')) })
    await abrirEdicion()

    await cambiarCostoDeLista('175')
    await guardar()

    expect(dialogoDeAlcance()).toHaveTextContent('Este artículo es parte de una familia. ¿Aplicar el cambio a toda la familia?')
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('abrir otro artículo suelta la familia del anterior: el rótulo es el del artículo que se muestra', async () => {
    escenarioDelMiembro({
      articulos: { 31: miembro(), 1: articuloFixture({ id: 1, codigoInterno: 'A0001', idFamilia: null }) },
      grilla: { items: [filaGrilla(), filaGrilla({ id: 31, codigoInterno: 'A0031', nombre: 'Vainilla' })], total: 2, pagina: 1, tamanio: 25, nombreListaPrecio: 'General' },
    })
    await abrirEdicion()
    expect(screen.getByText('Familia "Sabores" (3 artículos)')).toBeInTheDocument()

    const filaUno = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!filaUno) throw new Error('No se encontró la fila del artículo uno')
    await userEvent.click(within(filaUno).getByRole('link', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0001')

    expect(screen.queryByText(/^Familia "/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sacar de la familia' })).not.toBeInTheDocument()
  })
})

describe('Articulos — edición de un miembro: el alcance de un cambio', () => {
  beforeEach(() => {
    escenarioDelMiembro()
    apiPutMock.mockResolvedValue(miembro({ costoLista: 175 }))
  })

  it('un cambio solo de campos propios guarda directo: sin pregunta y sin alcance en el PUT', async () => {
    await abrirEdicion()

    await userEvent.type(screen.getByLabelText('Nombre'), ' 2')
    await guardar()

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument()
    expect(pedidosDeEdicion()[0][1]).toMatchObject({ nombre: 'Vainilla 2' })
    expect(pedidosDeEdicion()[0][1]).not.toHaveProperty('alcance')
  })

  it('guardar sin tocar nada tampoco pregunta', async () => {
    await abrirEdicion()

    await guardar()

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(pedidosDeEdicion()[0][1]).not.toHaveProperty('alcance')
  })

  it('un cambio en un campo compartido no escribe todavía: pregunta con la familia, y lista lo que cambia', async () => {
    await abrirEdicion()

    await cambiarCostoDeLista('175')
    await guardar()

    const dialogo = await screen.findByRole('dialog', { name: 'Cambio en una familia' })
    expect(dialogo).toHaveTextContent(PREGUNTA_DE_ALCANCE_SABORES)
    expect(dialogo).toHaveTextContent('Campos compartidos que cambian: Costo de lista.')
    expect(dialogo).toHaveTextContent(
      'Los campos propios (nombre, descripción, códigos, marca, activo y disponibilidad por empresa) se guardan siempre solo en este artículo.',
    )
    expect(within(dialogo).getByRole('button', { name: 'Toda la familia' })).toBeEnabled()
    expect(within(dialogo).getByRole('button', { name: 'Solo este artículo (sale de la familia)' })).toBeEnabled()
    expect(within(dialogo).getByRole('button', { name: 'Cancelar' })).toBeEnabled()
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('lista todos los campos compartidos que cambian, y no los propios', async () => {
    await abrirEdicion()

    await cambiarCostoDeLista('175')
    await userEvent.click(screen.getByLabelText(/^Controla lote/))
    await userEvent.type(screen.getByLabelText('Nombre'), ' 2')
    await guardar()

    const dialogo = await screen.findByRole('dialog', { name: 'Cambio en una familia' })
    expect(dialogo).toHaveTextContent('Campos compartidos que cambian: Controla lote, Costo de lista.')
    expect(dialogo).not.toHaveTextContent('Nombre')
  })

  it('"Toda la familia" escribe con alcance Familia, cierra la pregunta, avisa y refresca la grilla', async () => {
    await abrirEdicion()
    const lecturasDeLaGrillaAntes = llamadasAGrilla()
    await cambiarCostoDeLista('175')
    await guardar()

    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', { name: 'Toda la familia' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(pedidosDeEdicion()[0][0]).toBe('/articulos/31')
    expect(pedidosDeEdicion()[0][1]).toMatchObject({ costoLista: 175, alcance: 'Familia' })
    expect(
      await screen.findByText('Artículo "Vainilla" actualizado. Los cambios en los campos compartidos se aplicaron a toda la familia "Sabores".'),
    ).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument()
    expect(screen.getByText('Familia "Sabores" (3 artículos)')).toBeInTheDocument()
    await waitFor(() => expect(llamadasAGrilla()).toBeGreaterThan(lecturasDeLaGrillaAntes))
  })

  it('"Solo este artículo" escribe con alcance SoloEste: el artículo sale de la familia y el rótulo desaparece', async () => {
    apiPutMock.mockResolvedValue(miembro({ costoLista: 175, idFamilia: null }))
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()

    await userEvent.click(
      within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', {
        name: 'Solo este artículo (sale de la familia)',
      }),
    )

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(pedidosDeEdicion()[0][1]).toMatchObject({ costoLista: 175, alcance: 'SoloEste' })
    expect(
      await screen.findByText('Artículo "Vainilla" actualizado. Salió de la familia "Sabores" y el cambio quedó solo en él.'),
    ).toBeInTheDocument()
    expect(screen.queryByText(/^Familia "/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sacar de la familia' })).not.toBeInTheDocument()
  })

  it('después de salir con "Solo este", un cambio compartido ya no pregunta: guarda directo', async () => {
    apiPutMock.mockResolvedValueOnce(miembro({ costoLista: 175, idFamilia: null }))
    apiPutMock.mockResolvedValueOnce(miembro({ costoLista: 180, idFamilia: null }))
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()
    await userEvent.click(
      within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', {
        name: 'Solo este artículo (sale de la familia)',
      }),
    )
    await screen.findByText(/Salió de la familia "Sabores"/)

    await cambiarCostoDeLista('180')
    await guardar()

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(2))
    expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument()
    expect(pedidosDeEdicion()[1][1]).not.toHaveProperty('alcance')
  })

  it('"Cancelar" cierra la pregunta sin escribir nada y conserva lo tipeado; se puede volver a preguntar', async () => {
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()

    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument()
    expect(apiPutMock).not.toHaveBeenCalled()
    expect(screen.getByLabelText('Costo de lista con IVA')).toHaveValue('175,00')

    await guardar()
    expect(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).toBeInTheDocument()
  })

  it('Escape cierra solo la pregunta: el modal del artículo sigue abierto con lo tipeado', async () => {
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()
    await screen.findByRole('dialog', { name: 'Cambio en una familia' })

    fireEvent.keyDown(document, { key: 'Escape' })

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument())
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0031' })).toBeInTheDocument()
    expect(screen.getByLabelText('Costo de lista con IVA')).toHaveValue('175,00')
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('un cambio compartido que se deshace antes de guardar no pregunta nada', async () => {
    await abrirEdicion()

    await cambiarCostoDeLista('175')
    await cambiarCostoDeLista('150')
    await guardar()

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument()
    expect(pedidosDeEdicion()[0][1]).not.toHaveProperty('alcance')
  })

  /** Cláusula bajo prueba: la ventana inerte de la pregunta mientras el PUT está en vuelo (react-async-state
   * regla 5): las tres respuestas, el cierre y Escape quedan sin efecto hasta que el guardado terminó. */
  it('con el PUT en vuelo, las tres respuestas y el cierre quedan inertes', async () => {
    let resolverPut!: (articulo: ArticuloListado) => void
    apiPutMock.mockImplementation(() => new Promise<ArticuloListado>((resolver) => (resolverPut = resolver)))
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()
    const dialogo = await screen.findByRole('dialog', { name: 'Cambio en una familia' })

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Toda la familia' }))

    for (const boton of within(dialogo).getAllByRole('button')) expect(boton).toBeDisabled()
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.getByRole('dialog', { name: 'Cambio en una familia' })).toBeInTheDocument()
    expect(botonNuevo()).toBeDisabled()

    await act(async () => {
      resolverPut(miembro({ costoLista: 175 }))
    })
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument())
    expect(botonNuevo()).toBeEnabled()
  })

  /** Cláusula bajo prueba: `escrituraEnCursoRef` — dos clics sincrónicos en la misma respuesta emiten un solo PUT. */
  it('dos clics sincrónicos en "Toda la familia" emiten un solo PUT', async () => {
    apiPutMock.mockImplementation(() => new Promise(() => {}))
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()
    const toda = within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', { name: 'Toda la familia' })

    await act(async () => {
      toda.click()
      toda.click()
      await Promise.resolve()
    })

    expect(apiPutMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: `setAvisoGuardado('')` de `pedirGuardado`. Abrir la pregunta no pasa por `guardar`, que ya
   * limpia el aviso: esa línea es la única que saca el del guardado anterior. Evidencia de mutación
   * (mutation-proof-tests): sacarla hace fallar este test; revertido, vuelve a verde. */
  it('abrir la pregunta saca de pantalla el aviso del guardado anterior', async () => {
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', { name: 'Toda la familia' }))
    await screen.findByText('Artículo "Vainilla" actualizado. Los cambios en los campos compartidos se aplicaron a toda la familia "Sabores".')

    await cambiarCostoDeLista('180')
    await guardar()

    expect(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).toBeInTheDocument()
    expect(screen.queryByText(/actualizado\./)).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `setErrorGuardado('')` de `pedirGuardado`, la hermana de la anterior para el rechazo.
   * Evidencia de mutación (mutation-proof-tests): sacarla hace fallar este test; revertido, vuelve a verde. */
  it('abrir la pregunta saca de pantalla el rechazo del guardado anterior', async () => {
    apiPutMock.mockRejectedValueOnce(new ErrorApi(400, 'referencia_invalida', 'No existe el área 1.'))
    await abrirEdicion()
    await userEvent.type(screen.getByLabelText('Nombre'), ' 2')
    await guardar()
    await screen.findByText('No existe el área 1.')

    await cambiarCostoDeLista('175')
    await guardar()

    expect(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).toBeInTheDocument()
    expect(screen.queryByText('No existe el área 1.')).not.toBeInTheDocument()
  })

  /** Advertencia (react-async-state regla 12): jsdom no implementa la regla de "focus fixup" del navegador, así que las
   * pruebas de foco ejercitan la restauración explícita, no el comportamiento de un navegador real. Cláusula bajo
   * prueba: el cleanup de `FormularioArticulo` que enfoca "Nombre" cuando la pregunta se cierra con el foco perdido: tras
   * una respuesta el botón "Guardar" todavía está deshabilitado cuando `Modal` intenta devolverle el foco. Evidencia de
   * mutación (mutation-proof-tests): sacar ese efecto hace fallar las tres pruebas de foco de la respuesta; revertido,
   * vuelven a verde. */
  it.each([
    ['Toda la familia', () => miembro({ costoLista: 175 })],
    ['Solo este artículo (sale de la familia)', () => miembro({ costoLista: 175, idFamilia: null })],
  ])('al responder "%s" y cerrarse la pregunta, el foco queda en "Nombre" y no en el <body>', async (respuesta, guardado) => {
    apiPutMock.mockResolvedValue(guardado())
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()

    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', { name: respuesta }))

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument())
    await waitFor(() => expect(screen.getByLabelText('Nombre')).toHaveFocus())
  })

  it('si el PUT se rechaza, al cerrarse la pregunta el foco también queda en "Nombre"', async () => {
    apiPutMock.mockRejectedValueOnce(new ErrorApi(400, 'referencia_invalida', 'No existe el área 1.'))
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()

    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', { name: 'Toda la familia' }))

    await screen.findByText('No existe el área 1.')
    await waitFor(() => expect(screen.getByLabelText('Nombre')).toHaveFocus())
  })

  /** Cláusula bajo prueba: `document.activeElement === document.body` del cleanup anterior: con "Cancelar" el foco no se
   * perdió (`Modal` se lo devuelve a "Guardar") y no se le saca. Evidencia de mutación (mutation-proof-tests): enfocar
   * "Nombre" siempre hace fallar este test; revertido, vuelve a verde. */
  it('"Cancelar" deja el foco en "Guardar", el botón que abrió la pregunta, y no lo lleva a "Nombre"', async () => {
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()

    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', { name: 'Cancelar' }))

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Guardar' })).toHaveFocus()
  })

  it('cualquier otro rechazo del PUT se muestra en el formulario, cierra la pregunta y deja volver a intentar', async () => {
    apiPutMock.mockRejectedValueOnce(new ErrorApi(400, 'referencia_invalida', 'No existe el área 1.'))
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', { name: 'Toda la familia' }))

    expect(await screen.findByText('No existe el área 1.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
    expect(screen.getByText('Familia "Sabores" (3 artículos)')).toBeInTheDocument()
  })
})

describe('Articulos — edición de un miembro: lo que el servidor dice de la familia', () => {
  /** El formulario no sabía que el artículo es miembro: no pregunta, el servidor lo frena y la pregunta se hace
   * recién con su texto. */
  it('409 alcance_requerido: abre la pregunta con el texto del servidor y, al elegir, vuelve a escribir con ese alcance', async () => {
    const mensaje =
      'El artículo pertenece a la familia "Sabores" (3 artículos): la edición cambia campos compartidos y tiene que indicar si se aplican a toda la familia o solo a este artículo.'
    escenarioDelMiembro({ articulos: { 31: miembro({ idFamilia: null }) } })
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'alcance_requerido', mensaje))
    apiPutMock.mockResolvedValueOnce(miembro({ costoLista: 175 }))
    await abrirEdicion()

    await cambiarCostoDeLista('175')
    await guardar()

    const dialogo = await screen.findByRole('dialog', { name: 'Cambio en una familia' })
    expect(dialogo).toHaveTextContent(`${mensaje} ¿Aplicar el cambio a toda la familia?`)
    expect(dialogo).toHaveTextContent('Campos compartidos que cambian: Costo de lista.')
    expect(pedidosDeEdicion()[0][1]).not.toHaveProperty('alcance')
    expect(within(dialogo).getByRole('button', { name: 'Toda la familia' })).toBeEnabled()
    expect(screen.queryByText(mensaje, { selector: '.alert-danger' })).not.toBeInTheDocument()

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Toda la familia' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(2))
    expect(pedidosDeEdicion()[1][1]).toMatchObject({ costoLista: 175, alcance: 'Familia' })
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument())
  })

  /** Cláusula bajo prueba: el `seguimiento` de `guardar`, que recarga el artículo DESPUÉS de soltar el guardado.
   * Recargarlo dentro del `catch` invalidaría el token y dejaría el formulario ocupado para siempre. */
  it('409 familia_cambio: recarga el artículo con un aviso, cierra la pregunta y deja el formulario operable', async () => {
    escenarioDelMiembro()
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()

    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', { name: 'Toda la familia' }))

    expect(
      await screen.findByText(
        'La pertenencia del artículo a su familia cambió desde que se cargó la pantalla. Se recargó el artículo: revisá los datos y volvé a intentar.',
      ),
    ).toBeInTheDocument()
    expect(lecturasDelArticulo(31)).toBe(2)
    expect(unaFamiliaPidioSuDetalle()).toHaveLength(2)
    expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Costo de lista con IVA')).toHaveValue('150,00')
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
    expect(botonNuevo()).toBeEnabled()
    // Operable de verdad: se puede cambiar algo y guardar otra vez.
    apiPutMock.mockResolvedValueOnce(miembro({ nombre: 'Vainilla 2' }))
    await userEvent.type(screen.getByLabelText('Nombre'), ' 2')
    await guardar()
    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(2))
  })

  /** Cláusula bajo prueba: el `seguimiento` de `guardar`, ahora en el camino en que la relectura FALLA: el guardado
   * tiene que haberse soltado ANTES de recargar, o el formulario queda ocupado y el modal no se puede cerrar.
   * Evidencia de mutación (mutation-proof-tests): ejecutar el seguimiento dentro del `catch` hace fallar este test;
   * revertido, vuelve a verde. */
  it('409 familia_cambio con la relectura del artículo fallida: muestra el motivo y el modal se puede cerrar', async () => {
    escenarioDelMiembro({
      articuloImpl: (_id, llamada) =>
        llamada === 1 ? miembro() : Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó la relectura.')),
    })
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', { name: 'Toda la familia' }))

    expect(await screen.findByText('Se cayó la relectura.')).toBeInTheDocument()
    expect(botonNuevo()).toBeEnabled()
    await userEvent.click(screen.getByRole('button', { name: 'Volver al listado' }))

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Artículo' })).not.toBeInTheDocument())
  })

  /** Cláusula bajo prueba: la rama `campos.length === 0` del `alcance_requerido` de `tratarFalloDeGuardado`: el servidor
   * ve un cambio compartido que la pantalla no ve, así que lo que ella muestra está desactualizado y preguntar el
   * alcance ofrecería escribir esos valores viejos. Evidencia de mutación (mutation-proof-tests): sacar la rama hace
   * fallar este test (se abre la pregunta, sin campos y con el texto del servidor); revertido, vuelve a verde. */
  it('409 alcance_requerido sin ningún campo compartido cambiado a la vista: recarga el artículo y lo explica, en vez de preguntar', async () => {
    const mensaje =
      'El artículo pertenece a la familia "Sabores" (3 artículos): la edición cambia campos compartidos y tiene que indicar si se aplican a toda la familia o solo a este artículo.'
    escenarioDelMiembro({
      articuloImpl: (_id, llamada) => (llamada === 1 ? miembro({ idFamilia: null }) : miembro({ costoLista: 175 })),
    })
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'alcance_requerido', mensaje))
    await abrirEdicion()

    await userEvent.type(screen.getByLabelText('Nombre'), ' 2')
    await guardar()

    expect(
      await screen.findByText(
        'Los campos compartidos del artículo cambiaron desde que se cargó la pantalla. Se recargó el artículo: revisá los datos y volvé a intentar.',
      ),
    ).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument()
    expect(lecturasDelArticulo(31)).toBe(2)
    expect(screen.getByLabelText('Costo de lista con IVA')).toHaveValue('175,00')
    expect(screen.getByLabelText('Nombre')).toHaveValue('Vainilla')
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
    expect(botonNuevo()).toBeEnabled()
  })

  it('409 familia_cambio porque el artículo dejó de ser miembro: tras recargar ya no muestra familia', async () => {
    escenarioDelMiembro({ articuloImpl: (_id, llamada) => (llamada === 1 ? miembro() : miembro({ idFamilia: null })) })
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()

    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', { name: 'Toda la familia' }))

    await screen.findByText(/Se recargó el artículo/)
    expect(screen.queryByText(/^Familia "/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sacar de la familia' })).not.toBeInTheDocument()
  })
})

describe('Articulos — edición de un miembro: sacarlo de su familia', () => {
  beforeEach(() => {
    escenarioDelMiembro()
    apiDeleteMock.mockResolvedValue(undefined)
  })

  function confirmacionDeSalida() {
    return screen.getByRole('group', { name: 'Confirmar salida de la familia' })
  }

  it('"Sacar de la familia" no escribe: pide confirmar, nombrando al artículo y a la familia, y enfoca "Cancelar"', async () => {
    await abrirEdicion()

    await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))

    expect(confirmacionDeSalida()).toHaveTextContent('¿Sacar "Vainilla" de la familia "Sabores"?')
    expect(confirmacionDeSalida()).toHaveTextContent(
      'El artículo queda sin familia y conserva todos sus valores y sus precios; los demás artículos de la familia no cambian.',
    )
    expect(within(confirmacionDeSalida()).getByRole('button', { name: 'Cancelar' })).toHaveFocus()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  /** Cláusula bajo prueba: `bloqueado = ocupado || confirmandoSalida` de `FormularioArticulo`: la confirmación deja
   * inerte el resto del formulario y de sus hijos (react-async-state regla 13). */
  it('con la confirmación abierta el resto del formulario queda inerte, y la confirmación no', async () => {
    await abrirEdicion()

    await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))

    expect(screen.getByLabelText('Nombre')).toBeDisabled()
    expect(screen.getByLabelText('Costo de lista con IVA')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Sacar de la familia' })).toBeDisabled()
    expect(within(confirmacionDeSalida()).getByRole('button', { name: 'Confirmar salida' })).toBeEnabled()
    expect(within(confirmacionDeSalida()).getByRole('button', { name: 'Cancelar' })).toBeEnabled()
    // Los hijos que no están dentro del fieldset también: el editor de precios espera sus datos y los códigos de barra
    // cargan los suyos, y los dos reciben el bloqueo.
    expect(await screen.findByRole('button', { name: 'Calcular sugerencia de precio' })).toBeDisabled()
    expect(await screen.findByPlaceholderText('Código de barras')).toBeDisabled()
  })

  /** Advertencia (react-async-state regla 12): jsdom no implementa la regla de "focus fixup" del navegador, así que
   * esto prueba la restauración explícita del foco, no el comportamiento de un navegador real. */
  it('"Cancelar" cierra la confirmación sin escribir, reactiva el formulario y devuelve el foco al botón que la abrió', async () => {
    await abrirEdicion()
    const disparador = screen.getByRole('button', { name: 'Sacar de la familia' })
    await userEvent.click(disparador)

    await userEvent.click(within(confirmacionDeSalida()).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('group', { name: 'Confirmar salida de la familia' })).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(screen.getByLabelText('Nombre')).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
    expect(disparador).toBeEnabled()
    expect(disparador).toHaveFocus()
  })

  it('"Confirmar salida" saca al artículo: DELETE a la familia y el artículo, el rótulo desaparece y se avisa', async () => {
    await abrirEdicion()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))

    await userEvent.click(within(confirmacionDeSalida()).getByRole('button', { name: 'Confirmar salida' }))

    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledExactlyOnceWith('/familias/7/articulos/31'))
    expect(
      await screen.findByText('El artículo "Vainilla" salió de la familia "Sabores": conserva todos sus valores y sus precios.'),
    ).toBeInTheDocument()
    expect(screen.queryByText(/^Familia "/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sacar de la familia' })).not.toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Confirmar salida de la familia' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toBeEnabled()
  })

  it('sacar no deja "cambios sin guardar": cerrar el formulario después no pregunta nada', async () => {
    const confirmar = vi.spyOn(window, 'confirm').mockReturnValue(true)
    await abrirEdicion()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))
    await userEvent.click(within(confirmacionDeSalida()).getByRole('button', { name: 'Confirmar salida' }))
    await screen.findByText(/salió de la familia "Sabores"/)

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Editando artículo A0031' })).not.toBeInTheDocument())
    expect(confirmar).not.toHaveBeenCalled()
  })

  it('lo que se había tipeado antes de sacar al artículo sigue ahí, y sigue contando como cambio sin guardar', async () => {
    const confirmar = vi.spyOn(window, 'confirm').mockReturnValue(false)
    await abrirEdicion()
    await userEvent.type(screen.getByLabelText('Nombre'), ' 2')
    await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))
    await userEvent.click(within(confirmacionDeSalida()).getByRole('button', { name: 'Confirmar salida' }))
    await screen.findByText(/salió de la familia "Sabores"/)

    expect(screen.getByLabelText('Nombre')).toHaveValue('Vainilla 2')
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(confirmar).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0031' })).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `saliendoDeFamilia` dentro de `ocupado` y la guarda `escrituraEnCursoRef` (react-async-state
   * reglas 5 y 11): desde el clic hasta que el DELETE vuelve, todo queda inerte y no se emite un segundo DELETE. */
  it('con el DELETE en vuelo todo queda inerte y dos clics sincrónicos emiten un solo DELETE', async () => {
    let resolverDelete!: () => void
    apiDeleteMock.mockImplementation(() => new Promise<void>((resolver) => (resolverDelete = resolver)))
    await abrirEdicion()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))
    const confirmar = within(confirmacionDeSalida()).getByRole('button', { name: 'Confirmar salida' })

    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    expect(within(confirmacionDeSalida()).getByRole('button', { name: 'Sacando…' })).toBeDisabled()
    expect(within(confirmacionDeSalida()).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByLabelText('Nombre')).toBeDisabled()
    expect(botonNuevo()).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cerrar' })).toBeDisabled()

    await act(async () => {
      resolverDelete()
    })
    await waitFor(() => expect(screen.queryByRole('group', { name: 'Confirmar salida de la familia' })).not.toBeInTheDocument())
    expect(botonNuevo()).toBeEnabled()
  })

  it('después de sacarlo, un cambio compartido guarda directo: ya no es miembro, no hay nada que preguntar', async () => {
    apiPutMock.mockResolvedValue(miembro({ costoLista: 175, idFamilia: null }))
    await abrirEdicion()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))
    await userEvent.click(within(confirmacionDeSalida()).getByRole('button', { name: 'Confirmar salida' }))
    await screen.findByText(/salió de la familia "Sabores"/)

    await cambiarCostoDeLista('175')
    await guardar()

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument()
    expect(pedidosDeEdicion()[0][1]).not.toHaveProperty('alcance')
  })

  it('409 familia_cambio: recarga el artículo con un aviso y deja el formulario operable', async () => {
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    await abrirEdicion()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))

    await userEvent.click(within(confirmacionDeSalida()).getByRole('button', { name: 'Confirmar salida' }))

    expect(await screen.findByText(/Se recargó el artículo: revisá los datos y volvé a intentar\./)).toBeInTheDocument()
    expect(lecturasDelArticulo(31)).toBe(2)
    expect(screen.queryByRole('group', { name: 'Confirmar salida de la familia' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
    expect(botonNuevo()).toBeEnabled()
  })

  it('404 al sacarlo (la familia o el artículo ya no existen): recarga el artículo con el aviso, sin dejar el formulario ocupado', async () => {
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.'))
    await abrirEdicion()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))

    await userEvent.click(within(confirmacionDeSalida()).getByRole('button', { name: 'Confirmar salida' }))

    expect(await screen.findByText(/Se recargó el artículo: revisá los datos y volvé a intentar./)).toBeInTheDocument()
    expect(lecturasDelArticulo(31)).toBe(2)
    expect(screen.queryByRole('group', { name: 'Confirmar salida de la familia' })).not.toBeInTheDocument()
    expect(botonNuevo()).toBeEnabled()
  })

  /** Advertencia (react-async-state regla 12): jsdom no implementa la regla de "focus fixup" del navegador, así que las
   * dos pruebas de foco de la confirmación ejercitan la restauración explícita, no el comportamiento de un navegador
   * real. Cláusula bajo prueba: `focoDeSalidaRef.current = disparador` de `confirmarSalida`. Evidencia de mutación
   * (mutation-proof-tests): sacarla hace fallar las dos (el foco queda en `<body>`); revertido, vuelven a verde. */
  it('si la salida falla, el foco vuelve al botón "Sacar de la familia", que sigue ahí', async () => {
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Se cayó el servidor.'))
    await abrirEdicion()
    const disparador = screen.getByRole('button', { name: 'Sacar de la familia' })
    await userEvent.click(disparador)

    await userEvent.click(within(confirmacionDeSalida()).getByRole('button', { name: 'Confirmar salida' }))

    await screen.findByText('Se cayó el servidor.')
    await waitFor(() => expect(disparador).toHaveFocus())
  })

  /** Cláusula bajo prueba: el `else refNombre.current?.focus()` del efecto de `FormularioArticulo`: si salió bien, el
   * botón ya no existe y el foco va al primer campo editable. Evidencia de mutación (mutation-proof-tests): sacar ese
   * `else` hace fallar este test; revertido, vuelve a verde. */
  it('si la salida sale bien, el botón ya no existe y el foco va a "Nombre"', async () => {
    await abrirEdicion()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))

    await userEvent.click(within(confirmacionDeSalida()).getByRole('button', { name: 'Confirmar salida' }))

    await screen.findByText(/salió de la familia "Sabores"/)
    await waitFor(() => expect(screen.getByLabelText('Nombre')).toHaveFocus())
  })

  it('cualquier otro rechazo se muestra en el formulario, cierra la confirmación y el artículo sigue en su familia', async () => {
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Se cayó el servidor.'))
    await abrirEdicion()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))

    await userEvent.click(within(confirmacionDeSalida()).getByRole('button', { name: 'Confirmar salida' }))

    expect(await screen.findByText('Se cayó el servidor.')).toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Confirmar salida de la familia' })).not.toBeInTheDocument()
    expect(screen.getByText('Familia "Sabores" (3 artículos)')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sacar de la familia' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })
})

describe('Articulos — lo que el editor de precios de un artículo ya cerrado le avisa a la pantalla', () => {
  const otro = articuloFixture({ id: 5, codigoInterno: 'A0005', nombre: 'Otro', idFamilia: null })

  /** Con un precio en vuelo la pantalla no deja cambiar de artículo desde la grilla, pero el Atrás del navegador sí:
   * el editor del artículo que se dejó sigue esperando su respuesta, y la acción que llegue tarde no puede tocar al
   * que se abrió después. */
  async function dejarElArticuloConUnPrecioEnVuelo(
    respuestaDelPrecio: { promesa: Promise<unknown> },
    alcance: 'Toda la familia' | 'Solo este artículo (sale de la familia)',
    escenarioExtra: Escenario = {},
  ) {
    escenarioDelMiembro({ articulos: { 31: miembro(), 5: otro }, ...escenarioExtra })
    apiPostMock.mockImplementation(() => respuestaDelPrecio.promesa)
    renderArticulosConHistorial(['/articulos/edit/5', '/articulos/edit/31'], 1)
    await screen.findByText('Editando artículo A0031')

    await userEvent.click((await screen.findAllByRole('button', { name: 'Gestionar' }))[0])
    await userEvent.type(await screen.findByLabelText('Precio'), '1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))
    await userEvent.click(screen.getByRole('button', { name: alcance }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))

    act(() => navegar(-1))
    await screen.findByText('Editando artículo A0005')
  }

  /** Cláusula bajo prueba: `destinoModalRef.current !== idArticulo` de `recargarArticuloDesactualizado`. Evidencia de
   * mutación (mutation-proof-tests): sacar esa guarda hace fallar este test (el modal vuelve a abrir el artículo 31
   * encima del 5); revertido, vuelve a verde. */
  it('un familia_cambio que llega tarde no recarga el artículo anterior encima del que se abrió después', async () => {
    const respuesta = diferida<unknown>()
    await dejarElArticuloConUnPrecioEnVuelo(respuesta, 'Toda la familia')

    await act(async () => {
      respuesta.rechazar(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    })

    expect(screen.getByRole('dialog', { name: 'Editando artículo A0005' })).toBeInTheDocument()
    expect(screen.queryByText('Editando artículo A0031')).not.toBeInTheDocument()
    expect(screen.queryByText(/Se recargó el artículo/)).not.toBeInTheDocument()
    expect(lecturasDelArticulo(31)).toBe(1)
  })

  /** Cláusula bajo prueba: `destinoModalRef.current !== idArticulo` de `alSalirDeLaFamiliaPorUnPrecio`. Evidencia
   * de mutación (mutation-proof-tests): sacar esa guarda hace fallar este test (el aviso de que el artículo 31 salió
   * de su familia aparece en el formulario del 5); revertido, vuelve a verde. */
  it('un precio escrito con "solo este" que se confirma tarde no deja su aviso en el artículo que se abrió después', async () => {
    const respuesta = diferida<unknown>()
    await dejarElArticuloConUnPrecioEnVuelo(respuesta, 'Solo este artículo (sale de la familia)')

    await act(async () => {
      respuesta.resolver({ idArticulo: 31, idListaPrecio: 2, precio: 1500, fecha: '2026-10-05T00:00:00Z' })
    })

    expect(screen.getByRole('dialog', { name: 'Editando artículo A0005' })).toBeInTheDocument()
    expect(screen.queryByText(/salió de la familia/)).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `void familias.recargarOpciones()` ANTES de la guarda de `alSalirDeLaFamiliaPorUnPrecio`: el
   * artículo salió de su familia en el servidor aunque la pantalla ya muestre otro, así que las familias que se ofrecen
   * se vuelven a leer igual. Evidencia de mutación (mutation-proof-tests): pasar esa llamada después de la guarda hace
   * fallar este test; revertido, vuelve a verde. */
  it('un precio escrito con "solo este" que se confirma tarde igual actualiza las familias que se ofrecen', async () => {
    const respuesta = diferida<unknown>()
    let lecturasDeFamilias = 0
    await dejarElArticuloConUnPrecioEnVuelo(respuesta, 'Solo este artículo (sale de la familia)', {
      familiasImpl: () => Promise.resolve(++lecturasDeFamilias === 1 ? [familiaListado()] : [familiaListado({ cantidadArticulos: 2 })]),
    })

    await act(async () => {
      respuesta.resolver({ idArticulo: 31, idListaPrecio: 2, precio: 1500, fecha: '2026-10-05T00:00:00Z' })
    })

    await waitFor(() => expect(lecturasDeFamilias).toBe(2))
  })
})

describe('Articulos — lo que el editor de precios le avisa a la pantalla del artículo abierto', () => {
  async function escribirUnPrecioConAlcance(respuesta: 'Toda la familia' | 'Solo este artículo (sale de la familia)') {
    await userEvent.click((await screen.findAllByRole('button', { name: 'Gestionar' }))[0])
    await userEvent.type(await screen.findByLabelText('Precio'), '1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))
    await userEvent.click(screen.getByRole('button', { name: respuesta }))
  }

  /** Cláusula bajo prueba: el cableado `alSalirDeLaFamilia` → `alSalirDeLaFamiliaPorUnPrecio` de `Articulos`: un precio
   * escrito con "solo este" saca al artículo de su familia en el formulario, lo avisa y no lo deja como cambio sin
   * guardar. Evidencia de mutación (mutation-proof-tests): dejar `alSalirDeLaFamilia` como una función vacía hace
   * fallar este test (no aparece el aviso); revertido, vuelve a verde. */
  it('un precio escrito con "solo este" saca al artículo de su familia en el formulario, lo avisa y no deja cambios sin guardar', async () => {
    escenarioDelMiembro()
    apiPostMock.mockResolvedValue({ idArticulo: 31, idListaPrecio: 2, precio: 1500, fecha: '2026-10-05T00:00:00Z' })
    const confirmar = vi.spyOn(window, 'confirm').mockReturnValue(false)
    await abrirEdicion()

    await escribirUnPrecioConAlcance('Solo este artículo (sale de la familia)')

    expect(await screen.findByText('El artículo salió de la familia "Sabores": el precio se guardó solo en él.')).toBeInTheDocument()
    expect(screen.queryByText(/^Familia "/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sacar de la familia' })).not.toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('button', { name: 'Cancelar' })).toBeEnabled())

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Editando artículo A0031' })).not.toBeInTheDocument())
    expect(confirmar).not.toHaveBeenCalled()
  })

  /** Cláusula bajo prueba: el cableado `alCambiarLaFamilia` → `recargarArticuloDesactualizado` de `Articulos`: un
   * `familia_cambio` del editor de precios del artículo que sigue abierto lo recarga con el aviso. Evidencia de mutación
   * (mutation-proof-tests): dejar `alCambiarLaFamilia` como una función vacía hace fallar este test (el artículo no se
   * vuelve a leer); revertido, vuelve a verde. */
  it('un familia_cambio del editor de precios recarga el artículo con el aviso y deja el formulario operable', async () => {
    escenarioDelMiembro({ articuloImpl: (_id, llamada) => (llamada === 1 ? miembro() : miembro({ idFamilia: null })) })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    await abrirEdicion()

    await escribirUnPrecioConAlcance('Toda la familia')

    expect(
      await screen.findByText(
        'La pertenencia del artículo a su familia cambió desde que se cargó la pantalla. Se recargó el artículo: revisá los datos y volvé a intentar.',
      ),
    ).toBeInTheDocument()
    expect(lecturasDelArticulo(31)).toBe(2)
    expect(screen.queryByRole('button', { name: 'Sacar de la familia' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
    expect(botonNuevo()).toBeEnabled()
  })
})

describe('Articulos — las familias que se ofrecen en un alta se mantienen al día', () => {
  /** La primera lectura de las familias devuelve `antes`; las siguientes, `despues`. */
  function familiasAntesYDespues(antes: FamiliaListado[], despues: FamiliaListado[]) {
    let lecturas = 0
    return () => Promise.resolve(++lecturas === 1 ? antes : despues)
  }

  /** Cierra el modal del artículo que se estaba editando y abre un alta nueva: ahí se ve lo que la pantalla ofrece. */
  async function abrirUnAltaNueva() {
    await userEvent.click(within(screen.getByRole('dialog', { name: /^Editando artículo/ })).getByRole('button', { name: 'Cancelar' }))
    await waitFor(() => expect(screen.queryByRole('dialog', { name: /^Editando artículo/ })).not.toBeInTheDocument())
    await userEvent.click(botonNuevo())
    await screen.findByText('Nuevo artículo')
  }

  function opcionesDelSelector() {
    return within(screen.getByLabelText('Familia (opcional)'))
      .getAllByRole('option')
      .map((o) => o.textContent)
  }

  /** Cláusula bajo prueba: `void familias.recargarOpciones()` del alta dentro de una familia de `guardar`: la familia
   * ahora tiene un miembro más. Evidencia de mutación (mutation-proof-tests): sacar esa línea hace fallar este test (el
   * próximo alta sigue ofreciendo "Sabores (3 artículos)"); revertido, vuelve a verde. */
  it('crear un artículo dentro de una familia: el próximo alta la ofrece con un artículo más', async () => {
    const creado = articuloFixture({ id: 40, codigoInterno: 'A0040', nombre: 'Frutilla', idFamilia: 7, ...compartidosDelArticulo })
    mockearApi({
      familiasImpl: familiasAntesYDespues([familiaListado({ cantidadArticulos: 3 })], [familiaListado({ cantidadArticulos: 4 })]),
      detalles: { 7: detalleSabores() },
      articulos: { 40: creado },
    })
    apiPostMock.mockResolvedValue(creado)
    await abrirAltaEnLaFamilia()
    await userEvent.type(screen.getByLabelText('Nombre'), 'Frutilla')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Editando artículo A0040')

    await abrirUnAltaNueva()

    await waitFor(() => expect(opcionesDelSelector()).toEqual(['Sin familia', 'Sabores (4 artículos)']))
  })

  /** Cláusula bajo prueba: `void familias.recargarOpciones()` de `sacarDeLaFamilia`: una familia que se quedó sin
   * miembros deja de ofrecerse. Evidencia de mutación (mutation-proof-tests): sacar esa línea hace fallar este test (el
   * próximo alta sigue ofreciendo la familia vacía); revertido, vuelve a verde. */
  it('sacar al último miembro de una familia: el próximo alta ya no la ofrece', async () => {
    escenarioDelMiembro({
      familiasImpl: familiasAntesYDespues([familiaListado({ cantidadArticulos: 1 })], [familiaListado({ cantidadArticulos: 0 })]),
    })
    apiDeleteMock.mockResolvedValue(undefined)
    await abrirEdicion()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))
    await userEvent.click(within(screen.getByRole('group', { name: 'Confirmar salida de la familia' })).getByRole('button', { name: 'Confirmar salida' }))
    await screen.findByText(/salió de la familia "Sabores"/)

    await abrirUnAltaNueva()

    await waitFor(() => expect(within(screen.getByLabelText('Familia (opcional)')).getAllByRole('option').map((o) => o.textContent)).toEqual(['Sin familia']))
  })

  /** Cláusula bajo prueba: `if (alcance === 'SoloEste') void familias.recargarOpciones()` de `guardar`. Evidencia de
   * mutación (mutation-proof-tests): sacar esa línea hace fallar este test (el próximo alta sigue ofreciendo
   * "Sabores (3 artículos)"); revertido, vuelve a verde. */
  it('guardar con "solo este": el próximo alta ofrece la familia con un artículo menos', async () => {
    escenarioDelMiembro({
      familiasImpl: familiasAntesYDespues([familiaListado({ cantidadArticulos: 3 })], [familiaListado({ cantidadArticulos: 2 })]),
    })
    apiPutMock.mockResolvedValue(miembro({ costoLista: 175, idFamilia: null }))
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()
    await userEvent.click(
      within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', {
        name: 'Solo este artículo (sale de la familia)',
      }),
    )
    await screen.findByText(/Salió de la familia "Sabores"/)

    await abrirUnAltaNueva()

    await waitFor(() => expect(opcionesDelSelector()).toEqual(['Sin familia', 'Sabores (2 artículos)']))
  })

  /** Cláusula bajo prueba: `void familias.recargarOpciones()` de `alSalirDeLaFamiliaPorUnPrecio`. Evidencia de mutación
   * (mutation-proof-tests): sacar esa línea hace fallar este test; revertido, vuelve a verde. */
  it('un precio escrito con "solo este": el próximo alta ofrece la familia con un artículo menos', async () => {
    escenarioDelMiembro({
      familiasImpl: familiasAntesYDespues([familiaListado({ cantidadArticulos: 3 })], [familiaListado({ cantidadArticulos: 2 })]),
    })
    apiPostMock.mockResolvedValue({ idArticulo: 31, idListaPrecio: 2, precio: 1500, fecha: '2026-10-05T00:00:00Z' })
    await abrirEdicion()
    await userEvent.click((await screen.findAllByRole('button', { name: 'Gestionar' }))[0])
    await userEvent.type(await screen.findByLabelText('Precio'), '1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))
    await userEvent.click(screen.getByRole('button', { name: 'Solo este artículo (sale de la familia)' }))
    await screen.findByText(/el precio se guardó solo en él/)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Cancelar' })).toBeEnabled())

    await abrirUnAltaNueva()

    await waitFor(() => expect(opcionesDelSelector()).toEqual(['Sin familia', 'Sabores (2 artículos)']))
  })
})

describe('Articulos — abrir un artículo mientras el anterior todavía espera su familia', () => {
  /** Cláusula bajo prueba: el segundo `if (tokenEdicionRef.current !== token) return` de `abrirEdicion`, el que sigue a la
   * espera de la familia del miembro. Evidencia de mutación (mutation-proof-tests): sacarlo hace fallar este test (cuando
   * llega la familia del 31 el formulario del 31 se muestra encima del 5); revertido, vuelve a verde. */
  it('el miembro que espera su familia no pisa al artículo que se abrió después', async () => {
    const familiaDelMiembro = diferida<FamiliaDetalle>()
    escenarioDelMiembro({
      articulos: { 31: miembro(), 5: articuloFixture({ id: 5, codigoInterno: 'A0005', nombre: 'Otro' }) },
      grilla: {
        items: [filaGrilla({ id: 5, codigoInterno: 'A0005', nombre: 'Otro' })],
        total: 1,
        pagina: 1,
        tamanio: 25,
        nombreListaPrecio: 'General',
      },
      detalleDeFamiliaImpl: () => familiaDelMiembro.promesa,
    })
    renderArticulos('/articulos/edit/31')
    await waitFor(() => expect(unaFamiliaPidioSuDetalle()).toHaveLength(1))
    const filaOtro = (await screen.findByText('Otro')).closest('tr')
    if (!filaOtro) throw new Error('No se encontró la fila del artículo 5')

    await userEvent.click(within(filaOtro).getByRole('link', { name: 'Editar' }))
    await screen.findByText('Editando artículo A0005')
    await act(async () => {
      familiaDelMiembro.resolver(detalleSabores())
    })

    expect(screen.getByRole('dialog', { name: 'Editando artículo A0005' })).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Otro')
    expect(screen.queryByText('Editando artículo A0031')).not.toBeInTheDocument()
    expect(screen.queryByText(/^Familia "/)).not.toBeInTheDocument()
  })
})

describe('Articulos — la URL cambia a un id inválido con la pregunta de alcance abierta', () => {
  /** Cláusula bajo prueba: `setDecisionDeAlcance(null)` de la rama de id inválido del efecto de apertura. Evidencia de
   * mutación (mutation-proof-tests): sacarla hace fallar este test (la pregunta sigue abierta sobre un formulario que ya
   * no existe); revertido, vuelve a verde. */
  it('cierra la pregunta: ya no hay artículo sobre el que preguntar', async () => {
    escenarioDelMiembro()
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderArticulosConHistorial(['/articulos/edit/31', '/articulos/edit/abc'], 0)
    await screen.findByText('Editando artículo A0031')
    await cambiarCostoDeLista('175')
    await guardar()
    await screen.findByRole('dialog', { name: 'Cambio en una familia' })

    act(() => navegar(1))

    expect(await screen.findByText('No se especificó un artículo válido.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Cambio en una familia' })).not.toBeInTheDocument()
  })
})

describe('Articulos — después de recargar el artículo por un rechazo de familia, el foco queda en "Nombre"', () => {
  /** Cláusula bajo prueba: el efecto de montaje de `FormularioArticulo` que enfoca "Nombre" cuando el foco está en `<body>`.
   * Evidencia de mutación (mutation-proof-tests): sacar ese efecto hace fallar las cinco pruebas (el foco queda en
   * `<body>`); revertido, vuelven a verde. Advertencia (react-async-state regla 12): jsdom no implementa el "focus fixup"
   * del navegador, pero acá no hace falta: el foco se pierde porque se desmonta el elemento que lo tenía.
   *
   * La relectura del artículo queda en manos del test: mientras espera, la pantalla muestra "Cargando artículo…" y el
   * formulario anterior ya se desmontó. Con una relectura inmediata el formulario no llega a desmontarse y el foco queda
   * donde estaba. Devuelve lo que hay que esperar una vez disparada la recarga: que se vea el estado de carga, que llegue
   * la relectura y que el foco quede en "Nombre". */
  function relecturaPendiente(primeraLectura: ArticuloListado) {
    const relectura = diferida<ArticuloListado>()
    escenarioDelMiembro({ articuloImpl: (_id, llamada) => (llamada === 1 ? primeraLectura : relectura.promesa) })
    return async (releida: ArticuloListado) => {
      await screen.findByText('Cargando artículo…')
      await act(async () => {
        relectura.resolver(releida)
      })
      await waitFor(() => expect(screen.getByLabelText('Nombre')).toHaveFocus())
    }
  }

  it('409 familia_cambio al elegir un alcance', async () => {
    const esperarLaRecarga = relecturaPendiente(miembro())
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    await abrirEdicion()
    await cambiarCostoDeLista('175')
    await guardar()

    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Cambio en una familia' })).getByRole('button', { name: 'Toda la familia' }))

    await esperarLaRecarga(miembro())
  })

  it('409 alcance_requerido sin ningún campo compartido cambiado a la vista', async () => {
    const esperarLaRecarga = relecturaPendiente(miembro({ idFamilia: null }))
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'alcance_requerido', 'El artículo pertenece a la familia "Sabores".'))
    await abrirEdicion()
    await userEvent.type(screen.getByLabelText('Nombre'), ' 2')
    await guardar()

    await esperarLaRecarga(miembro({ costoLista: 175 }))
  })

  it.each<[string, ErrorApi]>([
    ['409 familia_cambio', new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.')],
    ['404', new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.')],
  ])('%s al sacarlo de la familia', async (_caso, rechazo) => {
    const esperarLaRecarga = relecturaPendiente(miembro())
    apiDeleteMock.mockRejectedValueOnce(rechazo)
    await abrirEdicion()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))

    await esperarLaRecarga(miembro())
  })

  it('409 familia_cambio del editor de precios', async () => {
    const esperarLaRecarga = relecturaPendiente(miembro())
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    await abrirEdicion()
    await userEvent.click((await screen.findAllByRole('button', { name: 'Gestionar' }))[0])
    await userEvent.type(await screen.findByLabelText('Precio'), '1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))
    await userEvent.click(screen.getByRole('button', { name: 'Toda la familia' }))

    await esperarLaRecarga(miembro())
  })
})

// ---- crear una familia desde el formulario de artículo ("+") ------------------------------------------------

/** Responde cada POST por ruta; una ruta sin respuesta falla fuerte. */
function mockearPosts(respuestas: Record<string, (cuerpo: Record<string, unknown>) => Promise<unknown>>) {
  apiPostMock.mockImplementation((ruta: string, cuerpo: Record<string, unknown>) => {
    const respuesta = respuestas[ruta]
    return respuesta ? respuesta(cuerpo) : Promise.reject(new Error(`POST no mockeado en el test: ${ruta}`))
  })
}

function pedidosA(ruta: string) {
  return apiPostMock.mock.calls.filter((c) => c[0] === ruta) as [string, Record<string, unknown>][]
}

function lecturasDeFamilias() {
  return apiGetMock.mock.calls.filter((c) => c[0] === '/familias').length
}

/** "+" del selector, el nombre y "Aceptar". */
async function anotarFamiliaNueva(nombre: string) {
  await userEvent.click(screen.getByRole('button', { name: 'Nueva familia' }))
  const dialogo = await screen.findByRole('dialog', { name: 'Nueva familia' })
  await userEvent.type(within(dialogo).getByLabelText('Nombre'), nombre)
  await userEvent.click(within(dialogo).getByRole('button', { name: 'Aceptar' }))
}

function dialogoDeFamiliaNueva() {
  return screen.getByRole('dialog', { name: 'Nueva familia' })
}

describe('Articulos — alta: "+" para crear una familia nueva', () => {
  const creado = articuloFixture({ id: 40, codigoInterno: 'A0040', nombre: 'Camisa', idFamilia: null })

  beforeEach(() => {
    mockearApi({ familias: [familiaListado()], detalles: { 7: detalleSabores() }, articulos: { 40: creado } })
    apiPostMock.mockResolvedValue(creado)
  })

  async function guardarConNombre(nombre = 'Camisa') {
    await userEvent.type(screen.getByLabelText('Nombre'), nombre)
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalled())
  }

  it('no llama a la API: anota el nombre, lo muestra como familia nueva y deja editables los campos compartidos', async () => {
    await abrirAlta()
    await screen.findByRole('option', { name: 'Sabores (3 artículos)' })

    await anotarFamiliaNueva('Talles')

    expect(screen.queryByRole('dialog', { name: 'Nueva familia' })).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(screen.getByText('Familia nueva: Talles')).toBeInTheDocument()
    expect(screen.getByLabelText('Familia (opcional)')).toHaveDisplayValue('Nueva: Talles')
    for (const etiqueta of CAMPOS_COMPARTIDOS_DEL_FORMULARIO) {
      expect(screen.getByLabelText(etiqueta), String(etiqueta)).toBeEnabled()
    }
  })

  it('guardar manda nombreFamiliaNueva con idFamilia: null, y el artículo pasa a ser miembro de la familia creada', async () => {
    const enLaFamilia = articuloFixture({ id: 40, codigoInterno: 'A0040', nombre: 'Camisa', idFamilia: 50 })
    mockearApi({
      familiasImpl: () =>
        Promise.resolve(lecturasDeFamilias() <= 1 ? [] : [familiaListado({ id: 50, nombre: 'Talles', cantidadArticulos: 1 })]),
      detalles: { 50: detalleSabores({ id: 50, nombre: 'Talles', articulos: miembros(1) }) },
      articulos: { 40: enLaFamilia },
    })
    apiPostMock.mockResolvedValue(enLaFamilia)
    await abrirAlta()
    await waitFor(() => expect(lecturasDeFamilias()).toBe(1))
    await anotarFamiliaNueva('Talles')

    await guardarConNombre()

    expect(pedidosA('/articulos')[0][1]).toMatchObject({ nombre: 'Camisa', idFamilia: null, nombreFamiliaNueva: 'Talles' })
    expect(
      await screen.findByText('Artículo "Camisa" creado con código interno A0040. Creó la familia "Talles" y es su artículo de referencia.'),
    ).toBeInTheDocument()
    expect(await screen.findByText('Editando artículo A0040')).toBeInTheDocument()
    expect(await screen.findByText('Familia "Talles" (1 artículo)')).toBeInTheDocument()
    expect(screen.queryByText(/^Familia nueva:/)).not.toBeInTheDocument()
    await waitFor(() => expect(lecturasDeFamilias()).toBe(2))
    await waitFor(() => expect(llamadasAGrilla()).toBe(2))
  })

  it('con una familia nueva los costos quedan editables y viajan con nombreFamiliaNueva, sin costoNominal ni idFamilia', async () => {
    await abrirAlta()
    await anotarFamiliaNueva('Talles')
    await userEvent.type(screen.getByLabelText(/^Costo de lista sin IVA/), '100')
    await userEvent.selectOptions(screen.getByLabelText('Costo por'), 'Bulto')
    await userEvent.type(screen.getByLabelText(/^Unidades por bulto/), '6')

    await guardarConNombre()

    const cuerpo = pedidosA('/articulos')[0][1]
    expect(cuerpo).toMatchObject({
      idFamilia: null,
      nombreFamiliaNueva: 'Talles',
      costoLista: 100,
      costoListaPor: 'Bulto',
      costoListaIncluyeIva: false,
      unidadesPorBulto: 6,
    })
    expect(cuerpo).not.toHaveProperty('costoNominal')
  })

  /** Cláusula bajo prueba: `nombreFamiliaNueva: ''` de `elegirFamilia` al elegir una familia existente.
   * Evidencia de mutación (mutation-proof-tests): quitar la cláusula hace fallar este test; revertido, vuelve a verde. */
  it('elegir una familia existente después del "+" descarta el nombre: manda idFamilia y no manda nombreFamiliaNueva', async () => {
    await abrirAlta()
    await anotarFamiliaNueva('Talles')

    await elegirFamiliaEnElSelector('7')
    await waitFor(() => expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('150,00'))
    expect(screen.queryByText(/^Familia nueva:/)).not.toBeInTheDocument()
    await guardarConNombre()

    const cuerpo = pedidosA('/articulos')[0][1]
    expect(cuerpo.idFamilia).toBe(7)
    expect('nombreFamiliaNueva' in cuerpo).toBe(false)
  })

  /** Cláusula bajo prueba: `idFamilia: ''` de `crearFamilia` en un alta: el "+" suelta la familia ya elegida.
   * Evidencia de mutación (mutation-proof-tests): quitar la cláusula hace fallar este test; revertido, vuelve a verde. */
  it('el "+" después de elegir una familia existente la suelta: desbloquea los campos y manda solo la nueva', async () => {
    await abrirAltaEnLaFamilia()

    await anotarFamiliaNueva('Talles')

    for (const etiqueta of CAMPOS_COMPARTIDOS_DEL_FORMULARIO) {
      expect(screen.getByLabelText(etiqueta), String(etiqueta)).toBeEnabled()
    }
    expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('150,00')
    await guardarConNombre()
    expect(pedidosA('/articulos')[0][1]).toMatchObject({ idFamilia: null, nombreFamiliaNueva: 'Talles' })
  })

  it('"Quitar" deshace la familia nueva: vuelve a "Sin familia" y no manda el nombre', async () => {
    await abrirAlta()
    await anotarFamiliaNueva('Talles')

    await userEvent.click(screen.getByRole('button', { name: 'Quitar' }))

    expect(screen.queryByText(/^Familia nueva:/)).not.toBeInTheDocument()
    expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('')
    await guardarConNombre()
    expect('nombreFamiliaNueva' in pedidosA('/articulos')[0][1]).toBe(false)
  })

  it('"Cambiar nombre" reabre el modal con el nombre anotado y lo reemplaza', async () => {
    await abrirAlta()
    await anotarFamiliaNueva('Talles')

    await userEvent.click(screen.getByRole('button', { name: 'Cambiar nombre' }))
    const campo = within(dialogoDeFamiliaNueva()).getByLabelText('Nombre')
    expect(campo).toHaveValue('Talles')
    await userEvent.clear(campo)
    await userEvent.type(campo, 'Colores')
    await userEvent.click(within(dialogoDeFamiliaNueva()).getByRole('button', { name: 'Aceptar' }))

    expect(screen.getByText('Familia nueva: Colores')).toBeInTheDocument()
    await guardarConNombre()
    expect(pedidosA('/articulos')[0][1].nombreFamiliaNueva).toBe('Colores')
  })

  it('"Cancelar" en el modal no anota nada', async () => {
    await abrirAlta()
    await userEvent.click(screen.getByRole('button', { name: 'Nueva familia' }))

    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Nueva familia' })).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog', { name: 'Nueva familia' })).not.toBeInTheDocument()
    expect(screen.queryByText(/^Familia nueva:/)).not.toBeInTheDocument()
  })

  it('el submit del modal no llega al formulario del artículo: no se guarda el alta', async () => {
    await abrirAlta()

    await anotarFamiliaNueva('Talles')

    expect(apiPostMock).not.toHaveBeenCalled()
  })

  describe('validación del nombre en el modal', () => {
    async function abrirModalConFamiliasCargadas() {
      mockearApi({
        familias: [
          familiaListado(),
          familiaListado({ id: 8, nombre: 'Apagada', activo: false }),
          familiaListado({ id: 9, nombre: 'Vacía', cantidadArticulos: 0 }),
        ],
      })
      await abrirAlta()
      await screen.findByRole('option', { name: 'Sabores (3 artículos)' })
      await userEvent.click(screen.getByRole('button', { name: 'Nueva familia' }))
      return await screen.findByRole('dialog', { name: 'Nueva familia' })
    }

    async function probar(dialogo: HTMLElement, texto: string) {
      const campo = within(dialogo).getByLabelText('Nombre')
      await userEvent.clear(campo)
      if (texto !== '') await userEvent.type(campo, texto)
      await userEvent.click(within(dialogo).getByRole('button', { name: 'Aceptar' }))
    }

    it('el nombre en blanco y el de más de 150 caracteres se rechazan sin anotar nada', async () => {
      const dialogo = await abrirModalConFamiliasCargadas()

      await probar(dialogo, '   ')
      expect(within(dialogo).getByRole('alert')).toHaveTextContent('Escribí el nombre de la familia.')

      const campo = within(dialogo).getByLabelText('Nombre')
      await userEvent.clear(campo)
      await userEvent.click(campo)
      await userEvent.paste('a'.repeat(151))
      await userEvent.click(within(dialogo).getByRole('button', { name: 'Aceptar' }))
      expect(within(dialogo).getByRole('alert')).toHaveTextContent('El nombre de la familia no puede superar los 150 caracteres.')
      expect(screen.queryByText(/^Familia nueva:/)).not.toBeInTheDocument()
    })

    it('el nombre de una familia que se ofrece se rechaza sin distinguir mayúsculas ni espacios', async () => {
      const dialogo = await abrirModalConFamiliasCargadas()

      await probar(dialogo, '  sabores ')

      expect(within(dialogo).getByRole('alert')).toHaveTextContent('Ya existe una familia con ese nombre')
      expect(screen.queryByText(/^Familia nueva:/)).not.toBeInTheDocument()
    })

    /** Cláusula bajo prueba: la validación usa la lista completa de `GET /api/familias` y no solo las familias que se
     * ofrecen. Una inactiva y una vacía no están en el selector, pero su nombre está en uso.
   * Evidencia de mutación (mutation-proof-tests): quitar la cláusula hace fallar este test; revertido, vuelve a verde. */
    it.each(['Apagada', 'vacía'])('el nombre de una familia que NO se ofrece (%s: inactiva o vacía) también se rechaza, y lo explica', async (nombre) => {
      const dialogo = await abrirModalConFamiliasCargadas()
      expect(within(screen.getByLabelText('Familia (opcional)')).queryByRole('option', { name: /Apagada|Vacía/ })).not.toBeInTheDocument()

      await probar(dialogo, nombre)

      const alerta = within(dialogo).getByRole('alert')
      expect(alerta).toHaveTextContent('Puede estar inactiva o no tener artículos')
      expect(alerta).toHaveTextContent('pantalla de Familias')
      expect(apiPostMock).not.toHaveBeenCalled()
    })

    it('corregido el nombre, se acepta y el modal se cierra', async () => {
      const dialogo = await abrirModalConFamiliasCargadas()
      await probar(dialogo, 'Sabores')

      await probar(dialogo, 'Talles')

      expect(screen.queryByRole('dialog', { name: 'Nueva familia' })).not.toBeInTheDocument()
      expect(screen.getByText('Familia nueva: Talles')).toBeInTheDocument()
    })
  })

  describe('lo que el servidor rechaza de la familia nueva', () => {
    it('409 familia_nombre_duplicado: el artículo no se crea, el formulario conserva todo y se puede corregir el nombre y reintentar', async () => {
      apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_nombre_duplicado', 'Ya existe la familia "Talles".'))
      await abrirAlta()
      await anotarFamiliaNueva('Talles')
      await userEvent.type(screen.getByLabelText(/^Costo de lista con IVA/), '77')

      await guardarConNombre()

      expect(await screen.findByRole('alert')).toHaveTextContent('Ya existe una familia con ese nombre')
      expect(screen.getByRole('alert')).toHaveTextContent('El artículo no se creó.')
      expect(screen.getByText('Nuevo artículo')).toBeInTheDocument()
      expect(screen.getByLabelText('Nombre')).toHaveValue('Camisa')
      expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('77,00')
      expect(screen.getByText('Familia nueva: Talles')).toBeInTheDocument()
      await waitFor(() => expect(lecturasDeFamilias()).toBe(2))

      await userEvent.click(screen.getByRole('button', { name: 'Cambiar nombre' }))
      const campo = within(dialogoDeFamiliaNueva()).getByLabelText('Nombre')
      await userEvent.clear(campo)
      await userEvent.type(campo, 'Talles 2')
      await userEvent.click(within(dialogoDeFamiliaNueva()).getByRole('button', { name: 'Aceptar' }))
      await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

      await waitFor(() => expect(pedidosA('/articulos')).toHaveLength(2))
      expect(pedidosA('/articulos')[1][1]).toMatchObject({ nombre: 'Camisa', nombreFamiliaNueva: 'Talles 2' })
    })

    /** Cláusula bajo prueba: `setErrorDeFamiliaNueva('')` de `guardar` (react-async-state regla 16).
   * Evidencia de mutación (mutation-proof-tests): quitar la cláusula hace fallar este test; revertido, vuelve a verde. */
    it('reintentar el guardado saca de pantalla el rechazo anterior mientras se reenvía', async () => {
      apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_nombre_duplicado', 'Ya existe.'))
      await abrirAlta()
      await anotarFamiliaNueva('Talles')
      await guardarConNombre()
      await screen.findByText(/Ya existe una familia con ese nombre/)
      apiPostMock.mockImplementation(() => new Promise(() => {}))

      await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

      await waitFor(() => expect(pedidosA('/articulos')).toHaveLength(2))
      expect(screen.queryByText(/Ya existe una familia con ese nombre/)).not.toBeInTheDocument()
    })

    it('400 nombre_requerido con el nombre del artículo completo se atribuye a la familia', async () => {
      apiPostMock.mockRejectedValueOnce(new ErrorApi(400, 'nombre_requerido', 'El nombre es obligatorio.'))
      await abrirAlta()
      await anotarFamiliaNueva('Talles')

      await guardarConNombre()

      expect(await screen.findByRole('alert')).toHaveTextContent('Escribí el nombre de la familia. El artículo no se creó.')
    })

    it('400 nombre_muy_largo con el nombre del artículo válido se atribuye a la familia', async () => {
      apiPostMock.mockRejectedValueOnce(new ErrorApi(400, 'nombre_muy_largo', 'El nombre es muy largo.'))
      await abrirAlta()
      await anotarFamiliaNueva('Talles')

      await guardarConNombre()

      expect(await screen.findByRole('alert')).toHaveTextContent('El nombre de la familia no puede superar los 150 caracteres.')
    })

    it('otro rechazo del alta se muestra como siempre, en el banner del formulario', async () => {
      apiPostMock.mockRejectedValueOnce(new ErrorApi(400, 'referencia_invalida', 'Una referencia ya no existe.'))
      await abrirAlta()
      await anotarFamiliaNueva('Talles')

      await guardarConNombre()

      expect(await screen.findByText('Una referencia ya no existe.')).toBeInTheDocument()
      expect(screen.getByText('Familia nueva: Talles')).toBeInTheDocument()
    })
  })
})

describe('Articulos — edición: "+" crea una familia con el artículo como referencia', () => {
  const resultadoDeCrear = { idFamilia: 50, nombre: 'Talles', idArticuloReferencia: 1, articulos: [] }
  const unicoMiembro = [{ id: 1, codigoInterno: 'A0001', nombre: 'Articulo Uno', idMarca: null, activo: true }]

  function escenarioSinFamilia(extra: Escenario = {}) {
    mockearApi({
      articulos: { 1: articuloFixture() },
      familias: [],
      detalles: { 50: detalleSabores({ id: 50, nombre: 'Talles', articulos: unicoMiembro }) },
      ...extra,
    })
  }

  it('un artículo sin familia ve el selector con "Sin familia" y el "+"', async () => {
    escenarioSinFamilia()

    await abrirEdicion(1, 'A0001')

    expect(within(await screen.findByLabelText('Familia (opcional)')).getAllByRole('option').map((o) => o.textContent)).toEqual(['Sin familia'])
    expect(screen.getByRole('button', { name: 'Nueva familia' })).toBeEnabled()
  })

  it('crea la familia al instante con idsArticulos vacío y pasa a mostrar la pertenencia sin cerrar el modal', async () => {
    escenarioSinFamilia()
    mockearPosts({ '/familias': () => Promise.resolve(resultadoDeCrear) })
    await abrirEdicion(1, 'A0001')
    await waitFor(() => expect(lecturasDeFamilias()).toBe(1))

    await anotarFamiliaNueva('Talles')

    expect(await screen.findByText('Familia "Talles" (1 artículo)')).toBeInTheDocument()
    expect(pedidosA('/familias')).toEqual([['/familias', { nombre: 'Talles', idArticuloReferencia: 1, idsArticulos: [] }]])
    expect(screen.getByRole('dialog', { name: 'Editando artículo A0001' })).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Nueva familia' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sacar de la familia' })).toBeEnabled()
    expect(screen.queryByLabelText('Familia (opcional)')).not.toBeInTheDocument()
    expect(
      screen.getByText('Se creó la familia "Talles": este artículo es su referencia y conserva sus valores y sus precios.'),
    ).toBeInTheDocument()
    await waitFor(() => expect(lecturasDeFamilias()).toBe(2))
    await waitFor(() => expect(llamadasAGrilla()).toBe(2))
  })

  it('no vuelve a leer el artículo ni lo escribe', async () => {
    escenarioSinFamilia()
    mockearPosts({ '/familias': () => Promise.resolve(resultadoDeCrear) })
    await abrirEdicion(1, 'A0001')

    await anotarFamiliaNueva('Talles')
    await screen.findByText('Familia "Talles" (1 artículo)')

    expect(lecturasDelArticulo(1)).toBe(1)
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('creada la familia el formulario no queda con cambios sin guardar: cerrarlo no pregunta', async () => {
    escenarioSinFamilia()
    mockearPosts({ '/familias': () => Promise.resolve(resultadoDeCrear) })
    const confirmar = vi.spyOn(window, 'confirm')
    await abrirEdicion(1, 'A0001')
    await anotarFamiliaNueva('Talles')
    await screen.findByText('Familia "Talles" (1 artículo)')

    await userEvent.click(within(screen.getByRole('dialog', { name: /^Editando artículo/ })).getByRole('button', { name: 'Cancelar' }))

    expect(confirmar).not.toHaveBeenCalled()
  })

  /** Cláusula bajo prueba: el ref de reentrancia de `AltaRapidaFamilia` (react-async-state regla 11).
   * Evidencia de mutación (mutation-proof-tests): quitar la cláusula hace fallar este test; revertido, vuelve a verde. */
  it('dos clics sincrónicos en "Aceptar" emiten un solo POST y el modal sigue inerte, sin ningún aviso', async () => {
    escenarioSinFamilia()
    mockearPosts({ '/familias': () => new Promise(() => {}) })
    await abrirEdicion(1, 'A0001')
    await userEvent.click(screen.getByRole('button', { name: 'Nueva familia' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Nueva familia' })
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), 'Talles')

    const aceptar = within(dialogo).getByRole('button', { name: 'Aceptar' })
    await act(async () => {
      aceptar.click()
      aceptar.click()
      await Promise.resolve()
    })

    expect(pedidosA('/familias')).toHaveLength(1)
    expect(within(dialogo).queryByRole('alert')).not.toBeInTheDocument()
    expect(within(dialogo).getByRole('button', { name: 'Aceptando…' })).toBeDisabled()
  })

  it('con la escritura en vuelo el modal de la familia queda inerte', async () => {
    escenarioSinFamilia()
    mockearPosts({ '/familias': () => new Promise(() => {}) })
    await abrirEdicion(1, 'A0001')
    await anotarFamiliaNueva('Talles')

    const dialogo = dialogoDeFamiliaNueva()
    expect(within(dialogo).getByLabelText('Nombre')).toBeDisabled()
    expect(within(dialogo).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(within(dialogo).getByRole('button', { name: 'Aceptando…' })).toBeDisabled()
  })

  it('el rechazo del servidor se muestra en el modal con su ayuda, y el artículo sigue sin familia', async () => {
    escenarioSinFamilia()
    mockearPosts({
      '/familias': () => Promise.reject(new ErrorApi(409, 'familia_nombre_duplicado', 'Ya existe la familia "Talles".')),
    })
    await abrirEdicion(1, 'A0001')
    await waitFor(() => expect(lecturasDeFamilias()).toBe(1))

    await anotarFamiliaNueva('Talles')

    const dialogo = dialogoDeFamiliaNueva()
    expect(await within(dialogo).findByRole('alert')).toHaveTextContent('Ya existe la familia "Talles". Elegí otro nombre.')
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Talles')
    expect(within(dialogo).getByLabelText('Nombre')).toBeEnabled()
    expect(screen.queryByRole('button', { name: 'Sacar de la familia' })).not.toBeInTheDocument()
    await waitFor(() => expect(lecturasDeFamilias()).toBe(2))
  })

  it('409 articulo_en_otra_familia: recarga el artículo con el aviso de que la pertenencia cambió', async () => {
    escenarioSinFamilia({
      articuloImpl: (_id, llamada) => articuloFixture({ idFamilia: llamada === 1 ? null : 7 }),
      detalles: { 7: detalleSabores() },
    })
    mockearPosts({ '/familias': () => Promise.reject(new ErrorApi(409, 'articulo_en_otra_familia', 'El artículo ya es de una familia.')) })
    await abrirEdicion(1, 'A0001')

    await anotarFamiliaNueva('Talles')

    expect(
      await screen.findByText(
        'La pertenencia del artículo a su familia cambió desde que se cargó la pantalla. Se recargó el artículo: revisá los datos y volvé a intentar.',
      ),
    ).toBeInTheDocument()
    expect(await screen.findByText('Familia "Sabores" (3 artículos)')).toBeInTheDocument()
  })

  describe('con cambios sin guardar', () => {
    it('el selector y el "+" quedan deshabilitados con el aviso de guardar primero, y vuelven al guardar', async () => {
      escenarioSinFamilia({ familias: [familiaListado()] })
      apiPutMock.mockResolvedValue(articuloFixture({ nombre: 'Articulo UnoX' }))
      await abrirEdicion(1, 'A0001')
      await screen.findByRole('option', { name: 'Sabores (3 artículos)' })
      expect(screen.getByLabelText('Familia (opcional)')).toBeEnabled()

      await userEvent.type(screen.getByLabelText('Nombre'), 'X')

      expect(screen.getByLabelText('Familia (opcional)')).toBeDisabled()
      expect(screen.getByRole('button', { name: 'Nueva familia' })).toBeDisabled()
      expect(
        screen.getByText('Guardá los cambios del artículo antes de elegir o crear una familia: se usan los valores guardados.'),
      ).toBeInTheDocument()

      await guardar()
      await screen.findByText('Artículo "Articulo UnoX" actualizado.')

      expect(screen.getByLabelText('Familia (opcional)')).toBeEnabled()
      expect(screen.getByRole('button', { name: 'Nueva familia' })).toBeEnabled()
      expect(
        screen.queryByText('Guardá los cambios del artículo antes de elegir o crear una familia: se usan los valores guardados.'),
      ).not.toBeInTheDocument()
    })

    it.each([
      [
        'el costo de lista',
        async () => {
          await userEvent.clear(screen.getByLabelText(/^Costo de lista con IVA/))
          await userEvent.type(screen.getByLabelText(/^Costo de lista con IVA/), '55')
        },
      ],
      ['la base del costo', () => userEvent.selectOptions(screen.getByLabelText('Costo por'), 'Bulto')],
    ])('cambiar %s también deshabilita el selector y el "+": el servidor lee el costo guardado', async (_cambio, cambiar) => {
      escenarioSinFamilia({ familias: [familiaListado()] })
      await abrirEdicion(1, 'A0001')
      await screen.findByRole('option', { name: 'Sabores (3 artículos)' })
      expect(screen.getByLabelText('Familia (opcional)')).toBeEnabled()

      await cambiar()

      expect(screen.getByLabelText('Familia (opcional)')).toBeDisabled()
      expect(screen.getByRole('button', { name: 'Nueva familia' })).toBeDisabled()
    })

    it('en un alta el selector y el "+" no se deshabilitan por los cambios: el servidor no lee nada guardado', async () => {
      mockearApi({ familias: [familiaListado()] })
      await abrirAlta()
      await screen.findByRole('option', { name: 'Sabores (3 artículos)' })

      await userEvent.type(screen.getByLabelText('Nombre'), 'Camisa')

      expect(screen.getByLabelText('Familia (opcional)')).toBeEnabled()
      expect(screen.getByRole('button', { name: 'Nueva familia' })).toBeEnabled()
    })
  })
})

// ---- unir un artículo existente a una familia existente ------------------------------------------------------

describe('Articulos — edición: unir un artículo a una familia que ya existe', () => {
  /** Los quince valores del artículo sin familia de las fixtures (`articuloFixture`). */
  const valoresDelArticulo: ValoresCompartidosDeLaFamilia = {
    idArea: 1,
    idCategoria: null,
    idGrupo: null,
    idProveedorHabitual: null,
    idAlicuotaIva: 5,
    unidadVenta: 'Unidad',
    unidadesPorBulto: null,
    esProducto: true,
    controlaLote: false,
    acumulaEnVenta: true,
    costoLista: 100,
    costoListaPor: 'Unidad',
    costoListaIncluyeIva: true,
    descuentoProveedor: null,
    costoNominal: null,
  }

  function fotoDeUnion(sobrescribir: Partial<PrevisualizacionDeAgrupacion> = {}): PrevisualizacionDeAgrupacion {
    return {
      idArticuloReferencia: 31,
      idFamilia: 7,
      problemas: [],
      articulos: [
        {
          idArticulo: 1,
          campos: ['id_categoria', 'unidad_venta', 'costo_lista'],
          actual: valoresDelArticulo,
          nuevo: referencia,
          precios: [{ idListaPrecio: 2, actual: { vigente: 900, pendiente: null }, nuevo: { vigente: 1200, pendiente: null } }],
        },
      ],
      ...sobrescribir,
    }
  }

  const resultadoDeUnir = { idFamilia: 7, nombre: 'Sabores', idArticuloReferencia: 31, articulos: [] }
  const articuloUnido = articuloFixture({ idFamilia: 7, ...compartidosDelArticulo })

  /** El artículo 1 sin familia y la familia "Sabores" (referencia: el artículo 31). El POST de unir sale a `unir`. */
  function escenarioDeUnion(extra: Escenario = {}, unir: () => Promise<unknown> = () => Promise.resolve(resultadoDeUnir)) {
    mockearApi({
      articulos: { 1: articuloFixture() },
      familias: [familiaListado()],
      detalles: { 7: detalleSabores() },
      ...extra,
    })
    mockearPosts({
      '/familias/previsualizacion': () => Promise.resolve(fotoDeUnion()),
      '/familias/7/articulos': unir,
    })
  }

  function propuesta() {
    return screen.getByRole('group', { name: 'Unir el artículo a la familia' })
  }

  async function proponerLaFamilia(valor = '7') {
    await abrirEdicion(1, 'A0001')
    await elegirFamiliaEnElSelector(valor)
    return await screen.findByRole('group', { name: 'Unir el artículo a la familia' })
  }

  async function confirmarLaUnion() {
    await userEvent.click(within(propuesta()).getByRole('button', { name: 'Confirmar y unir a la familia' }))
  }

  it('elegir la familia no escribe: lee su detalle, previsualiza con la referencia de la familia y muestra qué cambia', async () => {
    escenarioDeUnion()

    const grupo = await proponerLaFamilia()

    expect(unaFamiliaPidioSuDetalle()).toEqual([['/familias/7']])
    expect(pedidosA('/familias/previsualizacion')).toEqual([
      ['/familias/previsualizacion', { idArticuloReferencia: 31, idsArticulos: [1] }],
    ])
    expect(pedidosA('/familias/7/articulos')).toHaveLength(0)
    expect(apiPutMock).not.toHaveBeenCalled()
    expect(within(grupo).getByRole('row', { name: /Categoría.*Sin asignar.*Bebidas/ })).toBeInTheDocument()
    expect(within(grupo).getByRole('row', { name: /General/ })).toBeInTheDocument()
    expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('7')
  })

  it('dice con claridad que confirmar reemplaza los valores y los precios del artículo con los de la familia', async () => {
    escenarioDeUnion()

    const grupo = await proponerLaFamilia()

    expect(grupo).toHaveTextContent('Al confirmar, el artículo toma de la familia "Sabores" sus quince campos compartidos')
    expect(grupo).toHaveTextContent('sus valores y precios actuales se reemplazan')
  })

  it('confirmar suma al artículo con agregarArticulos, lo vuelve a leer y muestra su pertenencia con los valores alineados', async () => {
    escenarioDeUnion({ articuloImpl: (_id, llamada) => (llamada === 1 ? articuloFixture() : articuloUnido) })
    await proponerLaFamilia()

    await confirmarLaUnion()

    await waitFor(() => expect(pedidosA('/familias/7/articulos')).toEqual([['/familias/7/articulos', { idsArticulos: [1] }]]))
    expect(await screen.findByText('Familia "Sabores" (3 artículos)')).toBeInTheDocument()
    expect(
      await screen.findByText('El artículo "Articulo Uno" se unió a la familia "Sabores": tomó sus valores compartidos y sus precios.'),
    ).toBeInTheDocument()
    await waitFor(() => expect(screen.getByLabelText(/^Costo de lista con IVA/)).toHaveValue('150,00'))
    expect(lecturasDelArticulo(1)).toBe(2)
    expect(screen.queryByLabelText('Familia (opcional)')).not.toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Unir el artículo a la familia' })).not.toBeInTheDocument()
    await waitFor(() => expect(llamadasAGrilla()).toBe(2))
    await waitFor(() => expect(lecturasDeFamilias()).toBe(2))
  })

  it('si la previsualización trae problemas los muestra y no deja confirmar', async () => {
    escenarioDeUnion()
    mockearPosts({
      '/familias/previsualizacion': () =>
        Promise.resolve(
          fotoDeUnion({
            articulos: [],
            problemas: [{ codigo: 'familia_precio_inalineable', mensaje: 'La lista General no se puede alinear.', idArticulo: 1, idListaPrecio: 2 }],
          }),
        ),
    })

    const grupo = await proponerLaFamilia()

    expect(grupo).toHaveTextContent('No se puede agrupar así.')
    expect(grupo).toHaveTextContent('La lista General no se puede alinear.')
    expect(within(grupo).getByRole('button', { name: 'Confirmar y unir a la familia' })).toBeDisabled()
    expect(pedidosA('/familias/7/articulos')).toHaveLength(0)
  })

  it('"Cancelar" no escribe y devuelve el selector a "Sin familia"', async () => {
    escenarioDeUnion()
    const grupo = await proponerLaFamilia()

    await userEvent.click(within(grupo).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('group', { name: 'Unir el artículo a la familia' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('')
    expect(pedidosA('/familias/7/articulos')).toHaveLength(0)
  })

  it('volver a "Sin familia" desde el selector también cancela', async () => {
    escenarioDeUnion()
    await proponerLaFamilia()

    await userEvent.selectOptions(screen.getByLabelText('Familia (opcional)'), '')

    expect(screen.queryByRole('group', { name: 'Unir el artículo a la familia' })).not.toBeInTheDocument()
    expect(pedidosA('/familias/7/articulos')).toHaveLength(0)
  })

  /** Cláusula bajo prueba: la generación de `usePrevisualizacion` (react-async-state regla 2): la foto de la familia que se
   * dejó de elegir llega tarde y no pisa a la de la elegida después.
   * Evidencia de mutación (mutation-proof-tests): quitar la cláusula hace fallar este test; revertido, vuelve a verde. */
  it('la previsualización tardía de una familia que se dejó de elegir no pisa a la de la elegida después', async () => {
    const deLaPrimera = diferida<PrevisualizacionDeAgrupacion>()
    const deLaSegunda = diferida<PrevisualizacionDeAgrupacion>()
    const miembroDeTalles = { id: 51, codigoInterno: 'A0051', nombre: 'Talle S', idMarca: null, activo: true }
    escenarioDeUnion({
      familias: [familiaListado(), familiaListado({ id: 8, nombre: 'Talles' })],
      detalles: { 7: detalleSabores(), 8: detalleSabores({ id: 8, nombre: 'Talles', articulos: [miembroDeTalles] }) },
    })
    mockearPosts({
      '/familias/previsualizacion': (cuerpo) => (cuerpo.idArticuloReferencia === 31 ? deLaPrimera.promesa : deLaSegunda.promesa),
    })
    await abrirEdicion(1, 'A0001')
    await elegirFamiliaEnElSelector('7')
    await waitFor(() => expect(pedidosA('/familias/previsualizacion')).toHaveLength(1))
    await elegirFamiliaEnElSelector('8')
    await waitFor(() => expect(pedidosA('/familias/previsualizacion')).toHaveLength(2))

    await act(async () => {
      deLaSegunda.resolver(fotoDeUnion({ idArticuloReferencia: 51, idFamilia: 8 }))
    })
    await screen.findByRole('group', { name: 'Unir el artículo a la familia' })
    await act(async () => {
      deLaPrimera.resolver(fotoDeUnion({ idArticuloReferencia: 31, idFamilia: 7 }))
    })

    expect(propuesta()).toHaveTextContent('de la familia "Talles"')
    expect(propuesta()).not.toHaveTextContent('La familia cambió desde que se la eligió.')
    expect(within(propuesta()).getByRole('button', { name: 'Confirmar y unir a la familia' })).toBeEnabled()
    expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('8')
  })

  /** Cláusula bajo prueba: la generación del detalle en `useFamiliasDelFormulario`: el detalle tardío de la familia que se
   * dejó de elegir no dispara una previsualización.
   * Evidencia de mutación (mutation-proof-tests): quitar la cláusula hace fallar este test; revertido, vuelve a verde. */
  it('el detalle tardío de una familia que se dejó de elegir no previsualiza', async () => {
    const detalleTardio = diferida<FamiliaDetalle>()
    escenarioDeUnion({ detalleDeFamiliaImpl: () => detalleTardio.promesa })
    await abrirEdicion(1, 'A0001')
    await elegirFamiliaEnElSelector('7')
    await screen.findByText('Cargando la familia…')

    await userEvent.selectOptions(screen.getByLabelText('Familia (opcional)'), '')
    await act(async () => {
      detalleTardio.resolver(detalleSabores())
    })

    expect(pedidosA('/familias/previsualizacion')).toHaveLength(0)
    expect(screen.queryByRole('group', { name: 'Unir el artículo a la familia' })).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el ref de reentrancia de `confirmarUnion` (react-async-state regla 11).
   * Evidencia de mutación (mutation-proof-tests): quitar la cláusula hace fallar este test; revertido, vuelve a verde. */
  it('dos clics sincrónicos en "Confirmar" emiten un solo POST', async () => {
    escenarioDeUnion({}, () => new Promise(() => {}))
    const grupo = await proponerLaFamilia()

    const confirmar = within(grupo).getByRole('button', { name: 'Confirmar y unir a la familia' })
    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(pedidosA('/familias/7/articulos')).toHaveLength(1)
  })

  it('con la escritura en vuelo todo queda inerte: el selector, el "+", "Confirmar" y "Cancelar"', async () => {
    escenarioDeUnion({}, () => new Promise(() => {}))
    const grupo = await proponerLaFamilia()

    await userEvent.click(within(grupo).getByRole('button', { name: 'Confirmar y unir a la familia' }))

    expect(within(propuesta()).getByRole('button', { name: 'Uniendo…' })).toBeDisabled()
    expect(within(propuesta()).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByLabelText('Familia (opcional)')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Nueva familia' })).toBeDisabled()
  })

  describe('lo que descarta una propuesta abierta', () => {
    function selectorDeFamilia() {
      return screen.getByLabelText('Familia (opcional)')
    }

    /** Cláusula bajo prueba: `descartarPropuestaDeUnion()` del `guardar` de una edición: la foto de la unión se calculó con los
     * valores guardados antes, así que al guardar el artículo ya no puede confirmarse.
     * Evidencia de mutación (mutation-proof-tests): sacar esa llamada hace fallar este test (la propuesta sigue y "Confirmar" se
     * habilita); revertido, vuelve a verde. */
    it('guardar el artículo con una propuesta abierta la descarta: ya no hay con qué confirmar la unión', async () => {
      escenarioDeUnion()
      apiPutMock.mockResolvedValue(articuloFixture({ nombre: 'Articulo UnoX' }))
      await proponerLaFamilia()
      await userEvent.type(screen.getByLabelText('Nombre'), 'X')

      await guardar()
      await screen.findByText('Artículo "Articulo UnoX" actualizado.')

      expect(screen.queryByRole('group', { name: 'Unir el artículo a la familia' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Confirmar y unir a la familia' })).not.toBeInTheDocument()
      expect(selectorDeFamilia()).toHaveValue('')
      expect(selectorDeFamilia()).toBeEnabled()
      expect(pedidosA('/familias/7/articulos')).toHaveLength(0)
    })

    /** Cláusula bajo prueba: `alEscribirPrecio()` de `guardarPrecio` y su cableado `descartarPropuestaDeUnion` en `Articulos`: un
     * precio escrito cambia lo que la unión reemplazaría, así que la propuesta abierta se descarta.
     * Evidencia de mutación (mutation-proof-tests): dejar `alEscribirPrecio` como una función vacía hace fallar este test (la
     * propuesta sigue abierta con su foto vieja); revertido, vuelve a verde. */
    it('escribir un precio del artículo con una propuesta abierta la descarta: ya no hay con qué confirmar la unión', async () => {
      escenarioDeUnion()
      await proponerLaFamilia()
      mockearPosts({
        '/familias/previsualizacion': () => Promise.resolve(fotoDeUnion()),
        '/familias/7/articulos': () => Promise.resolve(resultadoDeUnir),
        '/articulos/1/precios': () => Promise.resolve({ idArticulo: 1, idListaPrecio: 2, precio: 1500, fecha: '2026-10-05T00:00:00Z' }),
      })

      await userEvent.click((await screen.findAllByRole('button', { name: 'Gestionar' }))[0])
      await userEvent.type(await screen.findByLabelText('Precio'), '1500')
      await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

      await waitFor(() => expect(pedidosA('/articulos/1/precios')).toHaveLength(1))
      await waitFor(() => expect(screen.queryByRole('group', { name: 'Unir el artículo a la familia' })).not.toBeInTheDocument())
      expect(screen.queryByRole('button', { name: 'Confirmar y unir a la familia' })).not.toBeInTheDocument()
      expect(selectorDeFamilia()).toHaveValue('')
      expect(pedidosA('/familias/7/articulos')).toHaveLength(0)
    })

    /** Cláusula bajo prueba: `soltarFamiliaDelFormulario()` de `crearFamiliaDelArticulo`: crear una familia con "+" descarta la
     * propuesta abierta, así que tras "Sacar de la familia" el selector no conserva la elección vieja y elegir esa familia
     * vuelve a proponerla.
     * Evidencia de mutación (mutation-proof-tests): sacar esa llamada hace fallar este test (el selector queda en "7"); revertido,
     * vuelve a verde. */
    it('crear una familia con "+" descarta la propuesta: tras sacar al artículo el selector vuelve a "Sin familia" y elegir la misma familia la propone', async () => {
      const miembroUnico = [{ id: 1, codigoInterno: 'A0001', nombre: 'Articulo Uno', idMarca: null, activo: true }]
      escenarioDeUnion({
        detalles: { 7: detalleSabores(), 50: detalleSabores({ id: 50, nombre: 'Talles', articulos: miembroUnico }) },
      })
      mockearPosts({
        '/familias/previsualizacion': () => Promise.resolve(fotoDeUnion()),
        '/familias': () => Promise.resolve({ idFamilia: 50, nombre: 'Talles', idArticuloReferencia: 1, articulos: [] }),
      })
      apiDeleteMock.mockResolvedValue(undefined)
      await proponerLaFamilia()

      await anotarFamiliaNueva('Talles')
      await screen.findByText('Familia "Talles" (1 artículo)')
      await userEvent.click(screen.getByRole('button', { name: 'Sacar de la familia' }))
      await userEvent.click(
        within(screen.getByRole('group', { name: 'Confirmar salida de la familia' })).getByRole('button', { name: 'Confirmar salida' }),
      )

      await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledExactlyOnceWith('/familias/50/articulos/1'))
      await screen.findByLabelText('Familia (opcional)')
      expect(selectorDeFamilia()).toHaveValue('')
      expect(screen.queryByRole('group', { name: 'Unir el artículo a la familia' })).not.toBeInTheDocument()

      await elegirFamiliaEnElSelector('7')

      expect(await screen.findByRole('group', { name: 'Unir el artículo a la familia' })).toBeInTheDocument()
      expect(selectorDeFamilia()).toHaveValue('7')
    })

    /** Cláusula bajo prueba: el descarte del detalle en `soltarFamiliaDelFormulario` (generación de `useFamiliasDelFormulario`) al
     * guardar: el detalle tardío de la propuesta descartada no previsualiza.
     * Evidencia de mutación (mutation-proof-tests): sacar `descartarPropuestaDeUnion()` del `guardar` hace fallar este test
     * (el detalle tardío dispara la previsualización); revertido, vuelve a verde. */
    it('el detalle tardío de una propuesta descartada al guardar no previsualiza ni la reabre', async () => {
      const detalleTardio = diferida<FamiliaDetalle>()
      escenarioDeUnion({ detalleDeFamiliaImpl: () => detalleTardio.promesa })
      apiPutMock.mockResolvedValue(articuloFixture({ nombre: 'Articulo UnoX' }))
      await abrirEdicion(1, 'A0001')
      await elegirFamiliaEnElSelector('7')
      await screen.findByText('Cargando la familia…')
      await userEvent.type(screen.getByLabelText('Nombre'), 'X')

      await guardar()
      await screen.findByText('Artículo "Articulo UnoX" actualizado.')
      await act(async () => {
        detalleTardio.resolver(detalleSabores())
      })

      expect(pedidosA('/familias/previsualizacion')).toHaveLength(0)
      expect(screen.queryByRole('group', { name: 'Unir el artículo a la familia' })).not.toBeInTheDocument()
      expect(selectorDeFamilia()).toHaveValue('')
    })

    /** Cláusula bajo prueba: lo mismo para la previsualización en vuelo al guardar: su respuesta tardía no reabre la propuesta.
     * Evidencia de mutación (mutation-proof-tests): sacar `descartarPropuestaDeUnion()` del `guardar` hace fallar este test
     * (el selector queda en "7"); revertido, vuelve a verde. No prueba la generación de `invalidar`: quitándola también
     * pasa, porque el grupo de la propuesta no se dibuja mientras el selector está en "Sin familia" (la mata
     * `usePrevisualizacion.test.ts`). */
    it('la previsualización tardía de una propuesta descartada al guardar no la reabre', async () => {
      const fotoTardia = diferida<PrevisualizacionDeAgrupacion>()
      escenarioDeUnion()
      mockearPosts({ '/familias/previsualizacion': () => fotoTardia.promesa })
      apiPutMock.mockResolvedValue(articuloFixture({ nombre: 'Articulo UnoX' }))
      await abrirEdicion(1, 'A0001')
      await elegirFamiliaEnElSelector('7')
      await waitFor(() => expect(pedidosA('/familias/previsualizacion')).toHaveLength(1))
      await userEvent.type(screen.getByLabelText('Nombre'), 'X')

      await guardar()
      await screen.findByText('Artículo "Articulo UnoX" actualizado.')
      await act(async () => {
        fotoTardia.resolver(fotoDeUnion())
      })

      expect(screen.queryByRole('group', { name: 'Unir el artículo a la familia' })).not.toBeInTheDocument()
      expect(selectorDeFamilia()).toHaveValue('')
      expect(pedidosA('/familias/7/articulos')).toHaveLength(0)
    })

    /** Comportamiento de punta a punta, sin cláusula propia: la foto de una propuesta descartada al guardar llega tarde, cuando el
     * usuario ya volvió a elegir la MISMA familia (su `idFamilia` coincide, así que contaría como vigente), y nunca se muestra
     * ni se puede confirmar. La relectura del detalle va diferida para abrir la ventana en la que la foto vieja podría quedar en
     * el estado del hook; un observador del DOM registra si alguna vez se pintó.
     * Evidencia de mutación (mutation-proof-tests): quitar `generacionRef.current += 1` de `invalidar` NO hace fallar este test,
     * porque la llamada siguiente a `previsualizar` limpia el estado antes de que llegue a pintarse (observado); esa cláusula la mata
     * `usePrevisualizacion.test.ts` ("invalidar con una solicitud en vuelo…"). */
    it('al volver a elegir la misma familia, la previsualización tardía de la propuesta descartada no se muestra ni se confirma', async () => {
      const fotoVieja = diferida<PrevisualizacionDeAgrupacion>()
      const fotoNueva = diferida<PrevisualizacionDeAgrupacion>()
      const detalleDeLaReeleccion = diferida<FamiliaDetalle>()
      const conPrecio = (vigente: number) => ({
        precios: [{ idListaPrecio: 2, actual: { vigente: 900, pendiente: null }, nuevo: { vigente, pendiente: null } }],
      })
      const [deLaVieja, deLaNueva] = [conPrecio(4321), conPrecio(5678)]
      const fotoConPrecio = (precio: { precios: PrevisualizacionDeAgrupacion['articulos'][number]['precios'] }) =>
        fotoDeUnion({ articulos: [{ ...fotoDeUnion().articulos[0], ...precio }] })
      escenarioDeUnion({
        detalleDeFamiliaImpl: (_id, llamada) => (llamada === 1 ? Promise.resolve(detalleSabores()) : detalleDeLaReeleccion.promesa),
      })
      let pedidasLasFotos = 0
      mockearPosts({
        '/familias/previsualizacion': () => (pedidasLasFotos++ === 0 ? fotoVieja.promesa : fotoNueva.promesa),
        '/familias/7/articulos': () => Promise.resolve(resultadoDeUnir),
      })
      apiPutMock.mockResolvedValue(articuloFixture({ nombre: 'Articulo UnoX' }))
      await abrirEdicion(1, 'A0001')
      await elegirFamiliaEnElSelector('7')
      await waitFor(() => expect(pedidosA('/familias/previsualizacion')).toHaveLength(1))
      await userEvent.type(screen.getByLabelText('Nombre'), 'X')
      await guardar()
      await screen.findByText('Artículo "Articulo UnoX" actualizado.')
      await elegirFamiliaEnElSelector('7')
      await waitFor(() => expect(unaFamiliaPidioSuDetalle()).toHaveLength(2))

      let seMostroLaFotoVieja = false
      const observador = new MutationObserver(() => {
        if (/4\.321/.test(document.body.textContent ?? '')) seMostroLaFotoVieja = true
      })
      observador.observe(document.body, { subtree: true, childList: true, characterData: true })
      try {
        await act(async () => {
          fotoVieja.resolver(fotoConPrecio(deLaVieja))
        })
        await act(async () => {
          detalleDeLaReeleccion.resolver(detalleSabores())
        })
        await waitFor(() => expect(pedidosA('/familias/previsualizacion')).toHaveLength(2))
        await screen.findByText('Calculando qué cambia…')

        expect(seMostroLaFotoVieja).toBe(false)
        expect(screen.queryByRole('group', { name: 'Unir el artículo a la familia' })).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'Confirmar y unir a la familia' })).not.toBeInTheDocument()

        await act(async () => {
          fotoNueva.resolver(fotoConPrecio(deLaNueva))
        })
        expect(propuesta()).toHaveTextContent(/5\.678/)
        expect(propuesta()).not.toHaveTextContent(/4\.321/)
        expect(seMostroLaFotoVieja).toBe(false)
        expect(within(propuesta()).getByRole('button', { name: 'Confirmar y unir a la familia' })).toBeEnabled()
        expect(pedidosA('/familias/7/articulos')).toHaveLength(0)
      } finally {
        observador.disconnect()
      }
    })
  })

  describe('lo que el servidor rechaza al confirmar', () => {
    it.each([
      ['familia_inactiva', 409, 'La familia está inactiva.', 'Activala desde el listado de familias para poder sumarle artículos.'],
      ['familia_sin_articulos', 409, 'La familia no tiene artículos vivos.', 'Una familia sin artículos vivos no tiene referencia'],
      ['familia_precio_inalineable', 422, 'Un precio no se puede alinear.', 'Un precio nunca se quita'],
      ['referencia_invalida', 400, 'Una referencia ya no existe.', 'Revisá que todos los artículos existan'],
    ])('%s: muestra el motivo con su ayuda, vuelve a "Sin familia" y no toca el artículo', async (codigo, estado, mensaje, ayuda) => {
      escenarioDeUnion({}, () => Promise.reject(new ErrorApi(estado, codigo, mensaje)))
      await proponerLaFamilia()

      await confirmarLaUnion()

      expect(await screen.findByRole('alert')).toHaveTextContent(mensaje)
      expect(screen.getByRole('alert')).toHaveTextContent(ayuda)
      expect(screen.queryByRole('group', { name: 'Unir el artículo a la familia' })).not.toBeInTheDocument()
      expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('')
      expect(screen.getByLabelText('Familia (opcional)')).toBeEnabled()
      expect(lecturasDelArticulo(1)).toBe(1)
    })

    it('404: la familia que ya no existe deja de ofrecerse', async () => {
      escenarioDeUnion(
        { familiasImpl: () => Promise.resolve(lecturasDeFamilias() <= 1 ? [familiaListado()] : []) },
        () => Promise.reject(new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.')),
      )
      await proponerLaFamilia()

      await confirmarLaUnion()

      await screen.findByText(/No existe la familia 7\./)
      await waitFor(() => expect(within(screen.getByLabelText('Familia (opcional)')).getAllByRole('option').map((o) => o.textContent)).toEqual(['Sin familia']))
    })

    it('articulo_en_otra_familia: vuelve a leer el artículo y avisa que su pertenencia cambió', async () => {
      escenarioDeUnion(
        { articuloImpl: (_id, llamada) => (llamada === 1 ? articuloFixture() : articuloFixture({ idFamilia: 7, ...compartidosDelArticulo })) },
        () => Promise.reject(new ErrorApi(409, 'articulo_en_otra_familia', 'El artículo ya es de otra familia.')),
      )
      await proponerLaFamilia()

      await confirmarLaUnion()

      expect(
        await screen.findByText(
          'La pertenencia del artículo a su familia cambió desde que se cargó la pantalla. Se recargó el artículo: revisá los datos y volvé a intentar.',
        ),
      ).toBeInTheDocument()
      expect(await screen.findByText('Familia "Sabores" (3 artículos)')).toBeInTheDocument()
    })

    /** Cláusula bajo prueba: `setErrorDeUnion('')` de `soltarFamiliaDelFormulario` (react-async-state regla 16): el rechazo
     * anterior no queda en pantalla mientras se pide la previsualización siguiente.
   * Evidencia de mutación (mutation-proof-tests): quitar la cláusula hace fallar este test; revertido, vuelve a verde. */
    it('volver a elegir una familia saca de pantalla el rechazo anterior mientras se previsualiza', async () => {
      escenarioDeUnion({}, () => Promise.reject(new ErrorApi(409, 'familia_inactiva', 'La familia está inactiva.')))
      await proponerLaFamilia()
      await confirmarLaUnion()
      await screen.findByText(/La familia está inactiva\./)
      mockearPosts({ '/familias/previsualizacion': () => new Promise(() => {}) })

      await elegirFamiliaEnElSelector('7')

      await waitFor(() => expect(pedidosA('/familias/previsualizacion')).toHaveLength(2))
      expect(screen.queryByText(/La familia está inactiva\./)).not.toBeInTheDocument()
    })
  })

  describe('lo que pasa antes de poder confirmar', () => {
    it('una familia que el detalle dice inactiva no se previsualiza: se explica y se vuelve a "Sin familia"', async () => {
      escenarioDeUnion({ detalles: { 7: detalleSabores({ activo: false }) } })
      await abrirEdicion(1, 'A0001')

      await elegirFamiliaEnElSelector('7')

      expect(await screen.findByRole('alert')).toHaveTextContent('La familia "Sabores" está inactiva: no se le pueden agregar artículos.')
      expect(pedidosA('/familias/previsualizacion')).toHaveLength(0)
      expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('')
    })

    it('una familia sin referencia no se previsualiza', async () => {
      escenarioDeUnion({ detalles: { 7: detalleSabores({ articulos: [], valores: null }) } })
      await abrirEdicion(1, 'A0001')

      await elegirFamiliaEnElSelector('7')

      expect(await screen.findByRole('alert')).toHaveTextContent('no tiene artículos vivos')
      expect(pedidosA('/familias/previsualizacion')).toHaveLength(0)
    })

    it('si el detalle de la familia no carga lo dice y "Reintentar" lo vuelve a pedir', async () => {
      let lecturas = 0
      escenarioDeUnion({
        detalleDeFamiliaImpl: () =>
          ++lecturas === 1 ? Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó la familia.')) : Promise.resolve(detalleSabores()),
      })
      await abrirEdicion(1, 'A0001')
      await elegirFamiliaEnElSelector('7')
      expect(await screen.findByText('Se cayó la familia.')).toBeInTheDocument()

      await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

      expect(await screen.findByRole('group', { name: 'Unir el artículo a la familia' })).toBeInTheDocument()
      expect(screen.queryByText('Se cayó la familia.')).not.toBeInTheDocument()
    })

    it('si la previsualización falla lo dice y se puede cancelar', async () => {
      escenarioDeUnion()
      mockearPosts({ '/familias/previsualizacion': () => Promise.reject(new TypeError('Failed to fetch')) })
      await abrirEdicion(1, 'A0001')

      await elegirFamiliaEnElSelector('7')

      const aviso = await screen.findByText('No se pudo previsualizar la agrupación.')
      await userEvent.click(within(aviso.closest('.alert') as HTMLElement).getByRole('button', { name: 'Cancelar' }))
      expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('')
    })

    /** Cláusula bajo prueba: `previsualizacionDeUnionVigente` en la vista: la referencia que el servidor ve ya no está en
     * la familia elegida.
   * Evidencia de mutación (mutation-proof-tests): quitar la cláusula hace fallar este test; revertido, vuelve a verde. */
    it('si el servidor ve la referencia en otra familia, avisa y no deja confirmar', async () => {
      escenarioDeUnion()
      mockearPosts({ '/familias/previsualizacion': () => Promise.resolve(fotoDeUnion({ idFamilia: 99 })) })

      const grupo = await proponerLaFamilia()

      expect(grupo).toHaveTextContent('La familia cambió desde que se la eligió.')
      expect(within(grupo).getByRole('button', { name: 'Confirmar y unir a la familia' })).toBeDisabled()
    })

    it('con cambios sin guardar durante la propuesta no se puede confirmar, pero sí cancelar', async () => {
      escenarioDeUnion()
      const grupo = await proponerLaFamilia()

      await userEvent.type(screen.getByLabelText('Nombre'), 'X')

      expect(within(grupo).getByRole('button', { name: 'Confirmar y unir a la familia' })).toBeDisabled()
      expect(screen.getByLabelText('Familia (opcional)')).toBeDisabled()
      expect(within(grupo).getByRole('button', { name: 'Cancelar' })).toBeEnabled()
    })
  })

  describe('un alta con una familia existente sigue como antes', () => {
    it('elegirla en un alta no previsualiza ni escribe: prellena y bloquea los quince campos', async () => {
      mockearApi({ familias: [familiaListado()], detalles: { 7: detalleSabores() } })
      mockearPosts({})

      await abrirAltaEnLaFamilia()

      expect(pedidosA('/familias/previsualizacion')).toHaveLength(0)
      expect(screen.queryByRole('group', { name: 'Unir el artículo a la familia' })).not.toBeInTheDocument()
      expect(screen.getAllByText('(de la familia)').length).toBeGreaterThan(0)
      expect(screen.getByLabelText(/^Costo de lista con IVA/)).toBeDisabled()
    })
  })
})
