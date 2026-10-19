import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SeccionDeCuentas } from './SeccionDeCuentas'
import { ErrorApi } from '../../api/cliente'
import { ROL } from '../../api/tipos'
import type {
  CuentaConSaldo,
  CuentasDeLaEmpresa,
  LibroDeCuenta,
  MovimientoDeCuentaListado,
  UsuarioAutenticado,
} from '../../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
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

function usuarioFixture(rolId: number): UsuarioAutenticado {
  return {
    id: 9,
    usuario: 'usuario',
    mail: 'usuario@ways.test',
    rolId,
    rol: 'Rol',
    ultimaConexion: null,
    idTenant: 1,
  }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture(ROL.Admin)

vi.mock('../../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

afterEach(cleanup)

function diferido<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((res, rej) => {
    resolver = res
    rechazar = rej
  })

  return { promesa, resolver, rechazar }
}

function cuenta(sobrescribir: Partial<CuentaConSaldo> = {}): CuentaConSaldo {
  return {
    id: 1,
    nombre: 'Mercado Pago',
    activo: true,
    saldoInicial: 0,
    saldoActual: 1500,
    fechaUltimoMovimiento: '2026-10-10T12:00:00Z',
    ...sobrescribir,
  }
}

function empresaConCuentas(cuentas: CuentaConSaldo[], medios: { id: number; nombre: string }[] = []): CuentasDeLaEmpresa {
  return { idEmpresa: 1, cuentas, mediosSinCuenta: medios }
}

function fila(sobrescribir: Partial<MovimientoDeCuentaListado> = {}): MovimientoDeCuentaListado {
  return {
    id: 10,
    fecha: '2026-10-10T12:00:00Z',
    tipo: 'Cobro',
    concepto: 'Cobros con QR - turno 4',
    ingreso: 1000,
    egreso: 0,
    final: 1000,
    idTurnoCaja: 4,
    idMedioPago: 3,
    nombreMedioPago: 'QR',
    idEmpleado: 9,
    nombreEmpleado: 'ana',
    baseBruta: null,
    porcentajeAplicado: null,
    ...sobrescribir,
  }
}

function libro(sobrescribir: Partial<LibroDeCuenta> = {}): LibroDeCuenta {
  const items = sobrescribir.items ?? [fila()]
  return {
    idCuenta: 1,
    nombre: 'Mercado Pago',
    activo: true,
    saldoActual: 1500,
    saldoDeApertura: 200,
    saldoAlCierre: 1500,
    totales: {
      cobrado: 1000,
      comision: 50,
      retencionIibb: 10,
      retencionIva: 5,
      retencionGanancias: 0,
      ajustes: 365,
      otros: 0,
      neto: 1300,
    },
    items,
    total: items.length,
    pagina: 1,
    tamanio: 25,
    ...sobrescribir,
  }
}

function rutaDeLibro(ruta: string): number | null {
  const m = /^\/reportes\/tesoreria\/cuentas\/(\d+)\/libro/.exec(ruta)
  return m ? Number(m[1]) : null
}

function montar(idEmpresa: number | null = 1) {
  return render(<SeccionDeCuentas idEmpresa={idEmpresa} />, {
    wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter>,
  })
}

function llamadasA(prefijo: string) {
  return apiGetMock.mock.calls.filter((c: unknown[]) => (c[0] as string).startsWith(prefijo))
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  usuarioActual = usuarioFixture(ROL.Admin)
})

describe('SeccionDeCuentas — cuentas', () => {
  it('sin empresa elegida pide elegirla y no consulta nada', () => {
    montar(null)

    expect(screen.getByText('Elegí una empresa para ver sus cuentas.')).toBeInTheDocument()
    expect(apiGetMock).not.toHaveBeenCalled()
  })

  it('muestra el saldo y el último movimiento de cada cuenta, marca las inactivas y avisa de los medios sin cuenta', async () => {
    apiGetMock.mockResolvedValue(
      empresaConCuentas(
        [
          cuenta(),
          cuenta({ id: 2, nombre: 'Banco', activo: false, saldoActual: -20.5, fechaUltimoMovimiento: null }),
        ],
        [{ id: 8, nombre: 'QR' }],
      ),
    )
    montar()

    expect(await screen.findByRole('button', { name: /Mercado Pago/ })).toHaveTextContent('$ 1.500,00')
    expect(screen.getByRole('button', { name: /Banco/ })).toHaveTextContent('(inactiva)')
    expect(screen.getByRole('button', { name: /Banco/ })).toHaveTextContent('-$ 20,50')
    expect(screen.getByRole('button', { name: /Banco/ })).toHaveTextContent('Sin movimientos')
    expect(screen.getByText('Lo cobrado con el medio QR no entra a ninguna cuenta.')).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenCalledWith('/reportes/tesoreria/cuentas?idEmpresa=1')
  })

  it('sin cuentas lo dice y no inventa un aviso', async () => {
    apiGetMock.mockResolvedValue(empresaConCuentas([]))
    montar()

    expect(await screen.findByText('Esta empresa todavía no tiene cuentas de tesorería.')).toBeInTheDocument()
    expect(screen.queryByText(/no entra a ninguna cuenta/)).not.toBeInTheDocument()
  })

  it('el enlace al ABM de cuentas es solo del Admin', async () => {
    apiGetMock.mockResolvedValue(empresaConCuentas([cuenta()]))
    const { unmount } = montar()
    await screen.findByRole('button', { name: /Mercado Pago/ })
    expect(screen.getByRole('link', { name: 'Administrar cuentas' })).toHaveAttribute('href', '/caja/cuentas-tesoreria')
    unmount()

    usuarioActual = usuarioFixture(ROL.Supervisor)
    montar()
    await screen.findByRole('button', { name: /Mercado Pago/ })
    expect(screen.queryByRole('link', { name: 'Administrar cuentas' })).not.toBeInTheDocument()
  })

  it('un error de carga se muestra y Reintentar vuelve a pedir', async () => {
    apiGetMock.mockRejectedValueOnce(new ErrorApi(500, 'x', 'Falló la lectura'))
    apiGetMock.mockResolvedValue(empresaConCuentas([cuenta()]))
    const usuario = userEvent.setup()
    montar()

    expect(await screen.findByText('Falló la lectura')).toBeInTheDocument()
    await usuario.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(await screen.findByRole('button', { name: /Mercado Pago/ })).toBeInTheDocument()
    expect(screen.queryByText('Falló la lectura')).not.toBeInTheDocument()
  })

  it('una respuesta tardía de la empresa anterior no pisa a la nueva', async () => {
    const vieja = diferido<CuentasDeLaEmpresa>()
    const nueva = diferido<CuentasDeLaEmpresa>()
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.endsWith('idEmpresa=1') ? vieja.promesa : ruta.endsWith('idEmpresa=2') ? nueva.promesa : Promise.reject(new Error(ruta)),
    )
    const { rerender } = montar(1)

    rerender(<SeccionDeCuentas idEmpresa={2} />)
    await act(async () => {
      nueva.resolver(empresaConCuentas([cuenta({ id: 20, nombre: 'Cuenta nueva' })]))
    })
    await screen.findByRole('button', { name: /Cuenta nueva/ })

    await act(async () => {
      vieja.resolver(empresaConCuentas([cuenta({ id: 10, nombre: 'Cuenta vieja' })]))
    })

    expect(screen.getByRole('button', { name: /Cuenta nueva/ })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Cuenta vieja/ })).not.toBeInTheDocument()
  })
})

describe('SeccionDeCuentas — libro', () => {
  function mockearCuentasYLibros(libros: Record<number, Promise<LibroDeCuenta> | LibroDeCuenta>) {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/reportes/tesoreria/cuentas?')) {
        return Promise.resolve(empresaConCuentas([cuenta(), cuenta({ id: 2, nombre: 'Banco', saldoActual: 300 })]))
      }
      const id = rutaDeLibro(ruta)
      if (id !== null && libros[id] !== undefined) return Promise.resolve(libros[id])
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
  }

  it('elegir una cuenta trae su libro con apertura, totales y filas; el medio dado de baja no se confunde con "sin medio"', async () => {
    mockearCuentasYLibros({
      1: libro({
        items: [
          fila(),
          fila({ id: 11, tipo: 'Comision', concepto: 'Comisión de QR', ingreso: 0, egreso: 50, final: 950, porcentajeAplicado: 5, baseBruta: 1000 }),
          fila({ id: 12, idMedioPago: 4, nombreMedioPago: null, nombreEmpleado: null, idEmpleado: 33 }),
          fila({ id: 13, tipo: 'Ajuste', idMedioPago: null, nombreMedioPago: null, idTurnoCaja: null }),
        ],
      }),
    })
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))

    const totales = await screen.findByLabelText('Totales del período')
    expect(within(totales).getByText('Saldo de apertura').nextElementSibling).toHaveTextContent('$ 200,00')
    expect(within(totales).getByText('Bruto cobrado').nextElementSibling).toHaveTextContent('$ 1.000,00')
    expect(within(totales).getByText('Descuentos').nextElementSibling).toHaveTextContent('$ 65,00')
    expect(within(totales).getByText('Neto del período').nextElementSibling).toHaveTextContent('$ 1.300,00')
    expect(within(totales).getByText('Saldo al cierre').nextElementSibling).toHaveTextContent('$ 1.500,00')

    const filas = screen.getAllByRole('row')
    expect(within(filas[1]).getByText('QR')).toBeInTheDocument()
    expect(within(filas[2]).getByText('5 % sobre $ 1.000,00')).toBeInTheDocument()
    expect(within(filas[3]).getByText('(no disponible)')).toBeInTheDocument()
    expect(within(filas[3]).getByText('Empleado #33')).toBeInTheDocument()
    expect(within(filas[4]).getAllByText('—').length).toBeGreaterThan(0)
    expect(llamadasA('/reportes/tesoreria/cuentas/1/libro')[0][0]).toMatch(/desde=.+&hasta=.+&pagina=1&tamanio=25/)
  })

  it('cambiar de cuenta limpia el libro anterior al instante y una respuesta tardía de la anterior se descarta', async () => {
    const libroA = diferido<LibroDeCuenta>()
    const libroB = diferido<LibroDeCuenta>()
    mockearCuentasYLibros({ 1: libroA.promesa, 2: libroB.promesa })
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))
    await usuario.click(screen.getByRole('button', { name: /Banco/ }))

    await act(async () => {
      libroB.resolver(libro({ idCuenta: 2, nombre: 'Banco', items: [fila({ id: 21, concepto: 'Fila del banco' })] }))
    })
    await screen.findByText('Fila del banco')

    await act(async () => {
      libroA.resolver(libro({ idCuenta: 1, items: [fila({ id: 22, concepto: 'Fila de la otra cuenta' })] }))
    })

    expect(screen.getByText('Fila del banco')).toBeInTheDocument()
    expect(screen.queryByText('Fila de la otra cuenta')).not.toBeInTheDocument()
    expect(screen.getByText('Libro de Banco')).toBeInTheDocument()
  })

  it('al cambiar de cuenta no se ve el libro de la anterior mientras carga el nuevo', async () => {
    const libroB = diferido<LibroDeCuenta>()
    mockearCuentasYLibros({ 1: libro({ items: [fila({ concepto: 'Fila de A' })] }), 2: libroB.promesa })
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))
    await screen.findByText('Fila de A')
    await usuario.click(screen.getByRole('button', { name: /Banco/ }))

    expect(screen.queryByText('Fila de A')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Totales del período')).not.toBeInTheDocument()

    await act(async () => {
      libroB.resolver(libro({ idCuenta: 2, nombre: 'Banco' }))
    })
  })

  it('cambiar el rango pide el libro de nuevo desde la página 1 y descarta la respuesta del rango anterior', async () => {
    const primero = diferido<LibroDeCuenta>()
    const segundo = diferido<LibroDeCuenta>()
    let pedidos = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/reportes/tesoreria/cuentas?')) return Promise.resolve(empresaConCuentas([cuenta()]))
      if (rutaDeLibro(ruta) === 1) {
        pedidos += 1
        return pedidos === 1 ? primero.promesa : segundo.promesa
      }
      return Promise.reject(new Error(ruta))
    })
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))
    const desde = screen.getByLabelText('Desde')
    await usuario.clear(desde)
    await usuario.type(desde, '01/10/2026')

    await waitFor(() => expect(pedidos).toBeGreaterThanOrEqual(2))
    const ultimo = llamadasA('/reportes/tesoreria/cuentas/1/libro').at(-1)![0] as string
    expect(decodeURIComponent(ultimo)).toContain('desde=2026-10-01T00:00:00')
    expect(ultimo).toContain('pagina=1')

    await act(async () => {
      segundo.resolver(libro({ items: [fila({ concepto: 'Del rango nuevo' })] }))
    })
    await screen.findByText('Del rango nuevo')

    await act(async () => {
      primero.resolver(libro({ items: [fila({ id: 50, concepto: 'Del rango viejo' })] }))
    })

    expect(screen.getByText('Del rango nuevo')).toBeInTheDocument()
    expect(screen.queryByText('Del rango viejo')).not.toBeInTheDocument()
  })

  it('la paginación pide la página siguiente de la misma cuenta', async () => {
    mockearCuentasYLibros({ 1: libro({ items: [fila()], total: 60 }) })
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))
    await screen.findByText(/Página 1 de 3/)
    await usuario.click(screen.getByRole('button', { name: 'Página siguiente del libro de la cuenta' }))

    await waitFor(() => {
      const ultimo = llamadasA('/reportes/tesoreria/cuentas/1/libro').at(-1)![0] as string
      expect(ultimo).toContain('pagina=2')
    })
  })

  it('un libro vacío lo dice y un error del libro se muestra con Reintentar', async () => {
    let falla = true
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/reportes/tesoreria/cuentas?')) return Promise.resolve(empresaConCuentas([cuenta()]))
      if (rutaDeLibro(ruta) === 1) {
        if (falla) return Promise.reject(new ErrorApi(500, 'x', 'Falló el libro'))
        return Promise.resolve(libro({ items: [], total: 0 }))
      }
      return Promise.reject(new Error(ruta))
    })
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))
    expect(await screen.findByText('Falló el libro')).toBeInTheDocument()

    falla = false
    await usuario.click(screen.getAllByRole('button', { name: 'Reintentar' })[0])

    expect(await screen.findByText('No hay movimientos en este período.')).toBeInTheDocument()
    expect(screen.queryByText('Falló el libro')).not.toBeInTheDocument()
  })
})

describe('SeccionDeCuentas — ajuste de saldo', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  function mockearConLibro() {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/reportes/tesoreria/cuentas?')) return Promise.resolve(empresaConCuentas([cuenta()]))
      if (rutaDeLibro(ruta) === 1) return Promise.resolve(libro())
      return Promise.reject(new Error(ruta))
    })
  }

  it('"Ajustar saldo" es solo del Admin', async () => {
    mockearConLibro()
    usuarioActual = usuarioFixture(ROL.Supervisor)
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))
    await screen.findByLabelText('Totales del período')

    expect(screen.queryByRole('button', { name: 'Ajustar saldo' })).not.toBeInTheDocument()
  })

  async function ajustarPorSaldoReal(usuario: ReturnType<typeof userEvent.setup>) {
    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))
    await usuario.click(await screen.findByRole('button', { name: 'Ajustar saldo' }))
    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1750')
    await usuario.type(screen.getByLabelText('Concepto'), 'Conciliación')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))
  }

  it('al registrar el ajuste cierra el modal, confirma y vuelve a leer las cuentas y el libro', async () => {
    // El ajuste (10/10) tiene que caer dentro del rango por defecto, que se calcula con la fecha actual.
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date(2026, 9, 12, 12, 0))
    mockearConLibro()
    apiPostMock.mockResolvedValue({
      saldoActual: 1750,
      movimiento: fila({ id: 99, tipo: 'Ajuste', fecha: new Date().toISOString(), final: 1750 }),
    })
    const usuario = userEvent.setup()
    montar()
    const antesCuentas = () => llamadasA('/reportes/tesoreria/cuentas?').length
    const antesLibro = () => llamadasA('/reportes/tesoreria/cuentas/1/libro').length

    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))
    await screen.findByLabelText('Totales del período')
    const cuentasAntes = antesCuentas()
    const libroAntes = antesLibro()

    const cuentasConLista = cuentasAntes
    await usuario.click(screen.getByRole('button', { name: 'Ajustar saldo' }))
    // El modal relee el saldo por la lectura de cuentas (no por el libro).
    await waitFor(() => expect(antesCuentas()).toBe(cuentasConLista + 1))
    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1750')
    await usuario.type(screen.getByLabelText('Concepto'), 'Conciliación')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText('Ajuste registrado. Saldo de Mercado Pago: $ 1.750,00.')).toBeInTheDocument()
    expect(screen.queryByRole('radio', { name: 'Tengo el saldo real' })).not.toBeInTheDocument()
    await waitFor(() => expect(antesCuentas()).toBe(cuentasConLista + 2))
    await waitFor(() => expect(antesLibro()).toBe(libroAntes + 1))
    expect(apiPostMock).toHaveBeenCalledWith('/cuentas-tesoreria/1/ajustes', { concepto: 'Conciliación', saldoObjetivo: 1750 })
  })

  it('un resultado incierto cierra el modal, avisa que hay que verificar y vuelve a leer sin reintentar la escritura', async () => {
    mockearConLibro()
    apiPostMock.mockRejectedValue(new ErrorApi(503, 'resultado_incierto', 'No se pudo confirmar.'))
    const usuario = userEvent.setup()
    montar()

    await ajustarPorSaldoReal(usuario)

    expect(await screen.findByText(/No se pudo confirmar el ajuste de Mercado Pago/)).toBeInTheDocument()
    expect(screen.queryByRole('radio', { name: 'Tengo el saldo real' })).not.toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledTimes(1)

    await usuario.click(screen.getByRole('button', { name: 'Ya lo revisé' }))
    expect(screen.queryByText(/No se pudo confirmar el ajuste/)).not.toBeInTheDocument()
  })

  it('un rechazo cierto deja el modal abierto y no relee nada', async () => {
    mockearConLibro()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'ajuste_sin_diferencia', 'Sin diferencia'))
    const usuario = userEvent.setup()
    montar()
    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))
    await screen.findByLabelText('Totales del período')
    await usuario.click(screen.getByRole('button', { name: 'Ajustar saldo' }))
    await waitFor(() => expect(llamadasA('/reportes/tesoreria/cuentas?').length).toBe(2))
    const cuentasAntes = llamadasA('/reportes/tesoreria/cuentas?').length
    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1750')
    await usuario.type(screen.getByLabelText('Concepto'), 'Conciliación')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText(/ya coincide con el saldo informado/)).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Tengo el saldo real' })).toBeInTheDocument()
    expect(llamadasA('/reportes/tesoreria/cuentas?').length).toBe(cuentasAntes)
  })
})

describe('SeccionDeCuentas — rango por defecto con el reloj fijado', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  function mockearConLibro() {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/reportes/tesoreria/cuentas?')) return Promise.resolve(empresaConCuentas([cuenta()]))
      if (rutaDeLibro(ruta) === 1) return Promise.resolve(libro())
      return Promise.reject(new Error(ruta))
    })
    apiPostMock.mockImplementation(() => {
      const fecha = new Date().toISOString()
      return Promise.resolve({ saldoActual: 1750, movimiento: fila({ id: 99, tipo: 'Ajuste', fecha, final: 1750 }) })
    })
  }

  async function ajustar(usuario: ReturnType<typeof userEvent.setup>) {
    await usuario.click(screen.getByRole('button', { name: 'Ajustar saldo' }))
    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1750')
    await usuario.type(screen.getByLabelText('Concepto'), 'Conciliación')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))
  }

  function ultimoLibro(): string {
    return decodeURIComponent(llamadasA('/reportes/tesoreria/cuentas/1/libro').at(-1)![0] as string)
  }

  it('sin editar el rango, un refresco posterior a medianoche vuelve a calcular "hasta" con el día nuevo', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date(2026, 9, 12, 23, 50))
    mockearConLibro()
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))
    await screen.findByLabelText('Totales del período')
    expect(ultimoLibro()).toContain('hasta=2026-10-12T23:59:59.999')

    vi.setSystemTime(new Date(2026, 9, 13, 0, 10))
    await ajustar(usuario)
    await screen.findByText(/Ajuste registrado/)

    await waitFor(() => expect(ultimoLibro()).toContain('hasta=2026-10-13T23:59:59.999'))
    expect(ultimoLibro()).toContain('desde=2026-10-07T00:00:00')
    expect(screen.queryByText(/quedó fuera del rango mostrado/)).not.toBeInTheDocument()
  })

  it('con el rango editado se respeta y, si el ajuste cae fuera, se avisa', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date(2026, 9, 12, 23, 50))
    mockearConLibro()
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))
    await screen.findByLabelText('Totales del período')
    const desde = screen.getByLabelText('Desde')
    await usuario.clear(desde)
    await usuario.type(desde, '01/10/2026')
    await waitFor(() => expect(ultimoLibro()).toContain('desde=2026-10-01T00:00:00'))

    vi.setSystemTime(new Date(2026, 9, 13, 0, 10))
    await ajustar(usuario)

    expect(await screen.findByText(/El ajuste quedó fuera del rango mostrado/)).toBeInTheDocument()
    await waitFor(() => expect(ultimoLibro()).toContain('hasta=2026-10-12T23:59:59.999'))
  })

  it('con el rango por defecto, un ajuste fechado después de hoy (cola futura) avisa que quedó fuera', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date(2026, 9, 12, 12, 0))
    mockearConLibro()
    apiPostMock.mockResolvedValue({
      saldoActual: 1750,
      movimiento: fila({ id: 99, tipo: 'Ajuste', fecha: new Date(2026, 9, 15, 9, 0).toISOString(), final: 1750 }),
    })
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))
    await screen.findByLabelText('Totales del período')
    await ajustar(usuario)

    expect(await screen.findByText(/El ajuste quedó fuera del rango mostrado/)).toBeInTheDocument()
  })

  it('con el rango editado que sí incluye el ajuste no hay aviso', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date(2026, 9, 12, 12, 0))
    mockearConLibro()
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await screen.findByRole('button', { name: /Mercado Pago/ }))
    await screen.findByLabelText('Totales del período')
    const desde = screen.getByLabelText('Desde')
    await usuario.clear(desde)
    await usuario.type(desde, '01/10/2026')
    await ajustar(usuario)

    expect(await screen.findByText(/Ajuste registrado/)).toBeInTheDocument()
    expect(screen.queryByText(/quedó fuera del rango mostrado/)).not.toBeInTheDocument()
  })
})
