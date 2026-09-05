import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Tesoreria } from './Tesoreria'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { MovimientoTesoreriaListado, PaginaDeMovimientosTesoreria, PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiDescargarMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
    descargar: (...args: unknown[]) => apiDescargarMock(...(args as [string])),
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

const puntoVentaCentro: PuntoVentaListado = {
  id: 10,
  idTenant: 1,
  idEmpresa: 1,
  nombre: 'PV Centro',
  domicilio: null,
  horario: null,
  whatsapp: null,
  instagram: null,
  facebook: null,
  web: null,
  nombreTenant: 'Tenant Demo',
  razonSocialEmpresa: 'Empresa Demo',
}

const puntoVentaNorte: PuntoVentaListado = {
  id: 11,
  idTenant: 1,
  idEmpresa: 1,
  nombre: 'PV Norte',
  domicilio: null,
  horario: null,
  whatsapp: null,
  instagram: null,
  facebook: null,
  web: null,
  nombreTenant: 'Tenant Demo',
  razonSocialEmpresa: 'Empresa Demo',
}

function movimientoFixture(sobrescribir: Partial<MovimientoTesoreriaListado> = {}): MovimientoTesoreriaListado {
  return {
    id: 60,
    idPuntoVenta: 10,
    fecha: '2026-08-05T08:00:00Z',
    tipo: 'Deposito',
    idTurnoCaja: 412,
    concepto: 'Apertura de turno',
    inicio: 0,
    ingreso: 60,
    egreso: 0,
    final: 60,
    idEmpleado: 4,
    ...sobrescribir,
  }
}

function paginaFixture(items: MovimientoTesoreriaListado[] = [movimientoFixture()], sobrescribir: Partial<PaginaDeMovimientosTesoreria> = {}): PaginaDeMovimientosTesoreria {
  return { items, total: items.length, pagina: 1, tamanio: 25, ...sobrescribir }
}

function renderTesoreria() {
  return render(<Tesoreria />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

/** Monta detrás del mismo gate de rol que `App.tsx` usa para `/caja/tesoreria`
 * (`Politicas.LecturaDeReportes`). */
function renderTesoreriaProtegido() {
  return render(
    <MemoryRouter initialEntries={['/caja/tesoreria']}>
      <Routes>
        <Route
          path="/caja/tesoreria"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
              <Tesoreria />
            </RutaProtegida>
          }
        />
        <Route path="/" element={<div>Inicio (redirigido)</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

function mockearRutasBase(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaCentro, puntoVentaNorte])
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    if (ruta.startsWith('/reportes/tesoreria?')) return Promise.resolve(paginaFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDescargarMock.mockReset()
  apiDescargarMock.mockResolvedValue(undefined)
  usuarioActual = usuarioFixture()
})

describe('Tesoreria — libro (stage-11-exportacion-reportes, Slice 7 — web)', () => {
  it('arranca con el primer punto de venta cargado, nunca la opción "Todos"', async () => {
    mockearRutasBase()
    renderTesoreria()

    await screen.findByLabelText('Punto de venta')
    expect(screen.getByLabelText('Punto de venta')).toHaveValue('10')
    expect(screen.queryByText('Todos')).not.toBeInTheDocument()
  })

  it('un libro vacío muestra un estado vacío, no un re-query', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/tesoreria?')) return Promise.resolve(paginaFixture([]))
      return undefined
    })
    renderTesoreria()

    expect(await screen.findByText('No hay movimientos que coincidan con los filtros.')).toBeInTheDocument()
    const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/tesoreria?'))
    expect(llamadas).toHaveLength(1)
  })

  // Las filas se renderizan en el MISMO orden que llegan del backend (OrderBy(m => m.Id), spec
  // tesoreria: Book Preserves Chain Order) — sin ningún sort del lado del cliente. Tres filas
  // encadenadas con `final` values 60, 100, 145 (mismo fixture que el test de backend).
  it('renderiza las filas en el orden de cadena que devuelve el backend, con cada columna', async () => {
    const filaUno = movimientoFixture({ id: 60, inicio: 5, ingreso: 55, egreso: 0, final: 60, concepto: 'Apertura', idEmpleado: 4 })
    const filaDos = movimientoFixture({ id: 61, inicio: 60, ingreso: 40, egreso: 0, final: 100, concepto: 'Depósito', idEmpleado: 5 })
    const filaTres = movimientoFixture({ id: 62, inicio: 100, ingreso: 0, egreso: 45, final: 55, concepto: 'Retiro', idEmpleado: 4 })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/tesoreria?')) return Promise.resolve(paginaFixture([filaUno, filaDos, filaTres]))
      return undefined
    })
    renderTesoreria()

    await screen.findByText('Apertura')
    const filas = screen.getAllByRole('row').slice(1) // sin la fila de encabezado
    expect(within(filas[0]).getByText('Apertura')).toBeInTheDocument()
    expect(within(filas[0]).getByText('$60,00')).toBeInTheDocument()
    expect(within(filas[1]).getByText('Depósito')).toBeInTheDocument()
    expect(within(filas[1]).getByText('$100,00')).toBeInTheDocument()
    expect(within(filas[2]).getByText('Retiro')).toBeInTheDocument()
    expect(within(filas[2]).getByText('$55,00')).toBeInTheDocument()
  })

  it('cambiar el punto de venta dispara una nueva consulta con el idPuntoVenta elegido', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderTesoreria()

    await screen.findByText('Apertura de turno')
    apiGetMock.mockClear()
    mockearRutasBase()
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/tesoreria?'))
      expect(llamadas.some((call: unknown[]) => (call[0] as string).includes('idPuntoVenta=11'))).toBe(true)
    })
  })

  it('el botón de descarga apunta a /reportes/tesoreria/export con idPuntoVenta obligatorio', async () => {
    mockearRutasBase()
    apiDescargarMock.mockRejectedValueOnce(new Error('no se pudo descargar'))
    const usuario = userEvent.setup()
    renderTesoreria()

    await screen.findByText('Apertura de turno')
    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))

    expect(await screen.findByText('No se pudo descargar el archivo.')).toBeInTheDocument()
    expect(apiDescargarMock.mock.calls[0][0]).toMatch(/^\/reportes\/tesoreria\/export\?idPuntoVenta=10/)
  })

  it('una respuesta de listado desactualizada nunca pisa la más reciente (generación)', async () => {
    let resolverPrimera: (valor: PaginaDeMovimientosTesoreria) => void = () => {}
    const primera = new Promise<PaginaDeMovimientosTesoreria>((resolve) => {
      resolverPrimera = resolve
    })
    let cantidadDeLlamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/tesoreria?')) {
        cantidadDeLlamadas += 1
        if (cantidadDeLlamadas === 1) return primera
        return Promise.resolve(paginaFixture([movimientoFixture({ id: 999, concepto: 'segunda-respuesta' })]))
      }
      return undefined
    })

    const usuario = userEvent.setup()
    renderTesoreria()
    await screen.findByLabelText('Punto de venta')

    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')
    expect(await screen.findByText('segunda-respuesta')).toBeInTheDocument()

    resolverPrimera(paginaFixture([movimientoFixture({ id: 1, concepto: 'primera-respuesta-vieja' })]))
    await waitFor(() => expect(screen.queryByText('primera-respuesta-vieja')).not.toBeInTheDocument())
    expect(screen.getByText('segunda-respuesta')).toBeInTheDocument()
  })
})

describe('Tesoreria — role gating (spec: A Supervisor Reads The G2 Listing And The G3 Book)', () => {
  it('un Supervisor llega a /caja/tesoreria', async () => {
    mockearRutasBase()
    renderTesoreriaProtegido()

    await screen.findByText('Apertura de turno')
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
  })

  it('un Vendedor nunca llega a /caja/tesoreria: redirige a Inicio', async () => {
    usuarioActual = usuarioFixture({ id: 4, usuario: 'vendedor', rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()

    renderTesoreriaProtegido()

    expect(await screen.findByText('Inicio (redirigido)')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Tesorería')).not.toBeInTheDocument())
  })
})
