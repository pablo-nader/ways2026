import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useRef, useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { aLineaSolicitada, lineaCompletaParaEnvio, normalizarFilas, type LineaDeCompraFormulario } from '../../api/lineaDeCompra'
import { ErrorApi } from '../../api/cliente'
import type { AlicuotaIvaListado, ArticuloListado, ItemDeCompra } from '../../api/tipos'
import { itemAFormulario } from '../../api/lineaDeCompra'
import { filaDeItems, filasDeItems } from './ayudasDePruebaDeGrilla'
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

function articuloFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
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

/** Respuestas del buscador por término (`busqueda=`): lo no previsto devuelve una página vacía. */
let respuestas: Record<string, () => Promise<unknown>> = {}

function terminosBuscados(): string[] {
  return apiGetMock.mock.calls
    .map((c: unknown[]) => c[0] as string)
    .filter((r) => r.startsWith('/articulos?busqueda='))
    .map((r) => decodeURIComponent(r.slice('/articulos?busqueda='.length).split('&')[0]))
}

type Captura = { lineas: LineaDeCompraFormulario[] }

/** Arnés con el mismo manejo de estado que la pantalla: cambios y bajas pasan por `normalizarFilas`. */
function Arnes({
  captura,
  inicial = [],
  idProveedor = 1,
  disabled = false,
  marcarFaltantes = false,
}: {
  captura: Captura
  inicial?: LineaDeCompraFormulario[]
  idProveedor?: number | null
  disabled?: boolean
  marcarFaltantes?: boolean
}) {
  const claveRef = useRef(100)
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
        discriminaIva={false}
        porcentajePorAlicuota={PORCENTAJES}
        idProveedor={idProveedor}
        nombreProveedor="Proveedor Uno SA"
        marcarFaltantes={marcarFaltantes}
        onCambio={cambiar}
        onQuitar={quitar}
      />
      <button type="button">Fuera de la grilla</button>
    </div>
  )
}

function montar(props: Partial<Parameters<typeof Arnes>[0]> = {}) {
  const captura: Captura = { lineas: [] }
  const vista = render(<Arnes captura={captura} {...props} />)
  const enviadas = () => captura.lineas.filter(lineaCompletaParaEnvio).map(aLineaSolicitada)
  return { captura, enviadas, ...vista }
}

const campo = (fila: number, etiqueta: string) => within(filaDeItems(fila)).getByLabelText(etiqueta)

async function escribirCodigoYSalir(usuario: ReturnType<typeof userEvent.setup>, fila: number, codigo: string) {
  await usuario.type(campo(fila, 'Código'), codigo)
  await usuario.tab()
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  respuestas = {}
  apiGetMock.mockImplementation((ruta: string) => {
    const busqueda = /^\/articulos\?busqueda=([^&]+)/.exec(ruta)
    if (busqueda) return (respuestas[decodeURIComponent(busqueda[1])] ?? (() => pagina([])))()
    if (ruta === '/catalogos/areas') return Promise.resolve([AREA])
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
    expect(within(filaDeItems(0)).queryByRole('button', { name: 'Quitar línea' })).not.toBeInTheDocument()

    await usuario.type(campo(0, 'Cantidad'), '5')
    const quitar = within(filaDeItems(0)).getByRole('button', { name: 'Quitar línea' })
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
    expect(within(filaDeItems(0)).getByRole('button', { name: 'Quitar línea' })).toBeDisabled()
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

  it('un precio por bulto que no divide exacto viaja a 4 decimales y el total sigue siendo cantidad × importe', async () => {
    const usuario = userEvent.setup()
    const { enviadas } = montar({ inicial: [{ ...conBulto(), unidadesPorBulto: 3 }] })

    await usuario.selectOptions(campo(0, 'UM'), 'Bulto')
    await usuario.clear(campo(0, 'Cantidad'))
    await usuario.type(campo(0, 'Cantidad'), '1')
    await usuario.clear(campo(0, 'Importe'))
    await usuario.type(campo(0, 'Importe'), '100')

    expect(enviadas()[0].costoUnitario).toBe(33.3333)
    expect(within(filaDeItems(0)).getByText('$ 100,00')).toBeInTheDocument()
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

  it('la fila siempre manda actualizaCosto true para un artículo y false para un concepto, sin columna para elegirlo', () => {
    const { enviadas } = montar({
      inicial: [
        itemAFormulario(1, itemFixture({ actualizaCosto: false })),
        itemAFormulario(2, itemFixture({ idArticulo: null, descripcion: 'Flete', actualizaCosto: true })),
      ],
    })

    expect(screen.queryByLabelText('Actualiza costo')).not.toBeInTheDocument()
    expect(enviadas().map((l) => l.actualizaCosto)).toEqual([true, false])
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
    expect(screen.getByLabelText('Fecha de vencimiento')).toBeInTheDocument()
    await usuario.type(campo(0, 'Cantidad'), '3')
    await usuario.type(screen.getByLabelText('Fecha de vencimiento'), '01/12/2026')
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

  it('elegir el proveedor después no dispara la consulta sola: solo corre al salir de la celda', async () => {
    const usuario = userEvent.setup()
    const { rerender, captura } = montar({ idProveedor: null })
    await escribirCodigoYSalir(usuario, 0, 'AB-1')

    rerender(<Arnes captura={captura} idProveedor={1} />)
    await act(async () => {})

    expect(terminosBuscados()).toEqual([])
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

  describe('una respuesta tardía nunca pisa una fila que ya cambió', () => {
    it('si el operador tocó la fila mientras la consulta estaba en vuelo, se descarta', async () => {
      let resolver: (r: unknown) => void = () => {}
      respuestas['AB-1'] = () => new Promise((r) => (resolver = r))
      const usuario = userEvent.setup()
      montar()

      await escribirCodigoYSalir(usuario, 0, 'AB-1')
      await within(filaDeItems(0)).findByText('Buscando…')
      await usuario.type(campo(0, 'Cantidad'), '3')
      await act(async () => {
        resolver({ items: [POR_CODIGO], total: 1, pagina: 1, tamanio: 25 })
      })

      expect(campo(0, 'Detalle')).toHaveValue('')
      expect(campo(0, 'Importe')).toHaveValue('')
      expect(campo(0, 'Cantidad')).toHaveValue(3)
      expect(within(filaDeItems(0)).queryByText('Buscando…')).not.toBeInTheDocument()
    })

    it('si la fila se quitó mientras la consulta estaba en vuelo, no aparece ni se altera nada', async () => {
      let resolver: (r: unknown) => void = () => {}
      respuestas['AB-1'] = () => new Promise((r) => (resolver = r))
      const usuario = userEvent.setup()
      const { captura } = montar()

      await escribirCodigoYSalir(usuario, 0, 'AB-1')
      await usuario.click(within(filaDeItems(0)).getByRole('button', { name: 'Quitar línea' }))
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

    it('si cambia el proveedor mientras la consulta está en vuelo, la respuesta del anterior se descarta', async () => {
      let resolver: (r: unknown) => void = () => {}
      respuestas['AB-1'] = () => new Promise((r) => (resolver = r))
      const usuario = userEvent.setup()
      const { rerender, captura } = montar()

      await escribirCodigoYSalir(usuario, 0, 'AB-1')
      rerender(<Arnes captura={captura} idProveedor={2} />)
      await act(async () => {
        resolver({ items: [POR_CODIGO], total: 1, pagina: 1, tamanio: 25 })
      })

      expect(campo(0, 'Detalle')).toHaveValue('')
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
    expect(screen.getByLabelText('Código de lote')).toBeInTheDocument()

    await usuario.type(campo(0, 'Detalle'), ' (revisado)')

    expect(campo(0, 'UM')).toBeDisabled()
    expect(screen.queryByLabelText('Código de lote')).not.toBeInTheDocument()
    expect(enviadas()[0]).toMatchObject({ idArticulo: null, descripcion: 'Fideos 500g (revisado)', codigoLote: null, bultos: null })
  })

  it('no hay "+" en una fila que ya tiene artículo, ni la vieja oferta de asociar el código', async () => {
    respuestas['leche'] = () => pagina([LECHE])
    const usuario = userEvent.setup()
    montar()
    expect(within(filaDeItems(0)).getByRole('button', { name: 'Crear artículo' })).toBeInTheDocument()

    await usuario.type(campo(0, 'Código'), 'ZZ-9')
    await usuario.type(campo(0, 'Detalle'), 'leche')
    await usuario.click(await screen.findByRole('option', { name: /Leche en polvo 800g/ }))

    expect(within(filaDeItems(0)).queryByRole('button', { name: 'Crear artículo' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Asociar/ })).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  describe('crear un artículo desde la fila', () => {
    async function abrirAlta(usuario: ReturnType<typeof userEvent.setup>) {
      await usuario.click(within(filaDeItems(0)).getByRole('button', { name: 'Crear artículo' }))
      const dialogo = await screen.findByRole('dialog', { name: 'Nuevo artículo' })
      await within(dialogo).findByRole('option', { name: 'Almacén' })
      return dialogo
    }

    it('abre el alta con el texto del Detalle y el código de la fila, y al crear la fila queda resuelta al artículo nuevo', async () => {
      apiPostMock.mockResolvedValue(articuloFixture({ id: 77, codigoInterno: 'ART-77', nombre: 'Yerba nueva' }))
      const usuario = userEvent.setup()
      const { enviadas } = montar()

      await usuario.type(campo(0, 'Código'), 'AB-1234')
      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      await usuario.type(campo(0, 'Importe'), '12,3456')
      await usuario.selectOptions(campo(0, 'IVA'), '4')
      const dialogo = await abrirAlta(usuario)

      expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Yerba nueva')
      expect(within(dialogo).getByLabelText('Código del proveedor')).toHaveValue('AB-1234')
      expect(within(dialogo).getByLabelText('Alícuota de IVA')).toHaveValue('4')
      expect(within(dialogo).getByLabelText('Costo de lista')).toHaveValue('12,35')

      await usuario.selectOptions(within(dialogo).getByLabelText('Área'), '4')
      await usuario.click(within(dialogo).getByRole('button', { name: 'Crear' }))

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
        codigoInterno: null,
      })

      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      expect(campo(0, 'Detalle')).toHaveValue('Yerba nueva')
      // lo que el operador ya había tipeado en la fila se conserva
      expect(campo(0, 'Importe')).toHaveValue('12,3456')
      expect(campo(0, 'IVA')).toHaveValue('4')
      expect(campo(0, 'Código')).toHaveValue('AB-1234')
      expect(within(filaDeItems(0)).queryByRole('button', { name: 'Crear artículo' })).not.toBeInTheDocument()
      await waitFor(() => expect(campo(0, 'Cantidad')).toHaveFocus())

      await usuario.type(campo(0, 'Cantidad'), '2')
      expect(enviadas()[0]).toMatchObject({ idArticulo: 77, codigoProveedor: 'AB-1234', costoUnitario: 12.3456, unidades: 2 })
    })

    it('si el operador corrigió el código en el alta, la fila queda con el código que se asoció', async () => {
      apiPostMock.mockResolvedValue(articuloFixture({ id: 77, nombre: 'Yerba nueva' }))
      const usuario = userEvent.setup()
      const { captura } = montar()

      await usuario.type(campo(0, 'Código'), 'AB-1')
      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      const dialogo = await abrirAlta(usuario)
      await usuario.clear(within(dialogo).getByLabelText('Código del proveedor'))
      await usuario.type(within(dialogo).getByLabelText('Código del proveedor'), 'AB-2')
      await usuario.selectOptions(within(dialogo).getByLabelText('Área'), '4')
      await usuario.click(within(dialogo).getByRole('button', { name: 'Crear' }))

      await waitFor(() => expect(campo(0, 'Código')).toHaveValue('AB-2'))
      expect(captura.lineas[0].codigoConsultado).toBe('AB-2')
    })

    it('Ctrl+Enter en el Detalle abre el mismo alta, sin tocar el ratón', async () => {
      const usuario = userEvent.setup()
      montar()

      await usuario.type(campo(0, 'Detalle'), 'Cosa nueva')
      await usuario.keyboard('{Control>}{Enter}{/Control}')

      const dialogo = await screen.findByRole('dialog', { name: 'Nuevo artículo' })
      expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Cosa nueva')
    })

    it('cancelar el alta deja la fila como estaba', async () => {
      const usuario = userEvent.setup()
      montar()

      await usuario.type(campo(0, 'Detalle'), 'Cosa nueva')
      const dialogo = await abrirAlta(usuario)
      await usuario.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      expect(campo(0, 'Detalle')).toHaveValue('Cosa nueva')
      expect(within(filaDeItems(0)).getByRole('button', { name: 'Crear artículo' })).toBeInTheDocument()
    })

    it('sin proveedor el código del alta queda deshabilitado con una pista', async () => {
      const usuario = userEvent.setup()
      montar({ idProveedor: null })

      await usuario.type(campo(0, 'Detalle'), 'Cosa nueva')
      const dialogo = await abrirAlta(usuario)

      expect(within(dialogo).getByLabelText('Código del proveedor')).toBeDisabled()
      expect(within(dialogo).getByText('Elegí un proveedor en la compra para poder cargar su código.')).toBeInTheDocument()
    })

    it('un 409 codigo_proveedor_duplicado se muestra en el alta y la fila no cambia', async () => {
      apiPostMock.mockRejectedValue(new ErrorApi(409, 'codigo_proveedor_duplicado', 'Ese código ya pertenece a otro artículo del proveedor.'))
      const usuario = userEvent.setup()
      montar()

      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      const dialogo = await abrirAlta(usuario)
      await usuario.selectOptions(within(dialogo).getByLabelText('Área'), '4')
      await usuario.click(within(dialogo).getByRole('button', { name: 'Crear' }))

      expect(await within(dialogo).findByText('Ese código ya pertenece a otro artículo del proveedor.')).toBeInTheDocument()
      expect(within(dialogo).getByLabelText('Nombre')).toBeEnabled()
      expect(campo(0, 'Detalle')).toHaveValue('Yerba nueva')
    })

    it('un doble clic síncrono en Crear emite una sola request', async () => {
      let resolver: (a: ArticuloListado) => void = () => {}
      apiPostMock.mockImplementation(() => new Promise<ArticuloListado>((r) => (resolver = r)))
      const usuario = userEvent.setup()
      montar()

      await usuario.type(campo(0, 'Detalle'), 'Yerba nueva')
      const dialogo = await abrirAlta(usuario)
      await usuario.selectOptions(within(dialogo).getByLabelText('Área'), '4')

      const crear = within(dialogo).getByRole('button', { name: 'Crear' })
      act(() => {
        crear.click()
        crear.click()
      })
      expect(apiPostMock).toHaveBeenCalledTimes(1)

      await act(async () => {
        resolver(articuloFixture({ id: 77, nombre: 'Yerba nueva' }))
      })
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
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
