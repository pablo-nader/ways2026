import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CompraEditor } from './CompraEditor'
import { ROL } from '../api/tipos'
import type {
  AlicuotaIvaListado,
  CompraDetalle,
  EmpresaListado,
  ProveedorListado,
  PuntoVentaListado,
  TipoComprobanteListado,
  UsuarioAutenticado,
} from '../api/tipos'

// Precio final (precios con IVA incluido) y percepciones en el editor de compras: pre-carga desde
// proveedor y empresa, recálculo mientras el importe no se tocó, quitar/agregar y qué se envía.

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown?])),
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

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 1,
    usuario: 'admin',
    mail: 'admin@ways.test',
    rolId: ROL.Admin,
    rol: 'Admin',
    ultimaConexion: null,
    idTenant: 1,
    ...sobrescribir,
  }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

function proveedorFixture(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 1,
    razonSocial: 'Proveedor Uno SA',
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
    percibeIibb: true,
    percibeIva: true,
    preciosIncluyenIva: true,
    ...sobrescribir,
  }
}

const tipoFA: TipoComprobanteListado = {
  id: 5,
  clase: 'Compra',
  codigo: 'C-FA',
  nombre: 'Factura A de compra',
  letra: 'A',
  signo: 1,
  discriminaIva: true,
  esFiscal: false,
  afectaStock: true,
  codigoAfip: null,
  activo: true,
  registraLibroIva: true,
}

const tipoRM: TipoComprobanteListado = {
  ...tipoFA,
  id: 7,
  codigo: 'C-RM',
  nombre: 'Remito / comprobante no fiscal',
  letra: 'X',
  discriminaIva: false,
  registraLibroIva: false,
}

const alicuota21: AlicuotaIvaListado = { id: 3, nombre: '21%', porcentaje: 21, codigoAfip: null, activo: true }

const puntoVenta: PuntoVentaListado = {
  id: 2,
  idTenant: 1,
  idEmpresa: 10,
  nombre: 'Casa Central',
  domicilio: null,
  horario: null,
  whatsapp: null,
  instagram: null,
  facebook: null,
  web: null,
  nombreTenant: 'Tenant Demo',
  razonSocialEmpresa: 'Empresa Demo',
  modo: 'Web',
}

const empresa: EmpresaListado = {
  id: 10,
  idTenant: 1,
  razonSocial: 'Empresa Demo',
  nombreFantasia: null,
  cuit: null,
  nombreTenant: 'Tenant Demo',
  alicuotaPercepcionIibb: 3,
  alicuotaPercepcionIva: 1.5,
}

function compraFixture(sobrescribir: Partial<CompraDetalle> = {}): CompraDetalle {
  return {
    id: 1,
    idProveedor: 1,
    idTipoComprobante: 5,
    idPuntoVenta: 2,
    numeroExterno: '0003-00012345',
    fechaComprobante: '2026-08-01',
    fechaRecepcion: null,
    subtotal: 1210,
    descuentoTotal: 0,
    ivaTotal: 210,
    total: 1240,
    observaciones: null,
    estado: 'Borrador',
    items: [
      {
        orden: 1,
        idArticulo: null,
        descripcion: 'Total del comprobante',
        cantidad: 1,
        bultos: null,
        unidadesPorBulto: null,
        costoUnitario: 1210,
        descuento: 0,
        idAlicuotaIva: 3,
        porcentajeIva: 21,
        total: 1210,
        actualizaCosto: false,
        precioSugerido: null,
        codigoLote: null,
        fechaVencimiento: null,
        idLote: null,
      },
    ],
    idOrdenCompra: null,
    discriminaIva: true,
    alicuotas: [{ idAlicuotaIva: 3, porcentaje: 21, neto: 1000, iva: 210 }],
    preciosIncluyenIva: true,
    percepciones: [{ tipo: 'iibb', alicuota: 3, baseImponible: 1000, importe: 30 }],
    pagado: 0,
    saldoPendiente: 0,
    ...sobrescribir,
  }
}

function mockearReferencia(
  proveedores: ProveedorListado[] = [proveedorFixture()],
  sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined,
) {
  apiGetMock.mockImplementation((ruta: string) => {
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    if (ruta.startsWith('/proveedores')) {
      return Promise.resolve({ items: proveedores, total: proveedores.length, pagina: 1, tamanio: 200 })
    }
    if (ruta === '/catalogos-fiscales/tipos-comprobante') return Promise.resolve([tipoFA, tipoRM])
    if (ruta === '/catalogos-fiscales/alicuotas-iva') return Promise.resolve([alicuota21])
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVenta])
    if (ruta === '/empresas') return Promise.resolve([empresa])
    if (ruta === '/catalogos/listas-precio') return Promise.resolve([])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderEditor(idCompra: string | number) {
  return render(
    <MemoryRouter initialEntries={[`/compras/${idCompra}`]}>
      <Routes>
        <Route path="/compras/:id" element={<CompraEditor />} />
      </Routes>
    </MemoryRouter>,
  )
}

/** Compra nueva con proveedor, tipo y punto de venta elegidos en el orden en que lo hace un
 * operador: las sugerencias salen de esas tres elecciones. */
async function prepararCompraNueva(usuario: ReturnType<typeof userEvent.setup>, idTipo = '5') {
  renderEditor('nueva')
  // Esperar el DATO (las opciones cargadas), no el select que se renderiza antes del fetch.
  await screen.findByRole('option', { name: 'Proveedor Uno SA' })
  await screen.findByRole('option', { name: /C-FA/ })
  await screen.findByRole('option', { name: 'Casa Central' })
  await usuario.selectOptions(screen.getByLabelText('Proveedor'), '1')
  await usuario.selectOptions(screen.getByLabelText('Tipo'), idTipo)
  await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '2')
  await waitFor(() => expect(screen.getByRole('button', { name: 'Cargar por total' })).toBeEnabled())
}

async function cargarPorTotal(usuario: ReturnType<typeof userEvent.setup>, importe: string) {
  await usuario.click(screen.getByRole('button', { name: 'Cargar por total' }))
  await usuario.type(screen.getByLabelText('Importe total'), importe)
  await usuario.click(screen.getByRole('button', { name: 'Agregar como concepto' }))
}

function tablaDePercepciones() {
  return screen.getByRole('table', { name: 'Percepciones' })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  usuarioActual = usuarioFixture()
})

describe('CompraEditor — precios con IVA incluido', () => {
  it('elegir un proveedor cuyos precios incluyen IVA tilda el modo y el total no suma el IVA dos veces', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)

    expect(screen.getByLabelText('Precios con IVA incluido')).toBeChecked()

    await cargarPorTotal(usuario, '1210')

    // neto 1.000 + IVA 210 = lo tipeado; más las percepciones sugeridas (30 + 15) = 1.255
    const tabla = screen.getByRole('table', { name: 'Desglose de IVA' })
    const fila = within(tabla).getByText('21%').closest('tr') as HTMLElement
    expect(within(fila).getByText('$ 1.000,00')).toBeInTheDocument()
    expect(within(fila).getByLabelText('IVA impreso 21%')).toHaveValue('210,00')
    expect(screen.getByText('$ 1.255,00')).toBeInTheDocument()
  })

  it('un proveedor sin ese flag deja el modo apagado y el IVA se suma al neto tipeado', async () => {
    mockearReferencia([proveedorFixture({ preciosIncluyenIva: false, percibeIibb: false, percibeIva: false })])
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)

    expect(screen.getByLabelText('Precios con IVA incluido')).not.toBeChecked()

    await cargarPorTotal(usuario, '1000')

    expect(screen.getByText('$ 1.210,00')).toBeInTheDocument()
  })

  it('el tilde se puede cambiar a mano y descarta el IVA impreso, que pertenecía al desglose anterior', async () => {
    mockearReferencia([proveedorFixture({ percibeIibb: false, percibeIva: false })])
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)
    await cargarPorTotal(usuario, '1210')

    const campo = screen.getByLabelText('IVA impreso 21%')
    await usuario.clear(campo)
    await usuario.type(campo, '210,5')
    await usuario.tab()
    expect(screen.getByText('Calculado: $ 210,00')).toBeInTheDocument()

    await usuario.click(screen.getByLabelText('Precios con IVA incluido'))

    expect(screen.getByLabelText('Precios con IVA incluido')).not.toBeChecked()
    expect(screen.queryByText(/Calculado:/)).not.toBeInTheDocument()
    expect(screen.getByText('$ 1.464,10')).toBeInTheDocument()
  })

  it('un remito que no discrimina no ofrece el tilde y no manda el modo aunque el proveedor lo traiga', async () => {
    mockearReferencia()
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '7')
    await cargarPorTotal(usuario, '1000')

    expect(screen.queryByLabelText('Precios con IVA incluido')).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.preciosIncluyenIva).toBe(false)
    expect(cuerpo.percepciones).toBeNull()
  })

  it('al guardar manda el modo y el texto de "Cargar por total" habla del importe final', async () => {
    mockearReferencia([proveedorFixture({ percibeIibb: false, percibeIva: false })])
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)

    await usuario.click(screen.getByRole('button', { name: 'Cargar por total' }))
    expect(screen.getByText(/cargá el importe final, el IVA se discrimina/)).toBeInTheDocument()
    await usuario.type(screen.getByLabelText('Importe total'), '1210')
    await usuario.click(screen.getByRole('button', { name: 'Agregar como concepto' }))
    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.preciosIncluyenIva).toBe(true)
  })
})

describe('CompraEditor — percepciones', () => {
  it('elegir proveedor, tipo y punto de venta pre-carga IIBB e IVA con base en el neto gravado', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)
    await cargarPorTotal(usuario, '1210')

    const tabla = tablaDePercepciones()
    expect(within(tabla).getByLabelText('Base imponible Percepción IIBB')).toHaveValue('1.000,00')
    expect(within(tabla).getByLabelText('Alícuota Percepción IIBB')).toHaveValue('3,000')
    expect(within(tabla).getByLabelText('Importe Percepción IIBB')).toHaveValue('30,00')
    expect(within(tabla).getByLabelText('Importe Percepción IVA')).toHaveValue('15,00')
    expect(within(tabla).getAllByText('Sugerida')).toHaveLength(2)
  })

  it('mientras el importe no se tocó, la percepción sigue al neto cuando cambian las líneas', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)
    await cargarPorTotal(usuario, '1210')

    const costo = screen.getByLabelText('Costo unitario')
    await usuario.clear(costo)
    await usuario.type(costo, '2420')
    await usuario.tab()

    expect(within(tablaDePercepciones()).getByLabelText('Importe Percepción IIBB')).toHaveValue('60,00')
  })

  it('corregir el importe con el de la factura lo congela: un cambio de líneas ya no lo pisa', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)
    await cargarPorTotal(usuario, '1210')

    const importe = within(tablaDePercepciones()).getByLabelText('Importe Percepción IIBB')
    await usuario.clear(importe)
    await usuario.type(importe, '31,07')
    await usuario.tab()

    const costo = screen.getByLabelText('Costo unitario')
    await usuario.clear(costo)
    await usuario.type(costo, '2420')
    await usuario.tab()

    const tabla = tablaDePercepciones()
    expect(within(tabla).getByLabelText('Importe Percepción IIBB')).toHaveValue('31,07')
    // la otra sigue automática y sí se recalcula: 1,5% de 2.000
    expect(within(tabla).getByLabelText('Importe Percepción IVA')).toHaveValue('30,00')
  })

  it('quitar una percepción la retira del total y un cambio de líneas no la vuelve a agregar', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)
    await cargarPorTotal(usuario, '1210')

    const fila = within(tablaDePercepciones()).getByLabelText('Importe Percepción IIBB').closest('tr') as HTMLElement
    await usuario.click(within(fila).getByRole('button', { name: 'Quitar' }))

    expect(within(tablaDePercepciones()).queryByLabelText('Importe Percepción IIBB')).not.toBeInTheDocument()
    expect(screen.getByText('$ 1.225,00')).toBeInTheDocument()

    const costo = screen.getByLabelText('Costo unitario')
    await usuario.clear(costo)
    await usuario.type(costo, '2420')
    await usuario.tab()
    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')

    expect(within(tablaDePercepciones()).queryByLabelText('Importe Percepción IIBB')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '+ Percepción IIBB' })).toBeInTheDocument()
  })

  it('se puede agregar a mano una percepción quitada, ya congelada con la base vigente', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)
    await cargarPorTotal(usuario, '1210')
    const fila = within(tablaDePercepciones()).getByLabelText('Importe Percepción IIBB').closest('tr') as HTMLElement
    await usuario.click(within(fila).getByRole('button', { name: 'Quitar' }))

    await usuario.click(screen.getByRole('button', { name: '+ Percepción IIBB' }))

    const tabla = tablaDePercepciones()
    expect(within(tabla).getByLabelText('Importe Percepción IIBB')).toHaveValue('30,00')
    expect(within(tabla).queryAllByText('Sugerida')).toHaveLength(1)
  })

  it('al guardar manda cada percepción con el importe de la factura, base y alícuota informativas', async () => {
    mockearReferencia()
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)
    await cargarPorTotal(usuario, '1210')
    const importe = within(tablaDePercepciones()).getByLabelText('Importe Percepción IIBB')
    await usuario.clear(importe)
    await usuario.type(importe, '31,07')
    await usuario.tab()

    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.percepciones).toEqual([
      { tipo: 'iibb', baseImponible: 1000, alicuota: 3, importe: 31.07 },
      { tipo: 'iva', baseImponible: 1000, alicuota: 1.5, importe: 15 },
    ])
  })

  it('un proveedor que no percibe, o una empresa sin alícuota, no pre-carga nada pero deja agregarla a mano', async () => {
    mockearReferencia([proveedorFixture({ percibeIibb: false, percibeIva: false })])
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)

    expect(screen.queryByRole('table', { name: 'Percepciones' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '+ Percepción IIBB' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '+ Percepción IVA' })).toBeInTheDocument()
  })

  it('un tipo que no registra libro IVA no muestra el bloque de percepciones', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '7')

    expect(screen.queryByRole('table', { name: 'Percepciones' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '+ Percepción IIBB' })).not.toBeInTheDocument()
  })

  it('cambiar de factura a remito retira las sugeridas del total', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)
    await cargarPorTotal(usuario, '1210')
    expect(screen.getByText('$ 1.255,00')).toBeInTheDocument()

    await usuario.selectOptions(screen.getByLabelText('Tipo'), '7')

    expect(screen.queryByRole('table', { name: 'Percepciones' })).not.toBeInTheDocument()
    const total = screen.getByText('Total', { selector: 'div.small' }).parentElement as HTMLElement
    expect(within(total).getByText('$ 1.210,00')).toBeInTheDocument()
  })

  it('un borrador existente muestra lo guardado, sin recalcularlo, y lo reenvía al guardar', async () => {
    mockearReferencia([proveedorFixture()], (ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    apiPutMock.mockResolvedValue(compraFixture())
    const usuario = userEvent.setup()

    renderEditor(1)
    await screen.findByDisplayValue('0003-00012345')

    const tabla = tablaDePercepciones()
    expect(within(tabla).getByLabelText('Importe Percepción IIBB')).toHaveValue('30,00')
    expect(within(tabla).queryByText('Sugerida')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Precios con IVA incluido')).toBeChecked()

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.preciosIncluyenIva).toBe(true)
    expect(cuerpo.percepciones).toEqual([{ tipo: 'iibb', baseImponible: 1000, alicuota: 3, importe: 30 }])
  })

  it('una compra confirmada muestra las percepciones en solo lectura y sin controles de edición', async () => {
    mockearReferencia([proveedorFixture()], (ruta) =>
      ruta === '/compras/1' ? Promise.resolve(compraFixture({ estado: 'Confirmada' })) : undefined,
    )

    renderEditor(1)
    const tabla = await screen.findByRole('table', { name: 'Percepciones' })

    expect(within(tabla).getByText('Percepción IIBB')).toBeInTheDocument()
    expect(within(tabla).getByText('3%')).toBeInTheDocument()
    expect(within(tabla).getAllByText('$ 30,00').length).toBeGreaterThan(0)
    expect(within(tabla).queryByRole('button', { name: 'Quitar' })).not.toBeInTheDocument()
    expect(screen.getByText('Los precios del comprobante incluyen IVA.')).toBeInTheDocument()
  })

  it('el vendedor ve tipo y alícuota pero no base ni importe', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearReferencia([proveedorFixture()], (ruta) =>
      ruta === '/compras/1'
        ? Promise.resolve(
            compraFixture({
              estado: 'Confirmada',
              percepciones: [{ tipo: 'iibb', alicuota: 3, baseImponible: null, importe: null }],
            }),
          )
        : undefined,
    )

    renderEditor(1)
    const tabla = await screen.findByRole('table', { name: 'Percepciones' })

    const fila = within(tabla).getByText('Percepción IIBB').closest('tr') as HTMLElement
    expect(within(fila).getByText('3%')).toBeInTheDocument()
    expect(within(fila).queryByText(/\$ 30,00/)).not.toBeInTheDocument()
  })
})

describe('CompraEditor — borradores que arrancan de un origen y referencia que llega tarde', () => {
  const gasto = {
    id: 5,
    fecha: '2026-08-15T12:00:00Z',
    idEmpresa: 10,
    idPuntoVenta: 2,
    idProveedor: 1,
    concepto: 'Pago de mercadería',
    numeroFactura: null,
  }

  function orden(costoEstimado: number | null) {
    return {
      id: 30,
      idProveedor: 1,
      idPuntoVenta: 2,
      items: [{ orden: 1, idArticulo: 10, descripcion: 'Yerba', cantidadPedida: 7, costoUnitarioEstimado: costoEstimado }],
      cobertura: [{ idArticulo: 10, pedida: 7, recibida: 5, pendiente: 2, costoEstimado, costoReal: null, desvio: null }],
    }
  }

  async function esperarReferencia() {
    await screen.findByRole('option', { name: 'Proveedor Uno SA' })
    await screen.findByRole('option', { name: /C-FA/ })
    await screen.findByRole('option', { name: 'Casa Central' })
  }

  it('un borrador desde un gasto aplica el modo y las percepciones del proveedor al elegir el tipo', async () => {
    mockearReferencia([proveedorFixture()], (ruta) =>
      ruta === '/gastos/administracion/5' ? Promise.resolve(gasto) : undefined,
    )
    const usuario = userEvent.setup()

    renderEditor('nueva?desdeGasto=5')
    await esperarReferencia()
    await waitFor(() => expect((screen.getByLabelText('Proveedor') as HTMLSelectElement).value).toBe('1'))
    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')

    expect(screen.getByLabelText('Precios con IVA incluido')).toBeChecked()
    expect(within(tablaDePercepciones()).getByLabelText('Importe Percepción IIBB')).toBeInTheDocument()
  })

  it('un borrador desde una orden sin costos aplica el modo del proveedor', async () => {
    mockearReferencia([proveedorFixture()], (ruta) =>
      ruta === '/ordenes-compra/30' ? Promise.resolve(orden(null)) : undefined,
    )
    const usuario = userEvent.setup()

    renderEditor('nueva?idOrdenCompra=30')
    await esperarReferencia()
    await waitFor(() => expect((screen.getByLabelText('Proveedor') as HTMLSelectElement).value).toBe('1'))
    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')

    expect(screen.getByLabelText('Precios con IVA incluido')).toBeChecked()
    expect(screen.getByRole('table', { name: 'Percepciones' })).toBeInTheDocument()
  })

  it('un borrador desde una orden con costos estimados conserva el modo de precios netos', async () => {
    mockearReferencia([proveedorFixture()], (ruta) =>
      ruta === '/ordenes-compra/30' ? Promise.resolve(orden(100)) : undefined,
    )
    const usuario = userEvent.setup()

    renderEditor('nueva?idOrdenCompra=30')
    await esperarReferencia()
    await waitFor(() => expect((screen.getByLabelText('Proveedor') as HTMLSelectElement).value).toBe('1'))
    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')

    expect(screen.getByLabelText('Precios con IVA incluido')).not.toBeChecked()
    expect(screen.getByRole('table', { name: 'Percepciones' })).toBeInTheDocument()
  })

  it('las sugerencias aparecen cuando las empresas terminan de cargar, si llegaron después de elegir', async () => {
    let resolverEmpresas!: (lista: EmpresaListado[]) => void
    mockearReferencia([proveedorFixture()], (ruta) =>
      ruta === '/empresas'
        ? new Promise<EmpresaListado[]>((resolver) => {
            resolverEmpresas = resolver
          })
        : undefined,
    )
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)

    expect(screen.queryByRole('table', { name: 'Percepciones' })).not.toBeInTheDocument()

    resolverEmpresas([empresa])

    const tabla = await screen.findByRole('table', { name: 'Percepciones' })
    expect(within(tabla).getByLabelText('Alícuota Percepción IIBB')).toHaveValue('3,000')
  })

  it('la recomputación al llegar la referencia no pisa lo que el operador ya tocó', async () => {
    let resolverEmpresas!: (lista: EmpresaListado[]) => void
    mockearReferencia([proveedorFixture()], (ruta) =>
      ruta === '/empresas'
        ? new Promise<EmpresaListado[]>((resolver) => {
            resolverEmpresas = resolver
          })
        : undefined,
    )
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)
    await cargarPorTotal(usuario, '1210')
    await usuario.click(screen.getByRole('button', { name: '+ Percepción IIBB' }))
    const importe = screen.getByLabelText('Importe Percepción IIBB')
    await usuario.type(importe, '7')
    await usuario.tab()

    resolverEmpresas([empresa])

    await screen.findByLabelText('Importe Percepción IVA')
    expect(screen.getByLabelText('Importe Percepción IIBB')).toHaveValue('7,00')
  })

  it('si las empresas no cargan avisa junto a las percepciones que no se sugieren', async () => {
    mockearReferencia([proveedorFixture()], (ruta) => (ruta === '/empresas' ? Promise.reject(new Error('403')) : undefined))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)

    expect(await screen.findByText(/las percepciones no se sugieren/)).toBeInTheDocument()
    expect(screen.queryByRole('table', { name: 'Percepciones' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '+ Percepción IIBB' })).toBeInTheDocument()
  })

  it('sin error de empresas no muestra el aviso', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)

    expect(screen.queryByText(/las percepciones no se sugieren/)).not.toBeInTheDocument()
  })

  it('una percepción sugerida que da cero no se manda', async () => {
    mockearReferencia([proveedorFixture()], (ruta) =>
      ruta === '/empresas' ? Promise.resolve([{ ...empresa, alicuotaPercepcionIibb: 0 }]) : undefined,
    )
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)
    await cargarPorTotal(usuario, '1210')

    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.percepciones).toEqual([{ tipo: 'iva', baseImponible: 1000, alicuota: 1.5, importe: 15 }])
  })

  it('al elegir otro proveedor con costos ya tipeados el modo de precios no cambia', async () => {
    mockearReferencia([
      proveedorFixture({ id: 1, preciosIncluyenIva: false, percibeIibb: false, percibeIva: false }),
      proveedorFixture({ id: 2, razonSocial: 'Proveedor Dos SA', preciosIncluyenIva: true }),
    ])
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)
    await cargarPorTotal(usuario, '1000')
    expect(screen.getByLabelText('Precios con IVA incluido')).not.toBeChecked()

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '2')

    expect(screen.getByLabelText('Precios con IVA incluido')).not.toBeChecked()
    expect(within(tablaDePercepciones()).getByLabelText('Importe Percepción IIBB')).toBeInTheDocument()
  })
})
