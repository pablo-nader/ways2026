import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Caja } from './Caja'
import { ErrorApi } from '../api/cliente'
import type { MedioPagoListado, MovimientoRegistrado, PuntoVentaListado, ResumenDeTurno, TurnoResumen } from '../api/tipos'

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

function medioFixture(sobrescribir: Partial<MedioPagoListado> = {}): MedioPagoListado {
  return {
    id: 1,
    nombre: 'Efectivo',
    activo: true,
    idEmpresa: null,
    orden: 1,
    comportamiento: 'Efectivo',
    admiteVuelto: true,
    requiereReferencia: false,
    recargoPorcentaje: null,
    ...sobrescribir,
  }
}

function turnoFixture(sobrescribir: Partial<TurnoResumen> = {}): TurnoResumen {
  return {
    id: 501,
    idPuntoVenta: 7,
    idEmpleadoApertura: 3,
    idEmpleadoCierre: null,
    fechaApertura: '2026-08-04T12:00:00Z',
    fechaCierre: null,
    fondoInicial: 500,
    estado: 'Abierto',
    observaciones: null,
    ...sobrescribir,
  }
}

function resumenFixture(sobrescribir: Partial<ResumenDeTurno> = {}): ResumenDeTurno {
  return {
    idTurnoCaja: 501,
    idMedioAncla: 1,
    medios: [{ idMedioPago: 1, importeEsperado: 640 }],
    cantidadTickets: 0,
    primerTicket: null,
    ultimoTicket: null,
    ingresosPorArea: [],
    egresos: { porCategoria: [], porArea: [], retiros: 0 },
    ...sobrescribir,
  }
}

function movimientoFixture(sobrescribir: Partial<MovimientoRegistrado> = {}): MovimientoRegistrado {
  return {
    id: 90,
    idTurnoCaja: 501,
    tipo: 'Retiro',
    importe: 100,
    motivo: 'motivo de prueba',
    idEmpleado: 3,
    creadoEl: '2026-08-04T13:00:00Z',
    ...sobrescribir,
  }
}

const medioEfectivo = medioFixture()
const puntoVentaCentro = puntoVentaFixture()

/** Rutas comunes a toda la pantalla (puntos de venta + medios de pago) — cada test suma encima
 * las rutas de caja que le hacen falta (`/caja/turnos/...`). */
function mockearRutasBase(sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
    if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo])
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
})

describe('Caja — apertura', () => {
  it('sin turno abierto muestra el formulario de apertura; abrirlo lo reemplaza por el panel del turno y dispara el resumen', async () => {
    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(null)
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos') return Promise.resolve<TurnoResumen>(turnoFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('No hay un turno abierto en este punto de venta.')

    await userEvent.type(screen.getByLabelText('Fondo inicial'), '500')
    await userEvent.type(screen.getByLabelText('Observaciones'), 'apertura de prueba')
    await userEvent.click(screen.getByRole('button', { name: 'Abrir turno' }))

    await screen.findByText('Turno abierto')

    const llamada = apiPostMock.mock.calls.find((c) => c[0] === '/caja/turnos')
    expect(llamada?.[1]).toEqual({ idPuntoVenta: 7, fondoInicial: 500, observaciones: 'apertura de prueba' })

    await waitFor(() => expect(screen.getByText('$640,00')).toBeInTheDocument())
  })

  it('un fondo inicial negativo se rechaza localmente, sin disparar el POST', async () => {
    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(null)
      return undefined
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('No hay un turno abierto en este punto de venta.')

    await userEvent.type(screen.getByLabelText('Fondo inicial'), '-10')
    await userEvent.click(screen.getByRole('button', { name: 'Abrir turno' }))

    expect(await screen.findByText('El fondo inicial tiene que ser un número mayor o igual a 0.')).toBeInTheDocument()
    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/caja/turnos')).toHaveLength(0)
  })

  it('doble click en "Abrir turno" dispara exactamente un POST', async () => {
    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(null)
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      return undefined
    })

    let resolverApertura: (t: TurnoResumen) => void = () => {}
    const aperturaPendiente = new Promise<TurnoResumen>((resolve) => {
      resolverApertura = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos') return aperturaPendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('No hay un turno abierto en este punto de venta.')
    await userEvent.type(screen.getByLabelText('Fondo inicial'), '500')

    const boton = screen.getByRole('button', { name: 'Abrir turno' })
    await userEvent.click(boton)
    await userEvent.click(boton)
    fireEvent.click(boton)

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/caja/turnos')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Abriendo…' })).toBeDisabled()

    await act(async () => {
      resolverApertura(turnoFixture())
      await Promise.resolve()
    })
    await screen.findByText('Turno abierto')
  })

  it('autocuración del 409 turno_ya_abierto (task 7.8, judgment-day slice-6 finding): en vez de un error + formulario obsoleto, refetchea y muestra el turno que ganó la carrera', async () => {
    let llamadasAbierto = 0
    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') {
        llamadasAbierto += 1
        // La primera consulta (montaje) no encuentra turno — por eso se muestra el formulario.
        // La segunda (autocuración, tras el 409) sí lo encuentra: otra pestaña lo abrió primero.
        return llamadasAbierto === 1
          ? Promise.resolve<TurnoResumen | null>(null)
          : Promise.resolve<TurnoResumen | null>(turnoFixture())
      }
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos') return Promise.reject(new ErrorApi(409, 'turno_ya_abierto', 'Ya hay un turno abierto en este punto de venta.'))
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('No hay un turno abierto en este punto de venta.')

    await userEvent.type(screen.getByLabelText('Fondo inicial'), '500')
    await userEvent.click(screen.getByRole('button', { name: 'Abrir turno' }))

    await screen.findByText('Turno abierto')
    expect(screen.queryByText('Ya hay un turno abierto en este punto de venta.')).not.toBeInTheDocument()
    expect(llamadasAbierto).toBe(2)
  })
})

describe('Caja — movimientos', () => {
  it('registrar un movimiento bumpea la generación del resumen y descarta una respuesta tardía de la carga inicial', async () => {
    let resolverResumenLento: (valor: ResumenDeTurno) => void = () => {}
    const resumenLentoPromise = new Promise<ResumenDeTurno>((resolve) => {
      resolverResumenLento = resolve
    })
    let llamadasResumen = 0

    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(turnoFixture())
      if (ruta === '/caja/turnos/501/resumen') {
        llamadasResumen += 1
        if (llamadasResumen === 1) return resumenLentoPromise
        return Promise.resolve<ResumenDeTurno>(resumenFixture({ medios: [{ idMedioPago: 1, importeEsperado: 700 }] }))
      }
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/movimientos') return Promise.resolve<MovimientoRegistrado>(movimientoFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('Turno abierto')
    // El badge se monta en el mismo commit que dispara el efecto de carga del resumen, pero ese
    // efecto todavía no corrió bajo carga de la suite completa (mismo flake que PR #59 fijó más
    // abajo en este archivo) — hay que esperar a "Calculando…", no asumir que ya está.
    await screen.findByText('Calculando…')

    await userEvent.type(screen.getByLabelText('Importe'), '100')
    await userEvent.type(screen.getByLabelText('Motivo'), 'retiro de prueba')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar movimiento' }))

    await waitFor(() => expect(screen.getByText('$700,00')).toBeInTheDocument())

    // La respuesta lenta de la carga inicial (generación anterior) llega recién ahora, con datos
    // viejos (distintos del fondo inicial del turno, $500,00, para no confundir el assert con el
    // encabezado) — no puede pisar los $700,00 ya mostrados por el refetch posterior al movimiento.
    await act(async () => {
      resolverResumenLento(resumenFixture({ medios: [{ idMedioPago: 1, importeEsperado: 640 }] }))
      await Promise.resolve()
    })
    expect(screen.getByText('$700,00')).toBeInTheDocument()
    expect(screen.queryByText('$640,00')).not.toBeInTheDocument()
  })

  it('un motivo de menos de 5 caracteres se rechaza localmente, sin disparar el POST', async () => {
    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(turnoFixture())
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      return undefined
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('Turno abierto')

    await userEvent.type(screen.getByLabelText('Importe'), '100')
    await userEvent.type(screen.getByLabelText('Motivo'), 'abc')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar movimiento' }))

    expect(await screen.findByText('El motivo tiene que tener al menos 5 caracteres.')).toBeInTheDocument()
    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/caja/turnos/501/movimientos')).toHaveLength(0)
  })

  it('apertura de cajón fuerza el importe a 0 en el campo (deshabilitado) y en el cuerpo enviado', async () => {
    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(turnoFixture())
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/movimientos') {
        return Promise.resolve<MovimientoRegistrado>(
          movimientoFixture({ tipo: 'AperturaCajon', importe: 0, motivo: 'conteo inicial de turno' }),
        )
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('Turno abierto')

    await userEvent.selectOptions(screen.getByLabelText('Tipo de movimiento'), 'Apertura de cajón')
    expect(screen.getByLabelText('Importe')).toBeDisabled()
    expect(screen.getByLabelText('Importe')).toHaveValue(0)

    await userEvent.type(screen.getByLabelText('Motivo'), 'conteo inicial de turno')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar movimiento' }))

    await waitFor(() =>
      expect(apiPostMock.mock.calls.some((c) => c[0] === '/caja/turnos/501/movimientos')).toBe(true),
    )
    const llamada = apiPostMock.mock.calls.find((c) => c[0] === '/caja/turnos/501/movimientos')
    expect(llamada?.[1]).toEqual({ tipo: 'AperturaCajon', importe: 0, motivo: 'conteo inicial de turno' })
  })

  it('doble click en "Registrar movimiento" dispara exactamente un POST', async () => {
    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(turnoFixture())
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      return undefined
    })

    let resolverMovimiento: (m: MovimientoRegistrado) => void = () => {}
    const movimientoPendiente = new Promise<MovimientoRegistrado>((resolve) => {
      resolverMovimiento = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/movimientos') return movimientoPendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('Turno abierto')
    await userEvent.type(screen.getByLabelText('Importe'), '100')
    await userEvent.type(screen.getByLabelText('Motivo'), 'retiro de prueba')

    const boton = screen.getByRole('button', { name: 'Registrar movimiento' })
    await userEvent.click(boton)
    await userEvent.click(boton)
    fireEvent.click(boton)

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/caja/turnos/501/movimientos')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Registrando…' })).toBeDisabled()

    await act(async () => {
      resolverMovimiento(movimientoFixture())
      await Promise.resolve()
    })
  })

  it('un movimiento que falla igual dispara el refetch del resumen: "Calculando…" no queda colgado', async () => {
    let resolverResumenInicial: (valor: ResumenDeTurno) => void = () => {}
    const resumenInicialPromise = new Promise<ResumenDeTurno>((resolve) => {
      resolverResumenInicial = resolve
    })
    let llamadasResumen = 0

    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(turnoFixture())
      if (ruta === '/caja/turnos/501/resumen') {
        llamadasResumen += 1
        if (llamadasResumen === 1) return resumenInicialPromise
        return Promise.resolve<ResumenDeTurno>(resumenFixture({ medios: [{ idMedioPago: 1, importeEsperado: 850 }] }))
      }
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/movimientos') {
        return Promise.reject(new ErrorApi(400, 'error', 'No se pudo registrar el movimiento.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('Turno abierto')
    // El badge "Turno abierto" se monta en el mismo commit que dispara el efecto de carga del
    // resumen, pero ese efecto todavía no corrió: hay que esperar a "Calculando…" (no asumir que
    // ya está), si no la aserción es una carrera contra el flush del efecto y flakea bajo carga
    // (suite completa).
    await screen.findByText('Calculando…')

    await userEvent.type(screen.getByLabelText('Importe'), '100')
    await userEvent.type(screen.getByLabelText('Motivo'), 'retiro de prueba')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar movimiento' }))

    expect(await screen.findByText('No se pudo registrar el movimiento.')).toBeInTheDocument()

    // regla 6: aunque la respuesta inicial del resumen (generación anterior al bump previo a la
    // escritura) nunca llega, el refetch disparado tras la falla del movimiento cierra
    // "Calculando…" por su cuenta y trae el dato vigente.
    await waitFor(() => expect(screen.queryByText('Calculando…')).not.toBeInTheDocument())
    await waitFor(() => expect(screen.getByText('$850,00')).toBeInTheDocument())

    // la respuesta huérfana de la carga inicial, si llega tarde, no debe pisar nada.
    await act(async () => {
      resolverResumenInicial(resumenFixture({ medios: [{ idMedioPago: 1, importeEsperado: 111 }] }))
      await Promise.resolve()
    })
    expect(screen.getByText('$850,00')).toBeInTheDocument()
    expect(screen.queryByText('$111,00')).not.toBeInTheDocument()
  })
})

describe('Caja — selector de punto de venta bloqueado durante una escritura en vuelo (react-async-state regla 9)', () => {
  it('queda deshabilitado mientras la apertura del turno está en vuelo', async () => {
    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(null)
      return undefined
    })

    let resolverApertura: (t: TurnoResumen) => void = () => {}
    const aperturaPendiente = new Promise<TurnoResumen>((resolve) => {
      resolverApertura = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos') return aperturaPendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('No hay un turno abierto en este punto de venta.')

    await userEvent.type(screen.getByLabelText('Fondo inicial'), '500')
    await userEvent.click(screen.getByRole('button', { name: 'Abrir turno' }))

    expect(screen.getByLabelText('Punto de venta')).toBeDisabled()

    await act(async () => {
      resolverApertura(turnoFixture())
      await Promise.resolve()
    })
  })

  it('queda deshabilitado mientras un movimiento de caja está en vuelo', async () => {
    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(turnoFixture())
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      return undefined
    })

    let resolverMovimiento: (m: MovimientoRegistrado) => void = () => {}
    const movimientoPendiente = new Promise<MovimientoRegistrado>((resolve) => {
      resolverMovimiento = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/movimientos') return movimientoPendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('Turno abierto')
    await userEvent.type(screen.getByLabelText('Importe'), '100')
    await userEvent.type(screen.getByLabelText('Motivo'), 'retiro de prueba')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar movimiento' }))

    expect(screen.getByLabelText('Punto de venta')).toBeDisabled()

    await act(async () => {
      resolverMovimiento(movimientoFixture())
      await Promise.resolve()
    })
  })
})

describe('Caja — turno nuevo no hereda el estado del anterior (react-async-state regla 8)', () => {
  it('cambiar de punto de venta a uno con otro turno abierto resetea el formulario de movimiento y el resumen mostrado', async () => {
    const puntoVentaNorte = puntoVentaFixture({ id: 8, nombre: 'Local Norte' })
    const turnoNorte = turnoFixture({ id: 900, idPuntoVenta: 8, fondoInicial: 1000 })

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro, puntoVentaNorte])
      if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo])
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(turnoFixture())
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=8') return Promise.resolve<TurnoResumen | null>(turnoNorte)
      if (ruta === '/caja/turnos/501/resumen') {
        return Promise.resolve<ResumenDeTurno>(resumenFixture({ idTurnoCaja: 501, medios: [{ idMedioPago: 1, importeEsperado: 640 }] }))
      }
      if (ruta === '/caja/turnos/900/resumen') {
        return Promise.resolve<ResumenDeTurno>(resumenFixture({ idTurnoCaja: 900, medios: [{ idMedioPago: 1, importeEsperado: 999 }] }))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('Turno abierto')
    await waitFor(() => expect(screen.getByText('$640,00')).toBeInTheDocument())

    await userEvent.type(screen.getByLabelText('Motivo'), 'algo que no debería sobrevivir al cambio de turno')

    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), 'Local Norte')

    await waitFor(() => expect(screen.getByText('$999,00')).toBeInTheDocument())
    expect(screen.queryByText('$640,00')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Motivo')).toHaveValue('')
  })
})

describe('Caja — errores', () => {
  it('un turno abierto que falla al consultarse muestra el mensaje del servidor', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
      if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo])
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') {
        return Promise.reject(new ErrorApi(500, 'error', 'No se pudo consultar el turno.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Caja />, { wrapper: MemoryRouter })

    expect(await screen.findByText('No se pudo consultar el turno.')).toBeInTheDocument()
  })
})

describe('Caja — navegación a cierre (Slice 7)', () => {
  it('el panel del turno abierto ofrece "Cerrar turno" con el id del turno en la URL', async () => {
    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(turnoFixture())
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      return undefined
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('Turno abierto')

    expect(screen.getByRole('link', { name: 'Cerrar turno' })).toHaveAttribute('href', '/caja/cierre?idTurno=501')
  })
})

describe('Caja — resumen D6 (follow-up "Resumen parcial D6-content enrichment")', () => {
  it('muestra cantidad de tickets, primer/último ticket, ingresos por área y egresos por categoría + retiros', async () => {
    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(turnoFixture())
      if (ruta === '/caja/turnos/501/resumen') {
        return Promise.resolve<ResumenDeTurno>(
          resumenFixture({
            cantidadTickets: 3,
            primerTicket: { numero: 1001, fecha: '2026-08-04T12:00:00Z', codigo: 'TX' },
            ultimoTicket: { numero: 3, fecha: '2026-08-04T14:00:00Z', codigo: 'RC' },
            ingresosPorArea: [
              { idArea: 1, nombreArea: 'Almacén', total: 150 },
              { idArea: 2, nombreArea: 'Verdulería', total: 200 },
            ],
            egresos: {
              porCategoria: [{ categoria: 'Proveedor', total: 30 }],
              porArea: [
                { idArea: 3, nombreArea: 'Cigarrillos', total: 25 },
                { idArea: null, nombreArea: 'Sin área', total: 15 },
              ],
              retiros: 40,
            },
          }),
        )
      }
      return undefined
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('Turno abierto')
    await waitFor(() => expect(screen.getByText('Tickets')).toBeInTheDocument())

    expect(screen.getByText('3')).toBeInTheDocument()
    // mezcla de series independientes por tipo (design decisión 7): el código deja "TX #1001" y
    // "RC #3" inequívocos, aunque una RC pudiera numerar por debajo del último ticket TX.
    expect(screen.getByText('TX #1001 · ' + new Date('2026-08-04T12:00:00Z').toLocaleString('es-AR'))).toBeInTheDocument()
    expect(screen.getByText('RC #3 · ' + new Date('2026-08-04T14:00:00Z').toLocaleString('es-AR'))).toBeInTheDocument()

    expect(screen.getByText('Almacén')).toBeInTheDocument()
    expect(screen.getByText('$150,00')).toBeInTheDocument()
    expect(screen.getByText('Verdulería')).toBeInTheDocument()
    expect(screen.getByText('$200,00')).toBeInTheDocument()

    expect(screen.getByText('Proveedores')).toBeInTheDocument()
    expect(screen.getByText('$30,00')).toBeInTheDocument()
    expect(screen.getByText('Retiros')).toBeInTheDocument()
    expect(screen.getByText('$40,00')).toBeInTheDocument()

    // egresos por área — bloque nuevo, incluye el bucket "Sin área".
    expect(screen.getByText('Por área')).toBeInTheDocument()
    expect(screen.getByText('Cigarrillos')).toBeInTheDocument()
    expect(screen.getByText('$25,00')).toBeInTheDocument()
    expect(screen.getByText('Sin área')).toBeInTheDocument()
    expect(screen.getByText('$15,00')).toBeInTheDocument()
  })

  it('un turno sin actividad muestra ceros, guiones y los avisos de "todavía no hay" en las cuatro secciones', async () => {
    mockearRutasBase((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen | null>(turnoFixture())
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      return undefined
    })

    render(<Caja />, { wrapper: MemoryRouter })
    await screen.findByText('Turno abierto')
    await waitFor(() => expect(screen.getByText('Tickets')).toBeInTheDocument())

    expect(screen.getByText('0')).toBeInTheDocument()
    expect(screen.getAllByText('—')).toHaveLength(2) // primer y último ticket, ambos null
    expect(screen.getByText('Todavía no hay ingresos en este turno.')).toBeInTheDocument()
    // "todavía no hay egresos" aparece una vez por tabla (por categoría y por área).
    expect(screen.getAllByText('Todavía no hay egresos en este turno.')).toHaveLength(2)
    // sin actividad, no debería renderizarse una fila de "Retiros" suelta.
    expect(screen.queryByText('Retiros')).not.toBeInTheDocument()
  })
})
