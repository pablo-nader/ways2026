import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CierreDeCaja } from './CierreDeCaja'
import { ErrorApi } from '../api/cliente'
import type { MedioPagoListado, ResumenDeTurno, TurnoConArqueos } from '../api/tipos'

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

function resumenFixture(sobrescribir: Partial<ResumenDeTurno> = {}): ResumenDeTurno {
  return {
    idTurnoCaja: 501,
    idMedioAncla: 1,
    medios: [{ idMedioPago: 1, importeEsperado: 640 }],
    ...sobrescribir,
  }
}

function turnoConArqueosFixture(sobrescribir: Partial<TurnoConArqueos> = {}): TurnoConArqueos {
  return {
    id: 501,
    idPuntoVenta: 7,
    idEmpleadoApertura: 3,
    idEmpleadoCierre: 3,
    fechaApertura: '2026-08-04T12:00:00Z',
    fechaCierre: '2026-08-04T20:00:00Z',
    fondoInicial: 500,
    estado: 'Cerrado',
    observaciones: null,
    arqueos: [{ idMedioPago: 1, importeEsperado: 640, importeDeclarado: 635, diferencia: 5 }],
    ...sobrescribir,
  }
}

const medioEfectivo = medioFixture()

function renderCierre(idTurno: string | null = '501') {
  const ruta = idTurno === null ? '/caja/cierre' : `/caja/cierre?idTurno=${idTurno}`
  return render(<CierreDeCaja />, { wrapper: ({ children }) => <MemoryRouter initialEntries={[ruta]}>{children}</MemoryRouter> })
}

/** Rutas comunes a toda la pantalla (medios de pago + resumen del turno 501) — cada test suma
 * encima las rutas de cierre que le hacen falta. */
function mockearRutasBase(sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo])
    if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
})

describe('CierreDeCaja — turno inválido', () => {
  it('sin idTurno en la URL muestra un aviso y no dispara ningún fetch', async () => {
    renderCierre(null)

    expect(await screen.findByText('No se especificó el turno a cerrar.')).toBeInTheDocument()
    expect(apiGetMock).not.toHaveBeenCalled()
  })
})

describe('CierreDeCaja — flujo feliz', () => {
  it('completar los conteos, confirmar y finalizar cierra el turno y muestra el comprobante Z con las diferencias', async () => {
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') return Promise.resolve<TurnoConArqueos>(turnoConArqueosFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()
    await screen.findByText('Efectivo')

    await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '635')
    expect(screen.getByText('$5,00')).toBeInTheDocument() // vista previa de diferencia (rule: preview, nunca autoritativa)

    await userEvent.click(screen.getByRole('checkbox'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    await screen.findByText('Turno #501 cerrado')
    const fila = screen.getByText('Efectivo').closest('tr') as HTMLElement
    expect(fila.textContent).toContain('$640,00')
    expect(fila.textContent).toContain('$635,00')
    expect(fila.textContent).toContain('$5,00')

    const llamada = apiPostMock.mock.calls.find((c) => c[0] === '/caja/turnos/501/cierre')
    expect(llamada?.[1]).toEqual({ conteos: [{ idMedioPago: 1, importeDeclarado: 635 }], observaciones: null })
  })

  it('sin completar todos los conteos, "Finalizar cierre" queda deshabilitado', async () => {
    mockearRutasBase()
    renderCierre()
    await screen.findByText('Efectivo')

    await userEvent.click(screen.getByRole('checkbox'))
    expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeDisabled()
  })

  it('sin marcar la confirmación de irreversibilidad, "Finalizar cierre" queda deshabilitado aunque los conteos estén completos', async () => {
    mockearRutasBase()
    renderCierre()
    await screen.findByText('Efectivo')

    await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '640')
    expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeDisabled()
  })
})

describe('CierreDeCaja — reentrancia (react-async-state regla 9)', () => {
  it('doble click en "Finalizar cierre" dispara exactamente un POST', async () => {
    mockearRutasBase()
    let resolverCierre: (t: TurnoConArqueos) => void = () => {}
    const cierrePendiente = new Promise<TurnoConArqueos>((resolve) => {
      resolverCierre = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') return cierrePendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()
    await screen.findByText('Efectivo')
    await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox'))

    const boton = screen.getByRole('button', { name: 'Finalizar cierre' })
    await userEvent.click(boton)
    await userEvent.click(boton)
    fireEvent.click(boton)

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/caja/turnos/501/cierre')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Cerrando…' })).toBeDisabled()
    expect(screen.getByLabelText('Declarado de Efectivo')).toBeDisabled()

    await act(async () => {
      resolverCierre(turnoConArqueosFixture())
      await Promise.resolve()
    })
  })
})

describe('CierreDeCaja — un cierre 2xx nunca se reporta como falla, ni se muestra sin datos (react-async-state regla 6)', () => {
  it('un POST de cierre exitoso muestra el turno cerrado CON los datos del arqueo, sin depender de ningún fetch posterior', async () => {
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') return Promise.resolve<TurnoConArqueos>(turnoConArqueosFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()
    await screen.findByText('Efectivo')
    await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox'))
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    await screen.findByText('Turno #501 cerrado')
    const fila = screen.getByText('Efectivo').closest('tr') as HTMLElement
    expect(fila.textContent).toContain('$640,00')
    expect(fila.textContent).toContain('$635,00')
    expect(fila.textContent).toContain('$5,00')
    expect(screen.queryByText('No se pudo cerrar el turno.')).not.toBeInTheDocument()

    // El payload del comprobante Z sale íntegro del POST — no hay ningún GET adicional a
    // `/caja/turnos/501` después del cierre.
    expect(apiGetMock.mock.calls.some((c) => c[0] === '/caja/turnos/501')).toBe(false)
  })
})

describe('CierreDeCaja — turno sin actividad (Fix: conteosCompletos ya no exige medios.length > 0)', () => {
  it('un turno sin actividad (resumen.medios vacío) se cierra con conteos: [] y muestra el comprobante Z vacío', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo])
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture({ medios: [] }))
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') {
        return Promise.resolve<TurnoConArqueos>(turnoConArqueosFixture({ arqueos: [] }))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()
    await screen.findByText('Este turno no tuvo actividad: no hay ningún medio para arquear.')

    await userEvent.click(screen.getByRole('checkbox'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    await screen.findByText('Turno #501 cerrado')
    expect(screen.getByText('Este turno no tuvo actividad: no hay ningún medio arqueado.')).toBeInTheDocument()

    const llamada = apiPostMock.mock.calls.find((c) => c[0] === '/caja/turnos/501/cierre')
    expect(llamada?.[1]).toEqual({ conteos: [], observaciones: null })
  })
})

describe('CierreDeCaja — checklist desactualizado tras un rechazo del servidor (Fix: refetch del resumen)', () => {
  it('arqueo_incompleto refresca el resumen, agrega el medio que apareció en el servidor, preserva los conteos ya tipeados y vuelve a habilitar "Finalizar cierre" una vez completado', async () => {
    const medioTarjeta = medioFixture({ id: 2, nombre: 'Tarjeta' })
    let resumenActual = resumenFixture()

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo, medioTarjeta])
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenActual)
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') {
        return Promise.reject(new ErrorApi(409, 'arqueo_incompleto', 'Faltan medios por declarar.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()
    await screen.findByText('Efectivo')
    await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())

    // Entre que se cargó este resumen y el click, el servidor ganó una carrera: apareció
    // actividad en un medio nuevo — el próximo GET /resumen ya lo refleja.
    resumenActual = resumenFixture({
      medios: [
        { idMedioPago: 1, importeEsperado: 640 },
        { idMedioPago: 2, importeEsperado: 300 },
      ],
    })

    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    expect(await screen.findByText('Faltan medios por declarar.')).toBeInTheDocument()
    expect(await screen.findByLabelText('Declarado de Tarjeta')).toBeInTheDocument()
    expect(screen.getByLabelText('Declarado de Efectivo')).toHaveValue(640)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeDisabled())

    await userEvent.type(screen.getByLabelText('Declarado de Tarjeta'), '300')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
  })
})

describe('CierreDeCaja — falla de carga (react-async-state regla 7)', () => {
  it('si el resumen no se puede cargar, se muestra un aviso y "Finalizar cierre" queda visible pero realmente deshabilitado', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo])
      if (ruta === '/caja/turnos/501/resumen') {
        return Promise.reject(new ErrorApi(500, 'error', 'No se pudo cargar el resumen del turno.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()

    expect(await screen.findByText('No se pudo cargar el resumen del turno.')).toBeInTheDocument()
    const boton = screen.getByRole('button', { name: 'Finalizar cierre' })
    expect(boton).toBeInTheDocument()
    expect(boton).toBeDisabled()
  })

  it('si los medios de pago no se pueden cargar, se muestra un aviso y "Finalizar cierre" queda deshabilitado aunque el resumen sí haya cargado', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/catalogos/medios-pago') return Promise.reject(new ErrorApi(500, 'error', 'No se pudieron cargar los medios de pago.'))
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()

    expect(await screen.findByText('No se pudieron cargar los medios de pago.')).toBeInTheDocument()
    await userEvent.type(await screen.findByLabelText('Declarado de Medio #1'), '640')
    await userEvent.click(screen.getByRole('checkbox'))
    expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeDisabled()
  })
})
