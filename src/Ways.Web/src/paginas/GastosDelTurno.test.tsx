import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { GastosDelTurno } from './GastosDelTurno'
import type { DetalleDeTurno, GastoDeTurno, MedioPagoListado, ProveedorListado, PuntoVentaListado, TurnoResumen } from '../api/tipos'
import type { EstadoDePuntoVenta } from '../puntoVenta/PuntoVentaContext'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
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
      this.name = 'ErrorApi'
      this.estado = estado
      this.codigo = codigo
    }
  },
}))

function puntoVentaFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 7,
    idTenant: 1,
    idEmpresa: 1,
    nombre: 'Local Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: 'Tenant Demo',
    razonSocialEmpresa: 'Empresa Demo',
    ...sobrescribir,
  }
}

let estadoDePuntoVenta: EstadoDePuntoVenta
function estadoDePuntoVentaPorDefecto(): EstadoDePuntoVenta {
  const pv = puntoVentaFixture()
  return { puntosVenta: [pv], puntoVenta: pv, elegir: () => undefined, recargar: () => Promise.resolve() }
}

vi.mock('../puntoVenta/usePuntoVenta', () => ({
  usePuntoVenta: () => estadoDePuntoVenta,
}))

function turnoFixture(sobrescribir: Partial<TurnoResumen> = {}): TurnoResumen {
  return {
    id: 55,
    idPuntoVenta: 7,
    idEmpleadoApertura: 1,
    idEmpleadoCierre: null,
    fechaApertura: '2026-09-19T12:00:00Z',
    fechaCierre: null,
    fondoInicial: 0,
    estado: 'Abierto',
    observaciones: null,
    ...sobrescribir,
  }
}

const medioEfectivo: MedioPagoListado = {
  id: 1,
  nombre: 'Efectivo',
  activo: true,
  idEmpresa: null,
  orden: 1,
  comportamiento: 'Efectivo',
  admiteVuelto: true,
  requiereReferencia: false,
  recargoPorcentaje: null,
}

const medioCuentaCorriente: MedioPagoListado = {
  id: 2,
  nombre: 'Cuenta corriente',
  activo: true,
  idEmpresa: null,
  orden: 2,
  comportamiento: 'CuentaCorriente',
  admiteVuelto: false,
  requiereReferencia: false,
  recargoPorcentaje: null,
}

function proveedorFixture(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 1,
    razonSocial: 'Distribuidora Sur SRL',
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

function gastoFixture(sobrescribir: Partial<GastoDeTurno> = {}): GastoDeTurno {
  return {
    id: 1,
    idPuntoVenta: 7,
    fecha: '2026-09-19T15:30:00Z',
    categoria: 'Otros',
    idMedioPago: 1,
    importe: 300,
    ...sobrescribir,
  }
}

function detalleFixture(gastos: GastoDeTurno[] = []): DetalleDeTurno {
  return {
    resumen: {
      idTurnoCaja: 55,
      idMedioAncla: 1,
      medios: [],
      cantidadTickets: 0,
      primerTicket: null,
      ultimoTicket: null,
      ingresosPorArea: [],
      egresos: { porCategoria: [], porArea: [], retiros: 0 },
    },
    tickets: [],
    gastos,
  }
}

function mockearRutas(opciones: {
  turno?: TurnoResumen | null
  medios?: MedioPagoListado[]
  proveedores?: ProveedorListado[]
  detalle?: DetalleDeTurno
  errorTurno?: unknown
  errorMedios?: unknown
  errorProveedores?: unknown
  errorDetalle?: unknown
}) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/caja/turnos/abierto')) {
      if (opciones.errorTurno) return Promise.reject(opciones.errorTurno)
      return Promise.resolve(opciones.turno === undefined ? turnoFixture() : opciones.turno)
    }
    if (ruta === '/catalogos/medios-pago') {
      if (opciones.errorMedios) return Promise.reject(opciones.errorMedios)
      return Promise.resolve(opciones.medios ?? [medioEfectivo, medioCuentaCorriente])
    }
    if (ruta === '/proveedores?tamanio=200') {
      if (opciones.errorProveedores) return Promise.reject(opciones.errorProveedores)
      return Promise.resolve({ items: opciones.proveedores ?? [proveedorFixture()], total: 1, pagina: 1, tamanio: 200 })
    }
    if (ruta.startsWith('/caja/turnos/') && ruta.endsWith('/detalle')) {
      if (opciones.errorDetalle) return Promise.reject(opciones.errorDetalle)
      return Promise.resolve(opciones.detalle ?? detalleFixture())
    }
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  estadoDePuntoVenta = estadoDePuntoVentaPorDefecto()
})

describe('GastosDelTurno — sin turno abierto', () => {
  it('muestra un aviso y no ofrece el formulario cuando el punto de venta no tiene turno abierto', async () => {
    mockearRutas({ turno: null })
    render(<GastosDelTurno />)

    await screen.findByText('No hay un turno abierto en este punto de venta.')
    expect(screen.queryByLabelText('Importe')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Registrar/ })).not.toBeInTheDocument()
  })
})

describe('GastosDelTurno — medio de pago', () => {
  it('excluye del selector los medios de comportamiento CuentaCorriente', async () => {
    mockearRutas({ medios: [medioEfectivo, medioCuentaCorriente] })
    render(<GastosDelTurno />)

    await screen.findByLabelText('Importe')
    expect(screen.getByRole('option', { name: 'Efectivo' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'Cuenta corriente' })).not.toBeInTheDocument()
  })
})

describe('GastosDelTurno — categoría auto-switch', () => {
  it('elegir un proveedor cambia la categoría a Proveedores', async () => {
    mockearRutas({})
    render(<GastosDelTurno />)

    await screen.findByLabelText('Importe')
    expect(screen.getByLabelText('Categoría')).toHaveValue('Otros')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')

    expect(screen.getByLabelText('Categoría')).toHaveValue('Proveedor')
  })

  it('limpiar el proveedor mientras la categoría sigue en Proveedores la vuelve a Otros', async () => {
    mockearRutas({})
    render(<GastosDelTurno />)

    await screen.findByLabelText('Importe')
    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')
    expect(screen.getByLabelText('Categoría')).toHaveValue('Proveedor')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Sin proveedor')

    expect(screen.getByLabelText('Categoría')).toHaveValue('Otros')
  })

  it('cambiar la categoría a mano después de elegir un proveedor y luego limpiarlo no la resetea', async () => {
    mockearRutas({})
    render(<GastosDelTurno />)

    await screen.findByLabelText('Importe')
    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')
    await userEvent.selectOptions(screen.getByLabelText('Categoría'), 'Servicios')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Sin proveedor')

    expect(screen.getByLabelText('Categoría')).toHaveValue('Servicios')
  })
})

describe('GastosDelTurno — alta', () => {
  async function completarFormulario() {
    await screen.findByLabelText('Importe')
    await userEvent.type(screen.getByLabelText('Importe'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
  }

  it('arma el cuerpo del POST con el concepto de las observaciones recortadas', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<GastosDelTurno />)

    await completarFormulario()
    await userEvent.type(screen.getByLabelText('Observaciones (opcional)'), '  Flete de mercadería  ')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith(
        '/gastos',
        expect.objectContaining({
          idPuntoVenta: 7,
          importe: 500,
          idMedioPago: 1,
          categoria: 'Otros',
          idProveedor: null,
          concepto: 'Flete de mercadería',
          detalle: null,
          idArea: null,
          numeroFactura: null,
          idComprobanteCompra: null,
        }),
      ),
    )
  })

  it('sin observaciones, el concepto cae al texto fijo "Gasto del turno"', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<GastosDelTurno />)

    await completarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/gastos', expect.objectContaining({ concepto: 'Gasto del turno' })),
    )
  })

  it('al confirmar con éxito limpia el formulario, muestra la confirmación y refresca el listado', async () => {
    mockearRutas({ detalle: detalleFixture() })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<GastosDelTurno />)

    await completarFormulario()
    await userEvent.type(screen.getByLabelText('Observaciones (opcional)'), 'Pago de flete')

    // El refresco posterior al alta trae el gasto recién creado.
    mockearRutas({ detalle: detalleFixture([gastoFixture({ id: 9, importe: 500 })]) })
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('Gasto registrado.')
    expect(screen.getByLabelText('Importe')).toHaveValue('')
    expect(screen.getByLabelText('Observaciones (opcional)')).toHaveValue('')
    expect(screen.getByLabelText('Categoría')).toHaveValue('Otros')
    await screen.findByText('$ 500,00')
  })

  it('un error del servidor se muestra inline y el formulario no se limpia', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutas({})
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'turno_no_abierto', 'El turno ya no está abierto.'))
    render(<GastosDelTurno />)

    await completarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('El turno ya no está abierto.')
    expect(screen.getByLabelText('Importe')).toHaveValue('500,00')
  })

  it('un importe en blanco o en cero se rechaza sin llegar a pegarle al servidor', async () => {
    mockearRutas({})
    render(<GastosDelTurno />)

    await screen.findByLabelText('Importe')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('El importe tiene que ser mayor a 0.')
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un doble click sobre "Registrar" en el mismo tick dispara una sola solicitud (guarda de reentrancia)', async () => {
    mockearRutas({})
    let resolverPost: (valor: unknown) => void = () => undefined
    apiPostMock.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolverPost = resolve
        }),
    )
    render(<GastosDelTurno />)

    await completarFormulario()
    const boton = screen.getByRole('button', { name: 'Registrar' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    resolverPost({ id: 1 })
  })
})

describe('GastosDelTurno — listado', () => {
  it('lista los gastos del turno con hora, categoría, medio y el total', async () => {
    mockearRutas({
      medios: [medioEfectivo],
      detalle: detalleFixture([
        gastoFixture({ id: 1, categoria: 'Servicios', idMedioPago: 1, importe: 300 }),
        gastoFixture({ id: 2, categoria: 'Viaticos', idMedioPago: 1, importe: 150 }),
      ]),
    })
    render(<GastosDelTurno />)

    const tabla = await screen.findByRole('table')
    expect(within(tabla).getByText('Servicios')).toBeInTheDocument()
    expect(within(tabla).getByText('Viáticos')).toBeInTheDocument()
    expect(within(tabla).getAllByText('Efectivo').length).toBe(2)
    expect(within(tabla).getByText('$ 300,00')).toBeInTheDocument()
    expect(within(tabla).getByText('$ 150,00')).toBeInTheDocument()
    expect(screen.getByText('Total: $ 450,00')).toBeInTheDocument()
  })

  it('sin gastos, muestra el mensaje de lista vacía y el total en 0', async () => {
    mockearRutas({ detalle: detalleFixture([]) })
    render(<GastosDelTurno />)

    await screen.findByText('Este turno todavía no tiene gastos.')
    expect(screen.getByText('Total: $ 0,00')).toBeInTheDocument()
  })
})
