import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CompraEditor } from './CompraEditor'
import { ROL } from '../api/tipos'
import type {
  AlicuotaIvaListado,
  CompraDetalle,
  CompraListada,
  ItemDeCompra,
  ListaPrecioListado,
  ProveedorListado,
  PuntoVentaListado,
  TipoComprobanteListado,
  UsuarioAutenticado,
} from '../api/tipos'

const apiGetMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
  },
  ErrorApi: class ErrorApiMock extends Error {},
}))

const usuario: UsuarioAutenticado = {
  id: 1,
  usuario: 'admin',
  mail: 'admin@ways.test',
  rolId: ROL.Admin,
  rol: 'Admin',
  ultimaConexion: null,
  idTenant: 1,
}

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

const ID_FACTURA = 5
const ID_NOTA = 6
const ID_OTRA_NOTA = 8
const ID_COMPRA_FACTURA_ASOCIABLE = 77

function tipo(sobrescribir: Partial<TipoComprobanteListado>): TipoComprobanteListado {
  return {
    id: ID_FACTURA,
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
    ...sobrescribir,
  }
}

const TIPOS = [
  tipo({}),
  tipo({ id: ID_NOTA, codigo: 'C-NCA', nombre: 'Nota de crédito A de compra', signo: -1 }),
  tipo({ id: ID_OTRA_NOTA, codigo: 'C-NCB', nombre: 'Nota de crédito B de compra', signo: -1 }),
]

function proveedor(id: number, razonSocial: string): ProveedorListado {
  return {
    id,
    razonSocial,
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

// Los dos puntos de venta son de la misma empresa: el selector no vuelve a pedir facturas al cambiar de uno a otro,
// así que solo la limpieza del editor puede vaciar la selección.
function puntoVenta(id: number, nombre: string): PuntoVentaListado {
  return {
    id,
    idTenant: 1,
    idEmpresa: 1,
    nombre,
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
}

const PROVEEDORES = [proveedor(1, 'Proveedor Uno SA'), proveedor(2, 'Proveedor Dos SA')]
const PUNTOS_VENTA = [puntoVenta(2, 'Casa Central'), puntoVenta(3, 'Sucursal Norte')]
const ALICUOTAS: AlicuotaIvaListado[] = [{ id: 3, nombre: '21%', porcentaje: 21, codigoAfip: null, activo: true }]
const LISTAS: ListaPrecioListado[] = [
  { id: 1, nombre: 'Lista General', activo: true, idEmpresa: null, esDefault: true, modo: 'Fija', idListaBase: null, porcentaje: null },
]

const ITEM: ItemDeCompra = {
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
  impuestoInterno: 0,
  actualizaCosto: true,
  precioSugerido: 114.95,
  codigoLote: null,
  fechaVencimiento: null,
  idLote: null,
  codigoProveedor: null,
}

function compra(sobrescribir: Partial<CompraDetalle> = {}): CompraDetalle {
  return {
    id: 1,
    idProveedor: 1,
    idTipoComprobante: ID_NOTA,
    idPuntoVenta: 2,
    numeroExterno: '0003-00012345',
    fechaComprobante: '2026-08-01',
    fechaRecepcion: null,
    subtotal: 1000,
    descuentoTotal: 50,
    ivaTotal: 199.5,
    impuestosInternos: 0,
    total: 1149.5,
    observaciones: null,
    estado: 'Borrador',
    origen: 'Manual',
    items: [ITEM],
    idOrdenCompra: null,
    discriminaIva: true,
    alicuotas: [{ idAlicuotaIva: 3, porcentaje: 21, neto: 950, iva: 199.5 }],
    preciosIncluyenIva: false,
    percepciones: [],
    pagado: 0,
    saldoPendiente: 0,
    ...sobrescribir,
  }
}

const FACTURA_ASOCIABLE: CompraListada = {
  id: ID_COMPRA_FACTURA_ASOCIABLE,
  idProveedor: 1,
  idTipoComprobante: ID_FACTURA,
  numeroExterno: '0001-00000099',
  estado: 'Confirmada',
  fechaRecepcion: '2026-07-01T12:00:00Z',
  total: 500,
  saldoPendiente: 0,
  origen: 'Manual',
  nombreProveedor: 'Proveedor Uno SA',
  fechaComprobante: '2026-07-01',
  estadoPago: 'Pagada',
}

function mockearApi(detalle: CompraDetalle) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/proveedores')) return Promise.resolve({ items: PROVEEDORES, total: PROVEEDORES.length, pagina: 1, tamanio: 200 })
    if (ruta === '/catalogos-fiscales/tipos-comprobante') return Promise.resolve(TIPOS)
    if (ruta === '/catalogos-fiscales/alicuotas-iva') return Promise.resolve(ALICUOTAS)
    if (ruta === '/puntos-venta') return Promise.resolve(PUNTOS_VENTA)
    if (ruta === '/catalogos/listas-precio') return Promise.resolve(LISTAS)
    if (ruta === '/compras/1') return Promise.resolve(detalle)
    if (ruta.startsWith('/compras?')) return Promise.resolve({ items: [FACTURA_ASOCIABLE], total: 1, pagina: 1, tamanio: 200 })
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderEditor() {
  return render(
    <MemoryRouter initialEntries={['/compras/1']}>
      <Routes>
        <Route path="/compras/:id" element={<CompraEditor />} />
      </Routes>
    </MemoryRouter>,
  )
}

const ETIQUETA_FACTURA = new RegExp(`C-FA 0001-00000099`)

/** Espera a que lleguen las facturas del proveedor (los datos, no el select, que se pinta antes) y elige la asociada. */
async function elegirFacturaAsociada(usr: ReturnType<typeof userEvent.setup>) {
  const opcion = await screen.findByRole('option', { name: ETIQUETA_FACTURA })
  const selector = screen.getByLabelText('Factura asociada (opcional)')
  await usr.selectOptions(selector, opcion)
  expect(selector).toHaveValue(String(ID_COMPRA_FACTURA_ASOCIABLE))
  return selector
}

beforeEach(() => {
  apiGetMock.mockReset()
})

describe('CompraEditor — nota de crédito', () => {
  it('el selector de factura asociada aparece con un tipo de nota de crédito y no con una factura', async () => {
    mockearApi(compra({ idTipoComprobante: ID_FACTURA }))
    const usr = userEvent.setup()
    renderEditor()

    const tipoSelect = await screen.findByLabelText('Tipo')
    expect(await screen.findByRole('option', { name: 'Nota de crédito A de compra' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Factura asociada (opcional)')).not.toBeInTheDocument()

    await usr.selectOptions(tipoSelect, String(ID_NOTA))
    expect(await screen.findByLabelText('Factura asociada (opcional)')).toBeInTheDocument()

    await usr.selectOptions(tipoSelect, String(ID_FACTURA))
    expect(screen.queryByLabelText('Factura asociada (opcional)')).not.toBeInTheDocument()
  })

  it('cambiar el punto de venta descarta la factura asociada elegida', async () => {
    mockearApi(compra())
    const usr = userEvent.setup()
    renderEditor()

    const selector = await elegirFacturaAsociada(usr)
    await usr.selectOptions(screen.getByLabelText('Punto de venta'), '3')

    expect(screen.getByLabelText('Punto de venta')).toHaveValue('3')
    expect(selector).toHaveValue('')
  })

  it('cambiar el proveedor descarta la factura asociada elegida', async () => {
    mockearApi(compra())
    const usr = userEvent.setup()
    renderEditor()

    await elegirFacturaAsociada(usr)
    await usr.selectOptions(screen.getByLabelText('Proveedor'), '2')

    expect(screen.getByLabelText('Proveedor')).toHaveValue('2')
    // Se espera a que lleguen las facturas del nuevo proveedor: mientras carga, el select está vacío de todos modos.
    await screen.findByRole('option', { name: ETIQUETA_FACTURA })
    expect(screen.getByLabelText('Factura asociada (opcional)')).toHaveValue('')
  })

  it('cambiar el tipo a otra nota de crédito descarta la factura asociada elegida', async () => {
    mockearApi(compra())
    const usr = userEvent.setup()
    renderEditor()

    await elegirFacturaAsociada(usr)
    await usr.selectOptions(screen.getByLabelText('Tipo'), String(ID_OTRA_NOTA))

    expect(screen.getByLabelText('Tipo')).toHaveValue(String(ID_OTRA_NOTA))
    expect(screen.getByLabelText('Factura asociada (opcional)')).toHaveValue('')
  })

  it('el total editable se rotula "Total a favor (crédito)" solo con una nota de crédito', async () => {
    mockearApi(compra())
    const usr = userEvent.setup()
    renderEditor()

    expect(await screen.findByText('Total a favor (crédito)')).toBeInTheDocument()

    await usr.selectOptions(screen.getByLabelText('Tipo'), String(ID_FACTURA))
    expect(screen.queryByText('Total a favor (crédito)')).not.toBeInTheDocument()
    expect(screen.getAllByText('Total').length).toBeGreaterThan(0)
  })

  it('una nota de crédito confirmada no ofrece Editar compra ni Pagar, y una factura con saldo sí', async () => {
    const confirmada = { estado: 'Confirmada', fechaRecepcion: '2026-08-05T12:00:00Z', saldoPendiente: 500 } as const
    mockearApi(compra({ ...confirmada, idTipoComprobante: ID_NOTA }))
    const { unmount } = renderEditor()

    expect(await screen.findByText('Total a favor (crédito)')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Editar compra' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Pagar' })).not.toBeInTheDocument()
    unmount()

    // Control: con los mismos datos, una factura confirmada con saldo muestra ambas acciones.
    mockearApi(compra({ ...confirmada, idTipoComprobante: ID_FACTURA }))
    renderEditor()

    expect(await screen.findByRole('button', { name: 'Editar compra' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Pagar' })).toBeInTheDocument()
  })
})
