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

/** Un servidor que responde con el rango que le pidieron (o con la ventana de un mes si no hubo "desde") y con la
 * página pedida, sobre los mismos movimientos de siempre. */
function paginaSegunLaRuta(ruta: string, sobrescribir: Partial<PaginaDeCuentaDePersonal> = {}): PaginaDeCuentaDePersonal {
  const query = new URLSearchParams(ruta.split('?')[1])
  const historico = query.get('historico') === 'true'

  return paginaDeCuenta({
    historico,
    desde: historico ? null : (query.get('desde') ?? VENTANA_POR_DEFECTO),
    hasta: historico ? null : query.get('hasta'),
    pagina: Number(query.get('pagina')),
    ...sobrescribir,
  })
}

/** El "desde" que aplica el servidor cuando la solicitud no trae uno. */
const VENTANA_POR_DEFECTO = '2026-09-01'

const lecturas = (prefijo: string) => apiGetMock.mock.calls.filter((c) => (c[0] as string).startsWith(prefijo))
const lecturasDeCuenta = () => lecturas('/personal/1/cuenta')

type Evento = ReturnType<typeof userEvent.setup>

/** Espera a los DATOS de los que dependen los botones, no a un elemento que ya estaba: un movimiento del
 * estado de cuenta (`cuenta`) y el valor hora vigente del empleado (`empleado`). */
async function esperarCuenta() {
  await screen.findByText('Turno mañana')
  const valorHora = screen.getByText('Valor hora vigente').parentElement as HTMLElement
  await within(valorHora).findByText('$ 3.500,00')
}

/** El sentido del ajuste no tiene un valor por defecto: cada test que envía un ajuste lo elige. */
async function elegirSentido(evento: Evento, dialogo: HTMLElement, sentido: 'aFavor' | 'enContra') {
  await evento.click(within(dialogo).getByLabelText(sentido === 'aFavor' ? /^A favor del empleado/ : /^En contra del empleado/))
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
    expect(within(pago).getByText('Efectivo: $ 6.000,00 (Caja del turno)')).toBeInTheDocument()
    expect(
      within(pago).getByText('Medio de pago dado de baja: $ 0,50 (Caja general), ref. op-9'),
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

  /** Cláusula bajo prueba: el paso parte de la página que se ve (`cuenta.pagina`), no de la pedida (`filtros.pagina`).
   * Una por botón: cada uno lleva su propia resta o suma. */
  it.each([
    ['Siguiente', 'pagina=3&'],
    ['Anterior', 'pagina=1&'],
  ])('tras un cambio de página fallido, "%s" parte de la página que se ve y pide %s', async (boton, pedida) => {
    let tercerasPedidas = 0
    mockearRutas({
      cuenta: (ruta) =>
        ruta.includes('pagina=3') && ++tercerasPedidas === 1
          ? Promise.reject(new ErrorApi(500, 'error', 'Se cayó la página tres.'))
          : Promise.resolve(paginaSegunLaRuta(ruta, { total: 60 })),
    })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    await evento.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText('Página 2 de 3 — 60 movimiento(s)')
    await evento.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText('Se cayó la página tres.')
    expect(screen.getByText('Página 2 de 3 — 60 movimiento(s)')).toBeInTheDocument()

    await evento.click(screen.getByRole('button', { name: boton }))

    await waitFor(() => expect(lecturasDeCuenta().at(-1)?.[0]).toContain(pedida))
  })

  /** Cláusula bajo prueba: `conElRangoEfectivo`, que vuelca en los filtros el rango que el servidor aplicó. */
  it('vaciar "Desde" muestra la ventana de un mes que aplicó el servidor, y el histórico sigue siendo la única forma de quitar el límite', async () => {
    mockearRutas({ cuenta: (ruta) => Promise.resolve(paginaSegunLaRuta(ruta)) })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()

    await evento.clear(screen.getByLabelText('Desde'))

    await waitFor(() => expect(screen.getByLabelText('Desde')).toHaveValue('01/09/2026'))
    expect(lecturasDeCuenta().at(-1)?.[0]).toBe('/personal/1/cuenta?pagina=1&tamanio=25')

    await evento.click(screen.getByLabelText('Ver histórico completo'))

    await waitFor(() => expect(lecturasDeCuenta().at(-1)?.[0]).toBe('/personal/1/cuenta?historico=true&pagina=1&tamanio=25'))
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
    expect(within(origen).getByRole('option', { name: 'Caja general' })).toBeInTheDocument()
    expect(within(origen).getByRole('option', { name: 'Caja del turno' })).toBeInTheDocument()
    expect(lecturas('/empresas')).toHaveLength(1)
  })

  it('el Supervisor no pide empresas y el origen solo le ofrece la caja del turno', async () => {
    rolActual = ROL.Supervisor
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Registrar pago')
    await evento.click(within(dialogo).getByRole('button', { name: 'Agregar línea' }))

    const origen = within(dialogo).getByLabelText('Origen del dinero')
    expect(within(origen).queryByRole('option', { name: 'Caja general' })).not.toBeInTheDocument()
    expect(within(origen).getByRole('option', { name: 'Caja del turno' })).toBeInTheDocument()
    expect(lecturas('/empresas')).toHaveLength(0)
  })

  it('el Supervisor sí puede cargar horas y registrar ajustes: ambas requests salen con su rol', async () => {
    rolActual = ROL.Supervisor
    apiPostMock.mockResolvedValue(MOV_HORAS)
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()

    const horas = await abrir(evento, 'Cargar horas')
    await evento.type(within(horas).getByLabelText('Horas'), '8')
    await evento.click(within(horas).getByRole('button', { name: 'Cargar horas' }))
    await screen.findByText('Horas cargadas.')
    expect(apiPostMock).toHaveBeenLastCalledWith('/personal/1/horas', { fecha: HOY, horas: 8, valorHora: 3500 })

    const ajuste = await abrir(evento, 'Ajuste manual')
    await elegirSentido(evento, ajuste, 'aFavor')
    await evento.type(within(ajuste).getByLabelText('Importe'), '100')
    await evento.type(within(ajuste).getByLabelText(/^Detalle/), 'Horas olvidadas')
    await evento.click(within(ajuste).getByRole('button', { name: 'Registrar ajuste' }))
    await screen.findByText('Ajuste registrado.')
    expect(apiPostMock).toHaveBeenLastCalledWith('/personal/1/ajustes', { fecha: HOY, importe: 100, detalle: 'Horas olvidadas' })
    expect(apiPostMock).toHaveBeenCalledTimes(2)
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

  /** Cláusula bajo prueba: `pagina: 1` del refresco posterior a una escritura. Se escribe parado en la página dos. */
  it('tras escribir desde la página dos refresca el estado de cuenta desde la primera página y la deuda del cliente', async () => {
    mockearRutas({ cuenta: (ruta) => Promise.resolve(paginaSegunLaRuta(ruta, { total: 60 })) })
    apiPostMock.mockResolvedValue(MOV_HORAS)
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    await screen.findByText('Deuda: $ 750,00')
    await evento.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText('Página 2 de 3 — 60 movimiento(s)')
    expect(lecturasDeCuenta().at(-1)?.[0]).toContain('pagina=2')
    const antesCuenta = lecturasDeCuenta().length
    const antesDeuda = lecturas('/clientes/21').length
    const dialogo = await abrir(evento, 'Cargar horas')

    await evento.type(within(dialogo).getByLabelText('Horas'), '8')
    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    await screen.findByText('Horas cargadas.')
    await screen.findByText('Página 1 de 3 — 60 movimiento(s)')
    expect(lecturasDeCuenta()).toHaveLength(antesCuenta + 1)
    expect(lecturasDeCuenta().at(-1)?.[0]).toContain('pagina=1&')
    expect(lecturas('/clientes/21')).toHaveLength(antesDeuda + 1)
  })

  it.each([
    ['1.5', 1.5, '$ 5.250,00'],
    ['1,5', 1.5, '$ 5.250,00'],
    ['7.5', 7.5, '$ 26.250,00'],
  ])('las horas "%s" se entienden como %s: el punto y la coma son decimales, no separador de miles', async (tipeado, horas, importe) => {
    apiPostMock.mockResolvedValue(MOV_HORAS)
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Cargar horas')

    await evento.type(within(dialogo).getByLabelText('Horas'), tipeado)

    expect(within(dialogo).getByLabelText('Horas')).toHaveValue(tipeado)
    expect(within(dialogo).getByText(`Importe: ${importe}`)).toBeInTheDocument()
    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/personal/1/horas', { fecha: HOY, horas, valorHora: 3500 }))
  })

  /** Cláusula bajo prueba: el patrón de `parsearHoras`. Un texto ambiguo no se adivina: sin vista previa y con error al enviar. */
  it.each(['1.234,5', '1,5.2', '-8', '1,234'])('las horas "%s" son inválidas: sin importe en la vista previa y con error al enviar', async (tipeado) => {
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Cargar horas')

    await evento.type(within(dialogo).getByLabelText('Horas'), tipeado)

    expect(within(dialogo).getByText('Importe: —')).toBeInTheDocument()
    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))
    expect(
      await within(dialogo).findByText('Las horas tienen que ser un número con hasta dos decimales, por ejemplo 7,5.'),
    ).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Horas')).toHaveAttribute('aria-invalid', 'true')
    expect(apiPostMock).not.toHaveBeenCalled()
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
    expect(within(dialogo).getByLabelText('Horas')).toHaveValue('8')
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
    await elegirSentido(evento, ajuste, 'aFavor')
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
})

describe('CuentaDePersonal — ajuste', () => {
  async function abrirAjuste(evento: Evento) {
    await esperarCuenta()

    return abrir(evento, 'Ajuste manual')
  }

  it('explica que así se corrige una carga equivocada y arranca con la fecha de hoy y sin sentido elegido', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirAjuste(evento)

    expect(within(dialogo).getByText(/una carga equivocada se corrige acá/)).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Fecha')).toHaveValue(HOY.split('-').reverse().join('/'))
    expect(within(dialogo).getByLabelText(/^A favor del empleado/)).not.toBeChecked()
    expect(within(dialogo).getByLabelText(/^En contra del empleado/)).not.toBeChecked()
  })

  it('sin elegir el sentido se rechaza en el propio campo y no se llama a la API', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirAjuste(evento)

    await evento.type(within(dialogo).getByLabelText('Importe'), '100')
    await evento.type(within(dialogo).getByLabelText(/^Detalle/), 'Corrección')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar ajuste' }))

    expect(await within(dialogo).findByText('Elegí si el ajuste es a favor o en contra del empleado.')).toBeInTheDocument()
    expect(within(dialogo).getByRole('group', { name: 'Sentido del ajuste' })).toHaveAttribute('aria-invalid', 'true')
    expect(apiPostMock).not.toHaveBeenCalled()

    await elegirSentido(evento, dialogo, 'enContra')
    expect(within(dialogo).queryByText('Elegí si el ajuste es a favor o en contra del empleado.')).not.toBeInTheDocument()
  })

  it('"en contra" manda el importe negativo y la vista previa baja el saldo', async () => {
    apiPostMock.mockResolvedValue(MOV_AJUSTE)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirAjuste(evento)

    await elegirSentido(evento, dialogo, 'enContra')
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

    await elegirSentido(evento, dialogo, 'aFavor')
    await evento.type(within(dialogo).getByLabelText('Importe'), '100')
    await evento.type(within(dialogo).getByLabelText(/^Detalle/), 'Horas olvidadas')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/personal/1/ajustes', { fecha: HOY, importe: 100, detalle: 'Horas olvidadas' }))
  })

  it('un detalle de menos de cinco caracteres se rechaza sin llamar a la API', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirAjuste(evento)

    await elegirSentido(evento, dialogo, 'aFavor')
    await evento.type(within(dialogo).getByLabelText('Importe'), '100')
    await evento.type(within(dialogo).getByLabelText(/^Detalle/), 'abcd')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar ajuste' }))

    expect(await within(dialogo).findByText('El detalle es obligatorio y tiene que tener al menos 5 caracteres.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('sin importe se rechaza en el campo del importe, ya con el sentido elegido', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirAjuste(evento)

    await elegirSentido(evento, dialogo, 'aFavor')
    await evento.type(within(dialogo).getByLabelText(/^Detalle/), 'Corrección')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar ajuste' }))

    expect(await within(dialogo).findByText('Ingresá un importe mayor que cero.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Importe')).toHaveAttribute('aria-invalid', 'true')
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un rechazo del servidor sobre el detalle se muestra en el detalle', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'ajuste_detalle_requerido', 'x'))
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirAjuste(evento)

    await elegirSentido(evento, dialogo, 'aFavor')
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

  it('caja del turno: manda el punto de venta de la sesión y nunca la empresa', async () => {
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

  it('caja del turno sin punto de venta elegido lo avisa antes de enviar y no llama a la API', async () => {
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
    ['turno_no_abierto', 409, 'origen', 'No hay un turno abierto en el punto de venta de la sesión. Abrí el turno desde Caja y volvé a intentar.'],
    ['descuento_supera_la_deuda', 409, 'descuento', 'El descuento supera la deuda actual del cliente vinculado. Revisá la deuda que muestra este formulario.'],
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

  /** Cláusulas bajo prueba: el `pidePuntoVenta` del descuento con tesorería y la regla de empresa con descuento. */
  it('tesorería con descuento: manda el descuento, la empresa del punto de venta y el punto de venta; con otra empresa no sale', async () => {
    apiPostMock.mockResolvedValue(MOV_PAGO)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await evento.type(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/), '500')
    await agregarLinea(evento, dialogo, 1, '4', '6000')
    await evento.selectOptions(within(dialogo).getByLabelText('Origen del dinero'), 'Tesoreria')
    await evento.selectOptions(within(dialogo).getByLabelText('Empresa que paga'), '3')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    expect(
      await within(dialogo).findByText('Con descuento de cuenta corriente, la empresa tiene que ser la del punto de venta de la sesión.'),
    ).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Empresa que paga')).toHaveAttribute('aria-invalid', 'true')
    expect(apiPostMock).not.toHaveBeenCalled()

    await evento.selectOptions(within(dialogo).getByLabelText('Empresa que paga'), '2')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/personal/1/pagos', {
        fecha: HOY,
        descuentoDeCuentaCorriente: 500,
        origenFondos: 'Tesoreria',
        lineas: [{ idMedioPago: 4, importe: 6000 }],
        idEmpresa: 2,
        idPuntoVenta: 9,
      }),
    )
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('si solo fallan las empresas no ofrece la caja general, lo dice, y el pago desde la caja del turno sigue funcionando', async () => {
    mockearRutas({ empresas: () => Promise.reject(new ErrorApi(500, 'error', 'x')) })
    apiPostMock.mockResolvedValue(MOV_PAGO)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    expect(
      within(dialogo).getByText(
        'No se pudieron cargar las empresas: no se puede pagar desde la caja general. El pago desde la caja del turno y el descuento siguen disponibles.',
      ),
    ).toBeInTheDocument()
    expect(within(dialogo).getByRole('button', { name: 'Agregar línea' })).toBeEnabled()
    await agregarLinea(evento, dialogo, 1, '4', '1000')
    const origen = within(dialogo).getByLabelText('Origen del dinero')
    expect(within(origen).queryByRole('option', { name: 'Caja general' })).not.toBeInTheDocument()
    await evento.selectOptions(origen, 'CajaTurno')
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

  it('un 403 de un pago de tesorería se explica en el origen', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'texto del servidor'))
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await agregarLinea(evento, dialogo, 1, '4', '1000')
    await evento.selectOptions(within(dialogo).getByLabelText('Origen del dinero'), 'Tesoreria')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    expect(await within(dialogo).findByText('Pagar desde la tesorería requiere ser administrador.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Origen del dinero')).toHaveAttribute('aria-invalid', 'true')
  })

  /** Cláusula bajo prueba: el 403 solo va al origen cuando el origen enviado fue la tesorería. */
  it.each([
    ['con la caja del turno', async (evento: Evento, dialogo: HTMLElement) => {
      await agregarLinea(evento, dialogo, 1, '4', '1000')
      await evento.selectOptions(within(dialogo).getByLabelText('Origen del dinero'), 'CajaTurno')
    }],
    ['solo con descuento, sin campo de origen', async (evento: Evento, dialogo: HTMLElement) => {
      await evento.type(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/), '100')
    }],
  ])('un 403 de un pago %s va arriba del formulario, sin hablar de la tesorería', async (_caso, preparar) => {
    apiPostMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'texto del servidor'))
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await preparar(evento, dialogo)
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    expect(await within(dialogo).findByText('No tenés permiso para registrar esto.')).toBeInTheDocument()
    expect(within(dialogo).queryByText('Pagar desde la tesorería requiere ser administrador.')).not.toBeInTheDocument()
    expect(within(dialogo).queryByLabelText('Origen del dinero')?.getAttribute('aria-invalid')).not.toBe('true')
  })

  /** Cláusula bajo prueba: `desactualizaLaDeuda` en `escribir`, que relee al empleado y la deuda tras el rechazo. */
  it('si el descuento supera la deuda, la deuda se relee: el formulario muestra la nueva y su tope', async () => {
    let lecturasDeDeuda = 0
    mockearRutas({ cliente: () => Promise.resolve(cliente(++lecturasDeDeuda === 1 ? 750 : 300)) })
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'descuento_supera_la_deuda', 'texto del servidor'))
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)
    const antesEmpleado = lecturas('/personal/1').filter((c) => c[0] === '/personal/1').length

    await evento.type(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/), '700')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    expect(
      await within(dialogo).findByText('El descuento supera la deuda actual del cliente vinculado. Revisá la deuda que muestra este formulario.'),
    ).toBeInTheDocument()
    expect(await within(dialogo).findByText('Deuda actual: $ 300,00. El descuento no mueve dinero.')).toBeInTheDocument()
    expect(lecturas('/personal/1').filter((c) => c[0] === '/personal/1')).toHaveLength(antesEmpleado + 1)
    expect(screen.getByRole('dialog', { name: 'Registrar pago' })).toBeInTheDocument()

    await waitFor(() => expect(within(dialogo).getByRole('button', { name: 'Registrar pago' })).toBeEnabled())
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    expect(await within(dialogo).findByText('El descuento no puede superar la deuda del cliente ($ 300,00).')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/)).toBeEnabled()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: `puedeDescontar` en el descuento efectivo de `ModalDePago`. */
  it('si la deuda releída es cero, el descuento se limpia y se puede seguir pagando sin cerrar la modal', async () => {
    let lecturasDeDeuda = 0
    mockearRutas({ cliente: () => Promise.resolve(cliente(++lecturasDeDeuda === 1 ? 750 : 0)) })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'descuento_supera_la_deuda', 'x')).mockResolvedValue(MOV_PAGO)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await evento.type(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/), '700')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    expect(await within(dialogo).findByText('El cliente no tiene deuda que descontar.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/)).toBeDisabled()
    expect(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/)).toHaveValue('')
    await waitFor(() => expect(within(dialogo).getByRole('button', { name: 'Registrar pago' })).toBeEnabled())

    await agregarLinea(evento, dialogo, 1, '4', '1000')
    await evento.selectOptions(within(dialogo).getByLabelText('Origen del dinero'), 'CajaTurno')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenLastCalledWith('/personal/1/pagos', {
        fecha: HOY,
        origenFondos: 'CajaTurno',
        idPuntoVenta: 9,
        lineas: [{ idMedioPago: 4, importe: 1000 }],
      }),
    )
    expect(apiPostMock).toHaveBeenCalledTimes(2)
  })

  it('si el empleado ya no tiene cliente vinculado, se relee al empleado: el descuento desaparece y no viaja', async () => {
    let lecturasDelEmpleado = 0
    mockearRutas({
      empleado: () =>
        Promise.resolve(++lecturasDelEmpleado === 1 ? empleadoBase() : empleadoBase({ idCliente: null, nombreCliente: null })),
    })
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'personal_sin_cliente_vinculado', 'texto del servidor'))
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await abrirPago(evento)

    await evento.type(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/), '100')
    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    expect(
      await within(dialogo).findByText(
        'El empleado no tiene un cliente vinculado vigente para descontar de su cuenta corriente. Sacá el descuento o revisá el cliente vinculado.',
      ),
    ).toBeInTheDocument()
    await waitFor(() => expect(within(dialogo).queryByLabelText(/Descuento de la cuenta corriente/)).not.toBeInTheDocument())
    await waitFor(() => expect(within(dialogo).getByRole('button', { name: 'Registrar pago' })).toBeEnabled())

    await evento.click(within(dialogo).getByRole('button', { name: 'Registrar pago' }))

    expect(await within(dialogo).findByText('Cargá un descuento de cuenta corriente o al menos una línea de dinero.')).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: `leyendoDeuda`. Con la lectura en vuelo la deuda es `null`, igual que si hubiera fallado. */
  it('con la deuda todavía leyéndose, el formulario no dice que no se pudo leer', async () => {
    const lectura = diferido<ClienteListado>()
    mockearRutas({ cliente: () => lectura.promesa })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    const dialogo = await abrir(evento, 'Registrar pago')

    expect(within(dialogo).getByText('Leyendo la deuda del cliente…')).toBeInTheDocument()
    expect(within(dialogo).queryByText(/No se pudo leer la deuda del cliente/)).not.toBeInTheDocument()
    expect(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/)).toBeDisabled()

    await act(async () => {
      lectura.resolver(cliente(750))
      await lectura.promesa
    })

    expect(await within(dialogo).findByText('Deuda actual: $ 750,00. El descuento no mueve dinero.')).toBeInTheDocument()
    expect(within(dialogo).queryByText('Leyendo la deuda del cliente…')).not.toBeInTheDocument()
    expect(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/)).toBeEnabled()
  })
})

describe('CuentaDePersonal — el día de hoy se toma al abrir la modal', () => {
  /** Cláusula bajo prueba: `setHoy` en `abrir`. Una por modal: cada una recibe `hoy` por su cuenta. */
  it.each([
    ['Cargar horas', 'Día trabajado'],
    ['Ajuste manual', 'Fecha'],
    ['Registrar pago', 'Fecha del pago'],
  ])('"%s" arranca con el día en que se abre, no con el del montaje, aunque cruce la medianoche', async (boton, etiqueta) => {
    vi.useFakeTimers({ toFake: ['Date'] })
    try {
      vi.setSystemTime(new Date(2026, 9, 12, 23, 59, 0))
      const evento = userEvent.setup()
      renderCuenta()
      await esperarCuenta()

      vi.setSystemTime(new Date(2026, 9, 13, 0, 1, 0))
      const dialogo = await abrir(evento, boton)

      expect(within(dialogo).getByLabelText(etiqueta)).toHaveValue('13/10/2026')
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('CuentaDePersonal — una escritura de resultado incierto', () => {
  const FECHA_DE_HOY = HOY.split('-').reverse().join('/')
  /** Una fecha anterior a cualquier "desde" por defecto: la fila nueva quedaría fuera del rango activo. */
  const FECHA_VIEJA = { tipeada: '15012020', iso: '2020-01-15', texto: '15/01/2020' }

  type Escritor = {
    nombre: string
    boton: string
    envio: string
    etiquetaDeFecha: string
    /** Completa una operación entera en la modal ya abierta. */
    cargar: (evento: Evento, dialogo: HTMLElement) => Promise<void>
    descripcion: (fechaTexto: string) => string
    /** Lo que se afirma sobre una modal recién reabierta: nada de lo cargado antes sobrevive. */
    estaVacia: (dialogo: HTMLElement) => void
  }

  const ESCRITORES: Escritor[] = [
    {
      nombre: 'cargar horas',
      boton: 'Cargar horas',
      envio: 'Cargar horas',
      etiquetaDeFecha: 'Día trabajado',
      cargar: async (evento, dialogo) => {
        await evento.type(within(dialogo).getByLabelText('Horas'), '7,5')
      },
      descripcion: (fecha) => `carga de 7,50 horas del ${fecha} por $ 26.250,00`,
      estaVacia: (dialogo) => {
        expect(within(dialogo).getByLabelText('Horas')).toHaveValue('')
      },
    },
    {
      nombre: 'ajuste',
      boton: 'Ajuste manual',
      envio: 'Registrar ajuste',
      etiquetaDeFecha: 'Fecha',
      cargar: async (evento, dialogo) => {
        await elegirSentido(evento, dialogo, 'enContra')
        await evento.type(within(dialogo).getByLabelText('Importe'), '250')
        await evento.type(within(dialogo).getByLabelText(/^Detalle/), 'Corrección de una carga')
      },
      descripcion: (fecha) => `ajuste del ${fecha} por -$ 250,00`,
      estaVacia: (dialogo) => {
        expect(within(dialogo).getByLabelText('Importe')).toHaveValue('')
        expect(within(dialogo).getByLabelText(/^En contra del empleado/)).not.toBeChecked()
      },
    },
    {
      nombre: 'pago',
      boton: 'Registrar pago',
      envio: 'Registrar pago',
      etiquetaDeFecha: 'Fecha del pago',
      cargar: async (evento, dialogo) => {
        await evento.type(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/), '500')
        await evento.click(within(dialogo).getByRole('button', { name: 'Agregar línea' }))
        await evento.selectOptions(within(dialogo).getByLabelText('Medio de pago de la línea 1'), '4')
        await evento.type(within(dialogo).getByLabelText('Importe de la línea 1'), '6000')
        await evento.selectOptions(within(dialogo).getByLabelText('Origen del dinero'), 'CajaTurno')
      },
      descripcion: (fecha) => `pago del ${fecha} por $ 6.500,00`,
      estaVacia: (dialogo) => {
        expect(within(dialogo).getByLabelText(/^Descuento de la cuenta corriente/)).toHaveValue('')
        expect(within(dialogo).queryByLabelText('Medio de pago de la línea 1')).not.toBeInTheDocument()
      },
    },
  ]

  const avisoIncierto = (descripcion: string) =>
    `No se pudo confirmar si se registró esta operación: ${descripcion}. Revisá los movimientos que se muestran abajo antes de volver a cargarla.`

  /** La cuenta que ve el usuario antes del POST y la que ve cuando se relee: el saldo cambia. */
  function cuentaQueSeReleeConOtroSaldo() {
    let lecturasHechas = 0
    mockearRutas({
      cuenta: (ruta) =>
        Promise.resolve(
          paginaSegunLaRuta(ruta, ++lecturasHechas === 1 ? {} : { encabezado: { idPersonal: 1, nombre: 'Ana Gómez', saldo: 9000 } }),
        ),
    })
  }

  async function prepararEscritura(evento: Evento, escritor: Escritor, fecha?: string) {
    await esperarCuenta()
    await screen.findByText('Deuda: $ 750,00')
    const dialogo = await abrir(evento, escritor.boton)
    if (fecha) {
      await evento.clear(within(dialogo).getByLabelText(escritor.etiquetaDeFecha))
      await evento.type(within(dialogo).getByLabelText(escritor.etiquetaDeFecha), fecha)
    }
    await escritor.cargar(evento, dialogo)

    return dialogo
  }

  /** Cláusula bajo prueba: la rama de resultado incierto de `escribir`. Una por escritura: cada una arma su descripción. */
  it.each(ESCRITORES)('$nombre: un fallo de red cierra la modal, relee la cuenta y avisa sin confirmar, con la operación a la vista', async (escritor) => {
    cuentaQueSeReleeConOtroSaldo()
    apiPostMock.mockRejectedValue(new Error('Failed to fetch'))
    const evento = userEvent.setup()
    renderCuenta()
    const antesCuenta = () => lecturasDeCuenta().length
    const dialogo = await prepararEscritura(evento, escritor)
    const cuentaAntes = antesCuenta()
    const deudaAntes = lecturas('/clientes/21').length

    await evento.click(within(dialogo).getByRole('button', { name: escritor.envio }))

    const aviso = await screen.findByText(avisoIncierto(escritor.descripcion(FECHA_DE_HOY)))
    expect(aviso.closest('[role="alert"]')).toHaveClass('alert-warning')
    expect(aviso.closest('[role="alert"]')).not.toHaveClass('alert-success')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(await screen.findByText('$ 9.000,00')).toBeInTheDocument()
    expect(lecturasDeCuenta()).toHaveLength(cuentaAntes + 1)
    expect(lecturasDeCuenta().at(-1)?.[0]).toContain('pagina=1&')
    expect(lecturas('/clientes/21')).toHaveLength(deudaAntes + 1)
    expect(apiPostMock).toHaveBeenCalledTimes(1)

    const reabierta = await abrir(evento, escritor.boton)
    escritor.estaVacia(reabierta)
    expect(screen.getByText(avisoIncierto(escritor.descripcion(FECHA_DE_HOY)))).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it.each([
    ['un 500', new ErrorApi(500, 'error', 'Se cayó.')],
    ['un 503 resultado_incierto', new ErrorApi(503, 'resultado_incierto', 'Verificá la cuenta del empleado.')],
  ])('%s también es incierto: cierra la modal y avisa, sin mostrar el texto del servidor en un formulario', async (_caso, fallo) => {
    cuentaQueSeReleeConOtroSaldo()
    apiPostMock.mockRejectedValue(fallo)
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await prepararEscritura(evento, ESCRITORES[0])

    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    expect(await screen.findByText(avisoIncierto(ESCRITORES[0].descripcion(FECHA_DE_HOY)))).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.queryByText('Verificá la cuenta del empleado.')).not.toBeInTheDocument()
  })

  it('si la relectura también falla, el aviso lo dice y manda a recargar la pantalla', async () => {
    let lecturasHechas = 0
    mockearRutas({
      cuenta: (ruta) =>
        ++lecturasHechas === 1 ? Promise.resolve(paginaSegunLaRuta(ruta)) : Promise.reject(new ErrorApi(500, 'error', 'Se cayó la cuenta.')),
    })
    apiPostMock.mockRejectedValue(new Error('Failed to fetch'))
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await prepararEscritura(evento, ESCRITORES[0])

    await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

    const aviso = await screen.findByText(
      `No se pudo confirmar si se registró esta operación: ${ESCRITORES[0].descripcion(FECHA_DE_HOY)}. Tampoco se pudo actualizar la vista: recargá la pantalla y revisá los movimientos antes de volver a cargarla.`,
    )
    expect(aviso.closest('[role="alert"]')).toHaveClass('alert-warning')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el aviso se AGREGA a la lista; otra incierta, un éxito posterior, paginar o
   * filtrar no lo borran, y solo su propio "Ya lo revisé" lo quita. */
  it('dos escrituras inciertas dejan dos avisos; descartar uno conserva el otro y un éxito posterior no los toca', async () => {
    apiPostMock.mockRejectedValue(new Error('Failed to fetch'))
    mockearRutas({ cuenta: (ruta) => Promise.resolve(paginaSegunLaRuta(ruta, { total: 60 })) })
    const evento = userEvent.setup()
    renderCuenta()
    const horas = await prepararEscritura(evento, ESCRITORES[0])
    await evento.click(within(horas).getByRole('button', { name: 'Cargar horas' }))
    const primero = avisoIncierto(ESCRITORES[0].descripcion(FECHA_DE_HOY))
    await screen.findByText(primero)

    const ajuste = await abrir(evento, 'Ajuste manual')
    await ESCRITORES[1].cargar(evento, ajuste)
    await evento.click(within(ajuste).getByRole('button', { name: 'Registrar ajuste' }))
    const segundo = avisoIncierto(ESCRITORES[1].descripcion(FECHA_DE_HOY))
    await screen.findByText(segundo)
    expect(screen.getByText(primero)).toBeInTheDocument()

    apiPostMock.mockResolvedValue(MOV_HORAS)
    const otra = await abrir(evento, 'Cargar horas')
    await evento.type(within(otra).getByLabelText('Horas'), '8')
    await evento.click(within(otra).getByRole('button', { name: 'Cargar horas' }))
    await screen.findByText('Horas cargadas.')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Siguiente' })).toBeEnabled())
    await evento.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText('Página 2 de 3 — 60 movimiento(s)')
    expect(screen.getByText(primero)).toBeInTheDocument()
    expect(screen.getByText(segundo)).toBeInTheDocument()

    const entradaDelPrimero = screen.getByText(primero).closest('[role="alert"]') as HTMLElement
    await evento.click(within(entradaDelPrimero).getByRole('button', { name: 'Ya lo revisé' }))

    expect(screen.queryByText(primero)).not.toBeInTheDocument()
    expect(screen.getByText(segundo)).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `disabled={bloqueado}` del "Ya lo revisé". */
  it('mientras hay una escritura en vuelo, "Ya lo revisé" queda inerte', async () => {
    apiPostMock.mockRejectedValueOnce(new Error('Failed to fetch')).mockImplementation(() => new Promise(() => {}))
    const evento = userEvent.setup()
    renderCuenta()
    const horas = await prepararEscritura(evento, ESCRITORES[0])
    await evento.click(within(horas).getByRole('button', { name: 'Cargar horas' }))
    await screen.findByText(avisoIncierto(ESCRITORES[0].descripcion(FECHA_DE_HOY)))
    const revisado = screen.getByRole('button', { name: 'Ya lo revisé' })
    await waitFor(() => expect(revisado).toBeEnabled())

    const otra = await abrir(evento, 'Cargar horas')
    await evento.type(within(otra).getByLabelText('Horas'), '8')
    await evento.click(within(otra).getByRole('button', { name: 'Cargar horas' }))

    await waitFor(() => expect(revisado).toBeDisabled())
  })

  /** Cláusula bajo prueba: solo un 4xx deja la modal abierta; el incierto no. Una por escritura. */
  it.each(ESCRITORES)('$nombre: un rechazo cierto (400) deja la modal abierta con el error en su campo y no relee ni avisa de incertidumbre', async (escritor) => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'fecha_requerida', 'x'))
    const evento = userEvent.setup()
    renderCuenta()
    const dialogo = await prepararEscritura(evento, escritor)
    const cuentaAntes = lecturasDeCuenta().length

    await evento.click(within(dialogo).getByRole('button', { name: escritor.envio }))

    expect(await within(dialogo).findByText('Ingresá la fecha.')).toBeInTheDocument()
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    expect(screen.queryByText(/No se pudo confirmar/)).not.toBeInTheDocument()
    expect(lecturasDeCuenta()).toHaveLength(cuentaAntes)
  })

  describe('el movimiento cae fuera del rango activo', () => {
    function servidorQueEcoaElRango() {
      mockearRutas({ cuenta: (ruta) => Promise.resolve(paginaSegunLaRuta(ruta)) })
    }

    /** Cláusula bajo prueba: `ampliarRangoParaFecha` tras una escritura confirmada. */
    it('con una escritura confirmada, ensancha el "desde" hasta esa fecha, relee con ese rango y lo dice en el aviso', async () => {
      servidorQueEcoaElRango()
      apiPostMock.mockResolvedValue(MOV_HORAS)
      const evento = userEvent.setup()
      renderCuenta()
      const dialogo = await prepararEscritura(evento, ESCRITORES[0], FECHA_VIEJA.tipeada)

      await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

      expect(
        await screen.findByText(`Horas cargadas. Se amplió el rango del estado de cuenta para incluir el ${FECHA_VIEJA.texto}.`),
      ).toBeInTheDocument()
      expect(lecturasDeCuenta().at(-1)?.[0]).toBe(`/personal/1/cuenta?desde=${FECHA_VIEJA.iso}&pagina=1&tamanio=25`)
      expect(screen.getByLabelText('Desde')).toHaveValue(FECHA_VIEJA.texto)
    })

    it('con un resultado incierto, la relectura incluye también la fecha cargada y el aviso lo dice', async () => {
      servidorQueEcoaElRango()
      apiPostMock.mockRejectedValue(new Error('Failed to fetch'))
      const evento = userEvent.setup()
      renderCuenta()
      const dialogo = await prepararEscritura(evento, ESCRITORES[0], FECHA_VIEJA.tipeada)

      await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

      expect(
        await screen.findByText(
          `${avisoIncierto(ESCRITORES[0].descripcion(FECHA_VIEJA.texto))} Se amplió el rango del estado de cuenta para incluir el ${FECHA_VIEJA.texto}.`,
        ),
      ).toBeInTheDocument()
      expect(lecturasDeCuenta().at(-1)?.[0]).toBe(`/personal/1/cuenta?desde=${FECHA_VIEJA.iso}&pagina=1&tamanio=25`)
    })

    /** Cláusula bajo prueba: `ampliado && cuentaOk`, y los filtros que solo cambian si la cuenta se releyó. */
    it('si la relectura falla, no ensancha los filtros ni habla del rango', async () => {
      let lecturasHechas = 0
      mockearRutas({
        cuenta: (ruta) =>
          ++lecturasHechas === 1 ? Promise.resolve(paginaSegunLaRuta(ruta)) : Promise.reject(new ErrorApi(500, 'error', 'Se cayó la cuenta.')),
      })
      apiPostMock.mockRejectedValue(new Error('Failed to fetch'))
      const evento = userEvent.setup()
      renderCuenta()
      await esperarCuenta()
      const desdeAntes = (screen.getByLabelText('Desde') as HTMLInputElement).value
      const dialogo = await prepararEscritura(evento, ESCRITORES[0], FECHA_VIEJA.tipeada)

      await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

      expect(
        await screen.findByText(
          `No se pudo confirmar si se registró esta operación: ${ESCRITORES[0].descripcion(FECHA_VIEJA.texto)}. Tampoco se pudo actualizar la vista: recargá la pantalla y revisá los movimientos antes de volver a cargarla.`,
        ),
      ).toBeInTheDocument()
      expect(screen.getByLabelText('Desde')).toHaveValue(desdeAntes)
    })

    it('una fecha dentro del rango no lo toca ni lo menciona', async () => {
      servidorQueEcoaElRango()
      apiPostMock.mockResolvedValue(MOV_HORAS)
      const evento = userEvent.setup()
      renderCuenta()
      const dialogo = await prepararEscritura(evento, ESCRITORES[0])
      const desdeAntes = (screen.getByLabelText('Desde') as HTMLInputElement).value

      await evento.click(within(dialogo).getByRole('button', { name: 'Cargar horas' }))

      expect(await screen.findByText('Horas cargadas.')).toBeInTheDocument()
      expect(screen.getByLabelText('Desde')).toHaveValue(desdeAntes)
    })
  })
})

describe('CuentaDePersonal — orden en el servidor', () => {
  it('sin clic el pedido no lleva orden y el clic en un encabezado recorre ascendente, descendente y el orden del libro', async () => {
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    expect(lecturasDeCuenta().every((c) => !(c[0] as string).includes('orden='))).toBe(true)

    await evento.click(screen.getByRole('button', { name: /^Importe/ }))
    await waitFor(() => expect(lecturasDeCuenta().at(-1)?.[0]).toContain('orden=importe&direccion=ascendente'))
    expect(screen.getByRole('columnheader', { name: /Importe/ })).toHaveAttribute('aria-sort', 'ascending')

    await evento.click(screen.getByRole('button', { name: /^Importe/ }))
    await waitFor(() => expect(lecturasDeCuenta().at(-1)?.[0]).toContain('orden=importe&direccion=descendente'))

    await evento.click(screen.getByRole('button', { name: /^Importe/ }))
    await waitFor(() => expect(lecturasDeCuenta().at(-1)?.[0]).not.toContain('orden='))
    expect(screen.getByRole('columnheader', { name: /Importe/ })).toHaveAttribute('aria-sort', 'none')
  })

  it.each([
    ['Fecha', 'fecha'],
    ['Tipo', 'tipo'],
    ['Horas', 'horas'],
    ['Valor hora', 'valorHora'],
    ['Importe', 'importe'],
    ['Saldo resultante', 'saldoResultante'],
    ['Detalle', 'detalle'],
  ])('la columna %s ordena por la clave %s', async (titulo, clave) => {
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()

    await evento.click(screen.getByRole('button', { name: new RegExp(`^${titulo}`) }))

    await waitFor(() => expect(lecturasDeCuenta().at(-1)?.[0]).toContain(`orden=${clave}&direccion=ascendente`))
  })

  it('cambiar el orden desde la página 2 vuelve a la página 1 y conserva el rango', async () => {
    mockearRutas({ cuenta: (ruta) => Promise.resolve(paginaSegunLaRuta(ruta, { total: 60 })) })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    await evento.click(screen.getByLabelText('Ver histórico completo'))
    await screen.findByText('Página 1 de 3 — 60 movimiento(s)')
    await evento.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText('Página 2 de 3 — 60 movimiento(s)')

    await evento.click(screen.getByRole('button', { name: /^Saldo resultante/ }))

    await waitFor(() => {
      const ultima = lecturasDeCuenta().at(-1)?.[0] as string
      expect(ultima).toContain('orden=saldoResultante&direccion=ascendente')
      expect(ultima).toContain('pagina=1')
      expect(ultima).toContain('historico=true')
    })
    await screen.findByText('Página 1 de 3 — 60 movimiento(s)')
  })

  it('el orden sobrevive a un cambio de página', async () => {
    mockearRutas({ cuenta: (ruta) => Promise.resolve(paginaSegunLaRuta(ruta, { total: 60 })) })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()
    await evento.click(screen.getByRole('button', { name: /^Importe/ }))
    await waitFor(() => expect(lecturasDeCuenta().at(-1)?.[0]).toContain('orden=importe'))

    await evento.click(screen.getByRole('button', { name: 'Siguiente' }))

    await waitFor(() => {
      const ultima = lecturasDeCuenta().at(-1)?.[0] as string
      expect(ultima).toContain('orden=importe&direccion=ascendente')
      expect(ultima).toContain('pagina=2')
    })
  })

  it('si la consulta con orden falla, el encabezado conserva el orden que las filas tienen y el siguiente clic vuelve a pedir el mismo', async () => {
    let pedidasPorImporte = 0
    mockearRutas({
      cuenta: (ruta) =>
        ruta.includes('orden=importe') && ++pedidasPorImporte === 1
          ? Promise.reject(new ErrorApi(500, 'error', 'Se cayó el orden.'))
          : Promise.resolve(paginaSegunLaRuta(ruta)),
    })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()

    await evento.click(screen.getByRole('button', { name: /^Importe/ }))

    await screen.findByText('Se cayó el orden.')
    expect(screen.getByText('Turno mañana')).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: /Importe/ })).toHaveAttribute('aria-sort', 'none')

    await evento.click(screen.getByRole('button', { name: /^Importe/ }))

    await waitFor(() => expect(lecturasDeCuenta().at(-1)?.[0]).toContain('orden=importe&direccion=ascendente'))
    await waitFor(() => expect(screen.getByRole('columnheader', { name: /Importe/ })).toHaveAttribute('aria-sort', 'ascending'))
  })

  it('una respuesta stale de un orden anterior no pisa la del orden más nuevo ni su encabezado', async () => {
    const vieja = diferido<PaginaDeCuentaDePersonal>()
    mockearRutas({
      cuenta: (ruta) => {
        if (ruta.includes('orden=importe')) return vieja.promesa
        if (ruta.includes('orden=fecha')) {
          return Promise.resolve(paginaSegunLaRuta(ruta, { items: [movimiento({ idMovimiento: 9, detalle: 'La nueva' })], total: 1 }))
        }
        return Promise.resolve(paginaSegunLaRuta(ruta))
      },
    })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()

    await evento.click(screen.getByRole('button', { name: /^Importe/ }))
    await evento.click(screen.getByRole('button', { name: /^Fecha/ }))
    await screen.findByText('La nueva')

    await act(async () => {
      vieja.resolver(paginaDeCuenta({ items: [movimiento({ idMovimiento: 8, detalle: 'La vieja' })], total: 1 }))
      await vieja.promesa
    })

    expect(screen.getByText('La nueva')).toBeInTheDocument()
    expect(screen.queryByText('La vieja')).not.toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: /Fecha/ })).toHaveAttribute('aria-sort', 'ascending')
    expect(screen.getByRole('columnheader', { name: /Importe/ })).toHaveAttribute('aria-sort', 'none')
  })

  it('cada fila conserva el saldo resultante que manda el servidor: ordenar nunca lo recalcula', async () => {
    mockearRutas({
      cuenta: (ruta) =>
        Promise.resolve(
          ruta.includes('orden=importe')
            ? paginaSegunLaRuta(ruta, {
                items: [
                  movimiento({ idMovimiento: 5, importe: 100, saldoResultante: 987654, detalle: 'Primero por importe' }),
                  MOV_HORAS,
                ],
                total: 2,
              })
            : paginaSegunLaRuta(ruta),
        ),
    })
    const evento = userEvent.setup()
    renderCuenta()
    await esperarCuenta()

    await evento.click(screen.getByRole('button', { name: /^Importe/ }))

    const fila = (await screen.findByText('Primero por importe')).closest('tr')!
    expect(within(fila).getByText('$ 987.654,00')).toBeInTheDocument()
  })
})
