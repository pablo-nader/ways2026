import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { VentasDelTurno } from './VentasDelTurno'
import type { PuntoVentaListado, TurnoResumen, UsuarioAutenticado, VentaDeTurnoListado } from '../api/tipos'
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
    mediosDePago: ['Efectivo'],
    ...sobrescribir,
  }
}

function mockearRutas(opciones: {
  turno?: TurnoResumen | null
  ventas?: VentaDeTurnoListado[]
  errorTurno?: unknown
  errorVentas?: unknown
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
          mediosDePago: ['Tarjeta'],
          nombreCliente: 'Juan Pérez',
        }),
      ],
    })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000001')
    expect(screen.getByText('0007-00000002')).toBeInTheDocument()
    expect(screen.getByText('Consumidor Final')).toBeInTheDocument()
    expect(screen.getByText('Juan Pérez')).toBeInTheDocument()
    expect(screen.getByText('Anulada')).toBeInTheDocument()

    // Totales: solo cuenta la no anulada (regla bajo prueba de mutation-proof-tests).
    expect(screen.getByText('1 venta(s) — total $100,00')).toBeInTheDocument()
  })

  it('no ofrece "Anular" sobre una fila ya anulada', async () => {
    mockearRutas({ turno: turnoFixture(), ventas: [ventaFixture({ id: 9, estado: 'Anulado' })] })
    render(<VentasDelTurno />)

    await screen.findByText('0007-00000001')
    expect(screen.queryByRole('button', { name: 'Anular' })).not.toBeInTheDocument()
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
    expect(screen.getByText(/¿Anular la venta 0007-00000003 por \$200,00\?/)).toBeInTheDocument()

    apiPostMock.mockResolvedValueOnce({ ...venta, estado: 'Anulado' })
    // El refresco posterior a la anulación reconsulta el listado.
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/caja/turnos/abierto')) return Promise.resolve(turnoFixture())
      if (ruta.startsWith('/ventas/por-turno/')) return Promise.resolve([{ ...venta, estado: 'Anulado' }])
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })

    await userEvent.click(screen.getByRole('button', { name: 'Anular venta' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/ventas/3/anulacion'))
    await waitFor(() => expect(screen.getByText('Anulada')).toBeInTheDocument())
    expect(screen.getByText('0 venta(s) — total $0,00')).toBeInTheDocument()
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
    expect(screen.getByText('Emitida')).toBeInTheDocument()
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
