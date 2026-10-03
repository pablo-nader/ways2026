import 'fake-indexeddb/auto'
import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CierreDeCaja } from './CierreDeCaja'
import { ErrorApi } from '../api/cliente'
import { crearAlmacenIndexedDb } from '../pos/almacenPos'
import { agregarAOutbox, agregarARechazada } from '../pos/outboxOffline'
import { guardarTurnoConfirmadoLocal } from '../pos/turnoConfirmadoLocal'
import { ROL } from '../api/tipos'
import type { MedioPagoListado, ResumenDeTurno, TurnoConArqueos, TurnoResumen, UsuarioAutenticado } from '../api/tipos'

/** `usuarioActual` es mutable a propósito (reset en `beforeEach`) — los tests del override de
 * rendición lo sobrescriben para probar la copia de rol sin remockear el módulo entero (mismo
 * patrón que `CuentaCorriente.test.tsx`). */
function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 9,
    usuario: 'supervisor',
    mail: 'supervisor@ways.test',
    rolId: ROL.Supervisor,
    rol: 'Supervisor',
    ultimaConexion: null,
    idTenant: 1,
    ...sobrescribir,
  }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

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
    cantidadTickets: 0,
    primerTicket: null,
    ultimoTicket: null,
    ingresosPorArea: [],
    egresos: { porCategoria: [], porArea: [], retiros: 0 },
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
    arqueos: [{ idMedioPago: 1, importeEsperado: 640, importeDeclarado: 635, diferencia: 5, importeEsperadoOriginal: null }],
    fechaRecalculo: null,
    idEmpleadoRecalculo: null,
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

/** Borra la base fake entre tests — sin esto, un outbox encolado por un test filtraría al
 * siguiente (el mismo `fake-indexeddb` vive para todo el archivo). */
function borrarAlmacenOffline(): Promise<void> {
  return new Promise((resolve) => {
    const solicitud = indexedDB.deleteDatabase('ways-pos-offline')
    solicitud.onsuccess = () => resolve()
    solicitud.onerror = () => resolve()
    solicitud.onblocked = () => resolve()
  })
}

beforeEach(async () => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  usuarioActual = usuarioFixture()
  await borrarAlmacenOffline()
})

function renderCierreConSeams(props: { rutaVolver?: string } = {}) {
  return render(<CierreDeCaja {...props} />, {
    wrapper: ({ children }) => <MemoryRouter initialEntries={['/caja/cierre?idTurno=501']}>{children}</MemoryRouter>,
  })
}

async function cerrarElTurno() {
  await screen.findByText('Efectivo')
  await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '635')
  await userEvent.click(screen.getByRole('checkbox'))
  await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
  await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))
  await screen.findByText('Turno #501 cerrado')
}

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
    expect(screen.getByText('$ 5,00')).toBeInTheDocument() // vista previa de diferencia (rule: preview, nunca autoritativa)

    await userEvent.click(screen.getByRole('checkbox'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    await screen.findByText('Turno #501 cerrado')
    const fila = screen.getByText('Efectivo').closest('tr') as HTMLElement
    expect(fila.textContent).toContain('$ 640,00')
    expect(fila.textContent).toContain('$ 635,00')
    expect(fila.textContent).toContain('$ 5,00')

    const llamada = apiPostMock.mock.calls.find((c) => c[0] === '/caja/turnos/501/cierre')
    expect(llamada?.[1]).toEqual({ conteos: [{ idMedioPago: 1, importeDeclarado: 635 }], observaciones: null })
  })

  it('cerrar el turno olvida el turno guardado para vender sin red en ese punto de venta', async () => {
    const turnoGuardado: TurnoResumen = { ...turnoConArqueosFixture(), estado: 'Abierto', idEmpleadoCierre: null, fechaCierre: null }
    guardarTurnoConfirmadoLocal(7, turnoGuardado)
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') return Promise.resolve<TurnoConArqueos>(turnoConArqueosFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()
    await screen.findByText('Efectivo')
    await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '635')
    await userEvent.click(screen.getByRole('checkbox'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
    expect(localStorage.getItem('ways.pos.turnoConfirmado.7')).not.toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    await screen.findByText('Turno #501 cerrado')
    expect(localStorage.getItem('ways.pos.turnoConfirmado.7')).toBeNull()
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

    // Los dos clicks van SINCRÓNICOS dentro del mismo `act`: con `userEvent`/`fireEvent` React
    // alcanza a re-renderizar entre uno y otro y el `disabled` del botón tapa la guarda de
    // reentrancia por `ref` (mutation-proof-tests: el mutante que la borra sobrevivía a este test).
    const boton = screen.getByRole('button', { name: 'Finalizar cierre' })
    await act(async () => {
      boton.click()
      boton.click()
    })

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
    expect(fila.textContent).toContain('$ 640,00')
    expect(fila.textContent).toContain('$ 635,00')
    expect(fila.textContent).toContain('$ 5,00')
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
    expect(screen.getByLabelText('Declarado de Efectivo')).toHaveValue('640,00')
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

describe('CierreDeCaja — seams del POS de escritorio (stage-desktop-pos)', () => {
  it('sin rutaVolver, "Volver a caja" y "Cancelar" apuntan a /caja (default, comportamiento intacto)', async () => {
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/caja/turnos/501/cierre' ? Promise.resolve<TurnoConArqueos>(turnoConArqueosFixture()) : Promise.reject(new Error(ruta)),
    )
    renderCierreConSeams()
    await cerrarElTurno()

    expect(screen.getByRole('link', { name: 'Volver a caja' })).toHaveAttribute('href', '/caja')
  })

  it('con rutaVolver, "Volver a caja" y "Cancelar" apuntan a la ruta indicada', async () => {
    mockearRutasBase()
    renderCierreConSeams({ rutaVolver: '/vender' })
    await screen.findByText('Efectivo')

    expect(screen.getByRole('link', { name: 'Cancelar' })).toHaveAttribute('href', '/vender')
  })

  // Fix judgment-day R2-3: el seam `contextoDeImpresion` (auto-impresión, "Reimprimir",
  // `errorImpresion`) se quitó de esta pantalla — quedó inalcanzable en producción apenas
  // `ShellPos.tsx` dejó de pasarlo (ronda 1, evitar la doble impresión del reporte Z). El shell es
  // ahora el único dueño de la impresión de escritorio (cola FIFO + avisos por trabajo, ver
  // `ShellPos.test.tsx`); esta pantalla no imprime nada, con o sin `alCerrarExitosamente`.
  it('un cierre exitoso nunca imprime nada ni muestra "Reimprimir" — esta pantalla no conoce la impresora', async () => {
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/caja/turnos/501/cierre' ? Promise.resolve<TurnoConArqueos>(turnoConArqueosFixture()) : Promise.reject(new Error(ruta)),
    )
    renderCierreConSeams()
    await cerrarElTurno()

    expect(screen.queryByRole('button', { name: 'Reimprimir' })).not.toBeInTheDocument()
    expect(screen.queryByText(/No se pudo imprimir/)).not.toBeInTheDocument()
  })
})

describe('CierreDeCaja — regla dura del outbox offline (stage-pos-venta-offline-web, Parte D)', () => {
  it('con el outbox NO vacío, "Finalizar cierre" queda deshabilitado aunque todo lo demás esté completo', async () => {
    const almacen = crearAlmacenIndexedDb()
    await agregarAOutbox(almacen, {
      idLocal: 'a',
      numeroPreasignado: 100,
      idPuntoVenta: 7,
      creadoEn: '2026-09-20T09:00:00.000Z',
      solicitud: { idPuntoVenta: 7, codigoTipoComprobante: 'TX', idComprobanteAsociado: null, pagos: [], direccionEntrega: null, observaciones: null },
    })

    mockearRutasBase()
    renderCierre()
    await screen.findByText('Efectivo')

    await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox'))

    // Nunca se habilita, ni esperando: a diferencia de "Cargando…", no hay ningún estado
    // posterior en el que este bloqueo se levante solo — necesita que el outbox drene primero.
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeDisabled()
  })

  it('con el outbox vacío, "Finalizar cierre" se habilita normalmente (comportamiento preexistente intacto)', async () => {
    mockearRutasBase()
    renderCierre()
    await screen.findByText('Efectivo')

    await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox'))

    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
  })

  // judgment-day ronda 1 (CRITICAL): una venta rechazada de forma permanente sale del outbox
  // (para no bloquear el drenado del resto de la cola) pero sigue siendo una venta real con
  // ticket entregado — el cierre tiene que seguir bloqueado igual que con el outbox no vacío.
  it('con una venta que necesita atención (outbox vacío), "Finalizar cierre" queda deshabilitado igual', async () => {
    const almacen = crearAlmacenIndexedDb()
    await agregarARechazada(almacen, {
      idLocal: 'a',
      numeroPreasignado: 100,
      idPuntoVenta: 7,
      creadoEn: '2026-09-20T09:00:00.000Z',
      solicitud: { idPuntoVenta: 7, codigoTipoComprobante: 'TX', idComprobanteAsociado: null, pagos: [], direccionEntrega: null, observaciones: null },
      mensaje: 'La venta 0007-00000100 no se pudo sincronizar: rechazo del servidor.',
    })

    mockearRutasBase()
    renderCierre()
    await screen.findByText('Efectivo')

    await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox'))

    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeDisabled()
  })
})

// judgment-day ronda 1 (WARNING): antes de este fix, `outboxCount`/`cantidadConError` se leían
// UNA vez al montar y nunca más — un cambio de cualquiera de los dos, en cualquier sentido,
// DESPUÉS de montar quedaba invisible para el resto de la vida de esta pantalla.
describe('CierreDeCaja — re-lee el outbox/las rechazadas (nunca queda desactualizado)', () => {
  it('una venta encolada DESPUÉS de montar (otra pestaña, o un drenado que la archivó) bloquea "Finalizar cierre" sin recargar la pantalla', async () => {
    mockearRutasBase()
    renderCierre()
    await screen.findByText('Efectivo')

    await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())

    // Otra pestaña (u otro ciclo de `useSincronizacionOffline`) encola una venta DESPUÉS de que
    // esta pantalla ya montó y ya leyó "outbox vacío" una vez.
    const almacen = crearAlmacenIndexedDb()
    await agregarAOutbox(almacen, {
      idLocal: 'tardia',
      numeroPreasignado: 900,
      idPuntoVenta: 7,
      creadoEn: '2026-09-20T09:05:00.000Z',
      solicitud: { idPuntoVenta: 7, codigoTipoComprobante: 'TX', idComprobanteAsociado: null, pagos: [], direccionEntrega: null, observaciones: null },
    })

    // El foco de la ventana es el backstop inmediato (mismo criterio que el evento `online` del
    // propio hook de sincronización) — nunca hace falta esperar el intervalo completo.
    await act(async () => {
      window.dispatchEvent(new Event('focus'))
    })

    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeDisabled())
  })

  it('una venta que ya no está (drenó/se resolvió en otra pestaña) DESPUÉS de montar deja de bloquear sin recargar la pantalla', async () => {
    const almacen = crearAlmacenIndexedDb()
    await agregarAOutbox(almacen, {
      idLocal: 'a',
      numeroPreasignado: 100,
      idPuntoVenta: 7,
      creadoEn: '2026-09-20T09:00:00.000Z',
      solicitud: { idPuntoVenta: 7, codigoTipoComprobante: 'TX', idComprobanteAsociado: null, pagos: [], direccionEntrega: null, observaciones: null },
    })

    mockearRutasBase()
    renderCierre()
    await screen.findByText('Efectivo')

    await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox'))
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeDisabled()

    // Otra pestaña (u otro ciclo) drena esa venta DESPUÉS de que esta pantalla ya la vio pendiente.
    await borrarAlmacenOffline()

    await act(async () => {
      window.dispatchEvent(new Event('focus'))
    })

    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
  })
})

// Guarda de rendición de cola de dispositivos: el gate local del outbox de arriba solo ve EL
// almacén de ESTA máquina, así que un cierre desde otra pasaba igual. El servidor tiene ahora su
// propia guarda (`409 rendicion_de_dispositivo_pendiente`) y un override supervisado.
describe('CierreDeCaja — rechazo por rendición de dispositivo pendiente', () => {
  const MENSAJE_DEL_SERVIDOR =
    "No se puede cerrar el turno: el dispositivo 'Caja 1' tiene 2 venta(s) sin sincronizar (TX)."
  const NOMBRE_OVERRIDE = /Cerrar igual, sin la rendición del dispositivo/
  const NOMBRE_CONFIRMACION = /Confirmo que estoy cerrando este turno de forma definitiva/

  /** Deja la pantalla lista y dispara un cierre que el servidor rechaza por rendición pendiente. */
  async function intentarCierreYRecibirElRechazo() {
    await screen.findByText('Efectivo')
    await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox', { name: NOMBRE_CONFIRMACION }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))
    expect(await screen.findByText(MENSAJE_DEL_SERVIDOR)).toBeInTheDocument()
  }

  it('muestra el mensaje del servidor (nunca el genérico) y recién ahí revela el override', async () => {
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') {
        return Promise.reject(new ErrorApi(409, 'rendicion_de_dispositivo_pendiente', MENSAJE_DEL_SERVIDOR))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()
    await screen.findByText('Efectivo')
    // Antes del rechazo el override no existe: nunca es un control visible por defecto.
    expect(screen.queryByRole('checkbox', { name: NOMBRE_OVERRIDE })).not.toBeInTheDocument()

    await intentarCierreYRecibirElRechazo()

    expect(screen.queryByText('No se pudo cerrar el turno.')).not.toBeInTheDocument()
    expect(screen.getByRole('checkbox', { name: NOMBRE_OVERRIDE })).toBeInTheDocument()
    expect(screen.getByLabelText('Motivo del cierre forzado (obligatorio)')).toBeInTheDocument()
  })

  it('forzar manda forzarSinRendicion + motivoSinRendicion; el intento anterior no mandó ninguno de los dos', async () => {
    mockearRutasBase()
    let rechazar = true
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') {
        if (rechazar) {
          rechazar = false
          return Promise.reject(new ErrorApi(409, 'rendicion_de_dispositivo_pendiente', MENSAJE_DEL_SERVIDOR))
        }
        return Promise.resolve<TurnoConArqueos>(turnoConArqueosFixture())
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()
    await intentarCierreYRecibirElRechazo()

    await userEvent.click(screen.getByRole('checkbox', { name: NOMBRE_OVERRIDE }))
    await userEvent.type(screen.getByLabelText('Motivo del cierre forzado (obligatorio)'), '  Caja 1 se llevó la tablet  ')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    await screen.findByText('Turno #501 cerrado')

    const cierres = apiPostMock.mock.calls.filter((c) => c[0] === '/caja/turnos/501/cierre')
    expect(cierres).toHaveLength(2)
    // Sin forzar: ninguno de los dos campos viaja (un motivo sin el flag es `400
    // motivo_sin_forzado` del lado del servidor).
    expect(cierres[0][1]).toEqual({ conteos: [{ idMedioPago: 1, importeDeclarado: 640 }], observaciones: null })
    expect(cierres[1][1]).toEqual({
      conteos: [{ idMedioPago: 1, importeDeclarado: 640 }],
      observaciones: null,
      forzarSinRendicion: true,
      motivoSinRendicion: 'Caja 1 se llevó la tablet',
    })
  })

  it('con el override marcado y el motivo vacío o en blanco, "Finalizar cierre" queda deshabilitado', async () => {
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') {
        return Promise.reject(new ErrorApi(409, 'rendicion_de_dispositivo_pendiente', MENSAJE_DEL_SERVIDOR))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()
    await intentarCierreYRecibirElRechazo()

    await userEvent.click(screen.getByRole('checkbox', { name: NOMBRE_OVERRIDE }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeDisabled())

    await userEvent.type(screen.getByLabelText('Motivo del cierre forzado (obligatorio)'), '   ')
    expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeDisabled()

    await userEvent.type(screen.getByLabelText('Motivo del cierre forzado (obligatorio)'), 'motivo real')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
  })

  /** react-async-state regla 9/11: un cierre es irreversible, así que un doble submit es el peor
   * defecto posible — y un forzado escribe además su fila de auditoría por cada POST. */
  it('doble click en el cierre forzado dispara exactamente un POST', async () => {
    mockearRutasBase()
    let rechazar = true
    let resolverCierre: (t: TurnoConArqueos) => void = () => {}
    const cierrePendiente = new Promise<TurnoConArqueos>((resolve) => {
      resolverCierre = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') {
        if (rechazar) {
          rechazar = false
          return Promise.reject(new ErrorApi(409, 'rendicion_de_dispositivo_pendiente', MENSAJE_DEL_SERVIDOR))
        }
        return cierrePendiente
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()
    await intentarCierreYRecibirElRechazo()

    await userEvent.click(screen.getByRole('checkbox', { name: NOMBRE_OVERRIDE }))
    await userEvent.type(screen.getByLabelText('Motivo del cierre forzado (obligatorio)'), 'motivo real')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())

    // Dos clicks SINCRÓNICOS dentro del mismo `act`: sin esto React alcanza a re-renderizar entre
    // uno y otro (`fireEvent` flushea por su cuenta) y el `disabled` del botón tapa la guarda de
    // reentrancia por `ref`, que es la única que sobrevive a un doble click del mismo tick.
    const boton = screen.getByRole('button', { name: 'Finalizar cierre' })
    await act(async () => {
      boton.click()
      boton.click()
    })

    // Dos POST en total: el rechazado de antes + exactamente UNO forzado.
    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/caja/turnos/501/cierre')).toHaveLength(2)
    expect(screen.getByRole('button', { name: 'Cerrando…' })).toBeDisabled()
    expect(screen.getByRole('checkbox', { name: NOMBRE_OVERRIDE })).toBeDisabled()
    expect(screen.getByLabelText('Motivo del cierre forzado (obligatorio)')).toBeDisabled()

    await act(async () => {
      resolverCierre(turnoConArqueosFixture())
      await Promise.resolve()
    })
  })

  it('un 403 al forzar dice que hace falta un supervisor y no cierra el turno', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor', usuario: 'jperez' })
    mockearRutasBase()
    let rechazarPorRendicion = true
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') {
        if (rechazarPorRendicion) {
          rechazarPorRendicion = false
          return Promise.reject(new ErrorApi(409, 'rendicion_de_dispositivo_pendiente', MENSAJE_DEL_SERVIDOR))
        }
        return Promise.reject(new ErrorApi(403, 'prohibido', 'Forzar el cierre sin rendición requiere un supervisor.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()
    await intentarCierreYRecibirElRechazo()

    // El rol del cliente no esconde el control: el 403 tiene que ser alcanzable.
    expect(screen.getByRole('checkbox', { name: NOMBRE_OVERRIDE })).toBeEnabled()
    await userEvent.click(screen.getByRole('checkbox', { name: NOMBRE_OVERRIDE }))
    await userEvent.type(screen.getByLabelText('Motivo del cierre forzado (obligatorio)'), 'motivo real')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    expect(
      await screen.findByText(
        'Solo un supervisor o un administrador puede cerrar el turno sin la rendición del dispositivo.',
      ),
    ).toBeInTheDocument()
    expect(screen.queryByText('Turno #501 cerrado')).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el conjunto `e.codigo === 'prohibido'` de la rama de rol. Un 403 con OTRO
   * código mientras se fuerza (sesión degradada, alcance de tenant, `OperacionDePos`) tiene otra
   * causa: se muestra el mensaje real del servidor, nunca el de "hace falta un supervisor", que
   * ocultaría el motivo verdadero.
   */
  it('un 403 con otro código mientras se fuerza muestra el mensaje real del servidor', async () => {
    mockearRutasBase()
    let rechazarPorRendicion = true
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') {
        if (rechazarPorRendicion) {
          rechazarPorRendicion = false
          return Promise.reject(new ErrorApi(409, 'rendicion_de_dispositivo_pendiente', MENSAJE_DEL_SERVIDOR))
        }
        return Promise.reject(new ErrorApi(403, 'punto_venta_fuera_de_alcance', 'El punto de venta no está en tu alcance.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()
    await intentarCierreYRecibirElRechazo()

    await userEvent.click(screen.getByRole('checkbox', { name: NOMBRE_OVERRIDE }))
    await userEvent.type(screen.getByLabelText('Motivo del cierre forzado (obligatorio)'), 'motivo real')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    expect(await screen.findByText('El punto de venta no está en tu alcance.')).toBeInTheDocument()
    expect(
      screen.queryByText('Solo un supervisor o un administrador puede cerrar el turno sin la rendición del dispositivo.'),
    ).not.toBeInTheDocument()
    expect(screen.queryByText('Turno #501 cerrado')).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el conjunto `forzarSinRendicion` de la misma rama. Un 403 `prohibido` en
   * un cierre que NO pidió forzar nada no tiene nada que ver con el rol del override: se muestra el
   * mensaje real del servidor y el override sigue sin revelarse.
   */
  it('un 403 prohibido SIN forzar muestra el mensaje real del servidor y no revela el override', async () => {
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') {
        return Promise.reject(new ErrorApi(403, 'prohibido', 'Tu sesión ya no puede operar esta caja.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderCierre()
    await screen.findByText('Efectivo')
    await userEvent.type(screen.getByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox', { name: NOMBRE_CONFIRMACION }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    expect(await screen.findByText('Tu sesión ya no puede operar esta caja.')).toBeInTheDocument()
    expect(
      screen.queryByText('Solo un supervisor o un administrador puede cerrar el turno sin la rendición del dispositivo.'),
    ).not.toBeInTheDocument()
    // Este rechazo no es la guarda de rendición: el override sigue sin existir.
    expect(screen.queryByRole('checkbox', { name: NOMBRE_OVERRIDE })).not.toBeInTheDocument()
  })

  it('con rol vendedor avisa que el servidor va a rechazar el forzado; con rol supervisor no', async () => {
    const avisoDeRol =
      'Con tu rol el servidor va a rechazar el cierre forzado: pedile a un supervisor o a un administrador que lo haga.'
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') {
        return Promise.reject(new ErrorApi(409, 'rendicion_de_dispositivo_pendiente', MENSAJE_DEL_SERVIDOR))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor', usuario: 'jperez' })
    const vista = renderCierre()
    await intentarCierreYRecibirElRechazo()
    expect(screen.getByText(avisoDeRol)).toBeInTheDocument()
    vista.unmount()

    usuarioActual = usuarioFixture()
    renderCierre()
    await intentarCierreYRecibirElRechazo()
    expect(screen.queryByText(avisoDeRol)).not.toBeInTheDocument()
  })
})

describe('CierreDeCaja — arqueo recalculado', () => {
  async function cerrarConRespuesta(respuesta: TurnoConArqueos) {
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') return Promise.resolve<TurnoConArqueos>(respuesta)
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    renderCierre()
    await cerrarElTurno()
  }

  it('un turno recalculado muestra la insignia y el esperado original de la línea modificada', async () => {
    await cerrarConRespuesta(
      turnoConArqueosFixture({
        fechaRecalculo: '2026-09-20T15:30:00Z',
        idEmpleadoRecalculo: 4,
        arqueos: [{ idMedioPago: 1, importeEsperado: 640, importeDeclarado: 635, diferencia: 5, importeEsperadoOriginal: 700 }],
      }),
    )

    expect(screen.getByText('Recalculado')).toBeInTheDocument()
    const fila = screen.getByText('Efectivo').closest('tr') as HTMLElement
    expect(within(fila).getByText('Esperado original: $ 700,00')).toBeInTheDocument()
  })

  it('un turno sin recálculo no muestra insignia ni esperado original', async () => {
    await cerrarConRespuesta(turnoConArqueosFixture())

    expect(screen.queryByText('Recalculado')).not.toBeInTheDocument()
    expect(screen.queryByText(/Esperado original/)).not.toBeInTheDocument()
  })
})
