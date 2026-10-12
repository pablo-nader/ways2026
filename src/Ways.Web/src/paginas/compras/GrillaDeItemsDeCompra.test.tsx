import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useRef, useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { aLineaSolicitada, lineaCompletaParaEnvio, normalizarFilas, type LineaDeCompraFormulario } from '../../api/lineaDeCompra'
import { ErrorApi } from '../../api/cliente'
import type { AlicuotaIvaListado, ArticuloListado, ItemDeCompra } from '../../api/tipos'
import { itemAFormulario } from '../../api/lineaDeCompra'
import { celdaDeItems, filaDeItems, filasDeItems } from './ayudasDePruebaDeGrilla'
import { GrillaDeItemsDeCompra } from './GrillaDeItemsDeCompra'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: vi.fn(),
    delete: vi.fn(),
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

const ALICUOTAS: AlicuotaIvaListado[] = [
  { id: 3, nombre: '21%', porcentaje: 21, codigoAfip: null, activo: true },
  { id: 4, nombre: '10,5%', porcentaje: 10.5, codigoAfip: null, activo: true },
]
const PORCENTAJES = { 3: 21, 4: 10.5 }
const AREA = { id: 4, nombre: 'Almacén', activo: true, orden: 1 }
const PROVEEDOR = { id: 1, razonSocial: 'Proveedor Uno SA', nombreFantasia: null, activo: true }
const OTRO_PROVEEDOR = { id: 2, razonSocial: 'Proveedor Dos SA', nombreFantasia: null, activo: true }

function articuloFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    publicadoWeb: false,
    tieneImagen: false,
    id: 20,
    codigoInterno: 'ART-20',
    nombre: 'Leche en polvo 800g',
    descripcion: null,
    idArea: 1,
    idCategoria: null,
    idMarca: null,
    idGrupo: null,
    idProveedorHabitual: null,
    idAlicuotaIva: 3,
    unidadVenta: 'Unidad',
    unidadesPorBulto: null,
    esProducto: true,
    costoLista: null,
    costoListaPor: null,
    costoListaIncluyeIva: null,
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

function itemFixture(sobrescribir: Partial<ItemDeCompra> = {}): ItemDeCompra {
  return {
    orden: 1,
    idArticulo: 10,
    descripcion: 'Fideos 500g',
    cantidad: 10,
    bultos: null,
    unidadesPorBulto: null,
    costoUnitario: 100,
    descuento: 50,
    idAlicuotaIva: 3,
    porcentajeIva: 21,
    total: 950,
    actualizaCosto: true,
    precioSugerido: null,
    codigoLote: null,
    fechaVencimiento: null,
    idLote: null,
    codigoProveedor: null,
    ...sobrescribir,
  }
}

function pagina(items: ArticuloListado[]) {
  return Promise.resolve({ items, total: items.length, pagina: 1, tamanio: 25 })
}

/** Respuestas del buscador por término (`busqueda=`), o por `termino@idProveedor` cuando difieren entre
 * proveedores: lo no previsto devuelve una página vacía. */
let respuestas: Record<string, () => Promise<unknown>> = {}

function terminosBuscados(): string[] {
  return apiGetMock.mock.calls
    .map((c: unknown[]) => c[0] as string)
    .filter((r) => r.startsWith('/articulos?busqueda='))
    .map((r) => decodeURIComponent(r.slice('/articulos?busqueda='.length).split('&')[0]))
}

function busquedasConProveedor(): string[] {
  return apiGetMock.mock.calls
    .map((c: unknown[]) => c[0] as string)
    .filter((r) => r.startsWith('/articulos?busqueda='))
    .map((r) => decodeURIComponent(r.slice('/articulos?busqueda='.length)))
}

type Captura = { lineas: LineaDeCompraFormulario[]; consultando: boolean[]; creando: boolean[] }

/** Arnés con el mismo manejo de estado que la pantalla: cambios y bajas pasan por `normalizarFilas`. */
function Arnes({
  captura,
  inicial = [],
  idProveedor: idProveedorInicial = 1,
  disabled = false,
  marcarFaltantes = false,
  discriminaIva = false,
  preciosFinales = false,
  puedeCrearArticulos = true,
}: {
  captura: Captura
  inicial?: LineaDeCompraFormulario[]
  idProveedor?: number | null
  disabled?: boolean
  marcarFaltantes?: boolean
  discriminaIva?: boolean
  preciosFinales?: boolean
  puedeCrearArticulos?: boolean
}) {
  const claveRef = useRef(100)
  const [idProveedor, setIdProveedor] = useState(idProveedorInicial)
  const [cambiosDeProveedor, setCambiosDeProveedor] = useState(0)
  const [lineas, setLineas] = useState(() => normalizarFilas(inicial, 0))
  captura.lineas = lineas

  function cambiar(clave: number, cambios: Partial<LineaDeCompraFormulario>) {
    const nueva = claveRef.current++
    setLineas((prev) => normalizarFilas(prev.map((l) => (l.clave === clave ? { ...l, ...cambios } : l)), nueva))
  }
  function quitar(clave: number) {
    const nueva = claveRef.current++
    setLineas((prev) => normalizarFilas(prev.filter((l) => l.clave !== clave), nueva))
  }

  return (
    <div>
      <GrillaDeItemsDeCompra
        lineas={lineas}
        alicuotas={ALICUOTAS}
        disabled={disabled}
        discriminaIva={discriminaIva}
        preciosFinales={preciosFinales}
        porcentajePorAlicuota={PORCENTAJES}
        idProveedor={idProveedor}
        puedeCrearArticulos={puedeCrearArticulos}
        marcarFaltantes={marcarFaltantes}
        cambiosDeProveedor={cambiosDeProveedor}
        onConsultando={(hay) => captura.consultando.push(hay)}
        onCreandoArticulo={(abierta) => captura.creando.push(abierta)}
        onCambio={cambiar}
        onQuitar={quitar}
      />
      <select
        aria-label="Proveedor de prueba"
        value={idProveedor ?? ''}
        onChange={(e) => {
          setIdProveedor(e.target.value === '' ? null : Number(e.target.value))
          setCambiosDeProveedor((n) => n + 1)
        }}
      >
        <option value="">Ninguno</option>
        <option value="1">Uno</option>
        <option value="2">Dos</option>
      </select>
      <button type="button" onClick={() => quitar(lineas[0].clave)}>
        Quitar la primera fila de prueba
      </button>
      <button type="button">Fuera de la grilla</button>
    </div>
  )
}

function montar(props: Partial<Parameters<typeof Arnes>[0]> = {}) {
  const captura: Captura = { lineas: [], consultando: [], creando: [] }
  const vista = render(<Arnes captura={captura} {...props} />)
  const enviadas = () => captura.lineas.filter(lineaCompletaParaEnvio).map(aLineaSolicitada)
  return { captura, enviadas, ...vista }
}

const campo = (fila: number, etiqueta: string) => celdaDeItems(fila, etiqueta)

async function escribirCodigoYSalir(usuario: ReturnType<typeof userEvent.setup>, fila: number, codigo: string) {
  await usuario.type(campo(fila, 'Código'), codigo)
  await usuario.tab()
}

async function abrirAlta(usuario: ReturnType<typeof userEvent.setup>) {
  await usuario.click(within(filaDeItems(0)).getByRole('button', { name: /^Crear artículo, línea \d+$/ }))
  const dialogo = await screen.findByRole('dialog', { name: 'Nuevo artículo' })
  // Los catálogos del formulario llegan después de que el diálogo se pinta: la opción es la única prueba de que aterrizaron.
  await within(dialogo).findByRole('option', { name: 'Almacén' })
  await within(dialogo).findByRole('option', { name: '21% (21%)' })
  await within(dialogo).findByRole('option', { name: 'Proveedor Uno SA' })
  return dialogo
}

/** No elige el Área: el alta la completa sola con la primera activa cuando llega el catálogo. */
async function guardarAlta(usuario: ReturnType<typeof userEvent.setup>, dialogo: HTMLElement) {
  await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  respuestas = {}
  apiGetMock.mockImplementation((ruta: string) => {
    const busqueda = /^\/articulos\?busqueda=([^&]+)(?:&idProveedor=(\d+))?/.exec(ruta)
    if (busqueda) {
      const termino = decodeURIComponent(busqueda[1])
      return (respuestas[`${termino}@${busqueda[2]}`] ?? respuestas[termino] ?? (() => pagina([])))()
    }
    if (ruta.startsWith('/catalogos/areas')) return Promise.resolve([AREA])
    if (/^\/catalogos\/(categorias|marcas|grupos)/.test(ruta)) return Promise.resolve([])
    if (ruta.startsWith('/proveedores')) return Promise.resolve({ items: [PROVEEDOR, OTRO_PROVEEDOR], total: 2, pagina: 1, tamanio: 200 })
    if (ruta === '/catalogos-fiscales/alicuotas-iva') return Promise.resolve(ALICUOTAS)
    if (ruta === '/empresas' || ruta === '/catalogos/listas-precio' || ruta === '/familias') return Promise.resolve([])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
})

describe('GrillaDeItemsDeCompra — estructura', () => {
  it('muestra las columnas Código, Detalle, UM, Cantidad, Importe, Descuento, IVA y Total, en ese orden', () => {
    montar()
    const encabezados = within(screen.getByRole('table', { name: 'Items de la compra' }))
      .getAllByRole('columnheader')
      .map((c) => c.textContent)
    expect(encabezados.slice(0, 8)).toEqual(['Código', 'Detalle', 'UM', 'Cantidad', 'Importe', 'Descuento', 'IVA', 'Total'])
  })

  it('siempre hay exactamente una fila vacía al final: al tipear en ella aparece otra, y al vaciarla vuelve a haber una sola', async () => {
    const usuario = userEvent.setup()
    montar()
    expect(filasDeItems()).toHaveLength(1)

    await usuario.type(campo(0, 'Cantidad'), '5')
    expect(filasDeItems()).toHaveLength(2)
    expect(campo(1, 'Cantidad')).toHaveValue(null)

    await usuario.type(campo(1, 'Código'), 'X')
    expect(filasDeItems()).toHaveLength(3)

    await usuario.clear(campo(1, 'Código'))
    expect(filasDeItems()).toHaveLength(2)
    await usuario.clear(campo(0, 'Cantidad'))
    expect(filasDeItems()).toHaveLength(1)
  })

  it('no se puede quitar la fila vacía y la fila con datos se quita con un botón fuera del orden de tabulación', async () => {
    const usuario = userEvent.setup()
    const { captura } = montar()
    expect(within(filaDeItems(0)).queryByRole('button', { name: /^Quitar línea \d+$/ })).not.toBeInTheDocument()

    await usuario.type(campo(0, 'Cantidad'), '5')
    const quitar = within(filaDeItems(0)).getByRole('button', { name: /^Quitar línea \d+$/ })
    expect(quitar).toHaveAttribute('tabindex', '-1')

    await usuario.click(quitar)
    expect(filasDeItems()).toHaveLength(1)
    expect(captura.lineas.every((l) => l.cantidad === '')).toBe(true)
  })

  it('Tab recorre las celdas en orden de lectura y pasa de fila sin detenerse en el botón de quitar; Shift+Tab vuelve', async () => {
    const usuario = userEvent.setup()
    montar()
    await usuario.type(campo(0, 'Código'), 'X')
    // la fila 0 es un concepto: su UM está deshabilitada y se salta
    expect(campo(0, 'UM')).toBeDisabled()

    await usuario.click(campo(0, 'Código'))
    const recorrido = ['Detalle', 'Cantidad', 'Importe', 'Descuento (%)', 'IVA']
    for (const etiqueta of recorrido) {
      await usuario.tab()
      expect(campo(0, etiqueta)).toHaveFocus()
    }
    await usuario.tab()
    expect(campo(1, 'Código')).toHaveFocus()

    await usuario.tab({ shift: true })
    expect(campo(0, 'IVA')).toHaveFocus()
    for (const etiqueta of [...recorrido].reverse().slice(1)) {
      await usuario.tab({ shift: true })
      expect(campo(0, etiqueta)).toHaveFocus()
    }
    await usuario.tab({ shift: true })
    expect(campo(0, 'Código')).toHaveFocus()
  })

  it('con un artículo que tiene bultos, la UM entra en el orden de tabulación entre Detalle y Cantidad', async () => {
    const usuario = userEvent.setup()
    montar({ inicial: [{ ...itemAFormulario(1, itemFixture()), unidadesPorBulto: 12, unidadVenta: 'Unidad' }] })

    await usuario.click(campo(0, 'Detalle'))
    await usuario.tab()
    expect(campo(0, 'UM')).toHaveFocus()
    await usuario.tab()
    expect(campo(0, 'Cantidad')).toHaveFocus()
  })

  it('Enter en una celda va al Código de la fila siguiente, sin tocar el ratón', async () => {
    const usuario = userEvent.setup()
    montar()
    await usuario.type(campo(0, 'Cantidad'), '5')

    await usuario.keyboard('{Enter}')
    expect(campo(1, 'Código')).toHaveFocus()

    await usuario.type(campo(1, 'Importe'), '9')
    await usuario.keyboard('{Enter}')
    expect(campo(2, 'Código')).toHaveFocus()
  })

  it('Enter en la última fila (la vacía) no se va a ninguna parte', async () => {
    const usuario = userEvent.setup()
    montar()
    await usuario.click(campo(0, 'Cantidad'))
    await usuario.keyboard('{Enter}')
    expect(campo(0, 'Cantidad')).toHaveFocus()
  })

  it('deshabilitada, ninguna celda se puede editar ni se puede quitar una fila', async () => {
    const usuario = userEvent.setup()
    montar({ inicial: [itemAFormulario(1, itemFixture())], disabled: true })

    expect(campo(0, 'Código')).toBeDisabled()
    expect(campo(0, 'Detalle')).toBeDisabled()
    expect(campo(0, 'Cantidad')).toBeDisabled()
    expect(campo(0, 'Importe')).toBeDisabled()
    expect(campo(0, 'Descuento (%)')).toBeDisabled()
    expect(campo(0, 'IVA')).toBeDisabled()
    expect(within(filaDeItems(0)).getByRole('button', { name: /^Quitar línea \d+$/ })).toBeDisabled()
    await usuario.click(campo(0, 'Cantidad'))
  })

  it('una fila con datos incompletos se marca con la clase de advertencia del tema, y solo marca sus celdas faltantes tras un intento de guardar', () => {
    const { rerender, captura } = montar({ inicial: [{ ...itemAFormulario(1, itemFixture()), importe: null }] })
    expect(filaDeItems(0)).toHaveClass('table-warning')
    expect(campo(0, 'Importe')).not.toHaveAttribute('aria-invalid')

    rerender(<Arnes captura={captura} inicial={[{ ...itemAFormulario(1, itemFixture()), importe: null }]} marcarFaltantes />)
    expect(campo(0, 'Importe')).toHaveAttribute('aria-invalid', 'true')
    expect(campo(0, 'Cantidad')).not.toHaveAttribute('aria-invalid')
    expect(filaDeItems(1)).not.toHaveClass('table-warning')
  })
})

describe('GrillaDeItemsDeCompra — UM, cantidad e importe', () => {
  const conBulto = () => ({ ...itemAFormulario(1, itemFixture({ descuento: 0 })), unidadesPorBulto: 12, unidadVenta: 'Unidad' as const })

  it('Bulto solo se puede elegir si el artículo trae unidades por bulto; un concepto o un artículo sin bulto es siempre Unidad', () => {
    montar({ inicial: [itemAFormulario(1, itemFixture()), conBulto(), itemAFormulario(3, itemFixture({ idArticulo: null, descripcion: 'Flete' }))] })

    expect(campo(0, 'UM')).toBeDisabled()
    expect(campo(1, 'UM')).toBeEnabled()
    expect(campo(1, 'UM')).toHaveValue('Unidad')
    expect(within(campo(1, 'UM')).getByRole('option', { name: 'Bulto' })).toBeEnabled()
    expect(campo(2, 'UM')).toBeDisabled()
    expect(campo(2, 'UM')).toHaveValue('Unidad')
  })

  it('con UM Bulto la cantidad son bultos y el importe el precio por bulto: viaja bultos, unidades por bulto y costo por unidad', async () => {
    const usuario = userEvent.setup()
    const { enviadas } = montar({ inicial: [conBulto()] })

    await usuario.selectOptions(campo(0, 'UM'), 'Bulto')
    await usuario.clear(campo(0, 'Cantidad'))
    await usuario.type(campo(0, 'Cantidad'), '2')
    await usuario.clear(campo(0, 'Importe'))
    await usuario.type(campo(0, 'Importe'), '120')

    expect(within(filaDeItems(0)).getByText('= 24 un. (12 por bulto)')).toBeInTheDocument()
    // 2 bultos × $120 = $240
    expect(within(filaDeItems(0)).getByText('$ 240,00')).toBeInTheDocument()
    expect(enviadas()[0]).toMatchObject({ unidades: 0, bultos: 2, unidadesPorBulto: 12, costoUnitario: 10, descuento: 0 })
  })

  it('un precio por bulto que no divide exacto viaja a 4 decimales; si el redondeo no cambia el bruto, el total coincide y no hay aviso', async () => {
    const usuario = userEvent.setup()
    const { enviadas } = montar({ inicial: [{ ...conBulto(), unidadesPorBulto: 3 }] })

    await usuario.selectOptions(campo(0, 'UM'), 'Bulto')
    await usuario.clear(campo(0, 'Cantidad'))
    await usuario.type(campo(0, 'Cantidad'), '1')
    await usuario.clear(campo(0, 'Importe'))
    await usuario.type(campo(0, 'Importe'), '100')

    expect(enviadas()[0].costoUnitario).toBe(33.3333)
    expect(within(filaDeItems(0)).getByText('$ 100,00')).toBeInTheDocument()
    expect(within(filaDeItems(0)).queryByText(/no se divide exacto/)).not.toBeInTheDocument()
  })

  it('si el costo por unidad redondeado hace que la línea no sume cantidad × importe, la fila avisa el total que se registra y la diferencia, sin bloquear', async () => {
    const usuario = userEvent.setup()
    const { enviadas } = montar({ inicial: [{ ...conBulto(), unidadesPorBulto: 3 }] })

    await usuario.selectOptions(campo(0, 'UM'), 'Bulto')
    await usuario.clear(campo(0, 'Cantidad'))
    await usuario.type(campo(0, 'Cantidad'), '1000')
    await usuario.clear(campo(0, 'Importe'))
    await usuario.type(campo(0, 'Importe'), '100')

    expect(
      within(filaDeItems(0)).getByText(
        'El precio por bulto no se divide exacto entre 3 unidades: la línea se registra por $ 99.999,90 (diferencia de $ 0,10).',
      ),
    ).toBeInTheDocument()
    // el Total es lo que viaja, no cantidad × importe (100.000,00)
    expect(filaDeItems(0).querySelector('[data-celda="total"]')).toHaveTextContent('$ 99.999,90')
    expect(enviadas()).toHaveLength(1)
    expect(enviadas()[0]).toMatchObject({ bultos: 1000, unidadesPorBulto: 3, costoUnitario: 33.3333 })
  })

  it('un bulto que divide exacto no muestra ningún aviso y el total es cantidad × importe', async () => {
    const usuario = userEvent.setup()
    montar({ inicial: [conBulto()] })

    await usuario.selectOptions(campo(0, 'UM'), 'Bulto')
    await usuario.clear(campo(0, 'Cantidad'))
    await usuario.type(campo(0, 'Cantidad'), '1000')
    await usuario.clear(campo(0, 'Importe'))
    await usuario.type(campo(0, 'Importe'), '120')

    expect(within(filaDeItems(0)).queryByText(/no se divide exacto/)).not.toBeInTheDocument()
    expect(filaDeItems(0).querySelector('[data-celda="total"]')).toHaveTextContent('$ 120.000,00')
  })

  it('una cantidad de bultos que da unidades fraccionadas en un artículo por unidad se acepta: el servidor no lo exige', async () => {
    const usuario = userEvent.setup()
    const { enviadas } = montar({ inicial: [{ ...conBulto(), unidadesPorBulto: 3 }] })

    await usuario.selectOptions(campo(0, 'UM'), 'Bulto')
    await usuario.clear(campo(0, 'Cantidad'))
    await usuario.type(campo(0, 'Cantidad'), '1.5')

    expect(campo(0, 'Cantidad')).not.toHaveClass('is-invalid')
    expect(enviadas()).toHaveLength(1)
    expect(enviadas()[0]).toMatchObject({ bultos: 1.5, unidadesPorBulto: 3 })
  })

  it('más de dos decimales de bultos se marcan (la base guarda dos) y con dos decimales se acepta', async () => {
    const usuario = userEvent.setup()
    const { enviadas } = montar({ inicial: [{ ...conBulto(), unidadVenta: 'Peso' as const }] })

    await usuario.selectOptions(campo(0, 'UM'), 'Bulto')
    await usuario.clear(campo(0, 'Cantidad'))
    await usuario.type(campo(0, 'Cantidad'), '1.234')
    expect(within(filaDeItems(0)).getByText('Los bultos admiten hasta 2 decimales.')).toBeInTheDocument()
    expect(enviadas()).toHaveLength(0)

    await usuario.clear(campo(0, 'Cantidad'))
    await usuario.type(campo(0, 'Cantidad'), '1.23')
    expect(within(filaDeItems(0)).queryByText('Los bultos admiten hasta 2 decimales.')).not.toBeInTheDocument()
    expect(enviadas()).toHaveLength(1)
  })

  it('una línea guardada solo de bultos se reabre como UM Bulto con el precio por bulto', () => {
    montar({ inicial: [itemAFormulario(1, itemFixture({ cantidad: 24, bultos: 2, unidadesPorBulto: 12, costoUnitario: 10, descuento: 0 }))] })

    expect(campo(0, 'UM')).toHaveValue('Bulto')
    expect(campo(0, 'Cantidad')).toHaveValue(2)
    expect(campo(0, 'Importe')).toHaveValue('120,0000')
  })

  it('una línea guardada que mezcla unidades sueltas y bultos se reabre como Unidad con la cantidad total y se reenvía igual si no se toca', () => {
    const { enviadas } = montar({
      inicial: [itemAFormulario(1, itemFixture({ cantidad: 20, bultos: 3, unidadesPorBulto: 6, costoUnitario: 10, descuento: 0 }))],
    })

    expect(campo(0, 'UM')).toHaveValue('Unidad')
    expect(campo(0, 'Cantidad')).toHaveValue(20)
    expect(enviadas()[0]).toMatchObject({ unidades: 2, bultos: 3, unidadesPorBulto: 6, costoUnitario: 10, descuento: 0 })
  })

  it('el descuento es un porcentaje del bruto de la línea y viaja como monto', async () => {
    const usuario = userEvent.setup()
    const { enviadas } = montar({ inicial: [itemAFormulario(1, itemFixture({ descuento: 0 }))] })

    await usuario.type(campo(0, 'Descuento (%)'), '12,5')

    // 10 × 100 = 1.000 de bruto; 12,5 % = 125
    expect(enviadas()[0].descuento).toBe(125)
    expect(within(filaDeItems(0)).getByText('$ 875,00')).toBeInTheDocument()
  })

  it('un descuento guardado se muestra como porcentaje y, sin tocarlo, viaja con el monto exacto guardado', () => {
    // 0,07 sobre 1.000 es 0,007 %: se muestra 0,01 %, pero recalcularlo daría 0,10.
    const { enviadas } = montar({ inicial: [itemAFormulario(1, itemFixture({ descuento: 0.07 }))] })

    expect(campo(0, 'Descuento (%)')).toHaveValue('0,01')
    expect(enviadas()[0].descuento).toBe(0.07)
  })

  it('un descuento mayor al 100 % deja la fila incompleta y lo dice', async () => {
    const usuario = userEvent.setup()
    const { enviadas } = montar({ inicial: [itemAFormulario(1, itemFixture({ descuento: 0 }))] })

    await usuario.type(campo(0, 'Descuento (%)'), '120')

    expect(within(filaDeItems(0)).getByText('No puede superar el 100 %.')).toBeInTheDocument()
    expect(enviadas()).toHaveLength(0)
  })

  it('un artículo por unidad no acepta fracciones: se marca y la fila queda incompleta', () => {
    const { enviadas } = montar({ inicial: [{ ...itemAFormulario(1, itemFixture()), unidadVenta: 'Unidad', cantidad: '2.5' }] })

    expect(campo(0, 'Cantidad')).toHaveClass('is-invalid')
    expect(screen.getByText('Este artículo se vende por unidad: la cantidad tiene que ser entera.')).toBeInTheDocument()
    expect(enviadas()).toHaveLength(0)
  })

  it('una línea cargada con actualizaCosto false lo conserva aunque se edite, lo avisa en la fila y no ofrece una columna para cambiarlo', async () => {
    const usuario = userEvent.setup()
    const { enviadas } = montar({
      inicial: [
        itemAFormulario(1, itemFixture({ actualizaCosto: false })),
        itemAFormulario(2, itemFixture({ idArticulo: 11, descripcion: 'Arroz', actualizaCosto: true })),
        itemAFormulario(3, itemFixture({ idArticulo: null, descripcion: 'Flete', actualizaCosto: true })),
      ],
    })

    expect(screen.queryByLabelText('Actualiza costo')).not.toBeInTheDocument()
    expect(within(filaDeItems(0)).getByText('No actualiza el costo del artículo')).toBeInTheDocument()
    expect(within(filaDeItems(1)).queryByText('No actualiza el costo del artículo')).not.toBeInTheDocument()
    expect(within(filaDeItems(2)).queryByText('No actualiza el costo del artículo')).not.toBeInTheDocument()
    expect(enviadas().map((l) => l.actualizaCosto)).toEqual([false, true, false])

    await usuario.clear(campo(0, 'Cantidad'))
    await usuario.type(campo(0, 'Cantidad'), '25')
    await usuario.clear(campo(0, 'Importe'))
    await usuario.type(campo(0, 'Importe'), '80')
    expect(enviadas()[0]).toMatchObject({ unidades: 25, costoUnitario: 80, actualizaCosto: false })
  })

  it('elegir otro artículo en esa fila manda actualizaCosto true y quita el aviso', async () => {
    respuestas['yerba'] = () => pagina([articuloFixture({ id: 40, nombre: 'Yerba 1kg', codigoInterno: 'ART-40' })])
    const usuario = userEvent.setup()
    const { enviadas } = montar({ inicial: [itemAFormulario(1, itemFixture({ actualizaCosto: false }))] })

    await usuario.clear(campo(0, 'Detalle'))
    await usuario.type(campo(0, 'Detalle'), 'yerba')
    await usuario.click(await screen.findByRole('option', { name: /Yerba 1kg/ }))

    expect(enviadas()[0]).toMatchObject({ idArticulo: 40, actualizaCosto: true })
    expect(within(filaDeItems(0)).queryByText('No actualiza el costo del artículo')).not.toBeInTheDocument()
  })
})

describe('GrillaDeItemsDeCompra — código del proveedor (T7)', () => {
  const POR_CODIGO = articuloFixture({
    id: 20,
    nombre: 'Leche en polvo 800g',
    codigoProveedor: 'AB-1',
    costoLista: 200,
    descuentoProveedor: 10,
    idAlicuotaIva: 4,
    unidadesPorBulto: 12,
    controlaLote: true,
  })

  it('al salir de un código encontrado, la fila se vuelve de artículo: Detalle de solo lectura, UM Unidad, importe de lista, descuento e IVA del artículo', async () => {
    respuestas['AB-1'] = () => pagina([POR_CODIGO])
    const usuario = userEvent.setup()
    const { enviadas } = montar()

    await escribirCodigoYSalir(usuario, 0, 'AB-1')

    await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche en polvo 800g'))
    expect(campo(0, 'Detalle')).toHaveAttribute('readonly')
    expect(campo(0, 'Detalle')).toHaveClass('bg-body-secondary')
    expect(campo(0, 'UM')).toHaveValue('Unidad')
    expect(campo(0, 'Importe')).toHaveValue('200,0000')
    expect(campo(0, 'Descuento (%)')).toHaveValue('10,00')
    expect(campo(0, 'IVA')).toHaveValue('4')
    expect(terminosBuscados()).toEqual(['AB-1'])
    expect(apiGetMock).toHaveBeenCalledWith('/articulos?busqueda=AB-1&idProveedor=1')

    // lista el lote, que este artículo controla, y deja cada celda editable salvo el Detalle
    expect(screen.getByLabelText(/^Fecha de vencimiento, línea \d+$/)).toBeInTheDocument()
    await usuario.type(campo(0, 'Cantidad'), '3')
    await usuario.type(screen.getByLabelText(/^Fecha de vencimiento, línea \d+$/), '01/12/2026')
    expect(enviadas()[0]).toMatchObject({
      idArticulo: 20,
      codigoProveedor: 'AB-1',
      unidades: 3,
      costoUnitario: 200,
      descuento: 60,
      idAlicuotaIva: 4,
      actualizaCosto: true,
    })
  })

  it('el foco sigue a la próxima celda editable: la UM si hay bultos, si no la Cantidad', async () => {
    respuestas['AB-1'] = () => pagina([POR_CODIGO])
    respuestas['CD-2'] = () => pagina([articuloFixture({ id: 21, codigoProveedor: 'CD-2' })])
    const usuario = userEvent.setup()
    montar()

    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await waitFor(() => expect(campo(0, 'UM')).toHaveFocus())

    await escribirCodigoYSalir(usuario, 1, 'CD-2')
    await waitFor(() => expect(campo(1, 'Cantidad')).toHaveFocus())
  })

  it('si el operador ya se fue a otra celda, la respuesta no le roba el foco', async () => {
    let resolver: (r: unknown) => void = () => {}
    respuestas['AB-1'] = () => new Promise((r) => (resolver = r))
    const usuario = userEvent.setup()
    montar()

    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await usuario.click(screen.getByRole('button', { name: 'Fuera de la grilla' }))
    await act(async () => {
      resolver({ items: [POR_CODIGO], total: 1, pagina: 1, tamanio: 25 })
    })

    await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche en polvo 800g'))
    expect(screen.getByRole('button', { name: 'Fuera de la grilla' })).toHaveFocus()
  })

  it('la coincidencia es exacta y sin distinguir mayúsculas: un resultado con otro código no cuenta', async () => {
    respuestas['ab-1'] = () =>
      pagina([
        articuloFixture({ id: 30, nombre: 'Otro', codigoProveedor: 'AB-10' }),
        articuloFixture({ id: 31, nombre: 'Sin código', codigoProveedor: null }),
        articuloFixture({ id: 32, nombre: 'El de este código', codigoProveedor: 'AB-1' }),
      ])
    const usuario = userEvent.setup()
    const { enviadas } = montar()

    await escribirCodigoYSalir(usuario, 0, 'ab-1')

    await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('El de este código'))
    await usuario.type(campo(0, 'Cantidad'), '1')
    await usuario.type(campo(0, 'Importe'), '5')
    expect(enviadas()[0].idArticulo).toBe(32)
  })

  it('un código que ningún artículo del proveedor tiene queda en la línea como pendiente de asociar y viaja con ella', async () => {
    respuestas['ZZ-9'] = () => pagina([articuloFixture({ id: 30, codigoProveedor: 'OTRO' })])
    const usuario = userEvent.setup()
    const { enviadas } = montar()

    await escribirCodigoYSalir(usuario, 0, 'ZZ-9')

    expect(await within(filaDeItems(0)).findByText('Pendiente de asociar')).toBeInTheDocument()
    expect(campo(0, 'Detalle')).not.toHaveAttribute('readonly')
    expect(campo(0, 'Detalle')).toHaveValue('')

    // se carga como concepto y el código viaja igual
    await usuario.type(campo(0, 'Detalle'), 'Cosa sin artículo')
    await usuario.type(campo(0, 'Cantidad'), '1')
    await usuario.type(campo(0, 'Importe'), '5')
    await usuario.selectOptions(campo(0, 'IVA'), '3')
    expect(enviadas()[0]).toMatchObject({ idArticulo: null, descripcion: 'Cosa sin artículo', codigoProveedor: 'ZZ-9' })
  })

  it('el código pendiente también viaja cuando la fila termina siendo un artículo elegido en el Detalle', async () => {
    respuestas['yerba'] = () => pagina([articuloFixture({ id: 40, nombre: 'Yerba 1kg', codigoInterno: 'ART-40' })])
    const usuario = userEvent.setup()
    const { enviadas } = montar()

    await escribirCodigoYSalir(usuario, 0, 'ZZ-9')
    await usuario.type(campo(0, 'Detalle'), 'yerba')
    await usuario.click(await screen.findByRole('option', { name: /Yerba 1kg/ }))
    await usuario.type(campo(0, 'Cantidad'), '1')
    await usuario.type(campo(0, 'Importe'), '5')

    expect(enviadas()[0]).toMatchObject({ idArticulo: 40, codigoProveedor: 'ZZ-9' })
    // el Detalle sigue editable: el artículo no salió del código
    expect(campo(0, 'Detalle')).not.toHaveAttribute('readonly')
  })

  it('sin proveedor no se consulta nada y la fila avisa por qué', async () => {
    const usuario = userEvent.setup()
    const { enviadas } = montar({ idProveedor: null })

    await escribirCodigoYSalir(usuario, 0, 'AB-1')

    expect(within(filaDeItems(0)).getByText('Elegí un proveedor para buscar el código.')).toBeInTheDocument()
    expect(terminosBuscados()).toEqual([])
    await usuario.type(campo(0, 'Detalle'), 'Flete')
    await usuario.type(campo(0, 'Cantidad'), '1')
    await usuario.type(campo(0, 'Importe'), '5')
    await usuario.selectOptions(campo(0, 'IVA'), '3')
    expect(enviadas()[0].codigoProveedor).toBe('AB-1')
  })

  it('elegir el proveedor después consulta las filas con código que habían quedado sin proveedor, y no antes', async () => {
    const usuario = userEvent.setup()
    montar({ idProveedor: null })
    await escribirCodigoYSalir(usuario, 0, 'AB-1')

    await usuario.selectOptions(screen.getByLabelText('Proveedor de prueba'), '1')
    await act(async () => {})

    // el cambio de proveedor SÍ consulta las filas con código (R5), pero no antes de elegirlo
    expect(terminosBuscados()).toEqual(['AB-1'])
  })

  it('volver a pasar por la celda sin cambiar el código no repite la consulta', async () => {
    respuestas['ZZ-9'] = () => pagina([])
    const usuario = userEvent.setup()
    montar()

    await escribirCodigoYSalir(usuario, 0, 'ZZ-9')
    await within(filaDeItems(0)).findByText('Pendiente de asociar')
    await usuario.click(campo(0, 'Código'))
    await usuario.tab()
    await act(async () => {})

    expect(terminosBuscados()).toEqual(['ZZ-9'])
  })

  it('no vuelve a consultar una fila ya resuelta por su código, ni pisa lo que el operador ajustó', async () => {
    respuestas['AB-1'] = () => pagina([POR_CODIGO])
    const usuario = userEvent.setup()
    montar()
    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await waitFor(() => expect(campo(0, 'Importe')).toHaveValue('200,0000'))

    await usuario.clear(campo(0, 'Importe'))
    await usuario.type(campo(0, 'Importe'), '150')
    await usuario.click(campo(0, 'Código'))
    await usuario.tab()
    await act(async () => {})

    expect(campo(0, 'Importe')).toHaveValue('150,0000')
    expect(terminosBuscados()).toEqual(['AB-1'])
  })

  it('editar el código de una fila resuelta por código libera el Detalle y vuelve a consultar con el código nuevo', async () => {
    respuestas['AB-1'] = () => pagina([POR_CODIGO])
    respuestas['AB-2'] = () => pagina([articuloFixture({ id: 50, nombre: 'Otro artículo', codigoProveedor: 'AB-2' })])
    const usuario = userEvent.setup()
    montar()
    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await waitFor(() => expect(campo(0, 'Detalle')).toHaveAttribute('readonly'))

    await usuario.clear(campo(0, 'Código'))
    expect(campo(0, 'Detalle')).not.toHaveAttribute('readonly')
    await usuario.type(campo(0, 'Código'), 'AB-2')
    await usuario.tab()

    await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Otro artículo'))
    expect(campo(0, 'Detalle')).toHaveAttribute('readonly')
    expect(terminosBuscados()).toEqual(['AB-1', 'AB-2'])
  })

  it('un artículo sin lista usa el costo nominal; sin costos visibles deja el importe vacío; sin alícuota válida usa el 21 %', async () => {
    respuestas['N-1'] = () => pagina([articuloFixture({ id: 61, codigoProveedor: 'N-1', costoNominal: 80 })])
    respuestas['S-1'] = () => pagina([articuloFixture({ id: 62, codigoProveedor: 'S-1', idAlicuotaIva: 99 })])
    const usuario = userEvent.setup()
    montar()

    await escribirCodigoYSalir(usuario, 0, 'N-1')
    await waitFor(() => expect(campo(0, 'Importe')).toHaveValue('80,0000'))
    expect(campo(0, 'Descuento (%)')).toHaveValue('')

    await escribirCodigoYSalir(usuario, 1, 'S-1')
    await waitFor(() => expect(campo(1, 'Detalle')).toHaveValue('Leche en polvo 800g'))
    expect(campo(1, 'Importe')).toHaveValue('')
    expect(campo(1, 'IVA')).toHaveValue('3')
  })

  it('si la consulta falla lo dice y vuelve a intentar al salir de la celda otra vez', async () => {
    let intentos = 0
    respuestas['AB-1'] = () => {
      intentos += 1
      return intentos === 1 ? Promise.reject(new ErrorApi(500, 'x', 'caído')) : pagina([POR_CODIGO])
    }
    const usuario = userEvent.setup()
    montar()

    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    expect(await within(filaDeItems(0)).findByText('No se pudo buscar el código.')).toBeInTheDocument()

    await usuario.click(campo(0, 'Código'))
    await usuario.tab()
    await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche en polvo 800g'))
    expect(within(filaDeItems(0)).queryByText('No se pudo buscar el código.')).not.toBeInTheDocument()
  })

  it('una fila que ya tiene artículo no consulta el código al salir: es solo el pendiente de asociar y no pisa precio ni actualizaCosto', async () => {
    respuestas['AB-1'] = () => pagina([POR_CODIGO])
    const usuario = userEvent.setup()
    const { enviadas } = montar({ inicial: [itemAFormulario(1, itemFixture({ actualizaCosto: false }))] })

    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await act(async () => {})

    expect(terminosBuscados()).toEqual([])
    expect(campo(0, 'Detalle')).toHaveValue('Fideos 500g')
    expect(campo(0, 'Importe')).toHaveValue('100,0000')
    expect(within(filaDeItems(0)).getByText('No actualiza el costo del artículo')).toBeInTheDocument()
    expect(enviadas()[0]).toMatchObject({ idArticulo: 10, codigoProveedor: 'AB-1', costoUnitario: 100, descuento: 50, actualizaCosto: false })
  })

  it('un IVA elegido mientras la consulta está en vuelo se conserva y el importe precargado se neta con ESE IVA', async () => {
    let resolver: (r: unknown) => void = () => {}
    respuestas['AB-1'] = () => new Promise((r) => (resolver = r))
    const usuario = userEvent.setup()
    montar({ discriminaIva: true })

    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await within(filaDeItems(0)).findByText('Buscando…')
    await usuario.selectOptions(campo(0, 'IVA'), '3')
    await act(async () => {
      resolver({ items: [{ ...POR_CODIGO, costoLista: 121, descuentoProveedor: null }], total: 1, pagina: 1, tamanio: 25 })
    })

    expect(campo(0, 'IVA')).toHaveValue('3')
    expect(campo(0, 'Importe')).toHaveValue('100,0000')
  })

  it('una consulta que nunca responde se da por fallida a los 15 s: la fila lo dice, guardar deja de esperar y el próximo blur reintenta', async () => {
    respuestas['AB-1'] = () => new Promise(() => {})
    const usuario = userEvent.setup()
    const { captura } = montar()
    await usuario.type(campo(0, 'Código'), 'AB-1')

    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    try {
      act(() => campo(0, 'Código').blur())
      expect(within(filaDeItems(0)).getByText('Buscando…')).toBeInTheDocument()
      expect(captura.consultando.at(-1)).toBe(true)

      await act(async () => {
        vi.advanceTimersByTime(14_999)
      })
      expect(within(filaDeItems(0)).getByText('Buscando…')).toBeInTheDocument()

      await act(async () => {
        vi.advanceTimersByTime(1)
      })
      expect(within(filaDeItems(0)).getByText('No se pudo buscar el código.')).toBeInTheDocument()
      expect(captura.consultando.at(-1)).toBe(false)

      respuestas['AB-1'] = () => pagina([POR_CODIGO])
      act(() => campo(0, 'Código').focus())
      await act(async () => campo(0, 'Código').blur())
      await act(async () => {})
      expect(campo(0, 'Detalle')).toHaveValue('Leche en polvo 800g')
    } finally {
      vi.useRealTimers()
    }
  })

  describe('una respuesta tardía nunca pisa una fila que ya cambió', () => {
    it('si el operador cambió el Detalle mientras la consulta estaba en vuelo, se descarta, no queda "Buscando…" y el código se vuelve a consultar al salir de la celda', async () => {
      const resolvers: ((r: unknown) => void)[] = []
      respuestas['AB-1'] = () => (resolvers.length === 0 ? new Promise((r) => resolvers.push(r)) : pagina([POR_CODIGO]))
      const usuario = userEvent.setup()
      montar()

      await escribirCodigoYSalir(usuario, 0, 'AB-1')
      await within(filaDeItems(0)).findByText('Buscando…')
      await usuario.type(campo(0, 'Detalle'), 'x')
      await act(async () => {
        resolvers[0]({ items: [POR_CODIGO], total: 1, pagina: 1, tamanio: 25 })
      })

      expect(campo(0, 'Detalle')).toHaveValue('x')
      expect(within(filaDeItems(0)).queryByText('Buscando…')).not.toBeInTheDocument()

      await usuario.click(campo(0, 'Código'))
      await usuario.tab()
      await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche en polvo 800g'))
      expect(terminosBuscados()).toEqual(['AB-1', 'AB-1'])
    })

    it('una respuesta que llega con la pantalla inerte (guardando) no escribe, no deja "Buscando…" y el código se reintenta después', async () => {
      const resolvers: ((r: unknown) => void)[] = []
      respuestas['AB-1'] = () => (resolvers.length === 0 ? new Promise((r) => resolvers.push(r)) : pagina([POR_CODIGO]))
      const usuario = userEvent.setup()
      const { rerender, captura } = montar()

      await escribirCodigoYSalir(usuario, 0, 'AB-1')
      await within(filaDeItems(0)).findByText('Buscando…')
      rerender(<Arnes captura={captura} disabled />)
      await act(async () => {
        resolvers[0]({ items: [POR_CODIGO], total: 1, pagina: 1, tamanio: 25 })
      })

      expect(campo(0, 'Detalle')).toHaveValue('')
      expect(within(filaDeItems(0)).queryByText('Buscando…')).not.toBeInTheDocument()
      expect(captura.consultando.at(-1)).toBe(false)

      rerender(<Arnes captura={captura} />)
      await usuario.click(campo(0, 'Código'))
      await usuario.tab()
      await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche en polvo 800g'))
    })

    it('editar otras celdas de la fila mientras la consulta está en vuelo NO la descarta: se aplica el artículo y se conserva lo tipeado', async () => {
      let resolver: (r: unknown) => void = () => {}
      respuestas['AB-1'] = () => new Promise((r) => (resolver = r))
      const usuario = userEvent.setup()
      const { captura } = montar()

      await escribirCodigoYSalir(usuario, 0, 'AB-1')
      await within(filaDeItems(0)).findByText('Buscando…')
      await usuario.type(campo(0, 'Cantidad'), '3')
      await usuario.type(campo(0, 'Importe'), '150')
      await act(async () => {
        resolver({ items: [POR_CODIGO], total: 1, pagina: 1, tamanio: 25 })
      })

      // el artículo se aplicó…
      expect(campo(0, 'Detalle')).toHaveValue('Leche en polvo 800g')
      expect(campo(0, 'Detalle')).toHaveAttribute('readonly')
      // …lo tipeado se respeta…
      expect(campo(0, 'Cantidad')).toHaveValue(3)
      expect(campo(0, 'Importe')).toHaveValue('150')
      // …Importe y Descuento son un par: tipeado uno, el artículo no precarga ninguno; el IVA vacío sí se precarga
      expect(campo(0, 'Descuento (%)')).toHaveValue('')
      expect(campo(0, 'IVA')).toHaveValue('4')
      expect(within(filaDeItems(0)).queryByText('Buscando…')).not.toBeInTheDocument()
      expect(captura.lineas[0]).toMatchObject({ idArticulo: 20, cantidad: '3', importe: 150, idAlicuotaIva: 4, descuentoPorcentaje: null })
    })

    it('avisa a la pantalla mientras hay consultas en vuelo, para que guardar espere', async () => {
      let resolver: (r: unknown) => void = () => {}
      respuestas['AB-1'] = () => new Promise((r) => (resolver = r))
      const usuario = userEvent.setup()
      const { captura } = montar()
      expect(captura.consultando).toEqual([])

      await escribirCodigoYSalir(usuario, 0, 'AB-1')
      await within(filaDeItems(0)).findByText('Buscando…')
      expect(captura.consultando).toEqual([true])

      await act(async () => {
        resolver({ items: [POR_CODIGO], total: 1, pagina: 1, tamanio: 25 })
      })
      expect(captura.consultando).toEqual([true, false])
    })

    it('si la fila se quitó mientras la consulta estaba en vuelo, no aparece ni se altera nada', async () => {
      let resolver: (r: unknown) => void = () => {}
      respuestas['AB-1'] = () => new Promise((r) => (resolver = r))
      const usuario = userEvent.setup()
      const { captura } = montar()

      await escribirCodigoYSalir(usuario, 0, 'AB-1')
      await usuario.click(within(filaDeItems(0)).getByRole('button', { name: /^Quitar línea \d+$/ }))
      await act(async () => {
        resolver({ items: [POR_CODIGO], total: 1, pagina: 1, tamanio: 25 })
      })

      expect(filasDeItems()).toHaveLength(1)
      expect(campo(0, 'Detalle')).toHaveValue('')
      expect(captura.lineas.some((l) => l.idArticulo !== '')).toBe(false)
    })

    it('si la fila se resolvió a otro artículo por búsqueda mientras tanto, gana el artículo elegido', async () => {
      let resolver: (r: unknown) => void = () => {}
      respuestas['AB-1'] = () => new Promise((r) => (resolver = r))
      respuestas['yerba'] = () => pagina([articuloFixture({ id: 40, nombre: 'Yerba 1kg', codigoInterno: 'ART-40' })])
      const usuario = userEvent.setup()
      const { enviadas } = montar()

      await escribirCodigoYSalir(usuario, 0, 'AB-1')
      await usuario.type(campo(0, 'Detalle'), 'yerba')
      await usuario.click(await screen.findByRole('option', { name: /Yerba 1kg/ }))
      await act(async () => {
        resolver({ items: [POR_CODIGO], total: 1, pagina: 1, tamanio: 25 })
      })

      expect(campo(0, 'Detalle')).toHaveValue('Yerba 1kg')
      await usuario.type(campo(0, 'Cantidad'), '1')
      await usuario.type(campo(0, 'Importe'), '5')
      expect(enviadas()[0].idArticulo).toBe(40)
    })

    it('una respuesta de otra consulta anterior de la misma fila tampoco gana', async () => {
      const resolvers: Record<string, (r: unknown) => void> = {}
      respuestas['AB-1'] = () => new Promise((r) => (resolvers['AB-1'] = r))
      respuestas['AB-2'] = () => pagina([articuloFixture({ id: 50, nombre: 'Otro artículo', codigoProveedor: 'AB-2' })])
      const usuario = userEvent.setup()
      montar()

      await escribirCodigoYSalir(usuario, 0, 'AB-1')
      await usuario.clear(campo(0, 'Código'))
      await usuario.type(campo(0, 'Código'), 'AB-2')
      await usuario.tab()
      await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Otro artículo'))

      await act(async () => {
        resolvers['AB-1']({ items: [POR_CODIGO], total: 1, pagina: 1, tamanio: 25 })
      })

      expect(campo(0, 'Detalle')).toHaveValue('Otro artículo')
    })

    it('si cambia el proveedor mientras la consulta está en vuelo, la respuesta del anterior se descarta y gana la del nuevo', async () => {
      const resolvers: Record<string, (r: unknown) => void> = {}
      respuestas['AB-1@1'] = () => new Promise((r) => (resolvers['1'] = r))
      respuestas['AB-1@2'] = () => new Promise((r) => (resolvers['2'] = r))
      const usuario = userEvent.setup()
      montar()

      await escribirCodigoYSalir(usuario, 0, 'AB-1')
      await usuario.selectOptions(screen.getByLabelText('Proveedor de prueba'), '2')
      await waitFor(() => expect(resolvers['2']).toBeDefined())
      await act(async () => {
        resolvers['1']({ items: [POR_CODIGO], total: 1, pagina: 1, tamanio: 25 })
      })
      expect(campo(0, 'Detalle')).toHaveValue('')
      expect(within(filaDeItems(0)).getByText('Buscando…')).toBeInTheDocument()

      await act(async () => {
        resolvers['2']({ items: [articuloFixture({ id: 51, nombre: 'Del proveedor dos', codigoProveedor: 'AB-1' })], total: 1, pagina: 1, tamanio: 25 })
      })
      expect(campo(0, 'Detalle')).toHaveValue('Del proveedor dos')
    })
  })
})

describe('GrillaDeItemsDeCompra — Detalle (T8)', () => {
  const LECHE = articuloFixture({ id: 20, nombre: 'Leche en polvo 800g', costoLista: 200, descuentoProveedor: 10, idAlicuotaIva: 4, unidadesPorBulto: 12 })

  it('Enter sobre la opción resaltada la elige y precarga la fila igual que un código encontrado', async () => {
    respuestas['leche'] = () => pagina([LECHE])
    const usuario = userEvent.setup()
    const { enviadas } = montar()

    await usuario.type(campo(0, 'Detalle'), 'leche')
    await screen.findByRole('option', { name: /Leche en polvo 800g/ })
    await usuario.keyboard('{ArrowDown}{Enter}')

    expect(campo(0, 'Detalle')).toHaveValue('Leche en polvo 800g')
    expect(campo(0, 'UM')).toHaveValue('Unidad')
    expect(campo(0, 'Importe')).toHaveValue('200,0000')
    expect(campo(0, 'Descuento (%)')).toHaveValue('10,00')
    expect(campo(0, 'IVA')).toHaveValue('4')
    // elegido por búsqueda: el Detalle sigue editable (se puede buscar otro)
    expect(campo(0, 'Detalle')).not.toHaveAttribute('readonly')
    expect(apiGetMock).toHaveBeenCalledWith('/articulos?busqueda=leche&idProveedor=1')

    await usuario.type(campo(0, 'Cantidad'), '2')
    expect(enviadas()[0]).toMatchObject({ idArticulo: 20, unidades: 2, costoUnitario: 200, descuento: 40, idAlicuotaIva: 4 })
  })

  describe('cambiar la UM convierte el Importe para conservar el costo por unidad', () => {
    async function precargar(usuario: ReturnType<typeof userEvent.setup>, costoLista: number) {
      respuestas['leche'] = () => pagina([{ ...LECHE, costoLista, descuentoProveedor: null, idAlicuotaIva: 3 }])
      await usuario.type(campo(0, 'Detalle'), 'leche')
      await screen.findByRole('option', { name: /Leche en polvo 800g/ })
      await usuario.keyboard('{ArrowDown}{Enter}')
    }

    it('un costo por unidad que no es redondo pasa a precio por bulto y el costo por unidad enviado se conserva', async () => {
      const usuario = userEvent.setup()
      const { enviadas } = montar()
      await precargar(usuario, 100.8333)
      expect(campo(0, 'Importe')).toHaveValue('100,8333')

      await usuario.selectOptions(campo(0, 'UM'), 'Bulto')
      expect(campo(0, 'Importe')).toHaveValue('1.209,9996')
      await usuario.type(campo(0, 'Cantidad'), '2')

      expect(enviadas()[0]).toMatchObject({ bultos: 2, unidadesPorBulto: 12, costoUnitario: 100.8333 })
      expect(within(filaDeItems(0)).queryByText(/no se divide exacto/)).not.toBeInTheDocument()
    })

    it('un costo por unidad redondo da un precio por bulto redondo y vuelve al cambiar de nuevo a Unidad', async () => {
      const usuario = userEvent.setup()
      const { enviadas } = montar()
      await precargar(usuario, 100)

      await usuario.selectOptions(campo(0, 'UM'), 'Bulto')
      expect(campo(0, 'Importe')).toHaveValue('1.200,0000')
      await usuario.type(campo(0, 'Cantidad'), '1')
      expect(enviadas()[0]).toMatchObject({ bultos: 1, costoUnitario: 100 })

      await usuario.selectOptions(campo(0, 'UM'), 'Unidad')
      expect(campo(0, 'Importe')).toHaveValue('100,0000')
      expect(campo(0, 'Cantidad')).toHaveValue(1)
      expect(enviadas()[0]).toMatchObject({ unidades: 1, bultos: null, costoUnitario: 100 })
    })
  })

  it('Tab sobre la opción resaltada la elige y el foco sigue a la celda siguiente', async () => {
    respuestas['leche'] = () => pagina([LECHE])
    const usuario = userEvent.setup()
    montar()

    await usuario.type(campo(0, 'Detalle'), 'leche')
    await screen.findByRole('option', { name: /Leche en polvo 800g/ })
    await usuario.keyboard('{ArrowDown}{Tab}')

    expect(campo(0, 'Importe')).toHaveValue('200,0000')
    // el artículo trae bultos: la UM es la próxima celda editable
    expect(campo(0, 'UM')).toHaveFocus()
  })

  it('Escape cierra la lista y deja el texto tipeado', async () => {
    respuestas['leche'] = () => pagina([LECHE])
    const usuario = userEvent.setup()
    montar()

    await usuario.type(campo(0, 'Detalle'), 'leche')
    await screen.findByRole('option', { name: /ART-20/ })
    await usuario.keyboard('{Escape}')

    expect(screen.queryByRole('option', { name: /ART-20/ })).not.toBeInTheDocument()
    expect(campo(0, 'Detalle')).toHaveValue('leche')
  })

  it('dejar el Detalle con texto libre y sin elegir nada lo manda como concepto con esa descripción', async () => {
    const usuario = userEvent.setup()
    const { enviadas } = montar()

    await usuario.type(campo(0, 'Detalle'), 'Flete a depósito')
    await usuario.tab()
    await usuario.type(campo(0, 'Cantidad'), '1')
    await usuario.type(campo(0, 'Importe'), '500')
    await usuario.selectOptions(campo(0, 'IVA'), '3')

    expect(enviadas()).toEqual([
      expect.objectContaining({ idArticulo: null, descripcion: 'Flete a depósito', actualizaCosto: false, bultos: null }),
    ])
  })

  it('escribir sobre el Detalle de un artículo ya elegido lo suelta: pasa a concepto y deja de ofrecer bultos y lote', async () => {
    const usuario = userEvent.setup()
    const { enviadas } = montar({
      inicial: [{ ...itemAFormulario(1, itemFixture({ codigoLote: 'L-1', fechaVencimiento: '2026-12-01' })), unidadesPorBulto: 12 }],
    })
    expect(screen.getByLabelText(/^Código de lote, línea \d+$/)).toBeInTheDocument()

    await usuario.type(campo(0, 'Detalle'), ' (revisado)')

    expect(campo(0, 'UM')).toBeDisabled()
    expect(screen.queryByLabelText(/^Código de lote, línea \d+$/)).not.toBeInTheDocument()
    expect(enviadas()[0]).toMatchObject({ idArticulo: null, descripcion: 'Fideos 500g (revisado)', codigoLote: null, bultos: null })
  })

  it('no hay "+" en una fila que ya tiene artículo, ni la vieja oferta de asociar el código', async () => {
    respuestas['leche'] = () => pagina([LECHE])
    const usuario = userEvent.setup()
    montar()
    expect(within(filaDeItems(0)).getByRole('button', { name: /^Crear artículo, línea \d+$/ })).toBeInTheDocument()

    await usuario.type(campo(0, 'Código'), 'ZZ-9')
    await usuario.type(campo(0, 'Detalle'), 'leche')
    await usuario.click(await screen.findByRole('option', { name: /Leche en polvo 800g/ }))

    expect(within(filaDeItems(0)).queryByRole('button', { name: /^Crear artículo, línea \d+$/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Asociar/ })).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  describe('crear un artículo desde la fila', () => {
    it('abre el formulario completo del ABM con lo que la fila ya tiene, todo editable', async () => {
      const usuario = userEvent.setup()
      montar()

      await usuario.type(campo(0, 'Código'), 'AB-1234')
      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      await usuario.type(campo(0, 'Importe'), '12,3456')
      await usuario.selectOptions(campo(0, 'IVA'), '4')
      const dialogo = await abrirAlta(usuario)

      expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Yerba nueva')
      expect(within(dialogo).getByLabelText('Código del proveedor')).toHaveValue('AB-1234')
      expect(within(dialogo).getByLabelText('Proveedor habitual')).toHaveValue('1')
      expect(within(dialogo).getByLabelText('Alícuota de IVA')).toHaveValue('4')
      expect(within(dialogo).getByLabelText('Costo de lista con IVA')).toHaveValue('12,35')
      expect(within(dialogo).getByLabelText('Costo por')).toHaveValue('Unidad')
      // lo que el alta reducida no tenía: es el formulario del ABM
      for (const etiqueta of [
        'Código interno',
        'Unidad de venta',
        'Unidades por bulto',
        'Descripción',
        'Categoría',
        'Marca',
        'Grupo',
        'Descuento (%)',
        'Controla lote / vencimiento',
        'Acumula en una sola línea al vender',
        'Disponible para todas las empresas del tenant',
      ]) {
        expect(within(dialogo).getByLabelText(etiqueta)).toBeEnabled()
      }
      expect(within(dialogo).getByRole('button', { name: 'Nuevo proveedor' })).toBeEnabled()
      expect(within(dialogo).getByText(/Guardá el artículo para poder cargar códigos de barra y precios/)).toBeInTheDocument()
      // sigue siendo editable
      await usuario.clear(within(dialogo).getByLabelText('Nombre'))
      await usuario.type(within(dialogo).getByLabelText('Nombre'), 'Yerba corregida')
      expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Yerba corregida')
    })

    it('al guardar manda el alta completa y la fila queda resuelta al artículo nuevo, con lo tipeado intacto', async () => {
      apiPostMock.mockResolvedValue(
        articuloFixture({ id: 77, codigoInterno: 'ART-77', nombre: 'Yerba nueva', idAlicuotaIva: 4, idProveedorHabitual: 1 }),
      )
      const usuario = userEvent.setup()
      const { enviadas } = montar()

      await usuario.type(campo(0, 'Código'), 'AB-1234')
      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      await usuario.type(campo(0, 'Importe'), '12,3456')
      await usuario.selectOptions(campo(0, 'IVA'), '4')
      const dialogo = await abrirAlta(usuario)
      expect(within(dialogo).getByLabelText('Área')).toHaveValue('4')
      await guardarAlta(usuario, dialogo)

      await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
      const [ruta, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
      expect(ruta).toBe('/articulos')
      expect(cuerpo).toMatchObject({
        nombre: 'Yerba nueva',
        idArea: 4,
        idAlicuotaIva: 4,
        idProveedorHabitual: 1,
        codigoProveedor: 'AB-1234',
        costoLista: 12.35,
        costoListaPor: 'Unidad',
        costoListaIncluyeIva: true,
        codigoInterno: null,
        unidadVenta: 'Unidad',
        controlaLote: false,
      })

      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      expect(campo(0, 'Detalle')).toHaveValue('Yerba nueva')
      // lo que el operador ya había tipeado en la fila se conserva
      expect(campo(0, 'Importe')).toHaveValue('12,3456')
      expect(campo(0, 'IVA')).toHaveValue('4')
      expect(campo(0, 'Código')).toHaveValue('AB-1234')
      expect(within(filaDeItems(0)).queryByRole('button', { name: /^Crear artículo, línea \d+$/ })).not.toBeInTheDocument()
      await waitFor(() => expect(campo(0, 'Cantidad')).toHaveFocus())

      await usuario.type(campo(0, 'Cantidad'), '2')
      expect(enviadas()[0]).toMatchObject({ idArticulo: 77, codigoProveedor: 'AB-1234', costoUnitario: 12.3456, unidades: 2 })
    })

    it('la fila toma la alícuota con la que se creó el artículo y conserva el importe y el descuento tipeados', async () => {
      apiPostMock.mockResolvedValue(articuloFixture({ id: 77, nombre: 'Yerba nueva', idAlicuotaIva: 4 }))
      const usuario = userEvent.setup()
      const { enviadas } = montar()

      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      await usuario.type(campo(0, 'Importe'), '12,3456')
      await usuario.type(campo(0, 'Descuento (%)'), '5')
      await usuario.selectOptions(campo(0, 'IVA'), '3')
      const dialogo = await abrirAlta(usuario)
      expect(within(dialogo).getByLabelText('Alícuota de IVA')).toHaveValue('3')
      await usuario.selectOptions(within(dialogo).getByLabelText('Alícuota de IVA'), '4')
      await guardarAlta(usuario, dialogo)

      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      expect((apiPostMock.mock.calls[0] as [string, Record<string, unknown>])[1]).toMatchObject({ idAlicuotaIva: 4 })
      expect(campo(0, 'IVA')).toHaveValue('4')
      expect(campo(0, 'Importe')).toHaveValue('12,3456')
      expect(campo(0, 'Descuento (%)')).toHaveValue('5,00')

      await usuario.type(campo(0, 'Cantidad'), '2')
      expect(enviadas()[0]).toMatchObject({ idArticulo: 77, idAlicuotaIva: 4, costoUnitario: 12.3456 })
    })

    it('un artículo creado con control de lote hace aparecer los campos de lote y uno con bultos habilita la UM Bulto', async () => {
      apiPostMock.mockResolvedValue(articuloFixture({ id: 77, nombre: 'Yogur', controlaLote: true, unidadesPorBulto: 6 }))
      const usuario = userEvent.setup()
      montar()

      await usuario.type(campo(0, 'Detalle'), 'Yogur')
      const dialogo = await abrirAlta(usuario)
      await usuario.click(within(dialogo).getByLabelText('Controla lote / vencimiento'))
      await usuario.type(within(dialogo).getByLabelText('Unidades por bulto'), '6')
      await guardarAlta(usuario, dialogo)

      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      expect((apiPostMock.mock.calls[0] as [string, Record<string, unknown>])[1]).toMatchObject({ controlaLote: true, unidadesPorBulto: 6 })
      expect(screen.getByLabelText(/^Fecha de vencimiento, línea \d+$/)).toBeInTheDocument()
      expect(campo(0, 'UM')).toBeEnabled()
    })

    it('si el operador corrigió el código en el alta, la fila queda con el código que se asoció', async () => {
      apiPostMock.mockResolvedValue(articuloFixture({ id: 77, nombre: 'Yerba nueva', idProveedorHabitual: 1 }))
      const usuario = userEvent.setup()
      const { captura } = montar()

      await usuario.type(campo(0, 'Código'), 'AB-1')
      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      const dialogo = await abrirAlta(usuario)
      await usuario.clear(within(dialogo).getByLabelText('Código del proveedor'))
      await usuario.type(within(dialogo).getByLabelText('Código del proveedor'), 'AB-2')
      await guardarAlta(usuario, dialogo)

      await waitFor(() => expect(campo(0, 'Código')).toHaveValue('AB-2'))
      expect(captura.lineas[0].codigoConsultado).toBe('AB-2')
    })

    it('si el operador quitó el proveedor habitual, el código no viaja y la fila conserva el suyo', async () => {
      apiPostMock.mockResolvedValue(articuloFixture({ id: 77, nombre: 'Yerba nueva', idProveedorHabitual: null }))
      const usuario = userEvent.setup()
      const { captura } = montar()

      await usuario.type(campo(0, 'Código'), 'AB-1')
      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      const dialogo = await abrirAlta(usuario)
      await usuario.selectOptions(within(dialogo).getByLabelText('Proveedor habitual'), '')
      await guardarAlta(usuario, dialogo)

      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
      expect(cuerpo).toMatchObject({ idProveedorHabitual: null, codigoProveedor: null })
      expect(campo(0, 'Código')).toHaveValue('AB-1')
      expect(captura.lineas[0].codigoConsultado).toBe('')
    })

    describe('el código de la fila es del proveedor de la compra', () => {
      it('con otro proveedor habitual el código no viaja, el campo queda de solo lectura y la fila lo conserva para asociarlo al confirmar', async () => {
        apiPostMock.mockResolvedValue(articuloFixture({ id: 77, nombre: 'Yerba nueva', idProveedorHabitual: 2 }))
        const usuario = userEvent.setup()
        const { captura } = montar()

        await usuario.type(campo(0, 'Código'), 'AB-1')
        await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
        const dialogo = await abrirAlta(usuario)
        expect(within(dialogo).getByLabelText('Código del proveedor')).toBeEnabled()
        await usuario.selectOptions(within(dialogo).getByLabelText('Proveedor habitual'), '2')

        expect(within(dialogo).getByLabelText('Código del proveedor')).toBeDisabled()
        expect(within(dialogo).getByLabelText('Código del proveedor')).toHaveValue('AB-1')
        expect(within(dialogo).getByText(/Este código es del proveedor de la compra/)).toBeInTheDocument()
        await guardarAlta(usuario, dialogo)

        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
        const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
        expect(cuerpo).toMatchObject({ idProveedorHabitual: 2, codigoProveedor: null })
        expect(campo(0, 'Código')).toHaveValue('AB-1')
        expect(captura.lineas[0].codigoConsultado).toBe('')
        expect(within(filaDeItems(0)).getByText('Se asociará al artículo al confirmar.')).toBeInTheDocument()
      })

      it('si el operador vuelve al proveedor de la compra, el campo se habilita y el código viaja', async () => {
        apiPostMock.mockResolvedValue(articuloFixture({ id: 77, nombre: 'Yerba nueva', idProveedorHabitual: 1 }))
        const usuario = userEvent.setup()
        const { captura } = montar()

        await usuario.type(campo(0, 'Código'), 'AB-1')
        await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
        const dialogo = await abrirAlta(usuario)
        await usuario.selectOptions(within(dialogo).getByLabelText('Proveedor habitual'), '2')
        await usuario.selectOptions(within(dialogo).getByLabelText('Proveedor habitual'), '1')

        expect(within(dialogo).getByLabelText('Código del proveedor')).toBeEnabled()
        expect(within(dialogo).queryByText(/Este código es del proveedor de la compra/)).not.toBeInTheDocument()
        await guardarAlta(usuario, dialogo)

        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
        const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
        expect(cuerpo).toMatchObject({ idProveedorHabitual: 1, codigoProveedor: 'AB-1' })
        expect(captura.lineas[0].codigoConsultado).toBe('AB-1')
      })
    })

    it('un artículo que entra a una familia desde la compra manda su idFamilia y la fila queda resuelta', async () => {
      const valores = {
        idArea: 4,
        idCategoria: null,
        idGrupo: null,
        idProveedorHabitual: 1,
        idAlicuotaIva: 3,
        unidadVenta: 'Unidad',
        unidadesPorBulto: null,
        esProducto: true,
        controlaLote: false,
        acumulaEnVenta: true,
        costoLista: 150,
        costoListaPor: 'Unidad',
        costoListaIncluyeIva: true,
        descuentoProveedor: null,
        costoNominal: 150,
      }
      const base = apiGetMock.getMockImplementation()!
      apiGetMock.mockImplementation((ruta: string) => {
        if (ruta === '/familias') return Promise.resolve([{ id: 7, nombre: 'Sabores', activo: true, cantidadArticulos: 3 }])
        if (ruta === '/familias/7') return Promise.resolve({ id: 7, nombre: 'Sabores', activo: true, articulos: [], valores, precios: [] })
        return base(ruta)
      })
      apiPostMock.mockResolvedValue(articuloFixture({ id: 77, nombre: 'Yerba nueva', idProveedorHabitual: 1, idFamilia: 7 }))
      const usuario = userEvent.setup()
      const { captura } = montar()

      await usuario.type(campo(0, 'Código'), 'AB-1')
      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      const dialogo = await abrirAlta(usuario)
      await usuario.selectOptions(await within(dialogo).findByLabelText('Familia (opcional)'), '7')
      await waitFor(() => expect(within(dialogo).getByLabelText(/^Costo de lista con IVA/)).toHaveValue('150,00'))
      await guardarAlta(usuario, dialogo)

      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
      expect(cuerpo).toMatchObject({ idFamilia: 7, idArea: 4, costoLista: 150, codigoProveedor: 'AB-1' })
      expect(captura.lineas[0].idArticulo).toBe(77)
    })

    it('con precios netos de un comprobante que discrimina IVA, el costo se guarda declarado sin IVA', async () => {
      apiPostMock.mockResolvedValue(articuloFixture({ id: 77, nombre: 'Yerba nueva' }))
      const usuario = userEvent.setup()
      montar({ discriminaIva: true, preciosFinales: false })

      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      await usuario.type(campo(0, 'Importe'), '100')
      const dialogo = await abrirAlta(usuario)
      expect(within(dialogo).getByLabelText('Costo de lista sin IVA')).toHaveValue('100,00')
      await guardarAlta(usuario, dialogo)

      await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
      const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
      expect(cuerpo).toMatchObject({ costoLista: 100, costoListaPor: 'Unidad', costoListaIncluyeIva: false })
    })

    it('con precios con IVA incluido de un comprobante que discrimina, el costo se guarda con IVA', async () => {
      apiPostMock.mockResolvedValue(articuloFixture({ id: 77, nombre: 'Yerba nueva' }))
      const usuario = userEvent.setup()
      montar({ discriminaIva: true, preciosFinales: true })

      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      await usuario.type(campo(0, 'Importe'), '121')
      const dialogo = await abrirAlta(usuario)
      expect(within(dialogo).getByLabelText('Costo de lista con IVA')).toHaveValue('121,00')
      await guardarAlta(usuario, dialogo)

      await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
      const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
      expect(cuerpo).toMatchObject({ costoLista: 121, costoListaPor: 'Unidad', costoListaIncluyeIva: true })
    })

    it('Ctrl+Enter en el Detalle abre el mismo alta, sin tocar el ratón', async () => {
      const usuario = userEvent.setup()
      montar()

      await usuario.type(campo(0, 'Detalle'), 'Cosa nueva')
      await usuario.keyboard('{Control>}{Enter}{/Control}')

      const dialogo = await screen.findByRole('dialog', { name: 'Nuevo artículo' })
      expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Cosa nueva')
    })

    it('cancelar el alta deja la fila como estaba y devuelve el foco al "+"', async () => {
      const usuario = userEvent.setup()
      montar()

      await usuario.type(campo(0, 'Detalle'), 'Cosa nueva')
      const dialogo = await abrirAlta(usuario)
      await usuario.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      expect(campo(0, 'Detalle')).toHaveValue('Cosa nueva')
      const mas = within(filaDeItems(0)).getByRole('button', { name: /^Crear artículo, línea \d+$/ })
      expect(mas).toHaveFocus()
      expect(apiPostMock).not.toHaveBeenCalled()
    })

    describe('cancelar un alta con cambios', () => {
      it('pide confirmar antes de descartar lo editado en el formulario, y si se rechaza el formulario sigue abierto', async () => {
        const confirmar = vi.spyOn(window, 'confirm').mockReturnValue(false)
        const usuario = userEvent.setup()
        montar()
        await usuario.type(campo(0, 'Detalle'), 'Cosa nueva')
        const dialogo = await abrirAlta(usuario)
        await usuario.type(within(dialogo).getByLabelText('Descripción'), 'algo')

        await usuario.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

        expect(confirmar).toHaveBeenCalledTimes(1)
        expect(screen.getByRole('dialog', { name: 'Nuevo artículo' })).toBeInTheDocument()
        expect(within(dialogo).getByLabelText('Descripción')).toHaveValue('algo')
        confirmar.mockRestore()
      })

      it('corregir solo el código del proveedor también cuenta como un cambio sin guardar', async () => {
        const confirmar = vi.spyOn(window, 'confirm').mockReturnValue(false)
        const usuario = userEvent.setup()
        montar()
        await usuario.type(campo(0, 'Código'), 'AB-1')
        await usuario.type(campo(0, 'Detalle'), 'Cosa nueva')
        const dialogo = await abrirAlta(usuario)
        await usuario.type(within(dialogo).getByLabelText('Código del proveedor'), '9')

        await usuario.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

        expect(confirmar).toHaveBeenCalledTimes(1)
        expect(screen.getByRole('dialog', { name: 'Nuevo artículo' })).toBeInTheDocument()
        confirmar.mockRestore()
      })

      it('editar solo el código del proveedor registra el aviso de salida de la pestaña, y un formulario intacto no', async () => {
        const usuario = userEvent.setup()
        montar()
        await usuario.type(campo(0, 'Código'), 'AB-1')
        await usuario.type(campo(0, 'Detalle'), 'Cosa nueva')
        const dialogo = await abrirAlta(usuario)
        const intentarSalir = () => {
          const evento = new Event('beforeunload', { cancelable: true })
          window.dispatchEvent(evento)
          return evento.defaultPrevented
        }
        expect(intentarSalir()).toBe(false)

        await usuario.type(within(dialogo).getByLabelText('Código del proveedor'), '9')

        expect(intentarSalir()).toBe(true)
      })

      it('si se acepta, cierra sin crear nada y la fila conserva todo lo tipeado en la compra', async () => {
        const confirmar = vi.spyOn(window, 'confirm').mockReturnValue(true)
        const usuario = userEvent.setup()
        const { captura } = montar()
        await usuario.type(campo(0, 'Código'), 'AB-1')
        await usuario.type(campo(0, 'Detalle'), 'Cosa nueva')
        await usuario.type(campo(0, 'Importe'), '7,5')
        const dialogo = await abrirAlta(usuario)
        await usuario.type(within(dialogo).getByLabelText('Descripción'), 'algo')

        await usuario.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

        expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
        expect(apiPostMock).not.toHaveBeenCalled()
        expect(campo(0, 'Código')).toHaveValue('AB-1')
        expect(campo(0, 'Detalle')).toHaveValue('Cosa nueva')
        expect(campo(0, 'Importe')).toHaveValue('7,5000')
        expect(captura.lineas[0].idArticulo).toBe('')
        confirmar.mockRestore()
      })
    })

    it('sin proveedor en la compra el código del alta queda deshabilitado con una pista', async () => {
      const usuario = userEvent.setup()
      montar({ idProveedor: null })

      await usuario.type(campo(0, 'Detalle'), 'Cosa nueva')
      await usuario.click(within(filaDeItems(0)).getByRole('button', { name: /^Crear artículo, línea \d+$/ }))
      const dialogo = await screen.findByRole('dialog', { name: 'Nuevo artículo' })
      await within(dialogo).findByRole('option', { name: 'Almacén' })

      expect(within(dialogo).getByLabelText('Proveedor habitual')).toHaveValue('')
      expect(within(dialogo).getByLabelText('Código del proveedor')).toBeDisabled()
      expect(within(dialogo).getByText('Elegí un proveedor habitual para cargar su código.')).toBeInTheDocument()
    })

    describe('un catálogo que no carga dentro de la compra', () => {
      function fallar(ruta: string) {
        const base = apiGetMock.getMockImplementation()!
        apiGetMock.mockImplementation((r: string) => (r.startsWith(ruta) ? Promise.reject(new Error('sin red')) : base(r)))
      }

      async function abrirConFallo() {
        const usuario = userEvent.setup()
        montar()
        await usuario.type(campo(0, 'Detalle'), 'Cosa nueva')
        await usuario.click(within(filaDeItems(0)).getByRole('button', { name: /^Crear artículo, línea \d+$/ }))
        return screen.findByRole('dialog', { name: 'Nuevo artículo' })
      }

      it('un catálogo requerido dice que se cierre y reabra el alta, y no que se recargue la página', async () => {
        fallar('/catalogos/areas')
        const dialogo = await abrirConFallo()

        expect(await within(dialogo).findByText(/cerrá el alta y volvé a abrirla para reintentar; no recargues la página: perderías la compra sin guardar\./)).toBeInTheDocument()
        expect(within(dialogo).queryByText(/recargá la página/i)).not.toBeInTheDocument()
      })

      it('las listas de precio que no cargan dan el mismo consejo', async () => {
        fallar('/catalogos/listas-precio')
        const dialogo = await abrirConFallo()

        expect(
          await within(dialogo).findByText(/No se pudieron cargar las listas de precio.*Cerrá el alta y volvé a abrirla para reintentar; no recargues la página/),
        ).toBeInTheDocument()
        expect(within(dialogo).queryByText(/recargá la página/i)).not.toBeInTheDocument()
      })
    })

    it('un 409 codigo_proveedor_duplicado se muestra en el formulario y la fila no cambia', async () => {
      apiPostMock.mockRejectedValue(new ErrorApi(409, 'codigo_proveedor_duplicado', 'Ese código ya pertenece a otro artículo del proveedor.'))
      const usuario = userEvent.setup()
      const { captura } = montar()

      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      const dialogo = await abrirAlta(usuario)
      await guardarAlta(usuario, dialogo)

      expect(await within(dialogo).findByText('Ese código ya pertenece a otro artículo del proveedor.')).toBeInTheDocument()
      expect(within(dialogo).getByLabelText('Nombre')).toBeEnabled()
      expect(within(dialogo).getByRole('button', { name: 'Guardar' })).toBeEnabled()
      expect(campo(0, 'Detalle')).toHaveValue('Yerba nueva')
      expect(captura.lineas[0].idArticulo).toBe('')
    })

    it('un doble clic síncrono en Guardar emite una sola request', async () => {
      let resolver: (a: ArticuloListado) => void = () => {}
      apiPostMock.mockImplementation(() => new Promise<ArticuloListado>((r) => (resolver = r)))
      const usuario = userEvent.setup()
      montar()

      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      const dialogo = await abrirAlta(usuario)
      await usuario.selectOptions(within(dialogo).getByLabelText('Área'), '4')

      const guardar = within(dialogo).getByRole('button', { name: 'Guardar' })
      act(() => {
        guardar.click()
        guardar.click()
      })
      expect(apiPostMock).toHaveBeenCalledTimes(1)

      await act(async () => {
        resolver(articuloFixture({ id: 77, nombre: 'Yerba nueva' }))
      })
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    })

    it('mientras el alta se está guardando no se puede cerrar el formulario', async () => {
      apiPostMock.mockImplementation(() => new Promise<ArticuloListado>(() => {}))
      const usuario = userEvent.setup()
      montar()

      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      const dialogo = await abrirAlta(usuario)
      await guardarAlta(usuario, dialogo)

      expect(within(dialogo).getByRole('button', { name: 'Cerrar' })).toBeDisabled()
      expect(within(dialogo).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
      await usuario.keyboard('{Escape}')
      expect(screen.getByRole('dialog', { name: 'Nuevo artículo' })).toBeInTheDocument()
    })

    it('avisa a la pantalla mientras el alta está abierta, para que no se pueda guardar la compra', async () => {
      const usuario = userEvent.setup()
      const { captura } = montar()
      expect(captura.creando.at(-1)).toBe(false)

      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      const dialogo = await abrirAlta(usuario)
      expect(captura.creando.at(-1)).toBe(true)

      await usuario.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))
      expect(captura.creando.at(-1)).toBe(false)
    })

    it('una respuesta que llega con el alta ya cerrada por la pantalla no toca la fila', async () => {
      let resolver: (a: ArticuloListado) => void = () => {}
      apiPostMock.mockImplementation(() => new Promise<ArticuloListado>((r) => (resolver = r)))
      const usuario = userEvent.setup()
      const { captura, rerender } = montar()

      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      const dialogo = await abrirAlta(usuario)
      await guardarAlta(usuario, dialogo)
      expect(apiPostMock).toHaveBeenCalledTimes(1)

      // la pantalla pierde el permiso con la request en vuelo: el alta se desmonta y la fila sigue ahí
      rerender(<Arnes captura={captura} puedeCrearArticulos={false} />)
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      // sin alta abierta la compra se puede guardar de nuevo
      expect(captura.creando.at(-1)).toBe(false)
      await act(async () => {
        resolver(articuloFixture({ id: 77, nombre: 'Yerba nueva' }))
      })

      expect(captura.lineas[0].idArticulo).toBe('')
      expect(captura.lineas[0].descripcion).toBe('Yerba nueva')
    })

    it('si la fila se quita con el alta en vuelo, la respuesta no crea ni resuelve ninguna fila', async () => {
      let resolver: (a: ArticuloListado) => void = () => {}
      apiPostMock.mockImplementation(() => new Promise<ArticuloListado>((r) => (resolver = r)))
      const usuario = userEvent.setup()
      const { captura } = montar()

      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      const dialogo = await abrirAlta(usuario)
      await guardarAlta(usuario, dialogo)
      await usuario.click(screen.getByRole('button', { name: 'Quitar la primera fila de prueba' }))
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

      await act(async () => {
        resolver(articuloFixture({ id: 77, nombre: 'Yerba nueva' }))
      })

      expect(filasDeItems()).toHaveLength(1)
      expect(captura.lineas.every((l) => l.idArticulo === '' && l.descripcion === '')).toBe(true)
    })

    describe('permisos', () => {
      it('sin permiso para crear artículos no se ofrece el "+" ni el atajo', async () => {
        const usuario = userEvent.setup()
        montar({ puedeCrearArticulos: false })

        await usuario.type(campo(0, 'Detalle'), 'Cosa nueva')
        await usuario.keyboard('{Control>}{Enter}{/Control}')

        expect(within(filaDeItems(0)).queryByRole('button', { name: /^Crear artículo, línea \d+$/ })).not.toBeInTheDocument()
        expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      })
    })
  })

  it('la lista de resultados no cambia el valor de la fila hasta que se elige', async () => {
    respuestas['leche'] = () => pagina([LECHE])
    const usuario = userEvent.setup()
    const { captura } = montar()

    await usuario.type(campo(0, 'Detalle'), 'leche')
    await screen.findByRole('option', { name: /ART-20/ })
    fireEvent.blur(campo(0, 'Detalle'))

    expect(captura.lineas[0].idArticulo).toBe('')
    expect(captura.lineas[0].descripcion).toBe('leche')
  })
})

describe('GrillaDeItemsDeCompra — editar el código de una fila ya resuelta', () => {
  const POR_CODIGO = articuloFixture({
    id: 20,
    nombre: 'Leche en polvo 800g',
    codigoProveedor: 'AB-1',
    costoLista: 200,
    descuentoProveedor: 10,
    idAlicuotaIva: 4,
    unidadesPorBulto: 12,
    controlaLote: true,
  })

  it('editar el código de una fila resuelta POR CÓDIGO le saca el artículo al instante: un código nuevo inexistente no viaja con el artículo viejo', async () => {
    respuestas['AB-1'] = () => pagina([POR_CODIGO])
    const usuario = userEvent.setup()
    const { enviadas } = montar()
    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await waitFor(() => expect(campo(0, 'Detalle')).toHaveAttribute('readonly'))
    expect(screen.getByLabelText(/^Fecha de vencimiento, línea \d+$/)).toBeInTheDocument()

    await usuario.clear(campo(0, 'Código'))

    // enseguida, sin esperar a ninguna consulta: sin artículo, Detalle editable y vacío, sin lote
    expect(campo(0, 'Detalle')).not.toHaveAttribute('readonly')
    expect(campo(0, 'Detalle')).toHaveValue('')
    expect(screen.queryByLabelText(/^Fecha de vencimiento, línea \d+$/)).not.toBeInTheDocument()

    await usuario.type(campo(0, 'Código'), 'AB-9')
    await usuario.tab()
    await within(filaDeItems(0)).findByText('Pendiente de asociar')
    await usuario.type(campo(0, 'Detalle'), 'Otra cosa')
    await usuario.type(campo(0, 'Cantidad'), '1')

    expect(enviadas()[0]).toMatchObject({ idArticulo: null, descripcion: 'Otra cosa', codigoProveedor: 'AB-9', actualizaCosto: false })
  })

  it('en una fila cuyo artículo se eligió BUSCANDO, editar el código conserva el artículo: el código es el pendiente de asociar', async () => {
    respuestas['yerba'] = () => pagina([articuloFixture({ id: 40, nombre: 'Yerba 1kg', codigoInterno: 'ART-40' })])
    const usuario = userEvent.setup()
    const { enviadas } = montar()

    await usuario.type(campo(0, 'Detalle'), 'yerba')
    await usuario.click(await screen.findByRole('option', { name: /Yerba 1kg/ }))
    await usuario.type(campo(0, 'Cantidad'), '1')
    await usuario.type(campo(0, 'Importe'), '5')
    await usuario.type(campo(0, 'Código'), 'ZZ-9')
    expect(campo(0, 'Detalle')).toHaveValue('Yerba 1kg')
    await usuario.tab()
    await act(async () => {})

    expect(terminosBuscados()).not.toContain('ZZ-9')
    expect(campo(0, 'Detalle')).toHaveValue('Yerba 1kg')
    expect(enviadas()[0]).toMatchObject({ idArticulo: 40, codigoProveedor: 'ZZ-9' })
  })

  it('en una fila cuyo artículo se CREÓ con "+", editar el código conserva el artículo', async () => {
    apiPostMock.mockResolvedValue(articuloFixture({ id: 77, nombre: 'Yerba nueva', idProveedorHabitual: 1 }))
    const usuario = userEvent.setup()
    const { enviadas } = montar()

    await usuario.type(campo(0, 'Código'), 'AB-1')
    await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
    await guardarAlta(usuario, await abrirAlta(usuario))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    await usuario.clear(campo(0, 'Código'))
    await usuario.type(campo(0, 'Código'), 'AB-7')
    await usuario.tab()
    await act(async () => {})
    expect(terminosBuscados()).not.toContain('AB-7')
    await usuario.type(campo(0, 'Cantidad'), '1')
    await usuario.type(campo(0, 'Importe'), '5')
    await usuario.selectOptions(campo(0, 'IVA'), '3')

    expect(enviadas()[0]).toMatchObject({ idArticulo: 77, codigoProveedor: 'AB-7' })
  })
})

describe('GrillaDeItemsDeCompra — cambio de proveedor', () => {
  const DEL_UNO = articuloFixture({ id: 20, nombre: 'Leche del uno', codigoProveedor: 'AB-1', costoLista: 200 })
  const DEL_DOS = articuloFixture({ id: 51, nombre: 'Leche del dos', codigoProveedor: 'AB-1', costoLista: 300 })
  const proveedor = (usuario: ReturnType<typeof userEvent.setup>, id: string) =>
    usuario.selectOptions(screen.getByLabelText('Proveedor de prueba'), id)

  it('las filas resueltas por código se sueltan y se consultan contra el proveedor nuevo: si ahí no existe, quedan como concepto con su código', async () => {
    respuestas['AB-1@1'] = () => pagina([DEL_UNO])
    respuestas['AB-1@2'] = () => pagina([])
    const usuario = userEvent.setup()
    const { enviadas } = montar()
    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche del uno'))

    await proveedor(usuario, '2')

    await within(filaDeItems(0)).findByText('Pendiente de asociar')
    expect(campo(0, 'Detalle')).toHaveValue('')
    expect(campo(0, 'Detalle')).not.toHaveAttribute('readonly')
    expect(campo(0, 'Código')).toHaveValue('AB-1')
    expect(busquedasConProveedor()).toEqual(['AB-1&idProveedor=1', 'AB-1&idProveedor=2'])

    await usuario.type(campo(0, 'Detalle'), 'Concepto')
    await usuario.type(campo(0, 'Cantidad'), '1')
    await usuario.type(campo(0, 'Importe'), '9')
    await usuario.selectOptions(campo(0, 'IVA'), '3')
    expect(enviadas()[0]).toMatchObject({ idArticulo: null, codigoProveedor: 'AB-1', costoUnitario: 9 })
  })

  it('si el código existe en el proveedor nuevo, la fila se resuelve a SU artículo', async () => {
    respuestas['AB-1@1'] = () => pagina([DEL_UNO])
    respuestas['AB-1@2'] = () => pagina([DEL_DOS])
    const usuario = userEvent.setup()
    const { enviadas } = montar()
    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche del uno'))

    await proveedor(usuario, '2')

    await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche del dos'))
    await usuario.type(campo(0, 'Cantidad'), '1')
    expect(enviadas()[0]).toMatchObject({ idArticulo: 51, codigoProveedor: 'AB-1' })
  })

  it('los marcadores de "ya consultado" se reinician: un código que no estaba en el proveedor anterior se vuelve a consultar', async () => {
    respuestas['ZZ-9@1'] = () => pagina([])
    respuestas['ZZ-9@2'] = () => pagina([articuloFixture({ id: 52, nombre: 'Aparece en el dos', codigoProveedor: 'ZZ-9' })])
    const usuario = userEvent.setup()
    montar()
    await escribirCodigoYSalir(usuario, 0, 'ZZ-9')
    await within(filaDeItems(0)).findByText('Pendiente de asociar')

    await proveedor(usuario, '2')

    await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Aparece en el dos'))
    expect(busquedasConProveedor()).toEqual(['ZZ-9&idProveedor=1', 'ZZ-9&idProveedor=2'])
  })

  it('las filas cuyo artículo se eligió buscando conservan artículo y código, y no se consultan', async () => {
    respuestas['yerba'] = () => pagina([articuloFixture({ id: 40, nombre: 'Yerba 1kg', codigoInterno: 'ART-40' })])
    const usuario = userEvent.setup()
    const { enviadas } = montar()
    await usuario.type(campo(0, 'Código'), 'ZZ-9')
    await usuario.type(campo(0, 'Detalle'), 'yerba')
    await usuario.click(await screen.findByRole('option', { name: /Yerba 1kg/ }))
    await usuario.type(campo(0, 'Cantidad'), '1')
    await usuario.type(campo(0, 'Importe'), '5')
    const antes = busquedasConProveedor().length

    await proveedor(usuario, '2')
    await act(async () => {})

    expect(busquedasConProveedor()).toHaveLength(antes)
    expect(campo(0, 'Detalle')).toHaveValue('Yerba 1kg')
    expect(enviadas()[0]).toMatchObject({ idArticulo: 40, codigoProveedor: 'ZZ-9' })

    // el marcador de "ya consultado" era del proveedor anterior: al volver por el código se consulta contra el nuevo
    expect(within(filaDeItems(0)).queryByText('Pendiente de asociar')).not.toBeInTheDocument()
    await usuario.click(campo(0, 'Código'))
    await usuario.tab()
    await act(async () => {})
    expect(busquedasConProveedor()).toHaveLength(antes)
    expect(campo(0, 'Detalle')).toHaveValue('Yerba 1kg')
  })

  it('una fila resuelta por código pierde lo que el operador ajustó: se vuelve a buscar desde cero y toma los valores del artículo nuevo', async () => {
    respuestas['AB-1@1'] = () => pagina([{ ...DEL_UNO, idAlicuotaIva: 4 }])
    respuestas['AB-1@2'] = () => pagina([{ ...DEL_DOS, descuentoProveedor: 20, idAlicuotaIva: 4 }])
    const usuario = userEvent.setup()
    montar()
    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await waitFor(() => expect(campo(0, 'Importe')).toHaveValue('200,0000'))
    await usuario.clear(campo(0, 'Importe'))
    await usuario.type(campo(0, 'Importe'), '150')
    await usuario.type(campo(0, 'Descuento (%)'), '5')
    await usuario.selectOptions(campo(0, 'IVA'), '3')

    await proveedor(usuario, '2')

    await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche del dos'))
    expect(campo(0, 'Importe')).toHaveValue('300,0000')
    expect(campo(0, 'Descuento (%)')).toHaveValue('20,00')
    expect(campo(0, 'IVA')).toHaveValue('4')
  })

  describe('una fila resuelta por código se busca de nuevo como una fila nueva', () => {
    const UNO = articuloFixture({ id: 20, nombre: 'Leche del uno', codigoProveedor: 'AB-1', costoLista: 200, descuentoProveedor: 10, idAlicuotaIva: 4 })
    const DOS = articuloFixture({ id: 51, nombre: 'Leche del dos', codigoProveedor: 'AB-1', costoLista: 300, descuentoProveedor: 20, idAlicuotaIva: 3 })

    async function resolverConElUno(usuario: ReturnType<typeof userEvent.setup>, props: Partial<Parameters<typeof Arnes>[0]> = {}) {
      const vista = montar(props)
      await escribirCodigoYSalir(usuario, 0, 'AB-1')
      await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche del uno'))
      return vista
    }

    it('conserva Código y Cantidad, y el artículo nuevo trae su importe, descuento e IVA', async () => {
      respuestas['AB-1@1'] = () => pagina([UNO])
      respuestas['AB-1@2'] = () => pagina([DOS])
      const usuario = userEvent.setup()
      const { enviadas } = await resolverConElUno(usuario)
      await usuario.type(campo(0, 'Cantidad'), '3')

      await proveedor(usuario, '2')

      await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche del dos'))
      expect(campo(0, 'Código')).toHaveValue('AB-1')
      expect(campo(0, 'Cantidad')).toHaveDisplayValue('3')
      expect(campo(0, 'Importe')).toHaveValue('300,0000')
      expect(campo(0, 'Descuento (%)')).toHaveValue('20,00')
      expect(campo(0, 'IVA')).toHaveValue('3')
      expect(enviadas()[0]).toMatchObject({ idArticulo: 51, unidades: 3, costoUnitario: 300, descuento: 180, idAlicuotaIva: 3, actualizaCosto: true })
    })

    it('un artículo nuevo sin costo deja Importe y Descuento vacíos: nunca los del artículo anterior', async () => {
      respuestas['AB-1@1'] = () => pagina([UNO])
      respuestas['AB-1@2'] = () => pagina([{ ...DOS, costoLista: null, costoNominal: null, descuentoProveedor: null }])
      const usuario = userEvent.setup()
      await resolverConElUno(usuario)

      await proveedor(usuario, '2')

      await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche del dos'))
      expect(campo(0, 'Importe')).toHaveValue('')
      expect(campo(0, 'Descuento (%)')).toHaveValue('')
      expect(campo(0, 'IVA')).toHaveValue('3')
    })

    it('el importe del artículo nuevo se neta con la alícuota del artículo nuevo, no con la que el operador había elegido', async () => {
      respuestas['AB-1@1'] = () => pagina([UNO])
      respuestas['AB-1@2'] = () => pagina([{ ...DOS, costoLista: 221, descuentoProveedor: null, idAlicuotaIva: 4 }])
      const usuario = userEvent.setup()
      await resolverConElUno(usuario, { discriminaIva: true })
      await usuario.selectOptions(campo(0, 'IVA'), '3')

      await proveedor(usuario, '2')

      await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche del dos'))
      expect(campo(0, 'IVA')).toHaveValue('4')
      expect(campo(0, 'Importe')).toHaveValue('200,0000')
    })

    it('si el proveedor nuevo no tiene el código, la fila queda pendiente y sin ningún valor: ni del artículo anterior ni del operador', async () => {
      respuestas['AB-1@1'] = () => pagina([UNO])
      respuestas['AB-1@2'] = () => pagina([])
      const usuario = userEvent.setup()
      await resolverConElUno(usuario)
      await usuario.clear(campo(0, 'Importe'))
      await usuario.type(campo(0, 'Importe'), '150')

      await proveedor(usuario, '2')

      await within(filaDeItems(0)).findByText('Pendiente de asociar')
      expect(campo(0, 'Detalle')).toHaveValue('')
      expect(campo(0, 'Importe')).toHaveValue('')
      expect(campo(0, 'Descuento (%)')).toHaveValue('')
      expect(campo(0, 'IVA')).toHaveValue('')
    })

    it('el mismo artículo en los dos proveedores queda con los mismos valores', async () => {
      respuestas['AB-1@1'] = () => pagina([UNO])
      respuestas['AB-1@2'] = () => pagina([UNO])
      const usuario = userEvent.setup()
      const { enviadas } = await resolverConElUno(usuario)

      await proveedor(usuario, '2')

      await waitFor(() => expect(busquedasConProveedor()).toContain('AB-1&idProveedor=2'))
      await act(async () => {})
      expect(campo(0, 'Detalle')).toHaveValue('Leche del uno')
      expect(campo(0, 'Importe')).toHaveValue('200,0000')
      expect(campo(0, 'Descuento (%)')).toHaveValue('10,00')
      expect(campo(0, 'IVA')).toHaveValue('4')
      await usuario.type(campo(0, 'Cantidad'), '1')
      expect(enviadas()[0]).toMatchObject({ idArticulo: 20, costoUnitario: 200, descuento: 20, idAlicuotaIva: 4 })
    })

    it('si la nueva consulta falla, la fila queda sin valores y el reintento al salir de la celda se comporta como una primera consulta', async () => {
      respuestas['AB-1@1'] = () => pagina([UNO])
      respuestas['AB-1@2'] = () => Promise.reject(new Error('sin red'))
      const usuario = userEvent.setup()
      await resolverConElUno(usuario)

      await proveedor(usuario, '2')

      await within(filaDeItems(0)).findByText('No se pudo buscar el código.')
      expect(campo(0, 'Detalle')).toHaveValue('')
      expect(campo(0, 'Importe')).toHaveValue('')
      expect(campo(0, 'Descuento (%)')).toHaveValue('')
      expect(campo(0, 'IVA')).toHaveValue('')

      respuestas['AB-1@2'] = () => pagina([DOS])
      await usuario.click(campo(0, 'Código'))
      await usuario.tab()

      await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche del dos'))
      expect(campo(0, 'Importe')).toHaveValue('300,0000')
      expect(campo(0, 'Descuento (%)')).toHaveValue('20,00')
      expect(campo(0, 'IVA')).toHaveValue('3')
    })

    it('un artículo elegido buscando después de resolver por código ya no cuenta como resuelto por código: no se consulta ni se borra', async () => {
      respuestas['AB-1@1'] = () => pagina([UNO])
      respuestas['AB-1@2'] = () => pagina([DOS])
      respuestas['yerba'] = () => pagina([articuloFixture({ id: 40, nombre: 'Yerba 1kg', codigoInterno: 'ART-40', costoLista: 80 })])
      const usuario = userEvent.setup()
      const { enviadas } = await resolverConElUno(usuario)
      await usuario.clear(campo(0, 'Código'))
      await usuario.type(campo(0, 'Código'), 'ZZ-9')
      await usuario.type(campo(0, 'Detalle'), 'yerba')
      await usuario.click(await screen.findByRole('option', { name: /Yerba 1kg/ }))
      const antes = busquedasConProveedor().length

      await proveedor(usuario, '2')
      await act(async () => {})

      expect(busquedasConProveedor()).toHaveLength(antes)
      await usuario.type(campo(0, 'Cantidad'), '1')
      expect(enviadas()[0]).toMatchObject({ idArticulo: 40, costoUnitario: 80 })
    })
  })

  describe('lo que no se resolvió por código no se toca al cambiar de proveedor', () => {
    it('una línea cargada de un borrador y un concepto conservan artículo, código y precios, y no se consultan', async () => {
      const usuario = userEvent.setup()
      montar({
        inicial: [
          itemAFormulario(1, itemFixture({ idArticulo: 10, descripcion: 'Fideos 500g', codigoProveedor: 'DR-1', costoUnitario: 100, descuento: 0 })),
          itemAFormulario(2, itemFixture({ idArticulo: null, descripcion: 'Flete', codigoProveedor: null, costoUnitario: 50, descuento: 0 })),
        ],
      })

      await proveedor(usuario, '2')
      await act(async () => {})

      expect(busquedasConProveedor()).toEqual([])
      expect(campo(0, 'Detalle')).toHaveValue('Fideos 500g')
      expect(campo(0, 'Código')).toHaveValue('DR-1')
      expect(campo(0, 'Importe')).toHaveValue('100,0000')
      expect(campo(1, 'Detalle')).toHaveValue('Flete')
      expect(campo(1, 'Importe')).toHaveValue('50,0000')
    })

    it('una fila creada con "+" conserva su artículo y su código, y no se consulta', async () => {
      apiPostMock.mockResolvedValue(articuloFixture({ id: 77, nombre: 'Yerba nueva', idProveedorHabitual: 1 }))
      const usuario = userEvent.setup()
      montar()
      await usuario.type(campo(0, 'Código'), 'AB-1')
      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      await guardarAlta(usuario, await abrirAlta(usuario))
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      const antes = busquedasConProveedor().length

      await proveedor(usuario, '2')
      await act(async () => {})

      expect(busquedasConProveedor()).toHaveLength(antes)
      expect(campo(0, 'Detalle')).toHaveValue('Yerba nueva')
      expect(campo(0, 'Código')).toHaveValue('AB-1')
    })
  })

  describe('pista de asociación del código (T2)', () => {
    it('un artículo elegido buscando con un código tipeado avisa que se asociará al confirmar', async () => {
      respuestas['yerba'] = () => pagina([articuloFixture({ id: 40, nombre: 'Yerba 1kg', codigoInterno: 'ART-40' })])
      const usuario = userEvent.setup()
      montar()
      await usuario.type(campo(0, 'Detalle'), 'yerba')
      await usuario.click(await screen.findByRole('option', { name: /Yerba 1kg/ }))
      expect(within(filaDeItems(0)).queryByText('Se asociará al artículo al confirmar.')).not.toBeInTheDocument()

      await usuario.type(campo(0, 'Código'), 'ZZ-9')
      await usuario.tab()
      await act(async () => {})

      expect(within(filaDeItems(0)).getByText('Se asociará al artículo al confirmar.')).toBeInTheDocument()
      expect(within(filaDeItems(0)).queryByText('Pendiente de asociar')).not.toBeInTheDocument()
    })

    it('un artículo creado con "+" avisa solo cuando el código se cambia después de crearlo', async () => {
      apiPostMock.mockResolvedValue(articuloFixture({ id: 77, nombre: 'Yerba nueva', idProveedorHabitual: 1 }))
      const usuario = userEvent.setup()
      montar()
      await usuario.type(campo(0, 'Código'), 'AB-1')
      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      await guardarAlta(usuario, await abrirAlta(usuario))
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      expect(within(filaDeItems(0)).queryByText('Se asociará al artículo al confirmar.')).not.toBeInTheDocument()

      await usuario.type(campo(0, 'Código'), '7')

      expect(within(filaDeItems(0)).getByText('Se asociará al artículo al confirmar.')).toBeInTheDocument()
    })

    it('una fila resuelta por su código no muestra ninguna pista: ya está asociado', async () => {
      respuestas['AB-1'] = () => pagina([articuloFixture({ id: 20, codigoProveedor: 'AB-1', costoLista: 200 })])
      const usuario = userEvent.setup()
      montar()

      await escribirCodigoYSalir(usuario, 0, 'AB-1')
      await waitFor(() => expect(campo(0, 'Detalle')).toHaveAttribute('readonly'))

      expect(within(filaDeItems(0)).queryByText('Se asociará al artículo al confirmar.')).not.toBeInTheDocument()
      expect(within(filaDeItems(0)).queryByText('Pendiente de asociar')).not.toBeInTheDocument()
    })

    it('un código que no se encontró y DESPUÉS un artículo elegido buscando: avisa que se asociará, sin importar el orden', async () => {
      respuestas['yerba'] = () => pagina([articuloFixture({ id: 40, nombre: 'Yerba 1kg', codigoInterno: 'ART-40' })])
      const usuario = userEvent.setup()
      montar()
      await escribirCodigoYSalir(usuario, 0, 'ZZ-9')
      await within(filaDeItems(0)).findByText('Pendiente de asociar')

      await usuario.type(campo(0, 'Detalle'), 'yerba')
      await usuario.click(await screen.findByRole('option', { name: /Yerba 1kg/ }))

      expect(within(filaDeItems(0)).getByText('Se asociará al artículo al confirmar.')).toBeInTheDocument()
      expect(within(filaDeItems(0)).queryByText('Pendiente de asociar')).not.toBeInTheDocument()
    })

    it('una línea cargada de un borrador con su código guardado no muestra ninguna pista mientras no se toque', () => {
      montar({ inicial: [itemAFormulario(1, itemFixture({ codigoProveedor: 'DR-1' }))] })

      expect(within(filaDeItems(0)).queryByText('Se asociará al artículo al confirmar.')).not.toBeInTheDocument()
      expect(within(filaDeItems(0)).queryByText('Pendiente de asociar')).not.toBeInTheDocument()
    })

    it('un código sin artículo sigue diciendo "Pendiente de asociar"', async () => {
      const usuario = userEvent.setup()
      montar()

      await escribirCodigoYSalir(usuario, 0, 'ZZ-9')

      await within(filaDeItems(0)).findByText('Pendiente de asociar')
      expect(within(filaDeItems(0)).queryByText('Se asociará al artículo al confirmar.')).not.toBeInTheDocument()
    })
  })


  it('los códigos tipeados antes de elegir un proveedor se consultan todos al elegirlo', async () => {
    respuestas['AB-1@1'] = () => pagina([DEL_UNO])
    respuestas['CD-2@1'] = () => pagina([articuloFixture({ id: 21, nombre: 'Otra del uno', codigoProveedor: 'CD-2' })])
    const usuario = userEvent.setup()
    montar({ idProveedor: null })
    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await escribirCodigoYSalir(usuario, 1, 'CD-2')
    expect(busquedasConProveedor()).toEqual([])

    await proveedor(usuario, '1')

    await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche del uno'))
    await waitFor(() => expect(campo(1, 'Detalle')).toHaveValue('Otra del uno'))
  })

  it('quedarse sin proveedor suelta las filas resueltas por código, borra sus precios y no consulta nada', async () => {
    respuestas['AB-1@1'] = () => pagina([DEL_UNO])
    const usuario = userEvent.setup()
    montar()
    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await waitFor(() => expect(campo(0, 'Detalle')).toHaveValue('Leche del uno'))

    await proveedor(usuario, '')

    expect(campo(0, 'Detalle')).toHaveValue('')
    expect(campo(0, 'Código')).toHaveValue('AB-1')
    expect(campo(0, 'Importe')).toHaveValue('')
    expect(within(filaDeItems(0)).getByText('Elegí un proveedor para buscar el código.')).toBeInTheDocument()
    expect(busquedasConProveedor()).toEqual(['AB-1&idProveedor=1'])
  })
})

describe('GrillaDeItemsDeCompra — precarga según el modo de precios', () => {
  const CON_IVA = articuloFixture({ id: 20, nombre: 'Leche', codigoProveedor: 'AB-1', costoLista: 121, idAlicuotaIva: 3 })

  it('con un comprobante que discrimina IVA y precios netos, el costo (que trae IVA) se carga sin IVA', async () => {
    respuestas['AB-1'] = () => pagina([CON_IVA])
    const usuario = userEvent.setup()
    montar({ discriminaIva: true, preciosFinales: false })

    await escribirCodigoYSalir(usuario, 0, 'AB-1')

    await waitFor(() => expect(campo(0, 'Importe')).toHaveValue('100,0000'))
  })

  it('con precios con IVA incluido, o en un comprobante que no discrimina, se carga tal cual', async () => {
    respuestas['AB-1'] = () => pagina([CON_IVA])
    const usuario = userEvent.setup()
    montar({ discriminaIva: true, preciosFinales: true })
    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await waitFor(() => expect(campo(0, 'Importe')).toHaveValue('121,0000'))
  })

  it('un comprobante que no discrimina IVA carga el costo tal cual', async () => {
    respuestas['AB-1'] = () => pagina([CON_IVA])
    const usuario = userEvent.setup()
    montar({ discriminaIva: false })
    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await waitFor(() => expect(campo(0, 'Importe')).toHaveValue('121,0000'))
  })

  it('cambiar el modo después NO reescribe el importe que ya está en la fila', async () => {
    respuestas['AB-1'] = () => pagina([CON_IVA])
    const usuario = userEvent.setup()
    const { rerender, captura } = montar({ discriminaIva: true, preciosFinales: false })
    await escribirCodigoYSalir(usuario, 0, 'AB-1')
    await waitFor(() => expect(campo(0, 'Importe')).toHaveValue('100,0000'))

    rerender(<Arnes captura={captura} discriminaIva preciosFinales />)

    expect(campo(0, 'Importe')).toHaveValue('100,0000')
  })

  it('elegir el artículo buscando también extrae el IVA en modo neto', async () => {
    respuestas['leche'] = () => pagina([CON_IVA])
    const usuario = userEvent.setup()
    montar({ discriminaIva: true, preciosFinales: false })

    await usuario.type(campo(0, 'Detalle'), 'leche')
    await usuario.click(await screen.findByRole('option', { name: /Leche/ }))

    expect(campo(0, 'Importe')).toHaveValue('100,0000')
  })
})

describe('GrillaDeItemsDeCompra — costo de lista cargado por bulto y sin IVA', () => {
  const POR_BULTO_SIN_IVA = articuloFixture({
    id: 20,
    nombre: 'Leche',
    codigoProveedor: 'AB-1',
    costoLista: 1200,
    costoListaPor: 'Bulto',
    costoListaIncluyeIva: false,
    unidadesPorBulto: 12,
    idAlicuotaIva: 3,
  })

  it('en un comprobante de precios netos precarga el costo por unidad sin IVA', async () => {
    respuestas['AB-1'] = () => pagina([POR_BULTO_SIN_IVA])
    const usuario = userEvent.setup()
    montar({ discriminaIva: true, preciosFinales: false })

    await escribirCodigoYSalir(usuario, 0, 'AB-1')

    await waitFor(() => expect(campo(0, 'Importe')).toHaveValue('100,0000'))
  })

  it('con precios con IVA incluido precarga el costo por unidad con IVA', async () => {
    respuestas['AB-1'] = () => pagina([POR_BULTO_SIN_IVA])
    const usuario = userEvent.setup()
    montar({ discriminaIva: true, preciosFinales: true })

    await escribirCodigoYSalir(usuario, 0, 'AB-1')

    await waitFor(() => expect(campo(0, 'Importe')).toHaveValue('121,0000'))
  })
})

describe('GrillaDeItemsDeCompra — accesibilidad', () => {
  it('cada celda lleva el número de su línea en el nombre accesible', () => {
    montar({ inicial: [itemAFormulario(1, itemFixture())] })

    for (const linea of [1, 2]) {
      for (const celda of ['Código', 'Detalle', 'UM', 'Cantidad', 'Importe', 'Descuento (%)', 'IVA']) {
        expect(screen.getByLabelText(`${celda}, línea ${linea}`)).toBeInTheDocument()
      }
    }
    expect(screen.getByRole('button', { name: 'Quitar línea 1' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear artículo, línea 2' })).toBeInTheDocument()
  })

  const tresFilas = () => [
    itemAFormulario(1, itemFixture({ idArticulo: 10, descripcion: 'A' })),
    itemAFormulario(2, itemFixture({ idArticulo: 11, descripcion: 'B' })),
    itemAFormulario(3, itemFixture({ idArticulo: 12, descripcion: 'C' })),
  ]

  it('Alt+Supr quita la fila donde está el foco y lo deja en el Código de la fila que ocupa su lugar', async () => {
    const usuario = userEvent.setup()
    montar({ inicial: tresFilas() })

    await usuario.click(campo(1, 'Importe'))
    await usuario.keyboard('{Alt>}{Delete}{/Alt}')

    expect(filasDeItems()).toHaveLength(3)
    expect(campo(0, 'Detalle')).toHaveValue('A')
    expect(campo(1, 'Detalle')).toHaveValue('C')
    expect(campo(1, 'Código')).toHaveFocus()
  })

  it('Alt+Supr funciona desde el Detalle y desde la última fila con datos (el foco pasa a la fila vacía)', async () => {
    const usuario = userEvent.setup()
    montar({ inicial: tresFilas() })

    await usuario.click(campo(2, 'Detalle'))
    await usuario.keyboard('{Alt>}{Delete}{/Alt}')

    expect(filasDeItems()).toHaveLength(3)
    expect(campo(2, 'Código')).toHaveFocus()
    expect(campo(2, 'Detalle')).toHaveValue('')
  })

  it('Supr solo no quita nada, y Alt+Supr tampoco quita la fila vacía', async () => {
    const usuario = userEvent.setup()
    montar({ inicial: tresFilas() })

    await usuario.click(campo(1, 'Importe'))
    await usuario.keyboard('{Delete}')
    expect(filasDeItems()).toHaveLength(4)

    await usuario.click(campo(3, 'Código'))
    await usuario.keyboard('{Alt>}{Delete}{/Alt}')
    expect(filasDeItems()).toHaveLength(4)
  })

  it('con la grilla deshabilitada Alt+Supr no quita nada', () => {
    montar({ inicial: tresFilas(), disabled: true })

    fireEvent.keyDown(campo(1, 'Importe'), { key: 'Delete', altKey: true })

    expect(filasDeItems()).toHaveLength(4)
  })

  it('Ctrl+Supr es el "borrar palabra siguiente" nativo del campo: no quita la fila', () => {
    montar({ inicial: tresFilas() })

    fireEvent.keyDown(campo(1, 'Importe'), { key: 'Delete', ctrlKey: true })

    expect(filasDeItems()).toHaveLength(4)
  })

  it('la repetición automática de Alt+Supr no sigue quitando filas', () => {
    montar({ inicial: tresFilas() })

    fireEvent.keyDown(campo(0, 'Importe'), { key: 'Delete', altKey: true })
    expect(filasDeItems()).toHaveLength(3)

    fireEvent.keyDown(campo(0, 'Importe'), { key: 'Delete', altKey: true, repeat: true })
    expect(filasDeItems()).toHaveLength(3)
  })

  it('el botón de quitar declara su atajo', () => {
    montar({ inicial: tresFilas() })
    const boton = screen.getByRole('button', { name: 'Quitar línea 1' })
    expect(boton).toHaveAttribute('aria-keyshortcuts', 'Alt+Delete')
    expect(boton).toHaveAttribute('title', 'Quitar línea (Alt+Supr)')
  })
})
