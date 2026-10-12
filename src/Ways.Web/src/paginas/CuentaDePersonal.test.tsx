import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CuentaDePersonal } from './CuentaDePersonal'
import { fechaDeHoyParaPago } from '../api/compras'
import { ROL } from '../api/tipos'
import { ErrorApi } from '../api/cliente'
import type {
  ClienteListado,
  EmpresaListado,
  MedioPagoListado,
  MovimientoDePersonal,
  PaginaDeCuentaDePersonal,
  PersonalListado,
  PuntoVentaListado,
} from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
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

let rolActual: number = ROL.Admin

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({
    usuario: { id: 1, usuario: 'yo', mail: 'yo@ways.test', rolId: rolActual, rol: 'Rol', ultimaConexion: null, idTenant: 1 },
    cargando: false,
    iniciarSesion: vi.fn(),
    cerrarSesion: vi.fn(),
  }),
}))

let puntoVentaActual: PuntoVentaListado | null = null

vi.mock('../puntoVenta/usePuntoVenta', () => ({
  usePuntoVenta: () => ({ puntosVenta: [], puntoVenta: puntoVentaActual, elegir: vi.fn(), recargar: vi.fn() }),
}))

const HOY = fechaDeHoyParaPago()

function SondaDeRuta() {
  const { pathname } = useLocation()

  return <span data-testid="ruta">{pathname}</span>
}

function renderCuenta(ruta = '/personal/1/cuenta') {
  return render(
    <MemoryRouter initialEntries={[ruta]}>
      <Routes>
        <Route path="/personal/:id/cuenta" element={<CuentaDePersonal />} />
        <Route path="*" element={<SondaDeRuta />} />
      </Routes>
    </MemoryRouter>,
  )
}

function diferido<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((res, rej) => {
    resolver = res
    rechazar = rej
  })

  return { promesa, resolver, rechazar }
}

function empleadoBase(sobrescribir: Partial<PersonalListado> = {}): PersonalListado {
  return {
    id: 1,
    nombre: 'Ana Gómez',
    documento: null,
    telefono: null,
    observaciones: null,
    valorHora: 3500,
    saldo: 12000,
    idEmpresa: null,
    idUsuario: null,
    nombreUsuario: null,
    idCliente: 21,
    nombreCliente: 'Cliente Ana',
    ...sobrescribir,
  }
}

function movimiento(sobrescribir: Partial<MovimientoDePersonal>): MovimientoDePersonal {
  return {
    idMovimiento: 1,
    fecha: '2026-10-10',
    tipo: 'Horas',
    horas: null,
    valorHora: null,
    importe: 0,
    saldoResultante: 0,
    detalle: null,
    creadoEn: '2026-10-10T12:00:00Z',
    desglose: null,
    ...sobrescribir,
  }
}

const MOV_HORAS = movimiento({
  idMovimiento: 3,
  tipo: 'Horas',
  horas: 8,
  valorHora: 3500,
  importe: 28000,
  saldoResultante: 12000,
  detalle: 'Turno mañana',
})
const MOV_PAGO = movimiento({
  idMovimiento: 2,
  fecha: '2026-10-09',
  tipo: 'Pago',
  importe: -6500,
  saldoResultante: -16000,
  desglose: {
    descuentoDeCuentaCorriente: 500,
    lineas: [
      { idGasto: 70, idMedioPago: 4, nombreMedioPago: 'Efectivo', importe: 6000, origenFondos: 'CajaTurno', referencia: null },
      { idGasto: 71, idMedioPago: 5, nombreMedioPago: null, importe: 0.5, origenFondos: 'Tesoreria', referencia: 'op-9' },
    ],
  },
})
const MOV_AJUSTE = movimiento({
  idMovimiento: 1,
  fecha: '2026-10-08',
  tipo: 'Ajuste',
  importe: -250,
  saldoResultante: -9500,
  detalle: 'Corrección de una carga',
})

function paginaDeCuenta(sobrescribir: Partial<PaginaDeCuentaDePersonal> = {}): PaginaDeCuentaDePersonal {
  return {
    encabezado: { idPersonal: 1, nombre: 'Ana Gómez', saldo: 12000 },
    items: [MOV_HORAS, MOV_PAGO, MOV_AJUSTE],
    total: 3,
    pagina: 1,
    tamanio: 25,
    historico: false,
    desde: '2026-09-12',
    hasta: null,
    ...sobrescribir,
  }
}

function cliente(saldo: number): ClienteListado {
  return { id: 21, nombre: 'Cliente Ana', saldo, activo: true, esConsumidorFinal: false } as ClienteListado
}

const MEDIOS = [
  { id: 4, nombre: 'Efectivo', activo: true, idEmpresa: null, comportamiento: 'Efectivo' },
  { id: 5, nombre: 'Transferencia', activo: true, idEmpresa: null, comportamiento: 'Electronico' },
  { id: 6, nombre: 'Cuenta corriente', activo: true, idEmpresa: null, comportamiento: 'CuentaCorriente' },
] as MedioPagoListado[]

const EMPRESAS = [
  { id: 2, razonSocial: 'Ways SA', nombreFantasia: null },
  { id: 3, razonSocial: 'Otra SRL', nombreFantasia: 'Otra' },
] as EmpresaListado[]

const PUNTO_DE_VENTA = { id: 9, idEmpresa: 2, nombre: 'Sucursal Centro' } as PuntoVentaListado

type Rutas = {
  empleado?: () => Promise<PersonalListado>
  cuenta?: (ruta: string) => Promise<PaginaDeCuentaDePersonal>
  cliente?: () => Promise<ClienteListado>
  medios?: () => Promise<MedioPagoListado[]>
  empresas?: () => Promise<EmpresaListado[]>
}

function mockearRutas(rutas: Rutas = {}) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/personal/1') return (rutas.empleado ?? (() => Promise.resolve(empleadoBase())))()
    if (ruta.startsWith('/personal/1/cuenta')) return (rutas.cuenta ?? (() => Promise.resolve(paginaDeCuenta())))(ruta)
    if (ruta === '/clientes/21') return (rutas.cliente ?? (() => Promise.resolve(cliente(750))))()
    if (ruta.startsWith('/catalogos/medios-pago')) return (rutas.medios ?? (() => Promise.resolve(MEDIOS)))()
    if (ruta === '/empresas') return (rutas.empresas ?? (() => Promise.resolve(EMPRESAS)))()
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

const lecturas = (prefijo: string) => apiGetMock.mock.calls.filter((c) => (c[0] as string).startsWith(prefijo))
const lecturasDeCuenta = () => lecturas('/personal/1/cuenta')

type Evento = ReturnType<typeof userEvent.setup>

/** Espera al DATO (el nombre del empleado en el título y el saldo), no a un elemento que ya estaba. */
async function esperarCuenta() {
  await screen.findByText('Turno mañana')
}

/** Abre la modal esperando a que el botón se habilite: lo habilita la llegada de los catálogos. */
async function abrir(evento: Evento, boton: string) {
  const control = screen.getByRole('button', { name: boton })
  await waitFor(() => expect(control).toBeEnabled())
  await evento.click(control)
  return screen.findByRole('dialog')
}

beforeEach(() => {
  rolActual = ROL.Admin
  puntoVentaActual = PUNTO_DE_VENTA
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  mockearRutas()
})

describe('CuentaDePersonal — lectura', () => {
  it('muestra el empleado, su saldo con la leyenda, el valor hora, el cliente con su deuda y los movimientos', async () => {
    renderCuenta()
    await esperarCuenta()

    expect(screen.getByRole('heading', { name: 'Cuenta de Ana Gómez' })).toBeInTheDocument()
    expect(within(screen.getByText('Saldo').parentElement as HTMLElement).getByText('$ 12.000,00')).toBeInTheDocument()
    expect(
      screen.getByText('Saldo positivo: la empresa le debe al empleado. Saldo negativo: adelanto ya pagado al empleado.'),
    ).toBeInTheDocument()
    expect(within(screen.getByText('Valor hora vigente').parentElement as HTMLElement).getByText('$ 3.500,00')).toBeInTheDocument()
    expect(screen.getByText('Cliente Ana')).toBeInTheDocument()
    expect(await screen.findByText('Deuda: $ 750,00')).toBeInTheDocument()

    const horas = screen.getByText('Turno mañana').closest('tr') as HTMLElement
    expect(within(horas).getByText('10/10/2026')).toBeInTheDocument()
    expect(within(horas).getByText('Horas trabajadas')).toBeInTheDocument()
    expect(within(horas).getByText('8,00')).toBeInTheDocument()
    expect(within(horas).getByText('$ 3.500,00')).toBeInTheDocument()
    expect(within(horas).getByText('+$ 28.000,00')).toBeInTheDocument()

    const ajuste = screen.getByText('Corrección de una carga').closest('tr') as HTMLElement
    expect(within(ajuste).getByText('Ajuste')).toBeInTheDocument()
    expect(within(ajuste).getByText('-$ 250,00')).toBeInTheDocument()
    expect(within(ajuste).getByText('-$ 9.500,00 (adelanto ya pagado)')).toBeInTheDocument()
  })

  it('un pago muestra su desglose: el descuento y cada línea con su medio, origen y referencia', async () => {
    renderCuenta()
    await esperarCuenta()

    const pago = screen.getByText('Descuento de cuenta corriente: $ 500,00').closest('tr') as HTMLElement
    expect(within(pago).getByText('-$ 6.500,00')).toBeInTheDocument()
    expect(within(pago).getByText('Efectivo: $ 6.000,00 (Cajón del turno)')).toBeInTheDocument()
    expect(
      within(pago).getByText('Medio de pago dado de baja: $ 0,50 (Tesorería), ref. op-9'),
    ).toBeInTheDocument()
  })

  it('un empleado sin cliente vinculado lo dice y no pide ninguna deuda', async () => {
    mockearRutas({ empleado: () => Promise.resolve(empleadoBase({ idCliente: null, nombreCliente: null })) })
    renderCuenta()
    await esperarCuenta()

    expect(await screen.findByText('sin vincular')).toBeInTheDocument()
    expect(lecturas('/clientes')).toHaveLength(0)
  })

  it('un cliente vinculado que se dio de baja se trata como sin vincular y no se pide su deuda', async () => {
    mockearRutas({ empleado: () => Promise.resolve(empleadoBase({ idCliente: 21, nombreCliente: null })) })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()

    expect(await screen.findByText('sin vincular')).toBeInTheDocument()
    const dialogo = await abrir(evento, 'Registrar pago')
    expect(within(dialogo).queryByLabelText(/Descuento de la cuenta corriente/)).not.toBeInTheDocument()
    expect(lecturas('/clientes')).toHaveLength(0)
  })

  it('un cliente con saldo a favor se muestra como tal, no como una deuda negativa', async () => {
    mockearRutas({ cliente: () => Promise.resolve(cliente(-20)) })
    renderCuenta()

    expect(await screen.findByText('Saldo a favor: $ 20,00')).toBeInTheDocument()
  })

  it('si no se puede leer la deuda del cliente lo dice y el resto de la cuenta sigue', async () => {
    mockearRutas({ cliente: () => Promise.reject(new ErrorApi(500, 'error', 'Se cayó.')) })
    renderCuenta()
    await esperarCuenta()

    expect(await screen.findByText('No se pudo leer su deuda.')).toBeInTheDocument()
  })

  it('un empleado inexistente (404) muestra el estado de no encontrado con la vuelta al personal', async () => {
    mockearRutas({
      empleado: () => Promise.reject(new ErrorApi(404, 'no_encontrado', 'No existe el personal 1.')),
      cuenta: () => Promise.reject(new ErrorApi(404, 'no_encontrado', 'No existe el personal 1.')),
    })
    const evento = userEvent.setup()
    renderCuenta()

    expect(await screen.findByText('No se encontró al empleado. Puede haberse dado de baja.')).toBeInTheDocument()
    await evento.click(screen.getByRole('link', { name: 'Volver al personal' }))
    expect(screen.getByTestId('ruta')).toHaveTextContent('/personal')
  })

  /** Cláusula bajo prueba: la generación de `cargarDeuda`. */
  it('la lectura inicial de la deuda que llega tarde no pisa la deuda refrescada tras una escritura', async () => {
    const inicial = diferido<ClienteListado>()
    let lecturasDeDeuda = 0
    mockearRutas({ cliente: () => (++lecturasDeDeuda === 1 ? inicial.promesa : Promise.resolve(cliente(800))) })
    apiPostMock.mockResolvedValue(MOV_HORAS)
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Cargar horas')
    await evento.type(within(dialogo).getByLabelText('Horas'), '8')
    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))
    expect(await screen.findByText('Deuda: $ 800,00')).toBeInTheDocument()

    await act(async () => {
      inicial.resolver(cliente(750))
      await inicial.promesa
    })

    expect(screen.getByText('Deuda: $ 800,00')).toBeInTheDocument()
    expect(screen.queryByText('Deuda: $ 750,00')).not.toBeInTheDocument()
  })

  it('un 404 solo del estado de cuenta también lleva al no encontrado', async () => {
    mockearRutas({ cuenta: () => Promise.reject(new ErrorApi(404, 'no_encontrado', 'No existe el personal 1.')) })
    renderCuenta()

    expect(await screen.findByText('No se encontró al empleado. Puede haberse dado de baja.')).toBeInTheDocument()
  })

  it('un id que no es un número positivo no pide nada y muestra el no encontrado', () => {
    renderCuenta('/personal/abc/cuenta')

    expect(screen.getByText('No se encontró al empleado. Puede haberse dado de baja.')).toBeInTheDocument()
    expect(apiGetMock).not.toHaveBeenCalled()
  })

  it('un fallo al leer al empleado ofrece reintentar y, al lograrlo, completa el encabezado', async () => {
    let intentos = 0
    mockearRutas({
      empleado: () =>
        ++intentos === 1 ? Promise.reject(new ErrorApi(500, 'error', 'Se cayó el empleado.')) : Promise.resolve(empleadoBase()),
    })
    const evento = userEvent.setup()
    renderCuenta()

    expect(await screen.findByText('Se cayó el empleado.')).toBeInTheDocument()
    await evento.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(await screen.findByText('Cliente Ana')).toBeInTheDocument()
    expect(screen.queryByText('Se cayó el empleado.')).not.toBeInTheDocument()
  })

  it('un fallo al leer el estado de cuenta ofrece reintentar', async () => {
    let intentos = 0
    mockearRutas({
      cuenta: () => (++intentos === 1 ? Promise.reject(new ErrorApi(500, 'error', 'Se cayó la cuenta.')) : Promise.resolve(paginaDeCuenta())),
    })
    const evento = userEvent.setup()
    renderCuenta()

    expect(await screen.findByText('Se cayó la cuenta.')).toBeInTheDocument()
    await evento.click(screen.getByRole('button', { name: 'Reintentar' }))

    await esperarCuenta()
    expect(screen.queryByText('Se cayó la cuenta.')).not.toBeInTheDocument()
  })
})

describe('CuentaDePersonal — filtros y paginación', () => {
  it('arranca con el rango de un mes sin tope superior', async () => {
    renderCuenta()
    await esperarCuenta()

    const ruta = lecturasDeCuenta()[0][0] as string
    expect(ruta).toMatch(/^\/personal\/1\/cuenta\?desde=\d{4}-\d{2}-\d{2}&pagina=1&tamanio=25$/)
  })

  it('"Ver histórico completo" pide el histórico sin rango y deshabilita las fechas', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()

    await evento.click(screen.getByLabelText('Ver histórico completo'))

    await waitFor(() => expect(lecturasDeCuenta().at(-1)?.[0]).toBe('/personal/1/cuenta?historico=true&pagina=1&tamanio=25'))
    expect(screen.getByLabelText('Desde')).toBeDisabled()
    expect(screen.getByLabelText('Hasta')).toBeDisabled()
  })

  it('"Siguiente" pide la página dos y "Anterior" vuelve a la uno', async () => {
    mockearRutas({
      cuenta: (ruta) => Promise.resolve(paginaDeCuenta({ total: 60, pagina: ruta.includes('pagina=2') ? 2 : 1 })),
    })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    expect(screen.getByText('Página 1 de 3 — 60 movimiento(s)')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()

    await evento.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText('Página 2 de 3 — 60 movimiento(s)')
    expect(lecturasDeCuenta().at(-1)?.[0]).toContain('pagina=2')

    await evento.click(screen.getByRole('button', { name: 'Anterior' }))
    await screen.findByText('Página 1 de 3 — 60 movimiento(s)')
  })

  /** Cláusula bajo prueba: la generación de `cargarCuenta`. */
  it('una respuesta vieja que llega después de la nueva no pisa la pantalla', async () => {
    const vieja = diferido<PaginaDeCuentaDePersonal>()
    const nueva = diferido<PaginaDeCuentaDePersonal>()
    let lecturasHechas = 0
    mockearRutas({
      cuenta: () => (++lecturasHechas === 1 ? Promise.resolve(paginaDeCuenta()) : lecturasHechas === 2 ? vieja.promesa : nueva.promesa),
    })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()

    await evento.click(screen.getByLabelText('Ver histórico completo'))
    await evento.click(screen.getByLabelText('Ver histórico completo'))
    await act(async () => {
      nueva.resolver(paginaDeCuenta({ items: [movimiento({ idMovimiento: 9, detalle: 'La nueva' })], total: 1 }))
      await nueva.promesa
    })
    expect(await screen.findByText('La nueva')).toBeInTheDocument()

    await act(async () => {
      vieja.resolver(paginaDeCuenta({ items: [movimiento({ idMovimiento: 8, detalle: 'La vieja' })], total: 1 }))
      await vieja.promesa
    })

    expect(screen.getByText('La nueva')).toBeInTheDocument()
    expect(screen.queryByText('La vieja')).not.toBeInTheDocument()
  })
})

describe('CuentaDePersonal — según el rol', () => {
  it('el Admin pide las empresas y ofrece la tesorería como origen', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Registrar pago')
    await evento.click(within(dialogo).getByRole('button', { name: 'Agregar línea' }))

    const origen = within(dialogo).getByLabelText('Origen del dinero')
    expect(within(origen).getByRole('option', { name: 'Tesorería' })).toBeInTheDocument()
    expect(within(origen).getByRole('option', { name: 'Cajón del turno' })).toBeInTheDocument()
    expect(lecturas('/empresas')).toHaveLength(1)
  })

  it('el Supervisor no pide empresas y el origen solo le ofrece el cajón', async () => {
    rolActual = ROL.Supervisor
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Registrar pago')
    await evento.click(within(dialogo).getByRole('button', { name: 'Agregar línea' }))

    const origen = within(dialogo).getByLabelText('Origen del dinero')
    expect(within(origen).queryByRole('option', { name: 'Tesorería' })).not.toBeInTheDocument()
    expect(within(origen).getByRole('option', { name: 'Cajón del turno' })).toBeInTheDocument()
    expect(lecturas('/empresas')).toHaveLength(0)
  })

  it('el Supervisor sí puede cargar horas y registrar ajustes', async () => {
    rolActual = ROL.Supervisor
    renderCuenta()
    await esperarCuenta()

    expect(screen.getByRole('button', { name: 'Cargar horas' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Ajuste manual' })).toBeEnabled()
  })
})

describe('CuentaDePersonal — cargar horas', () => {
  it('prellena fecha y valor hora, calcula la vista previa con el redondeo del servidor y envía el cuerpo del DTO', async () => {
    apiPostMock.mockResolvedValue(MOV_HORAS)
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Cargar horas')

    expect(within(dialogo).getByLabelText('Valor hora')).toHaveValue('3.500,00')
    expect(within(dialogo).getByLabelText('Día trabajado')).toHaveValue(
      HOY.split('-').reverse().join('/'),
    )

    await evento.type(within(dialogo).getByLabelText('Horas'), '7,5')
    expect(within(dialogo).getByText('Importe: $ 26.250,00')).toBeInTheDocument()
    await evento.clear(within(dialogo).getByLabelText('Valor hora'))
    await evento.type(within(dialogo).getByLabelText('Valor hora'), '1,15')
    await evento.clear(within(dialogo).getByLabelText('Horas'))
    await evento.type(within(dialogo).getByLabelText('Horas'), '0,7')
    expect(within(dialogo).getByText('Importe: $ 0,81')).toBeInTheDocument()

    await evento.type(within(dialogo).getByLabelText('Detalle (opcional)'), ' extra ')
    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(apiPostMock).toHaveBeenCalledWith('/personal/1/horas', { fecha: HOY, horas: 0.7, valorHora: 1.15, detalle: 'extra' })
    expect(await screen.findByText('Horas cargadas.')).toBeInTheDocument()
  })

  it('tras escribir refresca el estado de cuenta desde la primera página y la deuda del cliente', async () => {
    apiPostMock.mockResolvedValue(MOV_HORAS)
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    await screen.findByText('Deuda: $ 750,00')
    const antesCuenta = lecturasDeCuenta().length
    const antesDeuda = lecturas('/clientes/21').length
    const dialogo = await abrir(evento, 'Cargar horas')

    await evento.type(within(dialogo).getByLabelText('Horas'), '8')
    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    await screen.findByText('Horas cargadas.')
    expect(lecturasDeCuenta()).toHaveLength(antesCuenta + 1)
    expect(lecturas('/clientes/21')).toHaveLength(antesDeuda + 1)
  })

  it('sin horas no llama a la API y marca el campo', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Cargar horas')

    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    expect(await within(dialogo).findByText('Las horas deben ser mayores que 0 y hasta 24.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Horas')).toHaveAttribute('aria-invalid', 'true')
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('sin fecha no llama a la API', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Cargar horas')

    await evento.clear(within(dialogo).getByLabelText('Día trabajado'))
    await evento.type(within(dialogo).getByLabelText('Horas'), '8')
    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    expect(await within(dialogo).findByText('Ingresá el día trabajado.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un rechazo del servidor se muestra en su campo y la modal sigue abierta con lo tipeado', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'horas_invalidas', 'texto del servidor'))
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Cargar horas')

    await evento.type(within(dialogo).getByLabelText('Horas'), '8')
    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    expect(
      await within(dialogo).findByText('Las horas deben ser mayores que 0 y hasta 24, con hasta dos decimales.'),
    ).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Horas')).toHaveValue('8,00')
    expect(screen.queryByText('texto del servidor')).not.toBeInTheDocument()
  })

  it('un 404 al cargar dice que el empleado ya no existe', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el personal 1.'))
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Cargar horas')

    await evento.type(within(dialogo).getByLabelText('Horas'), '8')
    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    expect(await within(dialogo).findByText('El empleado ya no existe. Volvé al personal y actualizá el listado.')).toBeInTheDocument()
  })
})

describe('CuentaDePersonal — esqueleto asíncrono de las escrituras', () => {
  async function prepararCargaDeHoras(evento: Evento) {
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Cargar horas')
    await evento.type(within(dialogo).getByLabelText('Horas'), '8')

    return dialogo
  }

  /** Cláusula bajo prueba: `ocupadoRef` en `escribir`, la guarda de reentrancia del mismo tick. */
  it('dos clics sincrónicos sobre el envío emiten una sola request', async () => {
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await prepararCargaDeHoras(evento)

    const enviar = within(dialogo).getByRole('button', { name: 'Cargar horas' })
    await act(async () => {
      enviar.click()
      enviar.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('lo mismo vale para el pago', async () => {
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    await screen.findByText('Deuda: $ 750,00')

    const pago = await abrir(evento, 'Registrar pago')
    await evento.type(within(pago).getByLabelText(/^Descuento de la cuenta corriente/), '100')
    const enviarPago = within(pago).getByRole('button', { name: 'Registrar pago' })
    await act(async () => {
      enviarPago.click()
      enviarPago.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('lo mismo vale para el ajuste', async () => {
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()

    const ajuste = await abrir(evento, 'Ajuste manual')
    await evento.type(within(ajuste).getByLabelText('Importe'), '100')
    await evento.type(within(ajuste).getByLabelText(/^Detalle/), 'Corrección')
    const enviarAjuste = within(ajuste).getByRole('button', { name: 'Registrar ajuste' })
    await act(async () => {
      enviarAjuste.click()
      enviarAjuste.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: `bloqueado` cubre la escritura y su refresco, no solo la request. */
  it('durante la escritura y su refresco no queda ninguna acción alcanzable', async () => {
    const escritura = diferido<MovimientoDePersonal>()
    const refresco = diferido<PaginaDeCuentaDePersonal>()
    let lecturasHechas = 0
    mockearRutas({ cuenta: () => (++lecturasHechas === 1 ? Promise.resolve(paginaDeCuenta()) : refresco.promesa) })
    apiPostMock.mockReturnValue(escritura.promesa)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await prepararCargaDeHoras(evento)

    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    expect(within(dialogo).getByRole('button', { name: 'Registrando…' })).toBeDisabled()
    expect(within(dialogo).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(within(dialogo).getByLabelText('Horas')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cerrar' })).toBeDisabled()
    await evento.keyboard('{Escape}')
    expect(screen.getByRole('dialog', { name: 'Cargar horas' })).toBeInTheDocument()
    const volver = screen.getByRole('link', { name: 'Volver al personal' })
    expect(volver).toHaveAttribute('aria-disabled', 'true')
    await evento.click(volver)
    expect(screen.queryByTestId('ruta')).not.toBeInTheDocument()

    await act(async () => {
      escritura.resolver(MOV_HORAS)
      await escritura.promesa
    })

    expect(await screen.findByText('Horas cargadas.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    for (const nombre of ['Cargar horas', 'Ajuste manual', 'Registrar pago', 'Siguiente', 'Anterior']) {
      expect(screen.getByRole('button', { name: nombre })).toBeDisabled()
    }
    expect(screen.getByLabelText('Ver histórico completo')).toBeDisabled()
    expect(screen.getByLabelText('Desde')).toBeDisabled()
    expect(screen.getByRole('link', { name: 'Volver al personal' })).toHaveAttribute('aria-disabled', 'true')

    await act(async () => {
      refresco.resolver(paginaDeCuenta())
      await refresco.promesa
    })

    await waitFor(() => expect(screen.getByRole('button', { name: 'Cargar horas' })).toBeEnabled())
    expect(screen.getByRole('link', { name: 'Volver al personal' })).not.toHaveAttribute('aria-disabled', 'true')
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('con la pantalla libre el enlace de vuelta navega al personal', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()

    await evento.click(screen.getByRole('link', { name: 'Volver al personal' }))

    expect(screen.getByTestId('ruta')).toHaveTextContent('/personal')
  })

  /** Cláusula bajo prueba: la rama `catch` de `refrescarTrasEscribir`. Una escritura confirmada nunca se
   * reporta como fallida, aunque el refresco posterior explote. */
  it('un refresco fallido después de escribir avisa que se registró y cierra la modal', async () => {
    let lecturasHechas = 0
    mockearRutas({
      cuenta: () => (++lecturasHechas === 1 ? Promise.resolve(paginaDeCuenta()) : Promise.reject(new ErrorApi(500, 'error', 'Se cayó.'))),
    })
    apiPostMock.mockResolvedValue(MOV_HORAS)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await prepararCargaDeHoras(evento)

    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    expect(
      await screen.findByText('Horas cargadas. Pero no se pudo actualizar la vista. Recargá la pantalla.'),
    ).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.queryByText('Se cayó.')).not.toBeInTheDocument()
  })

  it('un refresco de la deuda del cliente fallido también avisa', async () => {
    let lecturasDeDeuda = 0
    mockearRutas({
      cliente: () => (++lecturasDeDeuda === 1 ? Promise.resolve(cliente(750)) : Promise.reject(new ErrorApi(500, 'error', 'Se cayó.'))),
    })
    apiPostMock.mockResolvedValue(MOV_HORAS)
    const evento = userEvent.setup()
    renderCuenta()
    await screen.findByText('Deuda: $ 750,00')
    const dialogo = await prepararCargaDeHoras(evento)

    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    expect(
      await screen.findByText('Horas cargadas. Pero no se pudo actualizar la vista. Recargá la pantalla.'),
    ).toBeInTheDocument()
  })

  it('el rechazo de un envío anterior desaparece apenas empieza el reintento, antes de que responda', async () => {
    const reintento = diferido<MovimientoDePersonal>()
    apiPostMock.mockRejectedValueOnce(new ErrorApi(400, 'horas_invalidas', 'x')).mockReturnValueOnce(reintento.promesa)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await prepararCargaDeHoras(evento)
    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))
    await within(dialogo).findByText('Las horas deben ser mayores que 0 y hasta 24, con hasta dos decimales.')

    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    expect(
      within(dialogo).queryByText('Las horas deben ser mayores que 0 y hasta 24, con hasta dos decimales.'),
    ).not.toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledTimes(2)
  })

  it('editar el formulario descarta el rechazo que estaba a la vista', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'horas_invalidas', 'x'))
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await prepararCargaDeHoras(evento)
    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))
    await within(dialogo).findByText('Las horas deben ser mayores que 0 y hasta 24, con hasta dos decimales.')

    await evento.type(within(dialogo).getByLabelText('Horas'), '1')

    expect(
      within(dialogo).queryByText('Las horas deben ser mayores que 0 y hasta 24, con hasta dos decimales.'),
    ).not.toBeInTheDocument()
  })

  it('cancelar un envío rechazado se lleva el rechazo y reabrir arranca limpio', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'horas_invalidas', 'x'))
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await prepararCargaDeHoras(evento)
    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))
    await within(dialogo).findByText('Las horas deben ser mayores que 0 y hasta 24, con hasta dos decimales.')

    await evento.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))
    const nueva = await abrir(evento, 'Cargar horas')

    expect(within(nueva).getByLabelText('Horas')).toHaveValue('')
    expect(
      within(nueva).queryByText('Las horas deben ser mayores que 0 y hasta 24, con hasta dos decimales.'),
    ).not.toBeInTheDocument()
  })

  it('el aviso de una escritura anterior no sobrevive a abrir la siguiente', async () => {
    apiPostMock.mockResolvedValue(MOV_HORAS)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await prepararCargaDeHoras(evento)
    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))
    await screen.findByText('Horas cargadas.')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ajuste manual' })).toBeEnabled())

    await evento.click(screen.getByRole('button', { name: 'Ajuste manual' }))

    await screen.findByRole('dialog', { name: 'Ajuste manual' })
    expect(screen.queryByText('Horas cargadas.')).not.toBeInTheDocument()
  })

  it('un fallo de red en la escritura no promete que no se escribió', async () => {
    apiPostMock.mockRejectedValue(new Error('Failed to fetch'))
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await prepararCargaDeHoras(evento)

    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    expect(
      await within(dialogo).findByText('No se pudo confirmar el resultado: revisá el estado de cuenta antes de reintentar.'),
    ).toBeInTheDocument()
  })
})

describe('CuentaDePersonal — ajuste', () => {
  async function abrirAjuste(evento: Evento) {
    await esperarCuenta()

    return abrir(evento, 'Ajuste manual')
  }

  it('explica que así se corrige una carga equivocada y arranca con la fecha de hoy', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirAjuste(evento)

    expect(within(dialogo).getByText(/una carga equivocada se corrige acá/)).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Fecha')).toHaveValue(HOY.split('-').reverse().join('/'))
    expect(within(dialogo).getByLabelText(/^A favor del empleado/)).toBeChecked()
  })

  it('"en contra" manda el importe negativo y la vista previa baja el saldo', async () => {
    apiPostMock.mockResolvedValue(MOV_AJUSTE)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirAjuste(evento)

    await evento.click(within(dialogo).getByLabelText(/^En contra del empleado/))
    await evento.type(within(dialogo).getByLabelText('Importe'), '250')
    expect(within(dialogo).getByText('Saldo después del ajuste: $ 11.750,00')).toBeInTheDocument()
    await evento.type(within(dialogo).getByLabelText(/^Detalle/), ' Corrección de una carga ')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(apiPostMock).toHaveBeenCalledWith('/personal/1/ajustes', {
      fecha: HOY,
      importe: -250,
      detalle: 'Corrección de una carga',
    })
    expect(await screen.findByText('Ajuste registrado.')).toBeInTheDocument()
  })

  it('"a favor" manda el importe positivo', async () => {
    apiPostMock.mockResolvedValue(MOV_AJUSTE)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirAjuste(evento)

    await evento.type(within(dialogo).getByLabelText('Importe'), '100')
    await evento.type(within(dialogo).getByLabelText(/^Detalle/), 'Horas olvidadas')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/personal/1/ajustes', { fecha: HOY, importe: 100, detalle: 'Horas olvidadas' }))
  })

  it('un detalle de menos de cinco caracteres se rechaza sin llamar a la API', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirAjuste(evento)

    await evento.type(within(dialogo).getByLabelText('Importe'), '100')
    await evento.type(within(dialogo).getByLabelText(/^Detalle/), 'abcd')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar ajuste' }))

    expect(await within(dialogo).findByText('El detalle es obligatorio y tiene que tener al menos 5 caracteres.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('sin importe se rechaza y se explica que el sentido se elige', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirAjuste(evento)

    await evento.type(within(dialogo).getByLabelText(/^Detalle/), 'Corrección')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar ajuste' }))

    expect(await within(dialogo).findByText(/elegí si es a favor o en contra/)).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un rechazo del servidor sobre el detalle se muestra en el detalle', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'ajuste_detalle_requerido', 'x'))
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirAjuste(evento)

    await evento.type(within(dialogo).getByLabelText('Importe'), '100')
    await evento.type(within(dialogo).getByLabelText(/^Detalle/), 'Corrección')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar ajuste' }))

    expect(await within(dialogo).findByText('El detalle es obligatorio y tiene que tener al menos 5 caracteres.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText(/^Detalle/)).toHaveAttribute('aria-invalid', 'true')
  })
})

describe('CuentaDePersonal — pago', () => {
  async function abrirPago(evento: Evento) {
    await esperarCuenta()
    await screen.findByText('Deuda: $ 750,00')

    return abrir(evento, 'Registrar pago')
  }

  /** Agrega una línea de dinero con medio e importe. */
  async function agregarLinea(evento: Evento, dialogo: HTMLElement, numero: number, medio: string, importe: string) {
    await evento.click(within(dialogo).getByRole('button', { name: 'Agregar línea' }))
    await evento.selectOptions(within(dialogo).getByLabelText(`Medio de pago de la línea ${numero}`), medio)
    await evento.type(within(dialogo).getByLabelText(`Importe de la línea ${numero}`), importe)
  }

  it('el botón espera a los catálogos: con los medios en vuelo está deshabilitado y después se habilita', async () => {
    const medios = diferido<MedioPagoListado[]>()
    mockearRutas({ medios: () => medios.promesa })
    renderCuenta()
    await esperarCuenta()

    expect(screen.getByRole('button', { name: 'Registrar pago' })).toBeDisabled()
    await act(async () => {
      medios.resolver(MEDIOS)
      await medios.promesa
    })

    await waitFor(() => expect(screen.getByRole('button', { name: 'Registrar pago' })).toBeEnabled())
  })

  it('la cuenta corriente no se ofrece como medio de una línea de dinero', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await evento.click(within(dialogo).getByRole('button', { name: 'Agregar línea' }))

    const medio = within(dialogo).getByLabelText('Medio de pago de la línea 1')
    expect(within(medio).getByRole('option', { name: 'Efectivo' })).toBeInTheDocument()
    expect(within(medio).getByRole('option', { name: 'Transferencia' })).toBeInTheDocument()
    expect(within(medio).queryByRole('option', { name: 'Cuenta corriente' })).not.toBeInTheDocument()
  })

  it('tesorería: manda la empresa elegida, el origen y las líneas, y nunca el punto de venta', async () => {
    apiPostMock.mockResolvedValue(MOV_PAGO)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await agregarLinea(evento, dialogo, 1, '4', '6000')
    await evento.type(within(dialogo).getByLabelText('Referencia de la línea 1'), ' op-1 ')
    await agregarLinea(evento, dialogo, 2, '5', '250,5')
    await evento.selectOptions(within(dialogo).getByLabelText('Origen del dinero'), 'Tesoreria')
    expect(within(dialogo).getByLabelText('Empresa que paga')).toHaveValue('2')
    await evento.selectOptions(within(dialogo).getByLabelText('Empresa que paga'), '3')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock).toHaveBeenCalledWith('/personal/1/pagos', {
      fecha: HOY,
      origenFondos: 'Tesoreria',
      idEmpresa: 3,
      lineas: [
        { idMedioPago: 4, importe: 6000, referencia: 'op-1' },
        { idMedioPago: 5, importe: 250.5 },
      ],
    })
    expect(await screen.findByText('Pago registrado.')).toBeInTheDocument()
  })

  it('cajón del turno: manda el punto de venta de la sesión y nunca la empresa', async () => {
    apiPostMock.mockResolvedValue(MOV_PAGO)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await agregarLinea(evento, dialogo, 1, '4', '1000')
    await evento.selectOptions(within(dialogo).getByLabelText('Origen del dinero'), 'CajaTurno')
    expect(within(dialogo).getByText('Sale del turno abierto en Sucursal Centro.')).toBeInTheDocument()
    expect(within(dialogo).queryByLabelText('Empresa que paga')).not.toBeInTheDocument()
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/personal/1/pagos', {
        fecha: HOY,
        origenFondos: 'CajaTurno',
        idPuntoVenta: 9,
        lineas: [{ idMedioPago: 4, importe: 1000 }],
      }),
    )
  })

  it('cajón del turno sin punto de venta elegido lo avisa antes de enviar y no llama a la API', async () => {
    puntoVentaActual = null
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await agregarLinea(evento, dialogo, 1, '4', '1000')
    await evento.selectOptions(within(dialogo).getByLabelText('Origen del dinero'), 'CajaTurno')

    expect(within(dialogo).getByText('Elegí un punto de venta desde el menú para poder pagar del cajón del turno.')).toBeInTheDocument()
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))
    await waitFor(() => expect(within(dialogo).getByLabelText('Origen del dinero')).toHaveAttribute('aria-invalid', 'true'))
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('solo descuento: muestra la deuda, manda el descuento y el punto de venta, sin origen ni líneas', async () => {
    apiPostMock.mockResolvedValue(MOV_PAGO)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    expect(within(dialogo).getByText('Deuda actual: $ 750,00. El descuento no mueve dinero.')).toBeInTheDocument()
    await evento.type(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/), '750')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/personal/1/pagos', {
        fecha: HOY,
        descuentoDeCuentaCorriente: 750,
        idPuntoVenta: 9,
      }),
    )
  })

  it('el descuento no puede superar la deuda del cliente', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await evento.type(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/), '750,01')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    expect(await within(dialogo).findByText('El descuento no puede superar la deuda del cliente ($ 750,00).')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un descuento sin punto de venta de la sesión lo avisa en el formulario, antes de enviar', async () => {
    puntoVentaActual = null
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await evento.type(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/), '100')

    expect(within(dialogo).getByText('Elegí un punto de venta desde el menú para poder descontar de la cuenta corriente.')).toBeInTheDocument()
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('sin cliente vinculado el formulario no ofrece descuento', async () => {
    mockearRutas({ empleado: () => Promise.resolve(empleadoBase({ idCliente: null, nombreCliente: null })) })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Registrar pago')

    expect(within(dialogo).queryByLabelText(/Descuento de la cuenta corriente/)).not.toBeInTheDocument()
  })

  it('con el cliente sin deuda el descuento queda deshabilitado y lo explica', async () => {
    mockearRutas({ cliente: () => Promise.resolve(cliente(-5)) })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    await screen.findByText('Saldo a favor: $ 5,00')
    const dialogo = await abrir(evento, 'Registrar pago')

    expect(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/)).toBeDisabled()
    expect(within(dialogo).getByText('El cliente no tiene deuda que descontar.')).toBeInTheDocument()
  })

  it('si la deuda no se pudo leer el descuento queda deshabilitado y lo explica', async () => {
    mockearRutas({ cliente: () => Promise.reject(new ErrorApi(500, 'error', 'x')) })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    await screen.findByText('No se pudo leer su deuda.')
    const dialogo = await abrir(evento, 'Registrar pago')

    expect(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/)).toBeDisabled()
    expect(within(dialogo).getByText('No se pudo leer la deuda del cliente: recargá la pantalla para poder descontar.')).toBeInTheDocument()
  })

  it('un pago vacío se rechaza sin llamar a la API', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    expect(await within(dialogo).findByText('Cargá un descuento de cuenta corriente o al menos una línea de dinero.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('con líneas y sin origen, el origen es obligatorio', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await agregarLinea(evento, dialogo, 1, '4', '1000')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    expect(await within(dialogo).findByText('Elegí de dónde sale el dinero.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Origen del dinero')).toHaveAttribute('aria-invalid', 'true')
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('las líneas se agregan y se quitan, y con veinte no se puede agregar otra', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)
    const agregar = within(dialogo).getByRole('button', { name: 'Agregar línea' })

    for (let i = 0; i < 20; i++) await evento.click(agregar)

    expect(within(dialogo).getByLabelText('Medio de pago de la línea 20')).toBeInTheDocument()
    expect(agregar).toBeDisabled()

    await evento.click(within(dialogo).getByRole('button', { name: 'Quitar la línea 1' }))

    expect(within(dialogo).queryByLabelText('Medio de pago de la línea 20')).not.toBeInTheDocument()
    expect(agregar).toBeEnabled()
  })

  it('muestra el total corriente y el saldo que queda, con la leyenda de adelanto si es negativo', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await evento.type(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/), '500')
    await agregarLinea(evento, dialogo, 1, '4', '6000')
    expect(within(dialogo).getByText('Total del pago: $ 6.500,00')).toBeInTheDocument()
    expect(within(dialogo).getByText('Saldo después del pago: $ 5.500,00')).toBeInTheDocument()

    await agregarLinea(evento, dialogo, 2, '5', '6000')
    expect(within(dialogo).getByText('Total del pago: $ 12.500,00')).toBeInTheDocument()
    expect(within(dialogo).getByText('Saldo después del pago: -$ 500,00 (adelanto ya pagado)')).toBeInTheDocument()
  })

  it.each([
    ['prohibido', 403, 'origen', 'Pagar desde la tesorería requiere ser administrador.'],
    ['turno_no_abierto', 409, 'origen', 'No hay un turno abierto en el punto de venta de la sesión. Abrí el turno desde Caja y volvé a intentar.'],
    ['personal_sin_cliente_vinculado', 409, 'descuento', 'El empleado no tiene un cliente vinculado vigente para descontar de su cuenta corriente.'],
    ['descuento_supera_la_deuda', 409, 'descuento', 'El descuento supera la deuda actual del cliente vinculado. Actualizá la pantalla.'],
  ])('el rechazo %s (%i) se muestra en el campo %s', async (codigo, estado, campo, mensaje) => {
    apiPostMock.mockRejectedValue(new ErrorApi(estado, codigo, 'texto del servidor'))
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await evento.type(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/), '100')
    await agregarLinea(evento, dialogo, 1, '4', '1000')
    await evento.selectOptions(within(dialogo).getByLabelText('Origen del dinero'), 'CajaTurno')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    expect(await within(dialogo).findByText(mensaje)).toBeInTheDocument()
    const control = campo === 'origen' ? within(dialogo).getByLabelText('Origen del dinero') : within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/)
    expect(control).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByRole('dialog', { name: 'Registrar pago' })).toBeInTheDocument()
    expect(screen.queryByText('texto del servidor')).not.toBeInTheDocument()
  })

  it('si los medios no cargaron lo avisa, no deja agregar líneas y el descuento sigue disponible', async () => {
    mockearRutas({ medios: () => Promise.reject(new ErrorApi(500, 'error', 'x')) })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    await screen.findByText('Deuda: $ 750,00')
    const dialogo = await abrir(evento, 'Registrar pago')

    expect(
      within(dialogo).getByText('No se pudieron cargar todos los datos del pago: no se pueden agregar líneas de dinero.'),
    ).toBeInTheDocument()
    expect(within(dialogo).getByRole('button', { name: 'Agregar línea' })).toBeDisabled()
    expect(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/)).toBeEnabled()
  })

  it('un pago con éxito refresca el estado de cuenta, el saldo y la deuda', async () => {
    let lecturasHechas = 0
    mockearRutas({
      cuenta: () =>
        Promise.resolve(
          ++lecturasHechas === 1
            ? paginaDeCuenta()
            : paginaDeCuenta({ encabezado: { idPersonal: 1, nombre: 'Ana Gómez', saldo: 5500 } }),
        ),
    })
    apiPostMock.mockResolvedValue(MOV_PAGO)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await agregarLinea(evento, dialogo, 1, '4', '6500')
    await evento.selectOptions(within(dialogo).getByLabelText('Origen del dinero'), 'CajaTurno')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    expect(await screen.findByText('$ 5.500,00')).toBeInTheDocument()
    expect(lecturas('/clientes/21')).toHaveLength(2)
  })
})
