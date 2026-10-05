import { StrictMode } from 'react'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation, useNavigate, type Location } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Familia } from './Familia'
import { ErrorApi } from '../api/cliente'
import type {
  ArticuloListado,
  CambiosDeUnArticulo,
  FamiliaDetalle,
  PaginaDe,
  PrevisualizacionDeAgrupacion,
  ResultadoDeAgrupacion,
  ValoresCompartidosDeLaFamilia,
} from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: vi.fn(),
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

const referencia: ValoresCompartidosDeLaFamilia = {
  idArea: 1,
  idCategoria: 2,
  idGrupo: null,
  idProveedorHabitual: 4,
  idAlicuotaIva: 5,
  unidadVenta: 'Peso',
  unidadesPorBulto: 12,
  esProducto: true,
  controlaLote: false,
  acumulaEnVenta: false,
  costoLista: 150,
  descuentoProveedor: 5,
  costoNominal: null,
}

function detalleSabores(sobrescribir: Partial<FamiliaDetalle> = {}): FamiliaDetalle {
  return {
    id: 7,
    nombre: 'Sabores',
    activo: true,
    articulos: [
      { id: 31, codigoInterno: 'A0031', nombre: 'Vainilla', idMarca: 10, activo: true },
      { id: 32, codigoInterno: 'A0032', nombre: 'Frutilla', idMarca: null, activo: true },
      { id: 33, codigoInterno: 'A0033', nombre: 'Chocolate', idMarca: 99, activo: false },
    ],
    valores: referencia,
    precios: [
      { idListaPrecio: 2, estado: { vigente: 1200, pendiente: null } },
      { idListaPrecio: 3, estado: { vigente: null, pendiente: null } },
    ],
    ...sobrescribir,
  }
}

function articulo(id: number, sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    id,
    codigoInterno: `A00${id}`,
    nombre: `Articulo ${id}`,
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
    acumulaEnVenta: true,
    idFamilia: null,
    ...sobrescribir,
  }
}

function pagina(items: ArticuloListado[], total = items.length): PaginaDe<ArticuloListado> {
  return { items, total, pagina: 1, tamanio: 25 }
}

function cambiosDe(idArticulo: number, sobrescribir: Partial<CambiosDeUnArticulo> = {}): CambiosDeUnArticulo {
  return { idArticulo, campos: [], actual: referencia, nuevo: referencia, precios: [], ...sobrescribir }
}

function previsualizacionDe(sobrescribir: Partial<PrevisualizacionDeAgrupacion> = {}): PrevisualizacionDeAgrupacion {
  return { idArticuloReferencia: 31, idFamilia: 7, articulos: [], problemas: [], ...sobrescribir }
}

function resultadoDe(articulos: CambiosDeUnArticulo[]): ResultadoDeAgrupacion {
  return { idFamilia: 7, nombre: 'Sabores', idArticuloReferencia: 31, articulos }
}

function diferida<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((resolve, reject) => {
    resolver = resolve
    rechazar = reject
  })
  return { promesa, resolver, rechazar }
}

// ---- API simulada ------------------------------------------------------------------------------------------

type Escenario = {
  /** Detalle de la familia 7; con `detalleImpl` gana esta, que recibe el número de lectura (1 la primera). */
  detalle?: FamiliaDetalle
  detalleImpl?: (llamada: number) => Promise<FamiliaDetalle>
  /** Resultados de la búsqueda de artículos; por defecto, tres artículos sin familia (40, 41, 42). */
  busqueda?: ArticuloListado[]
  /** Los catálogos que fallan: sus nombres, en minúscula, como los dice el aviso (`áreas`, `marcas`…). */
  catalogosCaidos?: ('areas' | 'marcas')[]
}

function mockearApi(escenario: Escenario = {}) {
  let llamadasDelDetalle = 0

  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/familias/7') {
      llamadasDelDetalle += 1
      return escenario.detalleImpl ? escenario.detalleImpl(llamadasDelDetalle) : Promise.resolve(escenario.detalle ?? detalleSabores())
    }
    if (ruta.startsWith('/articulos?busqueda=')) {
      return Promise.resolve(pagina(escenario.busqueda ?? [articulo(40), articulo(41), articulo(42)]))
    }
    if (ruta.startsWith('/catalogos/areas')) {
      return escenario.catalogosCaidos?.includes('areas')
        ? Promise.reject(new Error('caído'))
        : Promise.resolve([{ id: 1, nombre: 'Almacén', activo: true, idEmpresa: null, orden: 1 }])
    }
    if (ruta.startsWith('/catalogos/categorias')) {
      return Promise.resolve([{ id: 2, nombre: 'Bebidas', activo: true, idEmpresa: null, orden: 1, idCategoriaPadre: null }])
    }
    if (ruta.startsWith('/catalogos/grupos')) return Promise.resolve([])
    if (ruta.startsWith('/catalogos/marcas')) {
      return escenario.catalogosCaidos?.includes('marcas')
        ? Promise.reject(new Error('caído'))
        : Promise.resolve([{ id: 10, nombre: 'Alfa', activo: true, idEmpresa: null }])
    }
    if (ruta.startsWith('/catalogos/listas-precio')) {
      return Promise.resolve([{ id: 2, nombre: 'General', activo: true, idEmpresa: null, esDefault: true, modo: 'Fija', idListaBase: null, porcentaje: null }])
    }
    if (ruta.startsWith('/proveedores')) {
      return Promise.resolve({
        items: [{ id: 4, razonSocial: 'Alfa SA', nombreFantasia: null, activo: true }],
        total: 1,
        pagina: 1,
        tamanio: 200,
      })
    }
    if (ruta === '/catalogos-fiscales/alicuotas-iva') return Promise.resolve([{ id: 5, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true }])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

/** La ubicación que el router tiene ahora: sirve para ver si la entrada del historial conserva su estado. `irAtras` es el
 * Atrás del navegador. */
let ubicacionVigente: Location
let irAtras: () => void = () => {}

function EspiaDeUbicacion() {
  ubicacionVigente = useLocation()
  const navegar = useNavigate()
  irAtras = () => navegar(-1)
  return null
}

/** `anteriores` son las entradas del historial que preceden a `ruta`, la vigente. */
function montar(ruta: string | { pathname: string; state: unknown } = '/familias/7', anteriores: string[] = []) {
  return render(
    <MemoryRouter initialEntries={[...anteriores, ruta]} initialIndex={anteriores.length}>
      <EspiaDeUbicacion />
      <Routes>
        <Route path="/familias/:id" element={<Familia />} />
        <Route path="/familias" element={<p>Pantalla del listado de familias</p>} />
      </Routes>
    </MemoryRouter>,
  )
}

/** Espera al detalle de la familia (el dato), no al título de la caja, que se rinde antes. */
async function montarYEsperar(escenario: Escenario = {}) {
  mockearApi(escenario)
  montar()
  await screen.findByText('Vainilla')
}

function pares() {
  return Array.from(document.querySelectorAll('dl dt')).map((dt) => [dt.textContent, dt.nextElementSibling?.textContent])
}

/** La tabla de miembros es la primera de la pantalla; la de los precios de la referencia, la segunda. */
function filasDeMiembros() {
  return within(screen.getAllByRole('table')[0])
    .getAllByRole('row')
    .slice(1)
    .map((f) => within(f).getAllByRole('cell').map((c) => c.textContent))
}

function lecturasDelDetalle() {
  return apiGetMock.mock.calls.filter((c) => c[0] === '/familias/7').length
}

// ---- pruebas -----------------------------------------------------------------------------------------------

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiDeleteMock.mockReset()
  apiDeleteMock.mockResolvedValue(undefined)
})

describe('Familia — el detalle', () => {
  it('rinde el nombre, el estado y la cantidad de artículos vivos', async () => {
    await montarYEsperar()

    expect(screen.getByRole('heading', { name: 'Familia "Sabores"' })).toBeInTheDocument()
    expect(screen.getByText('Activa')).toBeInTheDocument()
    expect(screen.getByText('3 artículos')).toBeInTheDocument()
  })

  it('una familia inactiva se rotula "Inactiva"', async () => {
    await montarYEsperar({ detalle: detalleSabores({ activo: false }) })

    expect(screen.getByText('Inactiva')).toBeInTheDocument()
  })

  it('lista los miembros con código, nombre, marca y estado, y marca como referencia solo al primero', async () => {
    await montarYEsperar()
    await screen.findByText('Alfa')

    expect(filasDeMiembros()).toEqual([
      ['A0031', 'VainillaReferencia', 'Alfa', 'Activo', 'Sacar'],
      ['A0032', 'Frutilla', '—', 'Activo', 'Sacar'],
      ['A0033', 'Chocolate', '#99', 'Inactivo', 'Sacar'],
    ])
    expect(screen.getAllByText('Referencia')).toHaveLength(1)
  })

  it('los valores compartidos son los de la referencia, con los nombres de los catálogos', async () => {
    await montarYEsperar()

    // Cada nombre viene de su propia lectura (áreas, categorías, proveedores, alícuotas): se espera el dato de cada una,
    // no el de una sola.
    await waitFor(() =>
      expect(pares()).toEqual([
        ['Área', 'Almacén'],
        ['Categoría', 'Bebidas'],
        ['Grupo', 'Sin asignar'],
        ['Proveedor habitual', 'Alfa SA'],
        ['Alícuota de IVA', 'IVA 21%'],
        ['Unidad de venta', 'Por peso'],
        ['Unidades por bulto', '12'],
        ['Es producto', 'Sí'],
        ['Controla lote', 'No'],
        ['Acumula en una sola línea al vender', 'No'],
        ['Costo de lista', '$ 150,00'],
        ['Descuento de proveedor', '5%'],
        ['Costo nominal', '—'],
      ]),
    )
    expect(screen.getByText(/Son los del artículo de referencia \(A0031 — Vainilla\)/)).toBeInTheDocument()
  })

  it('los precios de cada lista son los de la referencia; una lista sin nombre conocido se muestra por su código', async () => {
    await montarYEsperar()
    await screen.findByText('General')

    const tablaDePrecios = screen.getAllByRole('table')[1]
    expect(within(tablaDePrecios).getAllByRole('row').slice(1).map((f) => within(f).getAllByRole('cell').map((c) => c.textContent))).toEqual([
      ['General', '$ 1.200,00'],
      ['Lista 3', '—'],
    ])
  })

  it('una familia sin listas de precio fijas lo dice en la tabla de precios', async () => {
    await montarYEsperar({ detalle: detalleSabores({ precios: [] }) })

    expect(screen.getByText('No hay listas de precio fijas.')).toBeInTheDocument()
  })

  it('una familia sin ningún miembro vivo lo dice y no muestra valores ni precios', async () => {
    mockearApi({ detalle: detalleSabores({ articulos: [], valores: null, precios: [] }) })
    montar()
    await screen.findByText('La familia no tiene artículos vivos.')

    expect(screen.queryByText('Valores compartidos')).not.toBeInTheDocument()
    expect(screen.queryByText('Precios por lista')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la generación de `cargar`. Bajo StrictMode el efecto de carga corre dos veces: la respuesta
   * tardía de la primera lectura no pisa a la de la segunda. Evidencia de mutación (mutation-proof-tests): sacar el
   * `if (generacion.current !== token) return` de la rama de éxito hace fallar este test; revertido, vuelve a verde. */
  it('bajo StrictMode, la respuesta tardía de la primera lectura no pisa a la de la segunda', async () => {
    const primera = diferida<FamiliaDetalle>()
    mockearApi({ detalleImpl: (llamada) => (llamada === 1 ? primera.promesa : Promise.resolve(detalleSabores())) })
    render(
      <StrictMode>
        <MemoryRouter initialEntries={['/familias/7']}>
          <Routes>
            <Route path="/familias/:id" element={<Familia />} />
          </Routes>
        </MemoryRouter>
      </StrictMode>,
    )
    await screen.findByText('Vainilla')

    await act(async () => {
      primera.resolver(detalleSabores({ nombre: 'Vieja' }))
    })

    expect(screen.getByRole('heading', { name: 'Familia "Sabores"' })).toBeInTheDocument()
    expect(screen.queryByText('Familia "Vieja"')).not.toBeInTheDocument()
  })

  function montarBajoStrictMode() {
    render(
      <StrictMode>
        <MemoryRouter initialEntries={['/familias/7']}>
          <Routes>
            <Route path="/familias/:id" element={<Familia />} />
          </Routes>
        </MemoryRouter>
      </StrictMode>,
    )
  }

  /** Cláusula bajo prueba: la generación en la rama de fallo de `cargar`. Los tres brazos de la misma compuerta (éxito,
   * fallo y `finally`) llevan cada uno su prueba (mutation-proof-tests regla 15): el fallo de la lectura superada no
   * se muestra mientras la vigente sigue en vuelo. */
  it('bajo StrictMode, el fallo de la primera lectura no se muestra mientras la segunda sigue en vuelo', async () => {
    const segunda = diferida<FamiliaDetalle>()
    mockearApi({
      detalleImpl: (llamada) => (llamada === 1 ? Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó la primera.')) : segunda.promesa),
    })
    montarBajoStrictMode()
    await act(async () => {})

    expect(screen.queryByText('Se cayó la primera.')).not.toBeInTheDocument()
    await act(async () => {
      segunda.resolver(detalleSabores())
    })
    expect(await screen.findByText('Vainilla')).toBeInTheDocument()
    expect(screen.queryByText('Se cayó la primera.')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la generación en el `finally` de `cargar` (regla 4 de react-async-state). */
  it('bajo StrictMode, la primera lectura que termina antes no apaga "Cargando…" de la segunda', async () => {
    const segunda = diferida<FamiliaDetalle>()
    mockearApi({ detalleImpl: (llamada) => (llamada === 1 ? Promise.resolve(detalleSabores({ nombre: 'Vieja' })) : segunda.promesa) })
    montarBajoStrictMode()
    await act(async () => {})

    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    expect(screen.queryByText('Vainilla')).not.toBeInTheDocument()
    await act(async () => {
      segunda.resolver(detalleSabores())
    })
    expect(await screen.findByText('Vainilla')).toBeInTheDocument()
    expect(screen.queryByText('Cargando…')).not.toBeInTheDocument()
  })

  it('"Volver al listado" lleva al listado', async () => {
    await montarYEsperar()

    await userEvent.click(screen.getByRole('link', { name: 'Volver al listado' }))

    expect(await screen.findByText('Pantalla del listado de familias')).toBeInTheDocument()
  })

  it('el aviso con el que se llegó (la familia recién creada) se muestra', async () => {
    mockearApi()
    montar({ pathname: '/familias/7', state: { aviso: 'Se creó la familia "Sabores" con 3 artículos.' } })

    expect(await screen.findByText('Se creó la familia "Sabores" con 3 artículos.')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el efecto de `DetalleDeFamilia` que deja la entrada del historial con `state: null`. El estado
   * de la navegación vive en el historial: sin esto el aviso de la creación reaparecía al recargar la pantalla o al volver
   * a ella. Evidencia de mutación (mutation-proof-tests): sacar ese efecto hace fallar este test (la entrada conserva el
   * aviso y la pantalla vuelta a montar con ella lo muestra de nuevo); revertido, vuelve a verde. Que la entrada se
   * reemplace y no se apile lo prueba la prueba siguiente. */
  it('el aviso con el que se llegó se consume: se ve una vez y la entrada del historial queda sin estado', async () => {
    mockearApi()
    const { unmount } = montar({ pathname: '/familias/7', state: { aviso: 'Se creó la familia "Sabores" con 3 artículos.' } })
    expect(await screen.findByText('Se creó la familia "Sabores" con 3 artículos.')).toBeInTheDocument()

    await waitFor(() => expect(ubicacionVigente.state).toBeNull())
    expect(ubicacionVigente.pathname).toBe('/familias/7')
    expect(screen.getByText('Se creó la familia "Sabores" con 3 artículos.')).toBeInTheDocument()

    // Recargar, o volver a la entrada: la pantalla se monta de nuevo con lo que el historial guarda.
    const entrada = { pathname: ubicacionVigente.pathname, state: ubicacionVigente.state }
    unmount()
    montar(entrada)
    await screen.findByText('Vainilla')

    expect(screen.queryByText('Se creó la familia "Sabores" con 3 artículos.')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `replace: true` de ese mismo efecto. Con un `push` la entrada con el aviso queda en el historial
   * y Atrás vuelve a ella en vez de volver a la pantalla desde la que se llegó. Evidencia de mutación
   * (mutation-proof-tests): quitar `replace: true` hace fallar este test (Atrás se queda en la familia y no llega al
   * listado); revertido, vuelve a verde. */
  it('el aviso con el que se llegó se consume reemplazando la entrada: Atrás vuelve a la pantalla desde la que se llegó', async () => {
    mockearApi()
    montar({ pathname: '/familias/7', state: { aviso: 'Se creó la familia "Sabores" con 3 artículos.' } }, ['/familias'])
    await screen.findByText('Vainilla')
    await waitFor(() => expect(ubicacionVigente.state).toBeNull())

    act(() => irAtras())

    expect(await screen.findByText('Pantalla del listado de familias')).toBeInTheDocument()
  })

  it('un id que no es un número no pide nada y lo dice', async () => {
    mockearApi()
    montar('/familias/abc')

    expect(await screen.findByText('No se especificó una familia válida.')).toBeInTheDocument()
    expect(apiGetMock.mock.calls.some((c) => (c[0] as string).startsWith('/familias'))).toBe(false)
  })

  it('una familia que no existe (404) muestra el mensaje del servidor y ningún detalle', async () => {
    mockearApi({ detalleImpl: () => Promise.reject(new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.')) })
    montar()

    expect(await screen.findByText('No existe la familia 7.')).toBeInTheDocument()
    expect(screen.queryByText('Artículos de la familia')).not.toBeInTheDocument()
  })

  it('un fallo que no es del servidor rinde el mensaje genérico', async () => {
    mockearApi({ detalleImpl: () => Promise.reject(new TypeError('Failed to fetch')) })
    montar()

    expect(await screen.findByText('No se pudo cargar la familia.')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el aviso de catálogos caídos dice lo que la pantalla de verdad hace: muestra el código en
   * vez del nombre (react-async-state regla 7). */
  it('si un catálogo no carga lo avisa y esos valores se muestran con su código', async () => {
    await montarYEsperar({ catalogosCaidos: ['areas'] })

    expect(await screen.findByText('No se pudieron cargar: áreas. Sus valores se muestran con el código en vez del nombre.')).toBeInTheDocument()
    expect(pares()[0]).toEqual(['Área', '#1'])
  })
})

describe('Familia — sacar un artículo', () => {
  function puerta() {
    return screen.getByRole('alertdialog', { name: 'Confirmar salida' })
  }

  it('"Sacar" no escribe: pide confirmar nombrando al artículo y diciendo que conserva sus valores y sus precios', async () => {
    await montarYEsperar()

    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))

    expect(puerta()).toHaveTextContent('¿Sacar el artículo "Vainilla" de la familia?')
    expect(puerta()).toHaveTextContent(
      'El artículo queda sin familia y conserva todos sus valores y sus precios; los demás artículos de la familia no cambian.',
    )
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  it('con la puerta abierta ningún otro "Sacar" ni el panel de agregado quedan alcanzables', async () => {
    await montarYEsperar()

    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))

    expect(screen.getByRole('button', { name: 'Sacar Frutilla de la familia' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Elegir artículos' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Confirmar salida' })).toBeEnabled()
  })

  it('"Cancelar" cierra la puerta sin llamar a la API', async () => {
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Sacar Frutilla de la familia' })).toBeEnabled()
  })

  it('confirmar saca a ese artículo: DELETE, aviso, y vuelve a leer la familia', async () => {
    await montarYEsperar({
      detalleImpl: (llamada) =>
        Promise.resolve(llamada === 1 ? detalleSabores() : detalleSabores({ articulos: detalleSabores().articulos.slice(1) })),
    })
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))

    expect(
      await screen.findByText('Se sacó el artículo "Vainilla" de la familia: conserva todos sus valores y sus precios.'),
    ).toBeInTheDocument()
    expect(apiDeleteMock).toHaveBeenCalledExactlyOnceWith('/familias/7/articulos/31')
    await waitFor(() => expect(screen.queryByText('Vainilla')).not.toBeInTheDocument())
    expect(screen.getByText('2 artículos')).toBeInTheDocument()
    // El que queda primero pasa a ser la referencia.
    expect(within(screen.getByText('Frutilla').closest('tr') as HTMLElement).getByText('Referencia')).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la ventana inerte cubre el DELETE y su refresco, y `ocupadoRef` descarta el segundo clic. */
  it('con el DELETE en vuelo todo queda inerte y dos clics sincrónicos emiten un solo DELETE', async () => {
    const borrado = diferida<void>()
    apiDeleteMock.mockReturnValue(borrado.promesa)
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))
    const confirmar = screen.getByRole('button', { name: 'Confirmar salida' })

    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: 'Sacando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Elegir artículos' })).toBeDisabled()

    await act(async () => {
      borrado.resolver()
    })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Sacar Frutilla de la familia' })).toBeEnabled())
  })

  it('un refresco fallido después de sacar no reporta la salida como fallida', async () => {
    await montarYEsperar({
      detalleImpl: (llamada) => (llamada === 1 ? Promise.resolve(detalleSabores()) : Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))),
    })
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))

    expect(
      await screen.findByText(
        'Se sacó el artículo "Vainilla" de la familia: conserva todos sus valores y sus precios. No se pudo actualizar la vista. Recargá la pantalla.',
      ),
    ).toBeInTheDocument()
    expect(screen.queryByText(/No se pudo sacar/)).not.toBeInTheDocument()
  })

  it('409 familia_cambio: muestra el rechazo, cierra la puerta y vuelve a leer la familia', async () => {
    apiDeleteMock.mockRejectedValue(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))

    expect(await screen.findByText('La pertenencia cambió.')).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    await waitFor(() => expect(lecturasDelDetalle()).toBe(2))
    expect(screen.getByRole('button', { name: 'Sacar Frutilla de la familia' })).toBeEnabled()
  })

  /** Cláusula bajo prueba: `!== 'inexistente'` del `catch` de `confirmarSalida`: si la relectura dice que la familia ya no
   * existe, ese mensaje cubre el rechazo de la salida y este no se repite al lado, sea cual sea el rechazo. Evidencia de
   * mutación (mutation-proof-tests): mostrar siempre el rechazo hace fallar los dos casos (con el 404 el mismo texto
   * aparece dos veces; con el 409 aparece el rechazo al lado); revertido, vuelven a verde. */
  it.each<[string, ErrorApi]>([
    ['404', new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.')],
    ['409 familia_cambio', new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.')],
  ])('%s y la familia ya no existe: deja de mostrar su detalle y dice una sola vez que no existe', async (_caso, rechazo) => {
    apiDeleteMock.mockRejectedValue(rechazo)
    await montarYEsperar({
      detalleImpl: (llamada) => (llamada === 1 ? Promise.resolve(detalleSabores()) : Promise.reject(new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.'))),
    })
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))

    await waitFor(() => expect(screen.queryByText('Artículos de la familia')).not.toBeInTheDocument())
    expect(screen.getAllByText('No existe la familia 7.')).toHaveLength(1)
    expect(screen.queryByText('La pertenencia cambió.')).not.toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: con la relectura que sale bien, el rechazo SÍ se muestra (la pantalla se actualizó y hay que
   * decir por qué la salida no se hizo). */
  it('404 pero la familia sigue existiendo (el artículo ya no está): relee la familia y muestra el rechazo una sola vez', async () => {
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el artículo 31.'))
    await montarYEsperar({
      detalleImpl: (llamada) => Promise.resolve(llamada === 1 ? detalleSabores() : detalleSabores({ articulos: detalleSabores().articulos.slice(1) })),
    })
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))

    await waitFor(() => expect(screen.queryByText('Vainilla')).not.toBeInTheDocument())
    expect(screen.getAllByText(/No existe el artículo 31\./)).toHaveLength(1)
    expect(lecturasDelDetalle()).toBe(2)
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `mensajeDeFalloDeEscritura` en la salida (react-async-state regla 6: el aviso dice lo que de
   * verdad se sabe). Con un 5xx o la red caída no se sabe si el DELETE llegó a commitear. Evidencia de mutación
   * (mutation-proof-tests): volver a `mensajeDeError` en `confirmarSalida` hace fallar estas dos; revertido, vuelven a
   * verde. */
  it.each<[string, () => unknown]>([
    ['un 5xx sin código estable', () => new ErrorApi(500, 'error_interno', 'Se cayó el servidor.')],
    ['la red caída', () => new TypeError('Failed to fetch')],
  ])('con %s no se sabe si la salida se hizo: lo dice, se muestra con la puerta abierta y no se anexa el texto del servidor', async (_caso, error) => {
    apiDeleteMock.mockRejectedValue(error())
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))

    const copia = 'No se pudo sacar el artículo de la familia. No se pudo confirmar el resultado: verificá el listado antes de reintentar.'
    expect(await screen.findByText(copia)).toBeInTheDocument()
    expect(screen.queryByText(/Se cayó el servidor/)).not.toBeInTheDocument()
    expect(puerta()).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirmar salida' })).toBeEnabled()
    expect(lecturasDelDetalle()).toBe(1)
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByText(copia)).not.toBeInTheDocument()
  })

  /** Cláusulas bajo prueba: cada acción que empieza borra lo que dijo la anterior (el aviso de éxito, el rechazo o el error
   * de la última lectura), para que no quede en pantalla un mensaje que ya no habla de lo que se está haciendo. */
  it('confirmar de nuevo borra el rechazo del intento anterior apenas empieza', async () => {
    const segundo = diferida<void>()
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(409, 'otro_rechazo', 'Se rechazó la salida.')).mockReturnValueOnce(segundo.promesa)
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))
    await screen.findByText('Se rechazó la salida.')

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))

    expect(apiDeleteMock).toHaveBeenCalledTimes(2)
    expect(screen.queryByText('Se rechazó la salida.')).not.toBeInTheDocument()
    await act(async () => {
      segundo.resolver()
    })
  })

  it('abrir la puerta borra el rechazo de la salida anterior', async () => {
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))
    await screen.findByText('La pertenencia cambió.')
    await waitFor(() => expect(lecturasDelDetalle()).toBe(2))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Sacar Frutilla de la familia' })).toBeEnabled())

    await userEvent.click(screen.getByRole('button', { name: 'Sacar Frutilla de la familia' }))

    expect(screen.queryByText('La pertenencia cambió.')).not.toBeInTheDocument()
  })

  it('abrir la puerta borra el aviso de la escritura anterior', async () => {
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))
    await screen.findByText(/Se sacó el artículo "Vainilla"/)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Sacar Frutilla de la familia' })).toBeEnabled())

    await userEvent.click(screen.getByRole('button', { name: 'Sacar Frutilla de la familia' }))

    expect(screen.queryByText(/Se sacó el artículo "Vainilla"/)).not.toBeInTheDocument()
  })

  it('el aviso con el que se llegó también se va al abrir la puerta', async () => {
    mockearApi()
    montar({ pathname: '/familias/7', state: { aviso: 'Se creó la familia "Sabores" con 3 artículos.' } })
    await screen.findByText('Vainilla')

    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))

    expect(screen.queryByText('Se creó la familia "Sabores" con 3 artículos.')).not.toBeInTheDocument()
  })

  /** Cláusulas bajo prueba: `!== 'inexistente'` del `catch` de `confirmarSalida`: con la relectura fallida por otra causa,
   * el rechazo de la salida se ve junto al fallo de la lectura; y `setError('')` en la rama de éxito de `cargar`: ese
   * error, que dejó el detalle viejo a la vista, lo borra la próxima lectura que sale bien. Evidencia de mutación
   * (mutation-proof-tests): mostrar el rechazo solo con la relectura aplicada (`=== 'aplicada'`) hace fallar el
   * `expect` del rechazo, y sacar `setError('')`, el último; revertidos, vuelve a verde. */
  it('con la relectura fallida se ven el rechazo de la salida y el fallo de la lectura, y una lectura que sale bien borra el segundo', async () => {
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.')).mockResolvedValueOnce(undefined)
    await montarYEsperar({
      detalleImpl: (llamada) =>
        llamada === 2 ? Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó la relectura.')) : Promise.resolve(detalleSabores()),
    })
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))
    await screen.findByText('Se cayó la relectura.')
    expect(await screen.findByText('La pertenencia cambió.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Frutilla de la familia' }))

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))

    await screen.findByText(/Se sacó el artículo "Frutilla"/)
    expect(screen.queryByText('Se cayó la relectura.')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `saliendo !== null` dentro de `bloqueadoPorLaPantalla`. La puerta ya se cerró, pero la relectura
   * posterior sigue en vuelo: el panel de agregado no puede habilitarse todavía. */
  it('durante la relectura posterior a sacar, el panel de agregado sigue inerte', async () => {
    const relectura = diferida<FamiliaDetalle>()
    await montarYEsperar({ detalleImpl: (llamada) => (llamada === 1 ? Promise.resolve(detalleSabores()) : relectura.promesa) })
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))
    await screen.findByText(/Se sacó el artículo "Vainilla"/)

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Elegir artículos' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Sacar Frutilla de la familia' })).toBeDisabled()

    await act(async () => {
      relectura.resolver(detalleSabores({ articulos: detalleSabores().articulos.slice(1) }))
    })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Elegir artículos' })).toBeEnabled())
  })

  /** Cláusula bajo prueba: el `await cargar(...)` del `catch` de `confirmarSalida`: la ventana inerte cubre también la
   * relectura que sigue a un rechazo (react-async-state regla 5). Evidencia de mutación (mutation-proof-tests): sacar ese
   * `await` hace fallar este test (el panel se habilita con la relectura todavía en vuelo); revertido, vuelve a verde. */
  it('durante la relectura posterior a un rechazo de la salida, el panel de agregado sigue inerte', async () => {
    const relectura = diferida<FamiliaDetalle>()
    apiDeleteMock.mockRejectedValue(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    await montarYEsperar({ detalleImpl: (llamada) => (llamada === 1 ? Promise.resolve(detalleSabores()) : relectura.promesa) })
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))
    await waitFor(() => expect(lecturasDelDetalle()).toBe(2))

    expect(screen.getByRole('button', { name: 'Elegir artículos' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Sacar Frutilla de la familia' })).toBeDisabled()

    await act(async () => {
      relectura.resolver(detalleSabores())
    })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Elegir artículos' })).toBeEnabled())
  })
})

/** Los pasos de elegir artículos para agregar a la familia, desde el buscador. */
async function elegirArticulos(...ids: number[]) {
  await userEvent.click(screen.getByRole('button', { name: 'Elegir artículos' }))
  const dialogo = await screen.findByRole('dialog', { name: 'Elegir artículos para agregar' })
  await userEvent.type(within(dialogo).getByLabelText('Buscar artículo'), 'art')
  await userEvent.click(within(dialogo).getByRole('button', { name: 'Buscar' }))
  for (const id of ids) await userEvent.click(await within(dialogo).findByLabelText(`Elegir A00${id} Articulo ${id}`))
  await userEvent.click(within(dialogo).getByRole('button', { name: 'Listo' }))
}

describe('Familia — agregar artículos', () => {
  it('explica que los artículos toman de la referencia sus campos compartidos y sus precios', async () => {
    await montarYEsperar()

    expect(screen.getByText(/Los artículos que sumes toman de la referencia \(A0031 — Vainilla\)/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeDisabled()
    expect(screen.getByText('Todavía no elegiste ningún artículo.')).toBeInTheDocument()
  })

  it('una familia inactiva no admite artículos: lo dice y no ofrece nada', async () => {
    await montarYEsperar({ detalle: detalleSabores({ activo: false }) })

    expect(screen.getByText(/La familia está inactiva: no admite artículos nuevos\. Activala desde el listado de familias/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Elegir artículos' })).not.toBeInTheDocument()
  })

  it('una familia sin miembros vivos no tiene referencia: lo dice y no ofrece nada', async () => {
    mockearApi({ detalle: detalleSabores({ articulos: [], valores: null, precios: [] }) })
    montar()

    expect(await screen.findByText(/La familia no tiene artículos vivos: no hay un artículo de referencia al que alinear/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Elegir artículos' })).not.toBeInTheDocument()
  })

  it('elegir artículos los muestra como elegidos, y se pueden quitar uno por uno', async () => {
    await montarYEsperar()

    await elegirArticulos(40, 41)

    expect(screen.getByText('A0040 — Articulo 40')).toBeInTheDocument()
    expect(screen.getByText('A0041 — Articulo 41')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeEnabled()

    await userEvent.click(screen.getByRole('button', { name: 'Quitar A0040 Articulo 40' }))
    expect(screen.queryByText('A0040 — Articulo 40')).not.toBeInTheDocument()
    expect(screen.getByText('A0041 — Articulo 41')).toBeInTheDocument()
  })

  it('el buscador no deja elegir a los miembros de esta familia ni a los de otra', async () => {
    await montarYEsperar({ busqueda: [articulo(40, { idFamilia: 7 }), articulo(41, { idFamilia: 9 }), articulo(42)] })
    await userEvent.click(screen.getByRole('button', { name: 'Elegir artículos' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Elegir artículos para agregar' })
    await userEvent.type(within(dialogo).getByLabelText('Buscar artículo'), 'art')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Buscar' }))

    expect(await within(dialogo).findByLabelText('Elegir A0040 Articulo 40')).toBeDisabled()
    expect(within(dialogo).getByText('Ya es miembro de esta familia')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Elegir A0041 Articulo 41')).toBeDisabled()
    expect(within(dialogo).getByText('Ya está en una familia')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Elegir A0042 Articulo 42')).toBeEnabled()
  })

  it('"Previsualizar" manda la referencia de la familia (el primer miembro) y los artículos elegidos, y muestra qué cambia', async () => {
    apiPostMock.mockResolvedValue(
      previsualizacionDe({
        articulos: [
          cambiosDe(40, { campos: ['costo_lista'], actual: { ...referencia, costoLista: 90 }, nuevo: referencia }),
          cambiosDe(41),
        ],
      }),
    )
    await montarYEsperar()
    await elegirArticulos(40, 41)

    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledExactlyOnceWith('/familias/previsualizacion', { idArticuloReferencia: 31, idsArticulos: [40, 41] }))
    expect(await screen.findByRole('heading', { name: 'A0040 — Articulo 40' })).toBeInTheDocument()
    expect(screen.getByText('Ya está alineado con la referencia: no cambia nada.')).toBeInTheDocument()
    const fila = screen.getByText('Costo de lista', { selector: 'td' }).closest('tr') as HTMLElement
    expect(within(fila).getAllByRole('cell').map((c) => c.textContent)).toEqual(['Costo de lista', '$ 90,00', '$ 150,00'])
  })

  it('"Confirmar y agregar" solo aparece con una previsualización, y se puede apretar cuando no tiene problemas', async () => {
    apiPostMock.mockResolvedValue(previsualizacionDe({ articulos: [cambiosDe(40)] }))
    await montarYEsperar()
    await elegirArticulos(40)
    expect(screen.queryByRole('button', { name: 'Confirmar y agregar' })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

    expect(await screen.findByRole('button', { name: 'Confirmar y agregar' })).toBeEnabled()
  })

  /** Cláusula bajo prueba: `!sinProblemas` en el `disabled` de "Confirmar y agregar": los problemas de la previsualización
   * bloquean la confirmación. `confirmar` no lo vuelve a mirar (solo tiene el espejo sincrónico contra el doble clic), así
   * que el botón deshabilitado es lo único que frena el POST. Evidencia de mutación (mutation-proof-tests): sacar
   * `|| !sinProblemas` del `disabled` hace fallar este test (el clic emite el POST); revertido, vuelve a verde. */
  it('los problemas de la previsualización se muestran y bloquean "Confirmar y agregar"', async () => {
    apiPostMock.mockResolvedValue(
      previsualizacionDe({
        articulos: [cambiosDe(40)],
        problemas: [{ codigo: 'articulo_en_otra_familia', mensaje: 'El artículo 41 ya está en la familia "Talles".', idArticulo: 41, idListaPrecio: null }],
      }),
    )
    await montarYEsperar()
    await elegirArticulos(40, 41)

    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

    expect(await screen.findByText(/El artículo 41 ya está en la familia "Talles"\. Agrupar no mueve a nadie de su familia/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirmar y agregar' })).toBeDisabled()
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar y agregar' }))
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('un fallo de la previsualización se muestra con la ayuda de su código', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'demasiados_articulos', 'Son demasiados pares.'))
    await montarYEsperar()
    await elegirArticulos(40)

    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

    expect(await screen.findByText('Son demasiados pares. Elegí menos artículos y agrupá en más de un paso.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar y agregar' })).not.toBeInTheDocument()
  })

  it('cambiar lo elegido descarta la previsualización: hay que volver a previsualizar antes de confirmar', async () => {
    apiPostMock.mockResolvedValue(previsualizacionDe({ articulos: [cambiosDe(40), cambiosDe(41)] }))
    await montarYEsperar()
    await elegirArticulos(40, 41)
    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))
    await screen.findByRole('button', { name: 'Confirmar y agregar' })

    await userEvent.click(screen.getByRole('button', { name: 'Quitar A0041 Articulo 41' }))

    expect(screen.queryByRole('button', { name: 'Confirmar y agregar' })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'A0040 — Articulo 40' })).not.toBeInTheDocument()
  })

  it('elegir más artículos desde el selector también descarta la previsualización', async () => {
    apiPostMock.mockResolvedValue(previsualizacionDe({ articulos: [cambiosDe(40), cambiosDe(41)] }))
    await montarYEsperar()
    await elegirArticulos(40, 41)
    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))
    await screen.findByRole('button', { name: 'Confirmar y agregar' })

    await elegirArticulos(42)

    expect(screen.queryByRole('button', { name: 'Confirmar y agregar' })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'A0040 — Articulo 40' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeEnabled()
  })

  it('confirmar agrega los artículos: POST con sus ids, aviso de lo que cambió, vuelve a leer la familia y limpia la selección', async () => {
    apiPostMock.mockImplementation((ruta: string) =>
      Promise.resolve(
        ruta === '/familias/previsualizacion'
          ? previsualizacionDe({ articulos: [cambiosDe(40, { campos: ['costo_lista'] }), cambiosDe(41)] })
          : resultadoDe([cambiosDe(40, { campos: ['costo_lista'] }), cambiosDe(41)]),
      ),
    )
    await montarYEsperar({
      detalleImpl: (llamada) =>
        Promise.resolve(
          llamada === 1
            ? detalleSabores()
            : detalleSabores({
                articulos: [...detalleSabores().articulos, { id: 40, codigoInterno: 'A0040', nombre: 'Articulo 40', idMarca: null, activo: true }],
              }),
        ),
    })
    await elegirArticulos(40, 41)
    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

    await userEvent.click(await screen.findByRole('button', { name: 'Confirmar y agregar' }))

    expect(
      await screen.findByText('Se agregaron 2 artículos a la familia "Sabores". 1 cambió sus valores o sus precios para igualar a la referencia.'),
    ).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledWith('/familias/7/articulos', { idsArticulos: [40, 41] })
    await waitFor(() => expect(screen.getByText('Articulo 40', { selector: 'td' })).toBeInTheDocument())
    expect(screen.getByText('4 artículos')).toBeInTheDocument()
    expect(screen.getByText('Todavía no elegiste ningún artículo.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar y agregar' })).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la ventana inerte cubre el POST y la relectura posterior (react-async-state regla 5), y
   * `confirmandoRef` descarta el segundo clic del mismo tick. */
  it('con el POST en vuelo todo queda inerte —también los "Sacar"— y dos clics sincrónicos emiten un solo POST', async () => {
    const escritura = diferida<ResultadoDeAgrupacion>()
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/familias/previsualizacion' ? Promise.resolve(previsualizacionDe({ articulos: [cambiosDe(40)] })) : escritura.promesa,
    )
    await montarYEsperar()
    await elegirArticulos(40)
    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))
    const confirmar = await screen.findByRole('button', { name: 'Confirmar y agregar' })

    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/familias/7/articulos')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Agregando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Elegir artículos' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar A0040 Articulo 40' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' })).toBeDisabled()

    await act(async () => {
      escritura.resolver(resultadoDe([cambiosDe(40)]))
    })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' })).toBeEnabled())
  })

  /** Cláusula bajo prueba: `await alAgregar(resultado)` dentro del `try` de `confirmar`: la ventana inerte cubre también la
   * relectura posterior a la escritura (react-async-state regla 5). Evidencia de mutación (mutation-proof-tests): sacar
   * ese `await` del `try` hace fallar este test (el panel se habilita antes de que la familia termine de releerse);
   * revertido, vuelve a verde. */
  it('la relectura posterior al POST también es ventana inerte: nada se habilita hasta que la familia terminó de releerse', async () => {
    const relectura = diferida<FamiliaDetalle>()
    apiPostMock.mockImplementation((ruta: string) =>
      Promise.resolve(ruta === '/familias/previsualizacion' ? previsualizacionDe({ articulos: [cambiosDe(40)] }) : resultadoDe([cambiosDe(40)])),
    )
    await montarYEsperar({ detalleImpl: (llamada) => (llamada === 1 ? Promise.resolve(detalleSabores()) : relectura.promesa) })
    await elegirArticulos(40)
    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Confirmar y agregar' }))
    await screen.findByText(/Se agregó 1 artículo a la familia "Sabores"/)

    expect(screen.getByRole('button', { name: 'Elegir artículos' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' })).toBeDisabled()

    await act(async () => {
      relectura.resolver(detalleSabores())
    })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Elegir artículos' })).toBeEnabled())
    expect(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' })).toBeEnabled()
  })

  it('si el servidor rechaza la confirmación muestra el motivo, descarta la previsualización y conserva lo elegido', async () => {
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/familias/previsualizacion'
        ? Promise.resolve(previsualizacionDe({ articulos: [cambiosDe(40)] }))
        : Promise.reject(new ErrorApi(409, 'articulo_en_otra_familia', 'El artículo 40 ya está en la familia "Talles".')),
    )
    await montarYEsperar()
    await elegirArticulos(40)
    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

    await userEvent.click(await screen.findByRole('button', { name: 'Confirmar y agregar' }))

    expect(
      await screen.findByText('El artículo 40 ya está en la familia "Talles". Agrupar no mueve a nadie de su familia: sacalo de la que tiene o dejalo fuera de la selección.'),
    ).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar y agregar' })).not.toBeInTheDocument()
    expect(screen.getByText('A0040 — Articulo 40')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeEnabled()
    expect(lecturasDelDetalle()).toBe(1)
  })

  it('un refresco fallido después de agregar no reporta el agregado como fallido', async () => {
    apiPostMock.mockImplementation((ruta: string) =>
      Promise.resolve(ruta === '/familias/previsualizacion' ? previsualizacionDe({ articulos: [cambiosDe(40)] }) : resultadoDe([cambiosDe(40)])),
    )
    await montarYEsperar({
      detalleImpl: (llamada) => (llamada === 1 ? Promise.resolve(detalleSabores()) : Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))),
    })
    await elegirArticulos(40)
    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

    await userEvent.click(await screen.findByRole('button', { name: 'Confirmar y agregar' }))

    expect(await screen.findByText('Se agregó 1 artículo a la familia "Sabores". No se pudo actualizar la vista. Recargá la pantalla.')).toBeInTheDocument()
    expect(screen.queryByText(/No se pudo agregar/)).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `invalidar` en el efecto de `[detalle]` y la generación de `usePrevisualizacion`: una
   * previsualización que llega después de que la familia cambió es de otro estado de la familia. */
  it('una previsualización que llega después de que la familia cambió no se aplica', async () => {
    const lectura = diferida<PrevisualizacionDeAgrupacion>()
    apiPostMock.mockReturnValue(lectura.promesa)
    await montarYEsperar({
      detalleImpl: (llamada) =>
        Promise.resolve(llamada === 1 ? detalleSabores() : detalleSabores({ articulos: detalleSabores().articulos.slice(1) })),
    })
    await elegirArticulos(40)
    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))
    await screen.findByRole('button', { name: 'Previsualizando…' })

    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))
    await waitFor(() => expect(screen.queryByText('Vainilla')).not.toBeInTheDocument())
    await act(async () => {
      lectura.resolver(previsualizacionDe({ articulos: [cambiosDe(40)] }))
    })

    expect(screen.queryByRole('heading', { name: 'A0040 — Articulo 40' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar y agregar' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeEnabled()
  })

  /** Cláusula bajo prueba: el tope de artículos por pedido en `excedido`: más de 100 no se previsualizan ni se mandan. */
  it('con más de 100 artículos elegidos avisa cuántos sobran y no deja previsualizar', async () => {
    const muchos = Array.from({ length: 101 }, (_, i) => articulo(500 + i))
    await montarYEsperar({ busqueda: muchos })
    await userEvent.click(screen.getByRole('button', { name: 'Elegir artículos' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Elegir artículos para agregar' })
    await userEvent.type(within(dialogo).getByLabelText('Buscar artículo'), 'art')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Buscar' }))
    await within(dialogo).findByLabelText('Elegir A00500 Articulo 500')
    for (const a of muchos) fireEvent.click(within(dialogo).getByLabelText(`Elegir ${a.codigoInterno} ${a.nombre}`))
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Listo' }))

    expect(screen.getByText('Se pueden agregar hasta 100 artículos por vez: quitá 1 para poder previsualizar.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeDisabled()

    await userEvent.click(screen.getByRole('button', { name: 'Quitar A00500 Articulo 500' }))
    expect(screen.queryByText(/Se pueden agregar hasta 100/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeEnabled()
  })

  it('"Cancelar" en el buscador lo cierra y no cambia lo elegido', async () => {
    await montarYEsperar()
    await elegirArticulos(40)
    await userEvent.click(screen.getByRole('button', { name: 'Elegir artículos' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Elegir artículos para agregar' })

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('A0040 — Articulo 40')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `previsualizando` dentro del `bloqueado` del panel. La previsualización en vuelo deja inertes sus
   * controles; los "Sacar" no, porque cambiar la familia es justo lo que la descarta (prueba de más arriba). */
  it('con la previsualización en vuelo los controles del panel quedan inertes', async () => {
    const lectura = diferida<PrevisualizacionDeAgrupacion>()
    apiPostMock.mockReturnValue(lectura.promesa)
    await montarYEsperar()
    await elegirArticulos(40)

    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

    expect(screen.getByRole('button', { name: 'Previsualizando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Elegir artículos' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar A0040 Articulo 40' })).toBeDisabled()
    await act(async () => {
      lectura.resolver(previsualizacionDe({ articulos: [cambiosDe(40)] }))
    })
    expect(await screen.findByRole('button', { name: 'Confirmar y agregar' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Elegir artículos' })).toBeEnabled()
  })

  /** Cláusulas bajo prueba: cada cambio de lo elegido, y volver a previsualizar, borran el motivo del rechazo anterior. */
  describe('después de que el servidor rechazó la confirmación', () => {
    const motivo = /El artículo 40 ya está en la familia "Talles"\./

    async function conRechazoVisible() {
      apiPostMock.mockImplementation((ruta: string) =>
        ruta === '/familias/previsualizacion'
          ? Promise.resolve(previsualizacionDe({ articulos: [cambiosDe(40)] }))
          : Promise.reject(new ErrorApi(409, 'articulo_en_otra_familia', 'El artículo 40 ya está en la familia "Talles".')),
      )
      await montarYEsperar()
      await elegirArticulos(40)
      await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))
      await userEvent.click(await screen.findByRole('button', { name: 'Confirmar y agregar' }))
      await screen.findByText(motivo)
    }

    it('quitar un artículo borra el motivo', async () => {
      await conRechazoVisible()

      await userEvent.click(screen.getByRole('button', { name: 'Quitar A0040 Articulo 40' }))

      expect(screen.queryByText(motivo)).not.toBeInTheDocument()
    })

    it('elegir otros artículos desde el buscador borra el motivo', async () => {
      await conRechazoVisible()

      await elegirArticulos(41)

      expect(screen.queryByText(motivo)).not.toBeInTheDocument()
    })

    it('volver a previsualizar borra el motivo apenas empieza', async () => {
      await conRechazoVisible()
      apiPostMock.mockReturnValue(new Promise(() => {}))

      await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

      expect(screen.getByRole('button', { name: 'Previsualizando…' })).toBeDisabled()
      expect(screen.queryByText(motivo)).not.toBeInTheDocument()
    })
  })

  /** Escenario completo, sin una cláusula propia: el rechazo de la salida anterior (que dejó la puerta cerrada) ya lo borró
   * `alEmpezarUnaAccion` cuando se eligió el artículo, mucho antes de que `alAgregar` corra; esa línea la mata la prueba
   * "el rechazo de una salida anterior también se va al empezar una acción del panel". */
  it('agregar con éxito borra el rechazo de una salida anterior', async () => {
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    apiPostMock.mockImplementation((ruta: string) =>
      Promise.resolve(ruta === '/familias/previsualizacion' ? previsualizacionDe({ articulos: [cambiosDe(40)] }) : resultadoDe([cambiosDe(40)])),
    )
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))
    await screen.findByText('La pertenencia cambió.')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Elegir artículos' })).toBeEnabled())
    await elegirArticulos(40)
    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

    await userEvent.click(await screen.findByRole('button', { name: 'Confirmar y agregar' }))

    await screen.findByText(/Se agregó 1 artículo a la familia "Sabores"/)
    expect(screen.queryByText('La pertenencia cambió.')).not.toBeInTheDocument()
  })
})

describe('Familia — "Volver al listado" no se alcanza con una puerta abierta ni con una escritura en vuelo', () => {
  async function esperarElEnlaceInerte() {
    const enlace = screen.getByRole('link', { name: 'Volver al listado' })
    expect(enlace).toHaveAttribute('aria-disabled', 'true')
    await userEvent.click(enlace)
    expect(screen.queryByText('Pantalla del listado de familias')).not.toBeInTheDocument()
  }

  /** Cláusula bajo prueba: `salida !== null` dentro de `bloqueado`: la puerta abierta deja inerte hasta el enlace.
   * Evidencia de mutación (mutation-proof-tests): sacar `aria-disabled` o el `preventDefault` del enlace hace fallar
   * este test y los dos siguientes; revertido, vuelven a verde. */
  it('con la puerta de la salida abierta el enlace no lleva a ningún lado', async () => {
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))

    await esperarElEnlaceInerte()

    expect(screen.getByRole('alertdialog', { name: 'Confirmar salida' })).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `saliendo !== null` dentro de `bloqueado`. La puerta ya se cerró, pero la relectura posterior
   * sigue en vuelo. Evidencia de mutación (mutation-proof-tests): sacar `saliendo !== null` de `bloqueado` hace fallar
   * este test; revertido, vuelve a verde. */
  it('durante la relectura posterior a sacar el enlace sigue inerte', async () => {
    const relectura = diferida<FamiliaDetalle>()
    await montarYEsperar({ detalleImpl: (llamada) => (llamada === 1 ? Promise.resolve(detalleSabores()) : relectura.promesa) })
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))
    await screen.findByText(/Se sacó el artículo "Vainilla"/)
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()

    await esperarElEnlaceInerte()

    await act(async () => {
      relectura.resolver(detalleSabores())
    })
    await waitFor(() => expect(screen.getByRole('link', { name: 'Volver al listado' })).not.toHaveAttribute('aria-disabled', 'true'))
  })

  /** Cláusula bajo prueba: `agregando` dentro de `bloqueado`. Evidencia de mutación (mutation-proof-tests): sacar
   * `agregando` de `bloqueado` hace fallar este test; revertido, vuelve a verde. */
  it('con el POST del agregado en vuelo el enlace sigue inerte, y al terminar vuelve a llevar al listado', async () => {
    const escritura = diferida<ResultadoDeAgrupacion>()
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/familias/previsualizacion' ? Promise.resolve(previsualizacionDe({ articulos: [cambiosDe(40)] })) : escritura.promesa,
    )
    await montarYEsperar()
    await elegirArticulos(40)
    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Confirmar y agregar' }))
    expect(await screen.findByRole('button', { name: 'Agregando…' })).toBeDisabled()

    await esperarElEnlaceInerte()

    await act(async () => {
      escritura.resolver(resultadoDe([cambiosDe(40)]))
    })
    await waitFor(() => expect(screen.getByRole('link', { name: 'Volver al listado' })).not.toHaveAttribute('aria-disabled', 'true'))
    await userEvent.click(screen.getByRole('link', { name: 'Volver al listado' }))
    expect(await screen.findByText('Pantalla del listado de familias')).toBeInTheDocument()
  })
})

describe('Familia — agregar artículos: cada acción del panel saca de pantalla lo que dijo la anterior', () => {
  const avisoDeLaSalida = /Se sacó el artículo "Frutilla"/

  /** Un artículo elegido y previsualizado, y después una salida que sale bien pero cuya relectura falla: la familia
   * que se ve es la de antes (lo previsualizado sigue en pie) y el aviso de la salida queda en pantalla. */
  async function conElAvisoDeUnaSalidaAnterior() {
    apiPostMock.mockImplementation((ruta: string) =>
      Promise.resolve(ruta === '/familias/previsualizacion' ? previsualizacionDe({ articulos: [cambiosDe(40)] }) : resultadoDe([cambiosDe(40)])),
    )
    await montarYEsperar({
      detalleImpl: (llamada) => (llamada === 1 ? Promise.resolve(detalleSabores()) : Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó la relectura.'))),
    })
    await elegirArticulos(40)
    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))
    await screen.findByRole('button', { name: 'Confirmar y agregar' })
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Frutilla de la familia' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))
    await screen.findByText(/No se pudo actualizar la vista/)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Confirmar y agregar' })).toBeEnabled())
    expect(screen.getByText(avisoDeLaSalida)).toBeInTheDocument()
  }

  /** Cláusulas bajo prueba: cada `alEmpezar()` del panel (`elegir`, `quitar`, `pedirPrevisualizacion` y `confirmar`) y el
   * `setAviso('')` de `alEmpezarUnaAccion` de la pantalla. Una prueba por hermana: sacar la llamada de una sola de las
   * cuatro deja el aviso en pantalla en esa y solo en esa. Evidencia de mutación (mutation-proof-tests): sacar cada
   * `alEmpezar()` hace fallar su caso; revertidos, vuelven a verde. */
  it.each([
    ['elegir otros artículos desde el buscador', () => elegirArticulos(41)],
    ['quitar un artículo elegido', () => userEvent.click(screen.getByRole('button', { name: 'Quitar A0040 Articulo 40' }))],
    ['volver a previsualizar', () => userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))],
    ['confirmar y agregar', () => userEvent.click(screen.getByRole('button', { name: 'Confirmar y agregar' }))],
  ])('%s borra el aviso de la escritura anterior', async (_accion, hacer) => {
    await conElAvisoDeUnaSalidaAnterior()
    apiPostMock.mockReturnValue(new Promise(() => {}))

    await hacer()

    expect(screen.queryByText(avisoDeLaSalida)).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `setErrorDeEscritura('')` de `alEmpezarUnaAccion`: el rechazo de una salida anterior (que dejó la
   * puerta cerrada y la familia releída) tampoco sobrevive a la acción siguiente. Evidencia de mutación
   * (mutation-proof-tests): sacar esa línea hace fallar este test; revertido, vuelve a verde. */
  it('el rechazo de una salida anterior también se va al empezar una acción del panel', async () => {
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    apiPostMock.mockReturnValue(new Promise(() => {}))
    await montarYEsperar()
    await elegirArticulos(40)
    await userEvent.click(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar salida' }))
    await screen.findByText('La pertenencia cambió.')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeEnabled())

    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

    expect(screen.queryByText('La pertenencia cambió.')).not.toBeInTheDocument()
  })
})

describe('Familia — agregar artículos: una previsualización cuyo artículo de referencia ya no es de esta familia', () => {
  const aviso = /La referencia de la previsualización no es la de esta familia/

  /** Cláusulas bajo prueba: `!referenciaDesactualizada` en `sinProblemas` y el efecto que vuelve a leer la familia. La
   * previsualización trae la familia a la que el servidor ve que pertenece el artículo de referencia que la pantalla le
   * mandó: si no es esta, lo que la pantalla muestra de la familia quedó viejo, así que no se puede confirmar y obliga a
   * releer. Evidencia de mutación (mutation-proof-tests): sacar `&& !referenciaDesactualizada`
   * habilita "Confirmar y agregar" con la relectura todavía en vuelo; sacar la llamada de `alRelerFamilia` deja la
   * familia sin releer; sacar el aviso deja sin explicar por qué se descartó lo previsualizado. Cada una hace fallar los
   * dos casos de esta tabla; revertidas, vuelven a verde. */
  it.each<[string, number | null]>([
    ['con el artículo de referencia en otra familia', 9],
    ['sin familia (el artículo de referencia ya no es miembro de ninguna)', null],
  ])('%s: bloquea "Confirmar y agregar", explica y vuelve a leer la familia', async (_caso, idFamilia) => {
    const relectura = diferida<FamiliaDetalle>()
    apiPostMock.mockResolvedValue(previsualizacionDe({ idFamilia, articulos: [cambiosDe(40)] }))
    await montarYEsperar({ detalleImpl: (llamada) => (llamada === 1 ? Promise.resolve(detalleSabores()) : relectura.promesa) })
    await elegirArticulos(40)

    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

    expect(await screen.findByText(aviso)).toBeInTheDocument()
    await waitFor(() => expect(lecturasDelDetalle()).toBe(2))
    expect(screen.getByRole('button', { name: 'Confirmar y agregar' })).toBeDisabled()

    await act(async () => {
      relectura.resolver(detalleSabores({ articulos: detalleSabores().articulos.slice(1) }))
    })

    expect(screen.queryByRole('button', { name: 'Confirmar y agregar' })).not.toBeInTheDocument()
    expect(screen.getByText(aviso)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeEnabled()
    expect(lecturasDelDetalle()).toBe(2)
  })

  it('una previsualización de la propia familia no relee nada ni avisa', async () => {
    apiPostMock.mockResolvedValue(previsualizacionDe({ idFamilia: 7, articulos: [cambiosDe(40)] }))
    await montarYEsperar()
    await elegirArticulos(40)

    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

    expect(await screen.findByRole('button', { name: 'Confirmar y agregar' })).toBeEnabled()
    expect(screen.queryByText(aviso)).not.toBeInTheDocument()
    expect(lecturasDelDetalle()).toBe(1)
  })
})

describe('Familia — agregar artículos: lo que el servidor rechaza al confirmar', () => {
  async function confirmarConRechazo(rechazo: unknown, escenario: Escenario = {}) {
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/familias/previsualizacion' ? Promise.resolve(previsualizacionDe({ articulos: [cambiosDe(40)] })) : Promise.reject(rechazo),
    )
    await montarYEsperar(escenario)
    await elegirArticulos(40)
    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Confirmar y agregar' }))
  }

  /** Cláusulas bajo prueba: cada uno de los tres términos de la condición que decide volver a leer la familia tras el
   * rechazo (`404`, `familia_inactiva`, `familia_sin_articulos`). Una prueba por término: lo que la pantalla muestra de la
   * familia dejó de ser cierto, y seguir mostrándolo ofrece agregar a algo que ya no lo admite. Evidencia de mutación
   * (mutation-proof-tests): sacar cualquiera de los tres términos hace fallar su caso; revertidos, vuelven a verde. */
  it.each([
    {
      caso: '404: la familia ya no existe',
      rechazo: new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.'),
      relectura: () => Promise.reject(new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.')),
      verificar: async () => {
        await waitFor(() => expect(screen.queryByText('Artículos de la familia')).not.toBeInTheDocument())
        expect(screen.getByText('No existe la familia 7.')).toBeInTheDocument()
      },
    },
    {
      caso: '409 familia_inactiva: la familia ya está inactiva',
      rechazo: new ErrorApi(409, 'familia_inactiva', 'La familia "Sabores" está inactiva: no se le pueden agregar artículos.'),
      relectura: () => Promise.resolve(detalleSabores({ activo: false })),
      verificar: async () => {
        expect(await screen.findByText(/La familia está inactiva: no admite artículos nuevos/)).toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'Elegir artículos' })).not.toBeInTheDocument()
      },
    },
    {
      caso: '409 familia_sin_articulos: la familia ya no tiene miembros vivos',
      rechazo: new ErrorApi(409, 'familia_sin_articulos', 'La familia "Sabores" no tiene artículos vivos: no hay un artículo de referencia al que alinear.'),
      relectura: () => Promise.resolve(detalleSabores({ articulos: [], valores: null, precios: [] })),
      verificar: async () => {
        expect(await screen.findByText(/La familia no tiene artículos vivos: no hay un artículo de referencia al que alinear/)).toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'Elegir artículos' })).not.toBeInTheDocument()
      },
    },
  ])('$caso: vuelve a leer la familia y muestra lo que ahora es cierto', async ({ rechazo, relectura, verificar }) => {
    await confirmarConRechazo(rechazo, { detalleImpl: (llamada) => (llamada === 1 ? Promise.resolve(detalleSabores()) : relectura()) })

    await waitFor(() => expect(lecturasDelDetalle()).toBe(2))
    await verificar()
  })

  /** Cláusula bajo prueba: el `await alRelerFamilia()` del `catch` de `confirmar`: la ventana inerte cubre también la
   * relectura que sigue a un rechazo (react-async-state regla 5). Evidencia de mutación (mutation-proof-tests): sacar ese
   * `await` hace fallar este test (la pantalla se habilita con la relectura todavía en vuelo); revertido, vuelve a verde. */
  it('durante la relectura posterior al rechazo la pantalla entera sigue inerte, también los "Sacar"', async () => {
    const relectura = diferida<FamiliaDetalle>()
    await confirmarConRechazo(new ErrorApi(409, 'familia_inactiva', 'La familia "Sabores" está inactiva: no se le pueden agregar artículos.'), {
      detalleImpl: (llamada) => (llamada === 1 ? Promise.resolve(detalleSabores()) : relectura.promesa),
    })
    await waitFor(() => expect(lecturasDelDetalle()).toBe(2))

    expect(screen.getByRole('button', { name: 'Elegir artículos' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' })).toBeDisabled()

    await act(async () => {
      relectura.resolver(detalleSabores({ activo: false }))
    })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Sacar Vainilla de la familia' })).toBeEnabled())
  })

  /** Cláusula bajo prueba: `mensajeDeFalloDeEscritura` en `confirmar`: con un 5xx o la red caída no se sabe si el POST
   * llegó a commitear. Evidencia de mutación (mutation-proof-tests): volver a `mensajeDeError` hace fallar estas dos;
   * revertido, vuelven a verde. */
  it.each<[string, () => unknown]>([
    ['un 5xx sin código estable', () => new ErrorApi(500, 'error_interno', 'Se cayó el servidor.')],
    ['la red caída', () => new TypeError('Failed to fetch')],
  ])('con %s no se sabe si se agregó: lo dice, y no vuelve a leer la familia', async (_caso, error) => {
    await confirmarConRechazo(error())

    expect(
      await screen.findByText('No se pudo agregar los artículos a la familia. No se pudo confirmar el resultado: verificá el listado antes de reintentar.'),
    ).toBeInTheDocument()
    expect(screen.queryByText(/Se cayó el servidor/)).not.toBeInTheDocument()
    expect(lecturasDelDetalle()).toBe(1)
  })
})
