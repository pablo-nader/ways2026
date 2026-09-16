import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ShellPos } from './ShellPos'
import { ROL } from '../api/tipos'
import type {
  ClienteListado,
  MedioPagoListado,
  PaginaDe,
  ParametroResuelto,
  PuntoVentaListado,
  ResumenDeTurno,
  TurnoConArqueos,
  TurnoResumen,
  UsuarioAutenticado,
} from '../api/tipos'
import type { DispositivoActual } from '../api/dispositivos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: vi.fn(),
    delete: vi.fn(),
  },
  alPerderLaSesion: () => () => {},
  ErrorApi: class ErrorApiMock extends Error {
    estado: number
    codigo: string
    constructor(estado: number, codigo: string, mensaje: string) {
      super(mensaje)
      this.estado = estado
      this.codigo = codigo
    }
    get esNoAutenticado() {
      return this.estado === 401
    }
  },
}))

const abrirConfiguracionMock = vi.fn()
const imprimirMock = vi.fn()
let escritorioMock = false
vi.mock('../impresion/impresora', () => ({
  enEscritorio: () => escritorioMock,
  abrirConfiguracion: (...args: unknown[]) => abrirConfiguracionMock(...args),
  imprimir: (...args: unknown[]) => imprimirMock(...args),
}))

const DISPOSITIVO: DispositivoActual = {
  id: 1,
  nombre: 'Caja 1',
  idPuntoVenta: 7,
  puntoVenta: { numero: 1, nombre: 'Local Centro' },
  empresa: { nombre: 'Almacén Demo' },
}

function usuarioFixture(): UsuarioAutenticado {
  return {
    id: 4,
    usuario: 'jperez',
    mail: 'jperez@ways.test',
    rolId: ROL.Vendedor,
    rol: 'Vendedor',
    ultimaConexion: null,
    idTenant: 1,
  }
}

function puntoVentaFixture(): PuntoVentaListado {
  return {
    id: 7,
    idTenant: 1,
    idEmpresa: 3,
    nombre: 'Local Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: 'Tenant Demo',
    razonSocialEmpresa: 'Empresa Demo',
  }
}

const consumidorFinal: ClienteListado = {
  id: 1,
  numero: 1,
  nombre: 'Consumidor Final',
  apellido: null,
  razonSocial: null,
  tipoDocumento: null,
  numeroDocumento: null,
  idCondicionFiscal: 1,
  nacimiento: null,
  domicilio: null,
  telefono: null,
  celular: null,
  email: null,
  observaciones: null,
  idListaPrecio: 1,
  limiteCredito: 0,
  creditoIlimitado: true,
  saldo: 0,
  activo: true,
  idEmpresa: null,
  esConsumidorFinal: true,
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

function turnoAbiertoFixture(sobrescribir: Partial<TurnoResumen> = {}): TurnoResumen {
  return {
    id: 501,
    idPuntoVenta: 7,
    idEmpleadoApertura: 4,
    idEmpleadoCierre: null,
    fechaApertura: '2026-09-16T12:00:00Z',
    fechaCierre: null,
    fondoInicial: 500,
    estado: 'Abierto',
    observaciones: null,
    ...sobrescribir,
  }
}

function resumenFixture(): ResumenDeTurno {
  return {
    idTurnoCaja: 501,
    idMedioAncla: 1,
    medios: [{ idMedioPago: 1, importeEsperado: 640 }],
    cantidadTickets: 0,
    primerTicket: null,
    ultimoTicket: null,
    ingresosPorArea: [],
    egresos: { porCategoria: [], porArea: [], retiros: 0 },
  }
}

function turnoConArqueosFixture(): TurnoConArqueos {
  return {
    ...turnoAbiertoFixture({ estado: 'Cerrado', fechaCierre: '2026-09-16T20:00:00Z', idEmpleadoCierre: 4 }),
    arqueos: [{ idMedioPago: 1, importeEsperado: 640, importeDeclarado: 640, diferencia: 0 }],
  }
}

/** Rutas que consume `Pos.tsx` una vez dentro del shell — se agregan al mock general para que la
 * pantalla de venta no se quede en un aviso de error, sin que este archivo tenga que probar de
 * nuevo el checkout (ya cubierto por `Pos.test.tsx`). Cada test de "Cerrar caja" suma encima las
 * rutas de caja que le hacen falta. */
function mockearRutasDePos(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    const propia = sobrescribir?.(ruta)
    if (propia) return propia

    if (ruta === '/clientes') {
      const pagina: PaginaDe<ClienteListado> = { items: [consumidorFinal], total: 1, pagina: 1, tamanio: 25 }
      return Promise.resolve(pagina)
    }
    if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo])
    if (ruta.startsWith('/parametros/tolerancia_pago')) return Promise.resolve<ParametroResuelto>({ clave: 'tolerancia_pago', valor: '10' })
    if (ruta.startsWith('/parametros/vuelto_maximo')) return Promise.resolve<ParametroResuelto>({ clave: 'vuelto_maximo', valor: '20' })
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderShell() {
  return render(
    <MemoryRouter initialEntries={['/vender']}>
      <ShellPos dispositivo={DISPOSITIVO} usuario={usuarioFixture()} puntoVenta={puntoVentaFixture()} alCerrarSesion={vi.fn()} />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPostMock.mockResolvedValue(undefined)
  abrirConfiguracionMock.mockReset()
  imprimirMock.mockReset()
  imprimirMock.mockResolvedValue({ ok: true })
  escritorioMock = false
  mockearRutasDePos()
})

describe('ShellPos', () => {
  it('el header muestra la empresa, el PV y el cajero del dispositivo/sesión', async () => {
    renderShell()
    expect(await screen.findByText('Almacén Demo')).toBeInTheDocument()
    expect(screen.getByText(/PV 1 — Local Centro · jperez/)).toBeInTheDocument()
  })

  it('sin escritorio (enEscritorio false), no aparece "Configuración"', async () => {
    renderShell()
    await screen.findByText('Almacén Demo')
    expect(screen.queryByRole('button', { name: 'Configuración' })).not.toBeInTheDocument()
  })

  it('con escritorio, "Configuración" invoca abrirConfiguracion', async () => {
    escritorioMock = true
    renderShell()
    await userEvent.click(await screen.findByRole('button', { name: 'Configuración' }))
    expect(abrirConfiguracionMock).toHaveBeenCalledTimes(1)
  })

  it('"Cerrar sesión" llama a POST /auth/logout y avisa alCerrarSesion', async () => {
    const alCerrarSesion = vi.fn()
    render(
      <MemoryRouter initialEntries={['/vender']}>
        <ShellPos dispositivo={DISPOSITIVO} usuario={usuarioFixture()} puntoVenta={puntoVentaFixture()} alCerrarSesion={alCerrarSesion} />
      </MemoryRouter>,
    )

    await userEvent.click(await screen.findByRole('button', { name: 'Cerrar sesión' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/auth/logout'))
    await waitFor(() => expect(alCerrarSesion).toHaveBeenCalledTimes(1))
  })
})

describe('ShellPos — "Cerrar caja" resuelve el turno abierto antes de navegar (stage-desktop-pos)', () => {
  it('con un turno abierto, navega a CierreDeCaja con su idTurno (nunca sin ?idTurno=)', async () => {
    mockearRutasDePos((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      return undefined
    })
    renderShell()

    await userEvent.click(await screen.findByRole('button', { name: 'Cerrar caja' }))

    expect(await screen.findByText('Cierre de turno #501')).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenCalledWith('/caja/turnos/abierto?idPuntoVenta=7')
  })

  it('sin turno abierto (null), muestra el aviso en el shell y no navega', async () => {
    mockearRutasDePos((ruta) => (ruta === '/caja/turnos/abierto?idPuntoVenta=7' ? Promise.resolve(null) : undefined))
    renderShell()

    await userEvent.click(await screen.findByRole('button', { name: 'Cerrar caja' }))

    expect(await screen.findByText('No hay un turno abierto en este punto de venta.')).toBeInTheDocument()
    expect(screen.queryByText(/Cierre de turno/)).not.toBeInTheDocument()
    // Sigue en /vender: el carrito del POS sigue montado.
    expect(screen.getByText('Escaneá o tipeá un código para empezar la venta.')).toBeInTheDocument()
  })

  it('si falla la consulta del turno abierto, muestra el aviso de error', async () => {
    mockearRutasDePos((ruta) =>
      ruta === '/caja/turnos/abierto?idPuntoVenta=7'
        ? Promise.reject(new Error('network error'))
        : undefined,
    )
    renderShell()

    await userEvent.click(await screen.findByRole('button', { name: 'Cerrar caja' }))

    expect(await screen.findByText('No se pudo consultar el turno abierto.')).toBeInTheDocument()
  })

  it('doble click en "Cerrar caja" dispara una única consulta del turno abierto (react-async-state regla 9)', async () => {
    let resolverAbierto: (turno: TurnoResumen | null) => void = () => {}
    const pendiente = new Promise<TurnoResumen | null>((resolve) => {
      resolverAbierto = resolve
    })
    mockearRutasDePos((ruta) => (ruta === '/caja/turnos/abierto?idPuntoVenta=7' ? pendiente : undefined))
    renderShell()

    const boton = await screen.findByRole('button', { name: 'Cerrar caja' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    resolverAbierto(null)
    await screen.findByText('No hay un turno abierto en este punto de venta.')

    expect(apiGetMock.mock.calls.filter((c) => c[0] === '/caja/turnos/abierto?idPuntoVenta=7')).toHaveLength(1)
  })

  it('un cierre exitoso navega a la Caja Z (auto-impresión incluida) e imprime exactamente una vez', async () => {
    const conArqueos = turnoConArqueosFixture()
    mockearRutasDePos((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      if (ruta === '/caja/turnos/501/detalle') {
        return Promise.resolve({
          resumen: resumenFixture(),
          tickets: [],
          gastos: [],
        })
      }
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/caja/turnos/501/cierre' ? Promise.resolve<TurnoConArqueos>(conArqueos) : Promise.resolve(undefined),
    )
    renderShell()

    await userEvent.click(await screen.findByRole('button', { name: 'Cerrar caja' }))
    await screen.findByText('Cierre de turno #501')

    await userEvent.type(await screen.findByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    expect(await screen.findByText('Caja Z — turno #501')).toBeInTheDocument()
    await waitFor(() => expect(imprimirMock).toHaveBeenCalledTimes(1))
  })
})
