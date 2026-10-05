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
  descuentoProveedor: 5,
  costoNominal: 120,
}

/** Los trece valores como los guarda un artículo: el área es obligatoria en él, aunque el detalle de una familia la
 * presente como `null` cuando está dada de baja. */
const compartidosDelArticulo = { ...referencia, idArea: 1 }

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

/** Vainilla, el miembro que se edita: sus trece campos compartidos son los de la referencia. */
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

/** Abre un alta y elige la familia: espera a que su detalle ya esté prellenado en el formulario (el dato, no el
 * elemento). */
async function abrirAltaEnLaFamilia(idFamilia = '7') {
  await abrirAlta()
  await userEvent.selectOptions(await screen.findByLabelText('Familia (opcional)'), idFamilia)
  await waitFor(() => expect(screen.getByLabelText(/^Costo de lista/)).toHaveValue('150,00'))
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
  /^Costo de lista/,
  /^Descuento de proveedor/,
  /^Costo nominal/,
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
    expect(within(selector).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'Sin familia',
      'Sabores (3 artículos)',
      'Talles (1 artículo)',
    ])
    expect(selector).toHaveValue('')
  })

  it('sin familias para elegir no ofrece el selector', async () => {
    mockearApi({ familias: [familiaListado({ activo: false })] })

    await abrirAlta()
    await waitFor(() => expect(apiGetMock.mock.calls.some((c) => c[0] === '/familias')).toBe(true))
    await act(async () => {})

    expect(screen.queryByLabelText('Familia (opcional)')).not.toBeInTheDocument()
    expect(screen.queryByText('Familia')).not.toBeInTheDocument()
    expect(screen.queryByText('Cargando la familia…')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el aviso del fallo de carga de las familias, que dice lo que la pantalla de verdad
   * impone: sin familias cargadas no hay selector (react-async-state regla 7). */
  it('si no se pudieron cargar las familias lo avisa y no ofrece el selector, pero el alta sigue disponible', async () => {
    mockearApi({ familiasImpl: () => Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.')) })

    await abrirAlta()

    expect(
      await screen.findByText('No se pudieron cargar las familias. No se puede elegir una familia al crear un artículo.'),
    ).toBeInTheDocument()
    expect(screen.queryByLabelText('Familia (opcional)')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  it('al editar un artículo el selector no existe, ni siquiera cuando las familias no cargaron', async () => {
    mockearApi({
      articulos: { 1: articuloFixture() },
      familiasImpl: () => Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.')),
    })

    await abrirEdicion(1, 'A0001')
    await act(async () => {})

    expect(screen.queryByLabelText('Familia (opcional)')).not.toBeInTheDocument()
    expect(screen.queryByText(/No se pudieron cargar las familias/)).not.toBeInTheDocument()
  })
})

describe('Articulos — alta: elegir una familia', () => {
  beforeEach(() => {
    mockearApi({ familias: [familiaListado()], detalles: { 7: detalleSabores() } })
  })

  it('pide el detalle de la familia y prellena los trece campos compartidos con sus valores', async () => {
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
    expect(screen.getByLabelText(/^Costo de lista/)).toHaveValue('150,00')
    expect(screen.getByLabelText(/^Descuento de proveedor/)).toHaveValue(5)
    expect(screen.getByLabelText(/^Costo nominal/)).toHaveValue('120,00')
  })

  it('los trece campos compartidos quedan bloqueados y su etiqueta lo dice: "(de la familia)"', async () => {
    await abrirAltaEnLaFamilia()

    for (const etiqueta of CAMPOS_COMPARTIDOS_DEL_FORMULARIO) {
      const control = screen.getByLabelText(etiqueta)
      expect(control, String(etiqueta)).toBeDisabled()
      const label = document.querySelector(`label[for="${control.id}"]`)
      expect(label?.textContent, String(etiqueta)).toContain('(de la familia)')
    }
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

    const tabla = await screen.findByRole('table', { name: 'Precios que se copian de la familia' })
    expect(within(tabla).getAllByRole('row').map((fila) => within(fila).queryAllByRole('cell').map((c) => c.textContent))).toEqual([
      [],
      ['General', '$ 1.200,00'],
      ['Mayorista', '—'],
    ])
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

  /** Cláusula bajo prueba: `altaConFamiliaLista` en el `disabled` de "Guardar" y en la guarda del submit: el
   * aviso "Cargando la familia…" promete que no se guarda mientras no llegó (react-async-state regla 7). */
  it('mientras la familia carga, sus campos ya están bloqueados y no se puede guardar', async () => {
    const lectura = diferida<FamiliaDetalle>()
    mockearApi({ familias: [familiaListado()], detalleDeFamiliaImpl: () => lectura.promesa })
    await abrirAlta()

    await userEvent.selectOptions(await screen.findByLabelText('Familia (opcional)'), '7')

    expect(await screen.findByText('Cargando la familia…')).toBeInTheDocument()
    expect(screen.getByLabelText(/^Costo de lista/)).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()

    await act(async () => {
      lectura.resolver(detalleSabores())
    })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled())
    expect(screen.queryByText('Cargando la familia…')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `!altaLista` en la guarda del submit del formulario: Enter en un campo envía el
   * formulario aunque el botón esté deshabilitado, y la familia que todavía no llegó no puede guardarse. Evidencia
   * de mutación (mutation-proof-tests): sacar `|| !altaLista` de esa guarda hace fallar este test (se emite el POST
   * sin los valores de la familia); revertido, vuelve a verde. */
  it('un submit con la familia todavía cargando (Enter en un campo) tampoco guarda', async () => {
    const lectura = diferida<FamiliaDetalle>()
    mockearApi({ familias: [familiaListado()], detalleDeFamiliaImpl: () => lectura.promesa })
    await abrirAlta()
    await userEvent.selectOptions(await screen.findByLabelText('Familia (opcional)'), '7')
    await screen.findByText('Cargando la familia…')

    const formulario = screen.getByRole('button', { name: 'Guardar' }).closest('form')
    if (!formulario) throw new Error('No se encontró el formulario')
    fireEvent.submit(formulario)

    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('si la carga de la familia falla: muestra el motivo, no deja guardar y "Reintentar" la vuelve a pedir', async () => {
    mockearApi({
      familias: [familiaListado()],
      detalleDeFamiliaImpl: (_id, llamada) =>
        llamada === 1 ? Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó la lectura.')) : Promise.resolve(detalleSabores()),
    })
    await abrirAlta()

    await userEvent.selectOptions(await screen.findByLabelText('Familia (opcional)'), '7')

    expect(await screen.findByText(/Se cayó la lectura\. El artículo no se puede guardar con esta familia hasta que cargue/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()

    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

    await waitFor(() => expect(screen.getByLabelText(/^Costo de lista/)).toHaveValue('150,00'))
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
    await userEvent.selectOptions(await screen.findByLabelText('Familia (opcional)'), '7')
    await screen.findByText(/Se cayó la lectura/)

    await userEvent.selectOptions(screen.getByLabelText('Familia (opcional)'), '')

    expect(screen.queryByText(/Se cayó la lectura/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  it('una familia sin ningún miembro vivo no se puede usar: avisa que no hay valores que copiar y no deja guardar', async () => {
    mockearApi({ familias: [familiaListado()], detalles: { 7: detalleSabores({ articulos: [], valores: null, precios: [] }) } })
    await abrirAlta()

    await userEvent.selectOptions(await screen.findByLabelText('Familia (opcional)'), '7')

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
    expect(screen.getByLabelText(/^Costo de lista/)).toHaveValue('150,00')
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

    await userEvent.selectOptions(selector, '7')
    await userEvent.selectOptions(selector, '8')
    await act(async () => {
      segunda.resolver(detalleSabores({ id: 8, nombre: 'Talles', valores: { ...referencia, costoLista: 999 } }))
    })
    await waitFor(() => expect(screen.getByLabelText(/^Costo de lista/)).toHaveValue('999,00'))

    await act(async () => {
      primera.resolver(detalleSabores({ id: 7, nombre: 'Sabores', valores: { ...referencia, costoLista: 111 } }))
    })

    expect(screen.getByLabelText(/^Costo de lista/)).toHaveValue('999,00')
    expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('8')
  })

  /** Cláusula bajo prueba: `familias.descartarDetalle()` en `descartarModal` y en la apertura del alta: la familia
   * que llega tarde, con el modal ya cerrado, no pisa el alta siguiente. */
  it('una familia que llega después de cerrar el modal no se aplica al alta que se abre luego', async () => {
    const lectura = diferida<FamiliaDetalle>()
    mockearApi({ familias: [familiaListado()], detalleDeFamiliaImpl: () => lectura.promesa })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    await abrirAlta()
    await userEvent.selectOptions(await screen.findByLabelText('Familia (opcional)'), '7')
    await screen.findByText('Cargando la familia…')

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    await waitFor(() => expect(screen.queryByText('Nuevo artículo')).not.toBeInTheDocument())
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Nuevo artículo')

    await act(async () => {
      lectura.resolver(detalleSabores())
    })

    expect(screen.getByLabelText(/^Costo de lista/)).toHaveValue('')
    expect(screen.getByLabelText(/^Costo de lista/)).toBeEnabled()
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

  it('manda idFamilia y los trece valores de la familia, junto con lo propio del artículo', async () => {
    await abrirAltaEnLaFamilia()

    await userEvent.type(screen.getByLabelText('Nombre'), 'Frutilla')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [ruta, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(ruta).toBe('/articulos')
    expect(cuerpo).toMatchObject({ idFamilia: 7, nombre: 'Frutilla', codigoInterno: null, ...referencia })
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

    expect(screen.getByLabelText(/^Costo de lista/)).toBeEnabled()
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
    await waitFor(() => expect(screen.getByLabelText(/^Costo de lista/)).toHaveValue('200,00'))
    expect(unaFamiliaPidioSuDetalle()).toHaveLength(2)
    // La familia sigue elegida y sus campos bloqueados: el artículo todavía quiere entrar a ella.
    expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('7')
    expect(screen.getByLabelText(/^Costo de lista/)).toBeDisabled()
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
    await waitFor(() => expect(screen.getByLabelText(/^Costo de lista/)).toHaveValue('200,00'))

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
    await waitFor(() => expect(screen.getByLabelText(/^Costo de lista/)).toHaveValue('999,00'))
    await act(async () => {
      relectura.resolver(detalleSabores({ valores: { ...referencia, costoLista: 111 } }))
    })

    expect(screen.queryByText(/Los valores del artículo no coinciden/)).not.toBeInTheDocument()
    expect(screen.queryByText(MENSAJE_DISTINTOS)).not.toBeInTheDocument()
    expect(screen.getByLabelText(/^Costo de lista/)).toHaveValue('999,00')
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
    expect(screen.getByLabelText(/^Costo de lista/)).toBeEnabled()
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
    // Ya no queda ninguna familia para elegir, y la que se había elegido dejó de bloquear los campos.
    await waitFor(() => expect(screen.queryByLabelText('Familia (opcional)')).not.toBeInTheDocument())
    expect(screen.getByLabelText(/^Costo de lista/)).toBeEnabled()
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

  it('404 de la familia: la saca de la selección con el mensaje del servidor', async () => {
    let lecturasDeFamilias = 0
    mockearApi({
      familiasImpl: () => Promise.resolve(++lecturasDeFamilias === 1 ? [familiaListado()] : []),
      detalles: { 7: detalleSabores() },
    })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.'))

    await guardarAltaEnLaFamilia()

    expect(await screen.findByText('No existe la familia 7. Elegí otra familia o creá el artículo sin familia.')).toBeInTheDocument()
    expect(screen.getByLabelText(/^Costo de lista/)).toBeEnabled()
  })

  it('cualquier otro rechazo se muestra tal cual y la familia sigue elegida', async () => {
    mockearApi({ familias: [familiaListado()], detalles: { 7: detalleSabores() } })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(400, 'referencia_invalida', 'No existe el área 1.'))

    await guardarAltaEnLaFamilia()

    expect(await screen.findByText('No existe el área 1.')).toBeInTheDocument()
    expect(screen.getByLabelText('Familia (opcional)')).toHaveValue('7')
    expect(screen.getByLabelText(/^Costo de lista/)).toBeDisabled()
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
  const campo = screen.getByLabelText('Costo de lista')
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
    expect(screen.getByLabelText('Costo de lista')).toHaveValue('175,00')

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
    expect(screen.getByLabelText('Costo de lista')).toHaveValue('175,00')
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
    expect(screen.getByLabelText('Costo de lista')).toHaveValue('150,00')
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
    expect(screen.getByLabelText('Costo de lista')).toBeDisabled()
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
  ) {
    escenarioDelMiembro({ articulos: { 31: miembro(), 5: otro } })
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

  /** Cláusula bajo prueba: `destinoModalRef.current !== idArticulo` de `recargarPorCambioDeFamilia`. Evidencia de
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
})
