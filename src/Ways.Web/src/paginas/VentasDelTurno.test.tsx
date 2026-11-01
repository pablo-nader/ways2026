import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { VentasDelTurno } from './VentasDelTurno'
import type {
  ComprobanteEmitido,
  DatosDeFacturaFiscal,
  FiscalDeVenta,
  MedioPagoListado,
  PuntoVentaListado,
  TurnoResumen,
  UsuarioAutenticado,
  VentaDeTurnoListado,
} from '../api/tipos'
import { ROL } from '../api/tipos'
import { Toaster } from '../toasts/Toaster'
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
    modo: 'Web',
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

let usuarioActual: UsuarioAutenticado
function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 1,
    usuario: 'cajera1',
    mail: 'cajera1@ways.test',
    rolId: ROL.Vendedor,
    rol: 'Vendedor',
    ultimaConexion: null,
    idTenant: 1,
    ...sobrescribir,
  }
}

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

function turnoFixture(sobrescribir: Partial<TurnoResumen> = {}): TurnoResumen {
  return {
    id: 55,
    idPuntoVenta: 7,
    idEmpleadoApertura: 1,
    idEmpleadoCierre: null,
    fechaApertura: '2026-09-16T12:00:00Z',
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
  porcentajeFacturacion: 0,
}

function ventaFixture(sobrescribir: Partial<VentaDeTurnoListado> = {}): VentaDeTurnoListado {
  return {
    id: 1,
    numero: 1,
    numeroVisible: '0007-00000001',
    estado: 'Emitido',
    fecha: '2026-09-16T12:05:00Z',
    idCliente: 3,
    nombreCliente: 'Consumidor Final',
    total: 100,
    descuentoManualTotal: 0,
    recargoManualTotal: 0,
    mediosDePago: [{ idMedioPago: 1, nombre: 'Efectivo', importe: 100 }],
    ...sobrescribir,
  }
}

function comprobanteFixture(sobrescribir: Partial<ComprobanteEmitido> = {}): ComprobanteEmitido {
  return {
    id: 1,
    numero: 1,
    numeroVisible: '0007-00000001',
    estado: 'Emitido',
    fecha: '2026-09-16T12:05:00Z',
    idPuntoVenta: 7,
    idCliente: 3,
    idComprobanteAsociado: null,
    subtotal: 100,
    descuentoTotal: 0,
    descuentoManualTotal: 0,
    recargoManualTotal: 0,
    total: 100,
    direccionEntrega: null,
    observaciones: null,
    items: [
      {
        orden: 1,
        idArticulo: 1,
        descripcion: 'Coca Cola 1L',
        codigoBarra: null,
        idArea: 1,
        idListaPrecio: 1,
        idOferta: null,
        idAlicuotaIva: 1,
        porcentajeIva: 21,
        cantidad: 1,
        precioUnitario: 100,
        descuento: 0,
        total: 100,
        idLote: null,
        codigoLote: null,
        loteVencido: false,
        ajusteManualPorcentaje: null,
        ajusteManual: 0,
      },
    ],
    pagos: [{ idMedioPago: 1, importe: 100, referencia: null, vuelto: 0 }],
    idPresupuestoOrigen: null,
    ...sobrescribir,
  }
}

function mockearRutas(opciones: {
  turno?: TurnoResumen | null
  ventas?: VentaDeTurnoListado[]
  errorTurno?: unknown
  errorVentas?: unknown
  medios?: MedioPagoListado[]
  errorMedios?: unknown
  comprobantes?: Record<number, ComprobanteEmitido>
  erroresComprobante?: Record<number, unknown>
}) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/caja/turnos/abierto')) {
      if (opciones.errorTurno) return Promise.reject(opciones.errorTurno)
      return Promise.resolve(opciones.turno ?? null)
    }
    if (ruta.startsWith('/ventas/por-turno/')) {
      if (opciones.errorVentas) return Promise.reject(opciones.errorVentas)
      return Promise.resolve(opciones.ventas ?? [])
    }
    if (ruta === '/catalogos/medios-pago') {
      if (opciones.errorMedios) return Promise.reject(opciones.errorMedios)
      return Promise.resolve(opciones.medios ?? [])
    }
    const matchDetalle = /^\/ventas\/(\d+)$/.exec(ruta)
    if (matchDetalle) {
      const id = Number(matchDetalle[1])
      if (opciones.erroresComprobante?.[id]) return Promise.reject(opciones.erroresComprobante[id])
      const comprobante = opciones.comprobantes?.[id]
      if (comprobante) return Promise.resolve(comprobante)
      return Promise.reject(new Error(`comprobante no mockeado en el test: ${id}`))
    }
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  estadoDePuntoVenta = estadoDePuntoVentaPorDefecto()
  usuarioActual = usuarioFixture()
})

describe('VentasDelTurno — sin turno abierto', () => {
  it('muestra un aviso cuando el punto de venta no tiene turno abierto', async () => {
    mockearRutas({ turno: null })
    render(<VentasDelTurno />)

    await screen.findByText('No hay un turno abierto en este punto de venta.')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })
})

describe('VentasDelTurno — listado', () => {
  it('lista las ventas del turno con cliente, medios de pago y estado', async () => {
    mockearRutas({
      turno: turnoFixture(),
      ventas: [
        ventaFixture({ id: 1, numeroVisible: '0007-00000001', total: 100, nombreCliente: 'Consumidor Final' }),
        ventaFixture({
          id: 2,
          numeroVisible: '0007-00000002',
          total: 50,
          estado: 'Anulado',
          mediosDePago: [{ idMedioPago: 2, nombre: 'Tarjeta', importe: 50 }],
          nombreCliente: 'Juan Pérez',
        }),
      ],
    })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000001')
    expect(screen.getByText('0007-00000002')).toBeInTheDocument()
    expect(screen.getByText('Consumidor Final')).toBeInTheDocument()
    expect(screen.getByText('Juan Pérez')).toBeInTheDocument()
    expect(screen.getByText('Anulada', { selector: 'span' })).toBeInTheDocument()

    // Totales arriba de la tabla: solo cuenta la no anulada (regla bajo prueba de mutation-proof-tests).
    expect(screen.getByText('Efectivo: $ 100,00')).toBeInTheDocument()
    expect(screen.getByText('Total general: $ 100,00')).toBeInTheDocument()
    expect(screen.getByText('1 venta')).toBeInTheDocument()
  })

  it('marca con "Desc. manual" y/o "Recargo" las ventas con el precio cambiado a mano, y deja sin marca a las demás', async () => {
    mockearRutas({
      turno: turnoFixture(),
      ventas: [
        ventaFixture({ id: 1, numeroVisible: '0007-00000001' }),
        ventaFixture({ id: 2, numeroVisible: '0007-00000002', descuentoManualTotal: 25 }),
        ventaFixture({ id: 3, numeroVisible: '0007-00000003', recargoManualTotal: 15 }),
        ventaFixture({ id: 4, numeroVisible: '0007-00000004', descuentoManualTotal: 10, recargoManualTotal: 5 }),
      ],
    })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000001')
    const fila = (numero: string) => screen.getByText(numero).closest('tr') as HTMLElement

    expect(within(fila('0007-00000001')).queryByText('Desc. manual')).not.toBeInTheDocument()
    expect(within(fila('0007-00000001')).queryByText('Recargo')).not.toBeInTheDocument()

    expect(within(fila('0007-00000002')).getByText('Desc. manual')).toHaveAttribute('title', 'Descuento manual: $ 25,00')
    expect(within(fila('0007-00000002')).queryByText('Recargo')).not.toBeInTheDocument()

    expect(within(fila('0007-00000003')).getByText('Recargo')).toHaveAttribute('title', 'Recargo manual: $ 15,00')
    expect(within(fila('0007-00000003')).queryByText('Desc. manual')).not.toBeInTheDocument()

    expect(within(fila('0007-00000004')).getByText('Desc. manual')).toBeInTheDocument()
    expect(within(fila('0007-00000004')).getByText('Recargo')).toBeInTheDocument()
  })

  it('no ofrece "Anular" sobre una fila ya anulada', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: [ventaFixture({ id: 9, estado: 'Anulado' })] })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000001')
    expect(screen.queryByRole('button', { name: 'Anular' })).not.toBeInTheDocument()
  })
})

describe('VentasDelTurno — totales por medio arriba de la tabla', () => {
  it('un badge por medio + total general, excluyendo anuladas', async () => {
    mockearRutas({
      turno: turnoFixture(),
      ventas: [
        ventaFixture({
          id: 1,
          numeroVisible: '0007-00000001',
          total: 150,
          mediosDePago: [
            { idMedioPago: 1, nombre: 'Efectivo', importe: 100 },
            { idMedioPago: 2, nombre: 'Tarjeta', importe: 50 },
          ],
        }),
        ventaFixture({
          id: 2,
          numeroVisible: '0007-00000002',
          total: 900,
          estado: 'Anulado',
          mediosDePago: [{ idMedioPago: 1, nombre: 'Efectivo', importe: 900 }],
        }),
      ],
    })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000001')
    expect(screen.getByText('Efectivo: $ 100,00')).toBeInTheDocument()
    expect(screen.getByText('Tarjeta: $ 50,00')).toBeInTheDocument()
    expect(screen.getByText('Total general: $ 150,00')).toBeInTheDocument()
    expect(screen.getByText('1 venta')).toBeInTheDocument()
  })

  it('los totales siguen a los filtros — al filtrar por medio, el total general baja con la fila', async () => {
    mockearRutas({
      turno: turnoFixture(),
      ventas: [
        ventaFixture({ id: 1, numeroVisible: '0007-00000001', total: 100, mediosDePago: [{ idMedioPago: 1, nombre: 'Efectivo', importe: 100 }] }),
        ventaFixture({ id: 2, numeroVisible: '0007-00000002', total: 250, mediosDePago: [{ idMedioPago: 2, nombre: 'Tarjeta', importe: 250 }] }),
      ],
    })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')
    expect(screen.getByText('Total general: $ 350,00')).toBeInTheDocument()

    await userEvent.selectOptions(screen.getByLabelText('Filtrar por medio de pago'), 'Tarjeta')

    expect(screen.queryByText('0007-00000001')).not.toBeInTheDocument()
    expect(screen.getByText('0007-00000002')).toBeInTheDocument()
    expect(screen.getByText('Total general: $ 250,00')).toBeInTheDocument()
    expect(screen.queryByText(/Efectivo:/)).not.toBeInTheDocument()
  })
})

describe('VentasDelTurno — filtros', () => {
  function ventasDeFiltro(): VentaDeTurnoListado[] {
    return [
      ventaFixture({
        id: 1,
        numeroVisible: '0007-00000001',
        nombreCliente: 'Consumidor Final',
        total: 100,
        estado: 'Emitido',
        mediosDePago: [{ idMedioPago: 1, nombre: 'Efectivo', importe: 100 }],
      }),
      ventaFixture({
        id: 2,
        numeroVisible: '0007-00000002',
        nombreCliente: 'Juan Pérez',
        total: 500,
        estado: 'Anulado',
        mediosDePago: [{ idMedioPago: 2, nombre: 'Tarjeta', importe: 500 }],
      }),
    ]
  }

  it('filtra por número (contains)', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: ventasDeFiltro() })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')

    await userEvent.type(screen.getByLabelText('Filtrar por número'), '00000002')

    expect(screen.queryByText('0007-00000001')).not.toBeInTheDocument()
    expect(screen.getByText('0007-00000002')).toBeInTheDocument()
  })

  it('filtra por cliente (contains, sin distinguir acentos/mayúsculas)', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: ventasDeFiltro() })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')

    await userEvent.type(screen.getByLabelText('Filtrar por cliente'), 'perez')

    expect(screen.queryByText('0007-00000001')).not.toBeInTheDocument()
    expect(screen.getByText('Juan Pérez')).toBeInTheDocument()
  })

  it('filtra por total mínimo y máximo', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: ventasDeFiltro() })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')

    await userEvent.type(screen.getByLabelText('Total mínimo'), '200')

    expect(screen.queryByText('0007-00000001')).not.toBeInTheDocument()
    expect(screen.getByText('0007-00000002')).toBeInTheDocument()

    await userEvent.clear(screen.getByLabelText('Total mínimo'))
    await userEvent.type(screen.getByLabelText('Total máximo'), '200')

    expect(screen.getByText('0007-00000001')).toBeInTheDocument()
    expect(screen.queryByText('0007-00000002')).not.toBeInTheDocument()
  })

  // JD-E1-1 (judgment-day ronda 0): los campos de total son `CampoImporte`, el mismo componente
  // que Pos/CierreDeCaja — nunca un `<input type="number">` con `Number(e.target.value)`, que
  // rechaza "150,50" (NaN). Estos dos tests pasan por el COMPONENTE (tipeo real, no el helper
  // puro) — mutation-proof-tests: volver a `Number(...)` los hace fallar (evidencia registrada en
  // el reporte de la tarea).
  it('el filtro de total interpreta "1500" como mil quinientos, no como un decimal', async () => {
    mockearRutas({
      turno: turnoFixture(),
      ventas: [
        ventaFixture({ id: 1, numeroVisible: '0007-00000001', total: 1499 }),
        ventaFixture({ id: 2, numeroVisible: '0007-00000002', total: 1500 }),
      ],
    })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')

    await userEvent.type(screen.getByLabelText('Total mínimo'), '1500')

    // Con un total mínimo de 1,5 las DOS filas pasarían el filtro — acá la de 1499 queda afuera
    // y la de 1500 (el límite exacto) adentro.
    expect(screen.queryByText('0007-00000001')).not.toBeInTheDocument()
    expect(screen.getByText('0007-00000002')).toBeInTheDocument()
  })

  it('el filtro de total acepta coma decimal ("150,50")', async () => {
    mockearRutas({
      turno: turnoFixture(),
      ventas: [
        ventaFixture({ id: 1, numeroVisible: '0007-00000001', total: 150 }),
        ventaFixture({ id: 2, numeroVisible: '0007-00000002', total: 151 }),
      ],
    })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')

    await userEvent.type(screen.getByLabelText('Total máximo'), '150,50')

    // Con `Number('150,50')` (NaN, el bug original) el filtro no habría excluido nada.
    expect(screen.getByText('0007-00000001')).toBeInTheDocument()
    expect(screen.queryByText('0007-00000002')).not.toBeInTheDocument()
  })

  it('filtra por medio de pago (select con los medios presentes en el turno)', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: ventasDeFiltro() })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')

    const select = screen.getByLabelText('Filtrar por medio de pago')
    expect(within(select).getByRole('option', { name: 'Efectivo' })).toBeInTheDocument()
    expect(within(select).getByRole('option', { name: 'Tarjeta' })).toBeInTheDocument()

    await userEvent.selectOptions(select, 'Efectivo')

    expect(screen.getByText('0007-00000001')).toBeInTheDocument()
    expect(screen.queryByText('0007-00000002')).not.toBeInTheDocument()
  })

  it('filtra por estado (Todas/Emitida/Anulada)', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: ventasDeFiltro() })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')

    await userEvent.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Anulada')

    expect(screen.queryByText('0007-00000001')).not.toBeInTheDocument()
    expect(screen.getByText('0007-00000002')).toBeInTheDocument()
  })

  it('"Limpiar filtros" restaura el listado completo y queda deshabilitado sin filtros activos', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: ventasDeFiltro() })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')

    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()

    await userEvent.type(screen.getByLabelText('Filtrar por número'), '00000002')
    expect(screen.queryByText('0007-00000001')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeEnabled()

    await userEvent.click(screen.getByRole('button', { name: 'Limpiar filtros' }))

    expect(screen.getByText('0007-00000001')).toBeInTheDocument()
    expect(screen.getByText('0007-00000002')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()
  })

  it('turno sin ventas: "Este turno todavía no tiene ventas."', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: [] })
    render(<VentasDelTurno />)
    await screen.findByText('Este turno todavía no tiene ventas.')
  })

  it('turno con ventas pero ningún filtro coincide: "Ninguna venta coincide con los filtros." (nunca el mensaje de turno vacío)', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: ventasDeFiltro() })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')

    await userEvent.type(screen.getByLabelText('Filtrar por número'), 'no existe ninguna así')

    expect(screen.queryByText('Este turno todavía no tiene ventas.')).not.toBeInTheDocument()
    await screen.findByText('Ninguna venta coincide con los filtros.')
  })
})

describe('VentasDelTurno — anulación', () => {
  it('anula con éxito: el modal pide confirmación, y al confirmar la fila pasa a Anulada y los totales se actualizan', async () => {
    const venta = ventaFixture({ id: 3, numeroVisible: '0007-00000003', total: 200 })
    mockearRutas({ turno: turnoFixture(), ventas: [venta] })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000003')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))

    // El modal nombra la venta y el total antes de confirmar.
    expect(screen.getByText(/¿Anular la venta 0007-00000003 por \$ 200,00\?/)).toBeInTheDocument()

    apiPostMock.mockResolvedValueOnce({ ...venta, estado: 'Anulado' })
    // El refresco posterior a la anulación reconsulta el listado.
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/caja/turnos/abierto')) return Promise.resolve(turnoFixture())
      if (ruta.startsWith('/ventas/por-turno/')) return Promise.resolve([{ ...venta, estado: 'Anulado' }])
      if (ruta === '/catalogos/medios-pago') return Promise.resolve([])
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })

    await userEvent.click(screen.getByRole('button', { name: 'Anular venta' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/ventas/3/anulacion'))
    await waitFor(() => expect(screen.getByText('Anulada', { selector: 'span' })).toBeInTheDocument())
    expect(screen.getByText('Total general: $ 0,00')).toBeInTheDocument()
    expect(screen.getByText('0 ventas')).toBeInTheDocument()
  })

  it('muestra el error dentro del modal cuando la anulación falla', async () => {
    const { ErrorApi } = await import('../api/cliente')
    const venta = ventaFixture({ id: 4 })
    mockearRutas({ turno: turnoFixture(), ventas: [venta] })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000001')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))

    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'turno_cerrado', 'El turno ya está cerrado.'))
    await userEvent.click(screen.getByRole('button', { name: 'Anular venta' }))

    const dialogo = screen.getByRole('dialog', { name: 'Confirmar anulación' })
    expect(await within(dialogo).findByText('El turno ya está cerrado.')).toBeInTheDocument()
    // La fila NO queda anulada: el error no se confunde con éxito.
    expect(screen.getByText('Emitida', { selector: 'span' })).toBeInTheDocument()
  })

  it('Cancelar después de un error de anulación limpia el aviso — no queda un banner huérfano', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutas({ turno: turnoFixture(), ventas: [ventaFixture({ id: 10 })] })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000001')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))

    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'turno_cerrado', 'El turno ya está cerrado.'))
    await userEvent.click(screen.getByRole('button', { name: 'Anular venta' }))
    await screen.findByText('El turno ya está cerrado.')

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByText('El turno ya está cerrado.')).not.toBeInTheDocument()
  })

  it('Escape después de un error de anulación también limpia el aviso', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutas({ turno: turnoFixture(), ventas: [ventaFixture({ id: 11 })] })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000001')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))

    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'turno_cerrado', 'El turno ya está cerrado.'))
    await userEvent.click(screen.getByRole('button', { name: 'Anular venta' }))
    await screen.findByText('El turno ya está cerrado.')

    await userEvent.keyboard('{Escape}')
    expect(screen.queryByText('El turno ya está cerrado.')).not.toBeInTheDocument()
  })

  it('un 403 del servidor se traduce a un mensaje de permisos, no al texto crudo del servidor', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutas({ turno: turnoFixture(), ventas: [ventaFixture({ id: 5 })] })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000001')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))

    apiPostMock.mockRejectedValueOnce(new ErrorApi(403, 'prohibido', 'forbidden'))
    await userEvent.click(screen.getByRole('button', { name: 'Anular venta' }))

    await screen.findByText('No tenés permiso para anular esta venta.')
  })

  /** Cláusula bajo prueba: `anulandoRef`, la guarda de re-entrancia del mismo tick. */
  it('dos clicks del mismo tick sobre "Anular venta" disparan una sola anulación', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: [ventaFixture({ id: 6 })] })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000001')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))

    let resolverAnulacion: (valor: unknown) => void = () => undefined
    apiPostMock.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolverAnulacion = resolve
        }),
    )

    const boton = screen.getByRole('button', { name: 'Anular venta' })
    await act(async () => {
      boton.click()
      boton.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    resolverAnulacion({ ...ventaFixture({ id: 6 }), estado: 'Anulado' })
  })

  it('Escape cierra el modal de confirmación sin anular', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: [ventaFixture({ id: 7 })] })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000001')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))
    expect(screen.getByRole('button', { name: 'Anular venta' })).toBeInTheDocument()

    await userEvent.keyboard('{Escape}')

    expect(screen.queryByRole('button', { name: 'Anular venta' })).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })
})

describe('VentasDelTurno — acciones con ícono y confirmación en modal', () => {
  it('las acciones de la fila son botones con ícono cuyo nombre accesible es su tooltip', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: [ventaFixture({ id: 50, numeroVisible: '0007-00000050' })] })
    usuarioActual = usuarioFixture({ rolId: ROL.Admin, rol: 'Admin' })
    render(<VentasDelTurno alReimprimir={vi.fn()} />)

    await screen.findByText('0007-00000050')
    for (const nombre of ['Actualizar', 'Detalle', 'Anular', 'Imprimir', 'Limpiar filtros']) {
      expect(screen.getByRole('button', { name: nombre })).toHaveAttribute('title', nombre)
    }
  })

  it('"Actualizar" vuelve a pedir el turno y las ventas', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: [ventaFixture({ id: 51, numeroVisible: '0007-00000051' })] })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000051')
    const listados = () => apiGetMock.mock.calls.filter(([ruta]) => String(ruta).startsWith('/ventas/por-turno/')).length
    expect(listados()).toBe(1)

    await userEvent.click(screen.getByRole('button', { name: 'Actualizar' }))

    await waitFor(() => expect(listados()).toBe(2))
  })

  it('mientras actualiza, "Actualizar" queda deshabilitado y ocupado sin cambiar de nombre', async () => {
    let resolverListado: (v: VentaDeTurnoListado[]) => void = () => undefined
    let cantidad = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/caja/turnos/abierto')) return Promise.resolve(turnoFixture())
      if (ruta.startsWith('/ventas/por-turno/')) {
        cantidad += 1
        if (cantidad === 1) return Promise.resolve([ventaFixture({ id: 52, numeroVisible: '0007-00000052' })])
        return new Promise((resolve) => {
          resolverListado = resolve
        })
      }
      if (ruta === '/catalogos/medios-pago') return Promise.resolve([])
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000052')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Actualizar' })).toBeEnabled())

    await userEvent.click(screen.getByRole('button', { name: 'Actualizar' }))

    const boton = screen.getByRole('button', { name: 'Actualizar' })
    expect(boton).toBeDisabled()
    expect(boton).toHaveAttribute('aria-busy', 'true')

    resolverListado([])
    await waitFor(() => expect(screen.getByRole('button', { name: 'Actualizar' })).toBeEnabled())
    expect(screen.getByRole('button', { name: 'Actualizar' })).not.toHaveAttribute('aria-busy')
  })

  it('la confirmación de anulación es un diálogo modal, no un panel dentro de la pantalla', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: [ventaFixture({ id: 53, numeroVisible: '0007-00000053', total: 200 })] })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000053')

    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))

    const dialogo = screen.getByRole('dialog', { name: 'Confirmar anulación' })
    expect(within(dialogo).getByText(/¿Anular la venta 0007-00000053 por \$ 200,00\?/)).toBeInTheDocument()
    expect(within(dialogo).getByText(/Se revierte el stock y la cuenta corriente/)).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(within(dialogo).getByRole('button', { name: 'Cancelar' })).toHaveFocus()
  })

  it('durante la anulación el modal queda inerte: botones deshabilitados, Escape no lo cierra', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: [ventaFixture({ id: 54 })] })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))
    apiPostMock.mockImplementationOnce(() => new Promise(() => undefined))

    await userEvent.click(screen.getByRole('button', { name: 'Anular venta' }))

    const dialogo = screen.getByRole('dialog', { name: 'Confirmar anulación' })
    expect(within(dialogo).getByRole('button', { name: 'Anulando…' })).toBeDisabled()
    expect(within(dialogo).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(within(dialogo).getByRole('button', { name: 'Cerrar confirmación' })).toBeDisabled()
    await userEvent.keyboard('{Escape}')
    expect(screen.getByRole('dialog', { name: 'Confirmar anulación' })).toBeInTheDocument()
  })

  it('cancelar devuelve el foco al botón "Anular" de la fila', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: [ventaFixture({ id: 55 })] })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')
    const anular = screen.getByRole('button', { name: 'Anular' })
    await userEvent.click(anular)

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await waitFor(() => expect(anular).toHaveFocus())
  })

  it('tras anular con éxito la fila ya no tiene botón "Anular": el foco va a "Actualizar" cuando termina de refrescar', async () => {
    const venta = ventaFixture({ id: 56 })
    mockearRutas({ turno: turnoFixture(), ventas: [venta] })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))
    apiPostMock.mockResolvedValueOnce({ ...venta, estado: 'Anulado' })
    mockearRutas({ turno: turnoFixture(), ventas: [{ ...venta, estado: 'Anulado' }] })

    await userEvent.click(screen.getByRole('button', { name: 'Anular venta' }))

    await screen.findByText('Anulada', { selector: 'span' })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Actualizar' })).toHaveFocus())
  })

  /**
   * Cláusulas bajo prueba: el cierre de la confirmación al cambiar el contexto (`cargarTurnoYVentas`
   * limpia `filaAAnular`) y la compuerta de generación de `confirmarAnular`.
   */
  it('si el punto de venta cambia con la anulación en vuelo, el modal se cierra y la respuesta tardía no relee el listado viejo', async () => {
    const venta = ventaFixture({ id: 57 })
    mockearRutas({ turno: turnoFixture(), ventas: [venta] })
    const { rerender } = render(
      <>
        <VentasDelTurno />
        <Toaster />
      </>,
    )
    await screen.findByText('0007-00000001')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))
    let resolverAnulacion: (valor: unknown) => void = () => undefined
    apiPostMock.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolverAnulacion = resolve
        }),
    )
    await userEvent.click(screen.getByRole('button', { name: 'Anular venta' }))
    const listados = () => apiGetMock.mock.calls.filter(([ruta]) => String(ruta).startsWith('/ventas/por-turno/')).length
    const listadosAntes = listados()

    estadoDePuntoVenta = { ...estadoDePuntoVenta, puntoVenta: puntoVentaFixture({ id: 8 }) }
    rerender(<VentasDelTurno />)

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await waitFor(() => expect(listados()).toBeGreaterThan(listadosAntes))
    const listadosConLaCargaNueva = listados()

    resolverAnulacion({ ...venta, estado: 'Anulado' })

    await waitFor(() => expect(screen.getByRole('button', { name: 'Actualizar' })).toBeEnabled())
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(listados()).toBe(listadosConLaCargaNueva)
  })

  /** Anula la venta 0007-00000001 y cambia de punto de venta con la escritura en vuelo; devuelve cómo resolverla. */
  async function anularYCambiarDePuntoVenta() {
    const venta = ventaFixture({ id: 60 })
    mockearRutas({ turno: turnoFixture(), ventas: [venta] })
    const { rerender } = render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))
    let resolver: (valor: unknown) => void = () => undefined
    let rechazar: (motivo: unknown) => void = () => undefined
    apiPostMock.mockImplementationOnce(
      () =>
        new Promise((resolve, reject) => {
          resolver = resolve
          rechazar = reject
        }),
    )
    await userEvent.click(screen.getByRole('button', { name: 'Anular venta' }))
    const listados = () => apiGetMock.mock.calls.filter(([ruta]) => String(ruta).startsWith('/ventas/por-turno/')).length
    const antes = listados()
    estadoDePuntoVenta = { ...estadoDePuntoVenta, puntoVenta: puntoVentaFixture({ id: 8 }) }
    rerender(
      <>
        <VentasDelTurno />
        <Toaster />
      </>,
    )
    await waitFor(() => expect(listados()).toBeGreaterThan(antes))
    return { resolver: () => resolver({ ...venta, estado: 'Anulado' }), rechazar: (e: unknown) => rechazar(e), listadosConLaCargaNueva: listados(), listados }
  }

  it('si el punto de venta cambió, una anulación que termina bien se informa en un toast que se puede cerrar, sin tocar el listado nuevo', async () => {
    const ctx = await anularYCambiarDePuntoVenta()

    await act(async () => {
      ctx.resolver()
    })

    const aviso = await screen.findByRole('status')
    expect(aviso).toHaveTextContent('La venta 0007-00000001 se anuló.')
    expect(aviso.closest('.ws-toaster')).not.toBeNull()
    expect(document.body.querySelector('.alert-success')).toBeNull()
    expect(ctx.listados()).toBe(ctx.listadosConLaCargaNueva)
    await userEvent.click(within(aviso).getByRole('button', { name: 'Cerrar aviso' }))
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })

  it('si el punto de venta cambió, una anulación que falla se informa con el motivo en un aviso con role="alert"', async () => {
    const { ErrorApi } = await import('../api/cliente')
    const ctx = await anularYCambiarDePuntoVenta()

    await act(async () => {
      ctx.rechazar(new ErrorApi(409, 'turno_cerrado', 'El turno ya está cerrado.'))
    })

    const aviso = await screen.findByRole('alert')
    expect(aviso).toHaveTextContent('No se pudo anular la venta 0007-00000001: El turno ya está cerrado.')
    expect(aviso.closest('.alert-danger')).not.toBeNull()
    expect(aviso.closest('.ws-toaster')).toBeNull()
    expect(ctx.listados()).toBe(ctx.listadosConLaCargaNueva)
  })

  it('al cerrarse la confirmación por un cambio de punto de venta el foco va a "Actualizar", no al documento', async () => {
    const ctx = await anularYCambiarDePuntoVenta()
    await act(async () => {
      ctx.resolver()
    })

    await waitFor(() => expect(screen.getByRole('button', { name: 'Actualizar' })).toHaveFocus())
  })

  it('el error de una anulación fallida se anuncia con role="alert" y el foco queda dentro del modal, en "Cancelar"', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutas({ turno: turnoFixture(), ventas: [ventaFixture({ id: 58 })] })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'turno_cerrado', 'El turno ya está cerrado.'))

    await userEvent.click(screen.getByRole('button', { name: 'Anular venta' }))

    const dialogo = screen.getByRole('dialog', { name: 'Confirmar anulación' })
    expect(await within(dialogo).findByRole('alert')).toHaveTextContent('El turno ya está cerrado.')
    await waitFor(() => expect(within(dialogo).getByRole('button', { name: 'Cancelar' })).toHaveFocus())
  })
})

describe('VentasDelTurno — "Imprimir" en el modal de detalle', () => {
  it('es un botón con ícono y texto visible "Imprimir"; mientras busca el comprobante queda deshabilitado, ocupado y con el mismo nombre', async () => {
    const venta = ventaFixture({ id: 59, numeroVisible: '0007-00000059' })
    let ciclo = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/caja/turnos/abierto')) return Promise.resolve(turnoFixture())
      if (ruta.startsWith('/ventas/por-turno/')) return Promise.resolve([venta])
      if (ruta === '/catalogos/medios-pago') return Promise.resolve([medioEfectivo])
      if (ruta === '/ventas/59') return ciclo === 0 ? Promise.resolve(comprobanteFixture({ id: 59, numeroVisible: '0007-00000059' })) : new Promise(() => undefined)
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    render(<VentasDelTurno alReimprimir={vi.fn()} />)
    await screen.findByText('0007-00000059')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))
    const dialogo = await screen.findByRole('dialog')
    await within(dialogo).findByText('Coca Cola 1L')
    const imprimir = within(dialogo).getByRole('button', { name: 'Imprimir' })
    expect(imprimir).toHaveTextContent('Imprimir')
    expect(imprimir.querySelector('svg')).toBeInTheDocument()

    ciclo = 1
    await userEvent.click(imprimir)

    await waitFor(() => expect(within(dialogo).getByRole('button', { name: 'Imprimir' })).toHaveAttribute('aria-busy', 'true'))
    expect(within(dialogo).getByRole('button', { name: 'Imprimir' })).toBeDisabled()
  })
})

describe('VentasDelTurno — respuestas obsoletas', () => {
  it('una respuesta de una carga anterior nunca pisa el resultado de una carga más reciente', async () => {
    const ventaVieja = ventaFixture({ id: 1, numeroVisible: '0007-00000001' })
    const ventaNueva = ventaFixture({ id: 2, numeroVisible: '0007-00000002' })

    let resolverListadoViejo: (valor: VentaDeTurnoListado[]) => void = () => undefined
    let cantidadDeListados = 0

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/caja/turnos/abierto')) return Promise.resolve(turnoFixture())
      if (ruta.startsWith('/ventas/por-turno/')) {
        cantidadDeListados += 1
        if (cantidadDeListados === 1) {
          return new Promise((resolve) => {
            resolverListadoViejo = resolve
          })
        }
        return Promise.resolve([ventaNueva])
      }
      if (ruta === '/catalogos/medios-pago') return Promise.resolve([])
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })

    const { rerender } = render(<VentasDelTurno />)
    await waitFor(() => expect(cantidadDeListados).toBe(1))

    // Cambia el punto de venta fijo BAJO EL MISMO componente montado (mismo criterio que
    // `Pos.test.tsx`, sección "remount por punto de venta"): el efecto depende de
    // `puntoVenta.id`, así que dispara una segunda carga (generación 2) ANTES de que la primera
    // (generación 1, todavía en vuelo) resuelva.
    estadoDePuntoVenta = { ...estadoDePuntoVenta, puntoVenta: puntoVentaFixture({ id: 8 }) }
    rerender(<VentasDelTurno />)
    await waitFor(() => expect(cantidadDeListados).toBe(2))
    await screen.findByText('0007-00000002')

    // La respuesta vieja (generación 1) llega TARDE — no debe pisar el listado ya más nuevo.
    resolverListadoViejo([ventaVieja])

    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(screen.getByText('0007-00000002')).toBeInTheDocument()
    expect(screen.queryByText('0007-00000001')).not.toBeInTheDocument()
  })
})

describe('VentasDelTurno — detalle de venta', () => {
  it('abre el modal, carga el detalle y lo muestra: número, fecha, cliente, estado, ítems, pagos y totales', async () => {
    const venta = ventaFixture({ id: 20, numeroVisible: '0007-00000020', nombreCliente: 'Juan Pérez' })
    const comprobante = comprobanteFixture({ id: 20, numeroVisible: '0007-00000020' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 20: comprobante } })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000020')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))

    const dialog = await screen.findByRole('dialog', { name: /Detalle de la venta 0007-00000020/ })
    // `ComprobanteEmitido` no trae el nombre del cliente (solo `idCliente`) — sale de la fila del
    // listado ya cargada, no de un segundo fetch.
    expect(within(dialog).getByText('Juan Pérez')).toBeInTheDocument()
    expect(within(dialog).getByText('Coca Cola 1L')).toBeInTheDocument()
    expect(within(dialog).getByText('Efectivo')).toBeInTheDocument()
    // El total del comprobante aparece en el pie del modal.
    expect(within(dialog).getByText('Total: $ 100,00')).toBeInTheDocument()
  })

  it('muestra el ajuste manual de cada línea (porcentaje y monto con signo) y los totales de descuento y recargo manual', async () => {
    const venta = ventaFixture({ id: 30, numeroVisible: '0007-00000030', descuentoManualTotal: 10, recargoManualTotal: 15, total: 105 })
    const base = comprobanteFixture().items[0]
    const comprobante = comprobanteFixture({
      id: 30,
      numeroVisible: '0007-00000030',
      subtotal: 200,
      descuentoManualTotal: 10,
      recargoManualTotal: 15,
      total: 205,
      items: [
        { ...base, orden: 1, descripcion: 'Maple de huevos', precioUnitario: 100, ajusteManualPorcentaje: -10, ajusteManual: -10, total: 90 },
        { ...base, orden: 2, descripcion: 'Cigarrillos', precioUnitario: 100, ajusteManualPorcentaje: 15, ajusteManual: 15, total: 115 },
        { ...base, orden: 3, descripcion: 'Agua 500ml', precioUnitario: 0, total: 0 },
      ],
    })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 30: comprobante } })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000030')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))

    const dialog = within(await screen.findByRole('dialog', { name: /Detalle de la venta 0007-00000030/ }))
    // Espera al DATO (la fila ya renderizada), no solo al diálogo, que existe antes de la carga.
    const filaDelMaple = (await dialog.findByText('Maple de huevos')).closest('tr') as HTMLElement
    expect(within(filaDelMaple).getByText('Desc. manual 10% -$ 10,00')).toBeInTheDocument()
    const filaDelCigarrillo = dialog.getByText('Cigarrillos').closest('tr') as HTMLElement
    expect(within(filaDelCigarrillo).getByText('Recargo 15% +$ 15,00')).toBeInTheDocument()
    const filaSinAjuste = dialog.getByText('Agua 500ml').closest('tr') as HTMLElement
    expect(within(filaSinAjuste).queryByText(/manual|Recargo/)).not.toBeInTheDocument()
    expect(within(filaSinAjuste).getByText('—')).toBeInTheDocument()

    expect(dialog.getByText('Desc. manual: -$ 10,00')).toBeInTheDocument()
    expect(dialog.getByText('Recargo: +$ 15,00')).toBeInTheDocument()
    expect(dialog.getByText('Total: $ 205,00')).toBeInTheDocument()
  })

  it('un comprobante sin ajustes no muestra filas de descuento ni recargo manual en los totales', async () => {
    const venta = ventaFixture({ id: 31, numeroVisible: '0007-00000031' })
    const comprobante = comprobanteFixture({ id: 31, numeroVisible: '0007-00000031' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 31: comprobante } })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000031')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))

    const dialog = within(await screen.findByRole('dialog', { name: /Detalle de la venta 0007-00000031/ }))
    await dialog.findByText('Total: $ 100,00')
    expect(dialog.queryByText(/Desc\. manual/)).not.toBeInTheDocument()
    expect(dialog.queryByText(/Recargo/)).not.toBeInTheDocument()
  })

  it('muestra "Lote X" cuando el ítem tiene lote', async () => {
    const venta = ventaFixture({ id: 21, numeroVisible: '0007-00000021' })
    const comprobante = comprobanteFixture({
      id: 21,
      numeroVisible: '0007-00000021',
      items: [{ ...comprobanteFixture().items[0], codigoLote: 'L-001' }],
    })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 21: comprobante } })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000021')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))

    expect(await screen.findByText(/Lote L-001/)).toBeInTheDocument()
  })

  it('muestra el vuelto solo cuando es mayor a cero', async () => {
    const venta = ventaFixture({ id: 22, numeroVisible: '0007-00000022' })
    const comprobante = comprobanteFixture({
      id: 22,
      numeroVisible: '0007-00000022',
      pagos: [{ idMedioPago: 1, importe: 150, referencia: null, vuelto: 50 }],
    })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 22: comprobante } })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000022')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))

    await screen.findByRole('dialog')
    expect(screen.getByText('$ 50,00')).toBeInTheDocument()
  })

  it('muestra un error cuando falla la carga del detalle', async () => {
    const { ErrorApi } = await import('../api/cliente')
    const venta = ventaFixture({ id: 23, numeroVisible: '0007-00000023' })
    mockearRutas({
      turno: turnoFixture(),
      ventas: [venta],
      medios: [medioEfectivo],
      erroresComprobante: { 23: new ErrorApi(404, 'no_encontrado', 'La venta no existe.') },
    })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000023')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))

    await screen.findByText('La venta no existe.')
  })

  it('Escape cierra el modal de detalle', async () => {
    const venta = ventaFixture({ id: 24, numeroVisible: '0007-00000024' })
    const comprobante = comprobanteFixture({ id: 24, numeroVisible: '0007-00000024' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 24: comprobante } })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000024')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))
    await screen.findByRole('dialog')

    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('"Cerrar" cierra el modal y devuelve el foco al botón "Detalle" que lo abrió', async () => {
    const venta = ventaFixture({ id: 25, numeroVisible: '0007-00000025' })
    const comprobante = comprobanteFixture({ id: 25, numeroVisible: '0007-00000025' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 25: comprobante } })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000025')
    const boton = screen.getByRole('button', { name: 'Detalle' })
    await userEvent.click(boton)
    await screen.findByRole('dialog')

    await userEvent.click(screen.getByRole('button', { name: 'Cerrar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    // jsdom no implementa la regla de "focus fixup" real de un navegador (react-async-state
    // regla 12): el `.focus()` explícito dentro del handler de `cerrarDetalle` sigue siendo la
    // prueba correcta de que el destino es el disparador, no `document.body`.
    expect(boton).toHaveFocus()
  })

  it('el botón "X" también cierra el modal', async () => {
    const venta = ventaFixture({ id: 26, numeroVisible: '0007-00000026' })
    const comprobante = comprobanteFixture({ id: 26, numeroVisible: '0007-00000026' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 26: comprobante } })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000026')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))
    await screen.findByRole('dialog')

    await userEvent.click(screen.getByRole('button', { name: 'Cerrar detalle' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  /**
   * Sobre `Modal`: al abrir, el foco entra al diálogo (el disparador queda deshabilitado detrás del
   * fondo) y Tab no escapa. Mutation-proof-tests: con el markup inline anterior, el foco se queda
   * en "Detalle" y el primer `expect` falla.
   */
  it('al abrir, el foco entra al modal ("Cerrar detalle") y Tab desde el último control vuelve adentro', async () => {
    const venta = ventaFixture({ id: 27, numeroVisible: '0007-00000027' })
    const comprobante = comprobanteFixture({ id: 27, numeroVisible: '0007-00000027' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 27: comprobante } })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000027')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))
    await screen.findByRole('dialog', { name: /0007-00000027/ })

    const cerrarDetalle = screen.getByRole('button', { name: 'Cerrar detalle' })
    expect(cerrarDetalle).toHaveFocus()

    screen.getByRole('button', { name: 'Cerrar' }).focus()
    await userEvent.tab()
    expect(cerrarDetalle).toHaveFocus()
  })

  it('una respuesta de detalle desactualizada nunca pisa el detalle ya cerrado ni el de otra venta', async () => {
    const ventaA = ventaFixture({ id: 30, numeroVisible: '0007-00000030' })
    const ventaB = ventaFixture({ id: 31, numeroVisible: '0007-00000031' })
    const comprobanteA = comprobanteFixture({ id: 30, numeroVisible: '0007-00000030' })
    const comprobanteB = comprobanteFixture({ id: 31, numeroVisible: '0007-00000031' })

    let resolverA: (valor: ComprobanteEmitido) => void = () => undefined
    const pendienteA = new Promise<ComprobanteEmitido>((resolve) => {
      resolverA = resolve
    })

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/caja/turnos/abierto')) return Promise.resolve(turnoFixture())
      if (ruta.startsWith('/ventas/por-turno/')) return Promise.resolve([ventaA, ventaB])
      if (ruta === '/catalogos/medios-pago') return Promise.resolve([medioEfectivo])
      if (ruta === '/ventas/30') return pendienteA
      if (ruta === '/ventas/31') return Promise.resolve(comprobanteB)
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })

    render(<VentasDelTurno />)
    await screen.findByText('0007-00000030')

    const filaA = screen.getByText('0007-00000030').closest('tr') as HTMLElement
    await userEvent.click(within(filaA).getByRole('button', { name: 'Detalle' }))
    await screen.findByRole('dialog')

    // Cierra ANTES de que la respuesta de A resuelva.
    await userEvent.click(screen.getByRole('button', { name: 'Cerrar' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    // Abre el detalle de B (generación nueva) y lo deja resuelto.
    const filaB = screen.getByText('0007-00000031').closest('tr') as HTMLElement
    await userEvent.click(within(filaB).getByRole('button', { name: 'Detalle' }))
    await screen.findByRole('dialog', { name: /0007-00000031/ })

    // La respuesta vieja (A) llega tarde — no debe reabrir el modal ni pisar el de B. Sigue
    // habiendo un ÚNICO dialog en pantalla (nunca dos, ni el de B reemplazado por el de A) y su
    // contenido sigue siendo el de B.
    resolverA(comprobanteA)
    await new Promise((resolve) => setTimeout(resolve, 0))

    const dialogos = screen.getAllByRole('dialog')
    expect(dialogos).toHaveLength(1)
    expect(dialogos[0]).toHaveAccessibleName(expect.stringContaining('0007-00000031'))
  })
})

describe('VentasDelTurno — reimprimir', () => {
  it('sin la prop alReimprimir, ninguna fila ni el modal de detalle ofrecen imprimir', async () => {
    const venta = ventaFixture({ id: 40, numeroVisible: '0007-00000040' })
    const comprobante = comprobanteFixture({ id: 40, numeroVisible: '0007-00000040' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 40: comprobante } })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000040')
    expect(screen.queryByRole('button', { name: 'Imprimir' })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))
    await screen.findByRole('dialog')
    expect(screen.queryByRole('button', { name: 'Imprimir' })).not.toBeInTheDocument()
  })

  it('con alReimprimir provisto, una venta Anulada nunca ofrece imprimir (fila ni modal)', async () => {
    const venta = ventaFixture({ id: 41, numeroVisible: '0007-00000041', estado: 'Anulado' })
    const comprobante = comprobanteFixture({ id: 41, numeroVisible: '0007-00000041', estado: 'Anulado' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 41: comprobante } })
    const alReimprimir = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} />)

    await screen.findByText('0007-00000041')
    expect(screen.queryByRole('button', { name: 'Imprimir' })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))
    await screen.findByRole('dialog')
    expect(screen.queryByRole('button', { name: 'Imprimir' })).not.toBeInTheDocument()
  })

  it('"Imprimir" de la fila busca el comprobante y llama a la prop con el comprobante y los medios', async () => {
    const venta = ventaFixture({ id: 42, numeroVisible: '0007-00000042' })
    const comprobante = comprobanteFixture({ id: 42, numeroVisible: '0007-00000042' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 42: comprobante } })
    const alReimprimir = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} />)

    await screen.findByText('0007-00000042')
    await userEvent.click(screen.getByRole('button', { name: 'Imprimir' }))

    await waitFor(() => expect(alReimprimir).toHaveBeenCalledWith(comprobante, [medioEfectivo]))
  })

  it('"Imprimir" desde el modal de detalle SIEMPRE vuelve a pedir el comprobante (nunca reusa comprobanteDetalle) — JD-E1-2', async () => {
    const venta = ventaFixture({ id: 43, numeroVisible: '0007-00000043' })
    const comprobante = comprobanteFixture({ id: 43, numeroVisible: '0007-00000043' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 43: comprobante } })
    const alReimprimir = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} />)

    await screen.findByText('0007-00000043')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))
    const dialog = await screen.findByRole('dialog')

    const cantidadDeGetsAntes = apiGetMock.mock.calls.filter(([ruta]) => ruta === '/ventas/43').length

    // Dos botones "Imprimir" en pantalla (fila + modal, la fila queda deshabilitada mientras el
    // modal está abierto) — se scopea al del modal.
    await userEvent.click(within(dialog).getByRole('button', { name: 'Imprimir' }))

    await waitFor(() => expect(alReimprimir).toHaveBeenCalledWith(comprobante, [medioEfectivo]))
    // La venta pudo anularse DESPUÉS de abrir el detalle — reusar `comprobanteDetalle` imprimiría
    // un ticket de una venta ya no válida. Acá SÍ hay un segundo GET, con el detalle ya cargado.
    const cantidadDeGetsDespues = apiGetMock.mock.calls.filter(([ruta]) => ruta === '/ventas/43').length
    expect(cantidadDeGetsDespues).toBe(cantidadDeGetsAntes + 1)
  })

  it('fila: si el listado dice Emitida pero el GET fresco dice Anulada, no imprime — muestra el error y la fila se refresca (el botón "Imprimir" desaparece) — JD-E1-2', async () => {
    const venta = ventaFixture({ id: 46, numeroVisible: '0007-00000046', estado: 'Emitido' })
    const comprobanteAnuladoFresco = comprobanteFixture({ id: 46, numeroVisible: '0007-00000046', estado: 'Anulado' })
    mockearRutas({
      turno: turnoFixture(),
      ventas: [venta],
      medios: [medioEfectivo],
      comprobantes: { 46: comprobanteAnuladoFresco },
    })
    const alReimprimir = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} />)

    await screen.findByText('0007-00000046')
    // Emitida en el listado — el botón "Imprimir" está visible antes de la búsqueda fresca.
    expect(screen.getByText('Emitida', { selector: 'span' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Imprimir' }))

    await screen.findByText('La venta fue anulada, no se puede reimprimir.')
    expect(alReimprimir).not.toHaveBeenCalled()
    // La fila se refresca con el estado real: el badge pasa a Anulada y "Imprimir" desaparece.
    expect(await screen.findByText('Anulada', { selector: 'span' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Imprimir' })).not.toBeInTheDocument()
  })

  it('modal: si el detalle ya cargado dice Emitida pero el GET fresco de "Imprimir" dice Anulada, no imprime — el modal se refresca (badge y botón) — JD-E1-2', async () => {
    const venta = ventaFixture({ id: 47, numeroVisible: '0007-00000047', estado: 'Emitido' })
    const comprobanteEmitidoInicial = comprobanteFixture({ id: 47, numeroVisible: '0007-00000047', estado: 'Emitido' })
    mockearRutas({
      turno: turnoFixture(),
      ventas: [venta],
      medios: [medioEfectivo],
      comprobantes: { 47: comprobanteEmitidoInicial },
    })
    const alReimprimir = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} />)

    await screen.findByText('0007-00000047')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('Emitida', { selector: 'span' })).toBeInTheDocument()

    // La venta se anula DESPUÉS de que el detalle ya cargó — el próximo GET (el de "Imprimir")
    // trae el estado real.
    const comprobanteAnuladoFresco = comprobanteFixture({ id: 47, numeroVisible: '0007-00000047', estado: 'Anulado' })
    mockearRutas({
      turno: turnoFixture(),
      ventas: [venta],
      medios: [medioEfectivo],
      comprobantes: { 47: comprobanteAnuladoFresco },
    })

    await userEvent.click(within(dialog).getByRole('button', { name: 'Imprimir' }))

    await within(dialog).findByText('La venta fue anulada, no se puede reimprimir.')
    expect(alReimprimir).not.toHaveBeenCalled()
    // El modal se refresca con el comprobante real: el badge pasa a Anulada y "Imprimir" ya no
    // se ofrece (no tiene sentido reimprimir algo que se acaba de confirmar anulado).
    expect(within(dialog).getByText('Anulada', { selector: 'span' })).toBeInTheDocument()
    expect(within(dialog).queryByRole('button', { name: 'Imprimir' })).not.toBeInTheDocument()
  })

  it('mientras busca el comprobante el botón "Imprimir" queda deshabilitado y ocupado con el mismo nombre, y un error visible si falla', async () => {
    const { ErrorApi } = await import('../api/cliente')
    const venta = ventaFixture({ id: 44, numeroVisible: '0007-00000044' })
    let rechazar: (motivo: unknown) => void = () => undefined
    const pendiente = new Promise<ComprobanteEmitido>((_resolve, reject) => {
      rechazar = reject
    })
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/caja/turnos/abierto')) return Promise.resolve(turnoFixture())
      if (ruta.startsWith('/ventas/por-turno/')) return Promise.resolve([venta])
      if (ruta === '/catalogos/medios-pago') return Promise.resolve([medioEfectivo])
      if (ruta === '/ventas/44') return pendiente
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const alReimprimir = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} />)

    await screen.findByText('0007-00000044')
    await userEvent.click(screen.getByRole('button', { name: 'Imprimir' }))

    await waitFor(() => expect(screen.getByRole('button', { name: 'Imprimir' })).toHaveAttribute('aria-busy', 'true'))
    expect(screen.getByRole('button', { name: 'Imprimir' })).toBeDisabled()

    rechazar(new ErrorApi(500, 'error', 'No se pudo obtener la venta.'))
    await screen.findByText('No se pudo obtener la venta.')
    expect(alReimprimir).not.toHaveBeenCalled()
  })

  it('un doble click sobre "Imprimir" de la misma fila solo dispara una búsqueda (guarda por fila)', async () => {
    const venta = ventaFixture({ id: 45, numeroVisible: '0007-00000045' })
    const comprobante = comprobanteFixture({ id: 45, numeroVisible: '0007-00000045' })
    let cantidadDeGets = 0
    let resolverComprobante: (valor: ComprobanteEmitido) => void = () => undefined
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/caja/turnos/abierto')) return Promise.resolve(turnoFixture())
      if (ruta.startsWith('/ventas/por-turno/')) return Promise.resolve([venta])
      if (ruta === '/catalogos/medios-pago') return Promise.resolve([medioEfectivo])
      if (ruta === '/ventas/45') {
        cantidadDeGets += 1
        return new Promise((resolve) => {
          resolverComprobante = resolve
        })
      }
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const alReimprimir = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} />)

    await screen.findByText('0007-00000045')
    const boton = screen.getByRole('button', { name: 'Imprimir' })
    await userEvent.click(boton)
    await userEvent.click(boton)

    expect(cantidadDeGets).toBe(1)
    resolverComprobante(comprobante)
    await waitFor(() => expect(alReimprimir).toHaveBeenCalledTimes(1))
  })
})

describe('VentasDelTurno — estado fiscal y "Reimprimir factura"', () => {
  const datosDeFactura: DatosDeFacturaFiscal = {
    idComprobante: 7,
    codigoTipoComprobante: 'FB',
    letra: 'B',
    puntoDeVentaFiscal: 3,
    numero: 12,
    fecha: '2026-10-14',
    cae: '70123456789012',
    caeVencimiento: '2026-10-24',
    payloadQr: null,
    observaciones: [],
  }

  const fiscalAprobado: FiscalDeVenta = { estado: 'aprobado', motivo: 'forzada', idFacturacion: 4, error: null, comprobante: datosDeFactura }

  /** Espera a que la fila haya llegado (el dato) y a que el catálogo de medios se haya pedido, antes de tocar nada. */
  async function esperarFila(numeroVisible: string) {
    await screen.findByText(numeroVisible)
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/catalogos/medios-pago'))
  }

  it('cada fila muestra su marca fiscal: Facturada, Pendiente (también sin_decidir), Rechazada, y ninguna para el resto', async () => {
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [
        ventaFixture({ id: 1, numeroVisible: '0007-00000001', estadoFiscal: 'aprobado' }),
        ventaFixture({ id: 2, numeroVisible: '0007-00000002', estadoFiscal: 'pendiente' }),
        ventaFixture({ id: 3, numeroVisible: '0007-00000003', estadoFiscal: 'sin_decidir' }),
        ventaFixture({ id: 4, numeroVisible: '0007-00000004', estadoFiscal: 'rechazado' }),
        ventaFixture({ id: 5, numeroVisible: '0007-00000005', estadoFiscal: 'no_corresponde' }),
        ventaFixture({ id: 6, numeroVisible: '0007-00000006', estadoFiscal: 'cancelado' }),
        ventaFixture({ id: 7, numeroVisible: '0007-00000007' }),
      ],
    })
    render(<VentasDelTurno />)
    await esperarFila('0007-00000007')

    const etiquetasDe = (numeroVisible: string) => {
      const fila = screen.getByText(numeroVisible).closest('tr') as HTMLElement
      return ['Facturada', 'Pendiente', 'Rechazada'].filter((t) => within(fila).queryByText(t) !== null)
    }
    expect(etiquetasDe('0007-00000001')).toEqual(['Facturada'])
    expect(etiquetasDe('0007-00000002')).toEqual(['Pendiente'])
    expect(etiquetasDe('0007-00000003')).toEqual(['Pendiente'])
    expect(etiquetasDe('0007-00000004')).toEqual(['Rechazada'])
    expect(etiquetasDe('0007-00000005')).toEqual([])
    expect(etiquetasDe('0007-00000006')).toEqual([])
    expect(etiquetasDe('0007-00000007')).toEqual([])
  })

  it('"Reimprimir factura" aparece solo en la fila con factura aprobada', async () => {
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [
        ventaFixture({ id: 1, numeroVisible: '0007-00000001', estadoFiscal: 'aprobado' }),
        ventaFixture({ id: 2, numeroVisible: '0007-00000002', estadoFiscal: 'pendiente' }),
        ventaFixture({ id: 3, numeroVisible: '0007-00000003', estadoFiscal: 'rechazado' }),
        ventaFixture({ id: 4, numeroVisible: '0007-00000004', estadoFiscal: 'no_corresponde' }),
        ventaFixture({ id: 5, numeroVisible: '0007-00000005' }),
      ],
    })
    render(<VentasDelTurno alReimprimir={vi.fn()} alReimprimirFactura={vi.fn()} />)
    await esperarFila('0007-00000005')

    const botones = screen.getAllByRole('button', { name: 'Reimprimir factura' })
    expect(botones).toHaveLength(1)
    expect((botones[0].closest('tr') as HTMLElement).textContent).toContain('0007-00000001')
    // El "Imprimir" del ticket sigue en todas las filas emitidas.
    expect(screen.getAllByRole('button', { name: 'Imprimir' })).toHaveLength(5)
  })

  it('sin la prop alReimprimirFactura no se ofrece reimprimir la factura', async () => {
    mockearRutas({ turno: turnoFixture(), medios: [medioEfectivo], ventas: [ventaFixture({ id: 1, estadoFiscal: 'aprobado' })] })
    render(<VentasDelTurno alReimprimir={vi.fn()} />)
    await esperarFila('0007-00000001')

    expect(screen.queryByRole('button', { name: 'Reimprimir factura' })).not.toBeInTheDocument()
  })

  it('al hacer click pide el comprobante por id y llama a la prop con su comprobante, la factura y los medios; no imprime el ticket', async () => {
    const comprobante = comprobanteFixture({ id: 42, numeroVisible: '0007-00000042', fiscal: fiscalAprobado })
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 42, numeroVisible: '0007-00000042', estadoFiscal: 'aprobado' })],
      comprobantes: { 42: comprobante },
    })
    const alReimprimir = vi.fn()
    const alReimprimirFactura = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} alReimprimirFactura={alReimprimirFactura} />)
    await esperarFila('0007-00000042')

    await userEvent.click(await screen.findByRole('button', { name: 'Reimprimir factura' }))

    await waitFor(() => expect(alReimprimirFactura).toHaveBeenCalledTimes(1))
    expect(alReimprimirFactura).toHaveBeenCalledWith(comprobante, datosDeFactura, [medioEfectivo])
    expect(alReimprimir).not.toHaveBeenCalled()
    expect(apiGetMock.mock.calls.filter(([ruta]) => ruta === '/ventas/42')).toHaveLength(1)
  })

  it('si el comprobante fresco no trae la factura aprobada muestra un error y no imprime nada, ni la factura ni el ticket', async () => {
    const sinFactura = comprobanteFixture({
      id: 43,
      numeroVisible: '0007-00000043',
      fiscal: { estado: 'pendiente', motivo: 'forzada', idFacturacion: 4, error: null, comprobante: null },
    })
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 43, numeroVisible: '0007-00000043', estadoFiscal: 'aprobado' })],
      comprobantes: { 43: sinFactura },
    })
    const alReimprimir = vi.fn()
    const alReimprimirFactura = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} alReimprimirFactura={alReimprimirFactura} />)
    await esperarFila('0007-00000043')

    await userEvent.click(await screen.findByRole('button', { name: 'Reimprimir factura' }))

    expect(await screen.findByText('La factura de esta venta no está disponible para reimprimir.')).toBeInTheDocument()
    expect(alReimprimirFactura).not.toHaveBeenCalled()
    expect(alReimprimir).not.toHaveBeenCalled()
  })

  it('si el comprobante fresco ya no está aprobado, la fila actualiza su marca y deja de ofrecer "Reimprimir factura"', async () => {
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 46, numeroVisible: '0007-00000046', estadoFiscal: 'aprobado' })],
      comprobantes: {
        46: comprobanteFixture({
          id: 46,
          numeroVisible: '0007-00000046',
          fiscal: { estado: 'pendiente', motivo: 'forzada', idFacturacion: 4, error: null, comprobante: null },
        }),
      },
    })
    render(<VentasDelTurno alReimprimir={vi.fn()} alReimprimirFactura={vi.fn()} />)
    await esperarFila('0007-00000046')
    const fila = () => screen.getByText('0007-00000046').closest('tr') as HTMLElement
    expect(within(fila()).getByText('Facturada')).toBeInTheDocument()

    await userEvent.click(await screen.findByRole('button', { name: 'Reimprimir factura' }))

    expect(await screen.findByText('La factura de esta venta no está disponible para reimprimir.')).toBeInTheDocument()
    expect(within(fila()).getByText('Pendiente')).toBeInTheDocument()
    expect(within(fila()).queryByText('Facturada')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reimprimir factura' })).not.toBeInTheDocument()
  })

  it('un comprobante fresco con datos de factura pero con estado fiscal no aprobado tampoco se imprime como factura', async () => {
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 50, numeroVisible: '0007-00000050', estadoFiscal: 'aprobado' })],
      comprobantes: {
        50: comprobanteFixture({ id: 50, numeroVisible: '0007-00000050', fiscal: { ...fiscalAprobado, estado: 'rechazado' } }),
      },
    })
    const alReimprimirFactura = vi.fn()
    render(<VentasDelTurno alReimprimir={vi.fn()} alReimprimirFactura={alReimprimirFactura} />)
    await esperarFila('0007-00000050')

    await userEvent.click(await screen.findByRole('button', { name: 'Reimprimir factura' }))

    expect(await screen.findByText('La factura de esta venta no está disponible para reimprimir.')).toBeInTheDocument()
    expect(alReimprimirFactura).not.toHaveBeenCalled()
  })

  it('si el comprobante fresco no trae ningún dato fiscal tampoco imprime', async () => {
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 44, numeroVisible: '0007-00000044', estadoFiscal: 'aprobado' })],
      comprobantes: { 44: comprobanteFixture({ id: 44, numeroVisible: '0007-00000044' }) },
    })
    const alReimprimirFactura = vi.fn()
    render(<VentasDelTurno alReimprimir={vi.fn()} alReimprimirFactura={alReimprimirFactura} />)
    await esperarFila('0007-00000044')

    await userEvent.click(await screen.findByRole('button', { name: 'Reimprimir factura' }))

    expect(await screen.findByText('La factura de esta venta no está disponible para reimprimir.')).toBeInTheDocument()
    expect(alReimprimirFactura).not.toHaveBeenCalled()
  })

  it('si el comprobante fresco dice Anulado no imprime la factura y muestra el aviso de venta anulada', async () => {
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 47, numeroVisible: '0007-00000047', estadoFiscal: 'aprobado' })],
      comprobantes: { 47: comprobanteFixture({ id: 47, numeroVisible: '0007-00000047', estado: 'Anulado', fiscal: fiscalAprobado }) },
    })
    const alReimprimirFactura = vi.fn()
    render(<VentasDelTurno alReimprimir={vi.fn()} alReimprimirFactura={alReimprimirFactura} />)
    await esperarFila('0007-00000047')

    await userEvent.click(await screen.findByRole('button', { name: 'Reimprimir factura' }))

    expect(await screen.findByText('La venta fue anulada, no se puede reimprimir.')).toBeInTheDocument()
    expect(alReimprimirFactura).not.toHaveBeenCalled()
  })

  it('un fallo al pedir el comprobante se informa y no imprime', async () => {
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 48, numeroVisible: '0007-00000048', estadoFiscal: 'aprobado' })],
      erroresComprobante: { 48: new Error('sin red') },
    })
    const alReimprimirFactura = vi.fn()
    render(<VentasDelTurno alReimprimir={vi.fn()} alReimprimirFactura={alReimprimirFactura} />)
    await esperarFila('0007-00000048')

    await userEvent.click(await screen.findByRole('button', { name: 'Reimprimir factura' }))

    expect(await screen.findByText('No se pudo obtener el comprobante para reimprimir.')).toBeInTheDocument()
    expect(alReimprimirFactura).not.toHaveBeenCalled()
  })

  it('un doble click sobre "Reimprimir factura" dispara una sola búsqueda, y el botón queda ocupado hasta que llega', async () => {
    const comprobante = comprobanteFixture({ id: 45, numeroVisible: '0007-00000045', fiscal: fiscalAprobado })
    let cantidadDeGets = 0
    let resolverComprobante: (valor: ComprobanteEmitido) => void = () => undefined
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/caja/turnos/abierto')) return Promise.resolve(turnoFixture())
      if (ruta.startsWith('/ventas/por-turno/')) return Promise.resolve([ventaFixture({ id: 45, numeroVisible: '0007-00000045', estadoFiscal: 'aprobado' })])
      if (ruta === '/catalogos/medios-pago') return Promise.resolve([medioEfectivo])
      if (ruta === '/ventas/45') {
        cantidadDeGets += 1
        return new Promise((resolve) => {
          resolverComprobante = resolve
        })
      }
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const alReimprimirFactura = vi.fn()
    render(<VentasDelTurno alReimprimir={vi.fn()} alReimprimirFactura={alReimprimirFactura} />)
    await esperarFila('0007-00000045')
    const boton = await screen.findByRole('button', { name: 'Reimprimir factura' })

    await userEvent.click(boton)
    await userEvent.click(boton)

    expect(cantidadDeGets).toBe(1)
    expect(boton).toBeDisabled()
    resolverComprobante(comprobante)
    await waitFor(() => expect(alReimprimirFactura).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(boton).toBeEnabled())
  })

  it('el "Imprimir" del ticket sigue llamando a alReimprimir con el comprobante, aunque la venta esté facturada', async () => {
    const comprobante = comprobanteFixture({ id: 49, numeroVisible: '0007-00000049', fiscal: fiscalAprobado })
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 49, numeroVisible: '0007-00000049', estadoFiscal: 'aprobado' })],
      comprobantes: { 49: comprobante },
    })
    const alReimprimir = vi.fn()
    const alReimprimirFactura = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} alReimprimirFactura={alReimprimirFactura} />)
    await esperarFila('0007-00000049')

    await userEvent.click(screen.getByRole('button', { name: 'Imprimir' }))

    await waitFor(() => expect(alReimprimir).toHaveBeenCalledWith(comprobante, [medioEfectivo]))
    expect(alReimprimirFactura).not.toHaveBeenCalled()
  })
})

describe('VentasDelTurno — nota de crédito (T8c)', () => {
  const datosDeFactura: DatosDeFacturaFiscal = {
    idComprobante: 7,
    codigoTipoComprobante: 'FB',
    letra: 'B',
    puntoDeVentaFiscal: 3,
    numero: 12,
    fecha: '2026-10-14',
    cae: '70123456789012',
    caeVencimiento: '2026-10-24',
    payloadQr: null,
    observaciones: [],
  }
  const datosDeNota: DatosDeFacturaFiscal = { ...datosDeFactura, idComprobante: 8, codigoTipoComprobante: 'NCB', numero: 5, fecha: '2026-10-15' }
  const fiscalConNota = (notaDeCredito: FiscalDeVenta['notaDeCredito']): FiscalDeVenta => ({
    estado: 'aprobado',
    motivo: 'forzada',
    idFacturacion: 4,
    error: null,
    comprobante: datosDeFactura,
    notaDeCredito,
  })
  const NC_APROBADA = { estado: 'aprobado', idFacturacion: 9, comprobante: datosDeNota } as const

  async function esperarFila(numeroVisible: string) {
    await screen.findByText(numeroVisible)
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/catalogos/medios-pago'))
  }

  /** Anula la única fila (id 3) y deja que el POST devuelva `respuesta`. */
  async function anularLaFila(respuesta: ComprobanteEmitido | Error, alImprimirNotaDeCredito = vi.fn()) {
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 3, numeroVisible: '0007-00000003', estadoFiscal: 'aprobado' })],
    })
    if (respuesta instanceof Error) apiPostMock.mockRejectedValueOnce(respuesta)
    else apiPostMock.mockResolvedValueOnce(respuesta)
    render(<VentasDelTurno alImprimirNotaDeCredito={alImprimirNotaDeCredito} />)
    await esperarFila('0007-00000003')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))
    await userEvent.click(screen.getByRole('button', { name: 'Anular venta' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/ventas/3/anulacion'))
    return alImprimirNotaDeCredito
  }

  it('al anular con la NC aprobada la imprime con la factura como asociada, sin marca de reimpresión', async () => {
    const respuesta = comprobanteFixture({ id: 3, estado: 'Anulado', fiscal: fiscalConNota(NC_APROBADA) })
    const imprimir = await anularLaFila(respuesta)

    await waitFor(() => expect(imprimir).toHaveBeenCalledTimes(1))
    expect(imprimir).toHaveBeenCalledWith(respuesta, datosDeNota, datosDeFactura, [medioEfectivo], { reimpresion: false })
    expect(screen.queryByText(/nota de crédito/i)).not.toBeInTheDocument()
  })

  it('con la NC pendiente no imprime y avisa que se emitirá sola', async () => {
    const imprimir = await anularLaFila(comprobanteFixture({ id: 3, estado: 'Anulado', fiscal: fiscalConNota({ estado: 'pendiente', idFacturacion: 9 }) }))

    const aviso = await screen.findByText('La venta 0007-00000003 se anuló. La nota de crédito quedó pendiente; se emitirá sola.')
    expect(aviso.closest('.alert-warning')).not.toBeNull()
    expect(document.body.querySelector('.alert-success')).toBeNull()
    expect(imprimir).not.toHaveBeenCalled()
  })

  it('con la NC rechazada no imprime y avisa que ARCA la rechazó', async () => {
    const imprimir = await anularLaFila(
      comprobanteFixture({ id: 3, estado: 'Anulado', fiscal: fiscalConNota({ estado: 'rechazado', idFacturacion: 9, error: 'x' }) }),
    )

    expect(await screen.findByText('La venta 0007-00000003 se anuló. ARCA rechazó la nota de crédito.')).toBeInTheDocument()
    expect(imprimir).not.toHaveBeenCalled()
  })

  it('con la NC aprobada pero sin comprobante no imprime y lo dice', async () => {
    const imprimir = await anularLaFila(
      comprobanteFixture({ id: 3, estado: 'Anulado', fiscal: fiscalConNota({ estado: 'aprobado', idFacturacion: 9, comprobante: null }) }),
    )

    expect(await screen.findByText(/La nota de crédito no está disponible para imprimir\./)).toBeInTheDocument()
    expect(imprimir).not.toHaveBeenCalled()
  })

  it('una venta sin nota de crédito en la respuesta se anula como siempre: no imprime ni avisa', async () => {
    const imprimir = await anularLaFila(comprobanteFixture({ id: 3, estado: 'Anulado' }))

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Confirmar anulación' })).not.toBeInTheDocument())
    expect(imprimir).not.toHaveBeenCalled()
    expect(screen.queryByText(/se anuló\./)).not.toBeInTheDocument()
  })

  it('el 409 venta_con_factura_en_tramite se muestra con el mensaje de trámite y la venta queda sin anular', async () => {
    const { ErrorApi } = await import('../api/cliente')
    const imprimir = await anularLaFila(new ErrorApi(409, 'venta_con_factura_en_tramite', 'texto del servidor'))

    expect(await screen.findByText('La factura está en trámite en ARCA; probá de nuevo en unos minutos.')).toBeInTheDocument()
    expect(screen.queryByText('texto del servidor')).not.toBeInTheDocument()
    expect(imprimir).not.toHaveBeenCalled()
  })

  it('cada fila anulada muestra la marca de su NC: emitida, pendiente, rechazada, y ninguna sin NC', async () => {
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [
        ventaFixture({ id: 1, numeroVisible: '0007-00000001', estado: 'Anulado', estadoNotaDeCredito: 'aprobado' }),
        ventaFixture({ id: 2, numeroVisible: '0007-00000002', estado: 'Anulado', estadoNotaDeCredito: 'pendiente' }),
        ventaFixture({ id: 3, numeroVisible: '0007-00000003', estado: 'Anulado', estadoNotaDeCredito: 'rechazado' }),
        ventaFixture({ id: 4, numeroVisible: '0007-00000004', estado: 'Anulado', estadoNotaDeCredito: null }),
        ventaFixture({ id: 5, numeroVisible: '0007-00000005', estado: 'Anulado' }),
      ],
    })
    render(<VentasDelTurno />)
    await esperarFila('0007-00000005')

    const etiquetasDe = (numeroVisible: string) => {
      const fila = screen.getByText(numeroVisible).closest('tr') as HTMLElement
      return ['NC emitida', 'NC pendiente', 'NC rechazada'].filter((t) => within(fila).queryByText(t) !== null)
    }
    expect(etiquetasDe('0007-00000001')).toEqual(['NC emitida'])
    expect(etiquetasDe('0007-00000002')).toEqual(['NC pendiente'])
    expect(etiquetasDe('0007-00000003')).toEqual(['NC rechazada'])
    expect(etiquetasDe('0007-00000004')).toEqual([])
    expect(etiquetasDe('0007-00000005')).toEqual([])
  })

  it('"Reimprimir NC" aparece solo en la fila anulada con NC aprobada y solo si el shell sabe imprimir', async () => {
    const ventas = [
      ventaFixture({ id: 1, numeroVisible: '0007-00000001', estado: 'Anulado', estadoNotaDeCredito: 'aprobado' }),
      ventaFixture({ id: 2, numeroVisible: '0007-00000002', estado: 'Anulado', estadoNotaDeCredito: 'pendiente' }),
      ventaFixture({ id: 3, numeroVisible: '0007-00000003', estado: 'Anulado', estadoNotaDeCredito: 'rechazado' }),
      ventaFixture({ id: 4, numeroVisible: '0007-00000004', estado: 'Anulado' }),
    ]
    mockearRutas({ turno: turnoFixture(), medios: [medioEfectivo], ventas })
    const { unmount } = render(<VentasDelTurno alImprimirNotaDeCredito={vi.fn()} />)
    await esperarFila('0007-00000004')

    const botones = screen.getAllByRole('button', { name: 'Reimprimir NC' })
    expect(botones).toHaveLength(1)
    expect((botones[0].closest('tr') as HTMLElement).textContent).toContain('0007-00000001')
    unmount()

    render(<VentasDelTurno />)
    await esperarFila('0007-00000004')
    expect(screen.queryByRole('button', { name: 'Reimprimir NC' })).not.toBeInTheDocument()
  })

  it('"Reimprimir NC" pide la venta por id e imprime la NC con la factura asociada y la marca de reimpresión, sin ticket ni factura', async () => {
    const comprobante = comprobanteFixture({ id: 42, numeroVisible: '0007-00000042', estado: 'Anulado', fiscal: fiscalConNota(NC_APROBADA) })
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 42, numeroVisible: '0007-00000042', estado: 'Anulado', estadoFiscal: 'aprobado', estadoNotaDeCredito: 'aprobado' })],
      comprobantes: { 42: comprobante },
    })
    const imprimirNc = vi.fn()
    const alReimprimir = vi.fn()
    const alReimprimirFactura = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} alReimprimirFactura={alReimprimirFactura} alImprimirNotaDeCredito={imprimirNc} />)
    await esperarFila('0007-00000042')

    await userEvent.click(screen.getByRole('button', { name: 'Reimprimir NC' }))

    await waitFor(() => expect(imprimirNc).toHaveBeenCalledTimes(1))
    expect(imprimirNc).toHaveBeenCalledWith(comprobante, datosDeNota, datosDeFactura, [medioEfectivo], { reimpresion: true })
    expect(alReimprimir).not.toHaveBeenCalled()
    expect(alReimprimirFactura).not.toHaveBeenCalled()
    expect(apiGetMock.mock.calls.filter(([ruta]) => ruta === '/ventas/42')).toHaveLength(1)
  })

  it('si la respuesta fresca no trae la NC aprobada con su comprobante muestra un error, actualiza la marca y no imprime nada', async () => {
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 43, numeroVisible: '0007-00000043', estado: 'Anulado', estadoNotaDeCredito: 'aprobado' })],
      comprobantes: {
        43: comprobanteFixture({
          id: 43,
          numeroVisible: '0007-00000043',
          estado: 'Anulado',
          fiscal: fiscalConNota({ estado: 'aprobado', idFacturacion: 9, comprobante: null }),
        }),
      },
    })
    const imprimirNc = vi.fn()
    const alReimprimir = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} alImprimirNotaDeCredito={imprimirNc} />)
    await esperarFila('0007-00000043')

    await userEvent.click(screen.getByRole('button', { name: 'Reimprimir NC' }))

    expect(await screen.findByText('La nota de crédito de esta venta no está disponible para reimprimir.')).toBeInTheDocument()
    expect(imprimirNc).not.toHaveBeenCalled()
    expect(alReimprimir).not.toHaveBeenCalled()
  })

  it('si la NC ya no está aprobada la fila actualiza su marca y deja de ofrecer "Reimprimir NC"', async () => {
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 44, numeroVisible: '0007-00000044', estado: 'Anulado', estadoNotaDeCredito: 'aprobado' })],
      comprobantes: {
        44: comprobanteFixture({
          id: 44,
          numeroVisible: '0007-00000044',
          estado: 'Anulado',
          fiscal: fiscalConNota({ estado: 'rechazado', idFacturacion: 9 }),
        }),
      },
    })
    render(<VentasDelTurno alImprimirNotaDeCredito={vi.fn()} />)
    await esperarFila('0007-00000044')

    await userEvent.click(screen.getByRole('button', { name: 'Reimprimir NC' }))

    await screen.findByText('NC rechazada')
    expect(screen.queryByRole('button', { name: 'Reimprimir NC' })).not.toBeInTheDocument()
  })

  it('un fallo al pedir la venta se informa y no imprime', async () => {
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 45, numeroVisible: '0007-00000045', estado: 'Anulado', estadoNotaDeCredito: 'aprobado' })],
      erroresComprobante: { 45: new Error('sin red') },
    })
    const imprimirNc = vi.fn()
    render(<VentasDelTurno alImprimirNotaDeCredito={imprimirNc} />)
    await esperarFila('0007-00000045')

    await userEvent.click(screen.getByRole('button', { name: 'Reimprimir NC' }))

    expect(await screen.findByText('No se pudo obtener el comprobante para reimprimir.')).toBeInTheDocument()
    expect(imprimirNc).not.toHaveBeenCalled()
  })

  it('dos clicks del mismo tick sobre "Reimprimir NC" piden y imprimen una sola vez', async () => {
    const comprobante = comprobanteFixture({ id: 46, numeroVisible: '0007-00000046', estado: 'Anulado', fiscal: fiscalConNota(NC_APROBADA) })
    mockearRutas({
      turno: turnoFixture(),
      medios: [medioEfectivo],
      ventas: [ventaFixture({ id: 46, numeroVisible: '0007-00000046', estado: 'Anulado', estadoNotaDeCredito: 'aprobado' })],
    })
    let resolverComprobante: (valor: ComprobanteEmitido) => void = () => undefined
    let cantidadDeGets = 0
    const base = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas/46') {
        cantidadDeGets += 1
        return new Promise<ComprobanteEmitido>((resolve) => {
          resolverComprobante = resolve
        })
      }
      return base(ruta)
    })
    const imprimirNc = vi.fn()
    render(<VentasDelTurno alImprimirNotaDeCredito={imprimirNc} />)
    await esperarFila('0007-00000046')

    const boton = screen.getByRole('button', { name: 'Reimprimir NC' })
    await userEvent.click(boton)
    await userEvent.click(boton)

    expect(cantidadDeGets).toBe(1)
    expect(boton).toBeDisabled()
    resolverComprobante(comprobante)
    await waitFor(() => expect(imprimirNc).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(boton).toBeEnabled())
  })
})

describe('VentasDelTurno — orden por columna', () => {
  const numeros = () =>
    Array.from(screen.getByRole('table').querySelectorAll('tbody tr')).map((fila) => fila.querySelector('td')?.textContent)

  const VENTAS = [
    ventaFixture({ id: 1, numeroVisible: '0007-00000001', total: 300, nombreCliente: 'Zulema', fecha: '2026-09-16T15:00:00Z' }),
    ventaFixture({ id: 2, numeroVisible: '0007-00000002', total: 5000, nombreCliente: 'Ana', fecha: '2026-09-16T12:00:00Z' }),
    ventaFixture({ id: 3, numeroVisible: '0007-00000003', total: 20, nombreCliente: 'Marta', fecha: '2026-09-16T09:00:00Z' }),
  ]

  it('el total ordena por su valor numérico y el pie de totales no se ve afectado', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: VENTAS })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')

    const total = screen.getByRole('columnheader', { name: 'Total' })
    await userEvent.click(within(total).getByRole('button'))

    expect(total).toHaveAttribute('aria-sort', 'ascending')
    expect(numeros()).toEqual(['0007-00000003', '0007-00000001', '0007-00000002'])
    expect(screen.getByText('Total general: $ 5.320,00')).toBeInTheDocument()
  })

  it('la fecha ordena cronológicamente y el cliente alfabéticamente; el tercer clic vuelve al orden original', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: VENTAS })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')

    await userEvent.click(within(screen.getByRole('columnheader', { name: 'Fecha' })).getByRole('button'))
    expect(numeros()).toEqual(['0007-00000003', '0007-00000002', '0007-00000001'])

    const cliente = screen.getByRole('columnheader', { name: 'Cliente' })
    await userEvent.click(within(cliente).getByRole('button'))
    expect(numeros()).toEqual(['0007-00000002', '0007-00000003', '0007-00000001'])
    expect(screen.getByRole('columnheader', { name: 'Fecha' })).toHaveAttribute('aria-sort', 'none')

    await userEvent.click(within(cliente).getByRole('button'))
    await userEvent.click(within(cliente).getByRole('button'))
    expect(numeros()).toEqual(['0007-00000001', '0007-00000002', '0007-00000003'])
  })

  it('ordenar por una columna no desactiva los filtros', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: VENTAS })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')

    await userEvent.type(screen.getByLabelText('Filtrar por número'), '000000')
    await userEvent.click(within(screen.getByRole('columnheader', { name: 'Total' })).getByRole('button'))
    await userEvent.clear(screen.getByLabelText('Filtrar por número'))
    await userEvent.type(screen.getByLabelText('Filtrar por número'), '00000002')

    expect(numeros()).toEqual(['0007-00000002'])
  })

  it('en el detalle, las líneas ordenan por el total numérico', async () => {
    const venta = ventaFixture({ id: 40, numeroVisible: '0007-00000040' })
    const base = comprobanteFixture().items[0]
    const comprobante = comprobanteFixture({
      id: 40,
      numeroVisible: '0007-00000040',
      items: [
        { ...base, orden: 1, descripcion: 'Maple', total: 1150 },
        { ...base, orden: 2, descripcion: 'Agua', total: 90 },
        { ...base, orden: 3, descripcion: 'Sal', total: 5 },
      ],
    })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 40: comprobante } })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000040')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))

    const dialog = within(await screen.findByRole('dialog', { name: /Detalle de la venta 0007-00000040/ }))
    await dialog.findByText('Maple')
    const [lineas] = dialog.getAllByRole('table')
    await userEvent.click(within(within(lineas).getByRole('columnheader', { name: 'Total' })).getByRole('button'))

    expect(Array.from(lineas.querySelectorAll('tbody tr')).map((fila) => fila.querySelector('td')?.textContent)).toEqual(['Sal', 'Agua', 'Maple'])
  })
})
