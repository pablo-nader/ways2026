import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { VentasDelTurno } from './VentasDelTurno'
import type {
  ComprobanteEmitido,
  MedioPagoListado,
  PuntoVentaListado,
  TurnoResumen,
  UsuarioAutenticado,
  VentaDeTurnoListado,
} from '../api/tipos'
import { ROL } from '../api/tipos'
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
    expect(screen.getByText('1 venta(s)')).toBeInTheDocument()
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
    expect(screen.getByText('1 venta(s)')).toBeInTheDocument()
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
  // interpreta "1.500" como 1.5 (parsea el "." como decimal) y rechaza "150,50" (NaN). Estos dos
  // tests pasan por el COMPONENTE (tipeo real, no el helper puro) — mutation-proof-tests: volver
  // a `Number(...)` los hace fallar (evidencia registrada en el reporte de la tarea).
  it('el filtro de total interpreta "1.500" como mil quinientos (formato es-AR del proyecto), nunca como 1,5', async () => {
    mockearRutas({
      turno: turnoFixture(),
      ventas: [
        ventaFixture({ id: 1, numeroVisible: '0007-00000001', total: 1499 }),
        ventaFixture({ id: 2, numeroVisible: '0007-00000002', total: 1500 }),
      ],
    })
    render(<VentasDelTurno />)
    await screen.findByText('0007-00000001')

    await userEvent.type(screen.getByLabelText('Total mínimo'), '1.500')

    // Con `Number('1.500') === 1.5` (el bug original) las DOS filas pasarían el filtro — acá la
    // de 1499 queda afuera y la de 1500 (el límite exacto) adentro.
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
    expect(screen.getByText('0 venta(s)')).toBeInTheDocument()
  })

  it('muestra el error inline cuando la anulación falla', async () => {
    const { ErrorApi } = await import('../api/cliente')
    const venta = ventaFixture({ id: 4 })
    mockearRutas({ turno: turnoFixture(), ventas: [venta] })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000001')
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))

    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'turno_cerrado', 'El turno ya está cerrado.'))
    await userEvent.click(screen.getByRole('button', { name: 'Anular venta' }))

    await screen.findByText('El turno ya está cerrado.')
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

  it('un doble click sobre "Anular venta" solo dispara una anulación (guarda de reentrancia)', async () => {
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
    await userEvent.click(boton)
    // Segundo click mientras la primera solicitud sigue en vuelo — el botón ya está disabled
    // (`ocupado`), pero se dispara igual el handler para probar la guarda de `anulandoRef`.
    await userEvent.click(boton)

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
  it('sin la prop alReimprimir, ninguna fila ni el modal de detalle ofrecen "Reimprimir"', async () => {
    const venta = ventaFixture({ id: 40, numeroVisible: '0007-00000040' })
    const comprobante = comprobanteFixture({ id: 40, numeroVisible: '0007-00000040' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 40: comprobante } })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000040')
    expect(screen.queryByRole('button', { name: 'Reimprimir' })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))
    await screen.findByRole('dialog')
    expect(screen.queryByRole('button', { name: 'Reimprimir' })).not.toBeInTheDocument()
  })

  it('con alReimprimir provisto, una venta Anulada nunca ofrece "Reimprimir" (fila ni modal)', async () => {
    const venta = ventaFixture({ id: 41, numeroVisible: '0007-00000041', estado: 'Anulado' })
    const comprobante = comprobanteFixture({ id: 41, numeroVisible: '0007-00000041', estado: 'Anulado' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 41: comprobante } })
    const alReimprimir = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} />)

    await screen.findByText('0007-00000041')
    expect(screen.queryByRole('button', { name: 'Reimprimir' })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))
    await screen.findByRole('dialog')
    expect(screen.queryByRole('button', { name: 'Reimprimir' })).not.toBeInTheDocument()
  })

  it('"Reimprimir" de la fila busca el comprobante y llama a la prop con el comprobante y los medios', async () => {
    const venta = ventaFixture({ id: 42, numeroVisible: '0007-00000042' })
    const comprobante = comprobanteFixture({ id: 42, numeroVisible: '0007-00000042' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 42: comprobante } })
    const alReimprimir = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} />)

    await screen.findByText('0007-00000042')
    await userEvent.click(screen.getByRole('button', { name: 'Reimprimir' }))

    await waitFor(() => expect(alReimprimir).toHaveBeenCalledWith(comprobante, [medioEfectivo]))
  })

  it('"Reimprimir" desde el modal de detalle SIEMPRE vuelve a pedir el comprobante (nunca reusa comprobanteDetalle) — JD-E1-2', async () => {
    const venta = ventaFixture({ id: 43, numeroVisible: '0007-00000043' })
    const comprobante = comprobanteFixture({ id: 43, numeroVisible: '0007-00000043' })
    mockearRutas({ turno: turnoFixture(), ventas: [venta], medios: [medioEfectivo], comprobantes: { 43: comprobante } })
    const alReimprimir = vi.fn()
    render(<VentasDelTurno alReimprimir={alReimprimir} />)

    await screen.findByText('0007-00000043')
    await userEvent.click(screen.getByRole('button', { name: 'Detalle' }))
    const dialog = await screen.findByRole('dialog')

    const cantidadDeGetsAntes = apiGetMock.mock.calls.filter(([ruta]) => ruta === '/ventas/43').length

    // Dos botones "Reimprimir" en pantalla (fila + modal, la fila queda deshabilitada mientras el
    // modal está abierto) — se scopea al del modal.
    await userEvent.click(within(dialog).getByRole('button', { name: 'Reimprimir' }))

    await waitFor(() => expect(alReimprimir).toHaveBeenCalledWith(comprobante, [medioEfectivo]))
    // La venta pudo anularse DESPUÉS de abrir el detalle — reusar `comprobanteDetalle` imprimiría
    // un ticket de una venta ya no válida. Acá SÍ hay un segundo GET, con el detalle ya cargado.
    const cantidadDeGetsDespues = apiGetMock.mock.calls.filter(([ruta]) => ruta === '/ventas/43').length
    expect(cantidadDeGetsDespues).toBe(cantidadDeGetsAntes + 1)
  })

  it('fila: si el listado dice Emitida pero el GET fresco dice Anulada, no imprime — muestra el error y la fila se refresca (el botón desaparece) — JD-E1-2', async () => {
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
    // Emitida en el listado — el botón "Reimprimir" está visible antes de la búsqueda fresca.
    expect(screen.getByText('Emitida', { selector: 'span' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reimprimir' }))

    await screen.findByText('La venta fue anulada, no se puede reimprimir.')
    expect(alReimprimir).not.toHaveBeenCalled()
    // La fila se refresca con el estado real: el badge pasa a Anulada y "Reimprimir" desaparece.
    expect(await screen.findByText('Anulada', { selector: 'span' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reimprimir' })).not.toBeInTheDocument()
  })

  it('modal: si el detalle ya cargado dice Emitida pero el GET fresco de "Reimprimir" dice Anulada, no imprime — el modal se refresca (badge y botón) — JD-E1-2', async () => {
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

    // La venta se anula DESPUÉS de que el detalle ya cargó — el próximo GET (el de "Reimprimir")
    // trae el estado real.
    const comprobanteAnuladoFresco = comprobanteFixture({ id: 47, numeroVisible: '0007-00000047', estado: 'Anulado' })
    mockearRutas({
      turno: turnoFixture(),
      ventas: [venta],
      medios: [medioEfectivo],
      comprobantes: { 47: comprobanteAnuladoFresco },
    })

    await userEvent.click(within(dialog).getByRole('button', { name: 'Reimprimir' }))

    await within(dialog).findByText('La venta fue anulada, no se puede reimprimir.')
    expect(alReimprimir).not.toHaveBeenCalled()
    // El modal se refresca con el comprobante real: el badge pasa a Anulada y "Reimprimir" ya no
    // se ofrece (no tiene sentido reimprimir algo que se acaba de confirmar anulado).
    expect(within(dialog).getByText('Anulada', { selector: 'span' })).toBeInTheDocument()
    expect(within(dialog).queryByRole('button', { name: 'Reimprimir' })).not.toBeInTheDocument()
  })

  it('muestra la etiqueta "Reimprimiendo…" mientras busca el comprobante, y un error visible si falla', async () => {
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
    await userEvent.click(screen.getByRole('button', { name: 'Reimprimir' }))

    await screen.findByRole('button', { name: 'Reimprimiendo…' })

    rechazar(new ErrorApi(500, 'error', 'No se pudo obtener la venta.'))
    await screen.findByText('No se pudo obtener la venta.')
    expect(alReimprimir).not.toHaveBeenCalled()
  })

  it('un doble click sobre "Reimprimir" de la misma fila solo dispara una búsqueda (guarda por fila)', async () => {
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
    const boton = screen.getByRole('button', { name: 'Reimprimir' })
    await userEvent.click(boton)
    await userEvent.click(boton)

    expect(cantidadDeGets).toBe(1)
    resolverComprobante(comprobante)
    await waitFor(() => expect(alReimprimir).toHaveBeenCalledTimes(1))
  })
})
