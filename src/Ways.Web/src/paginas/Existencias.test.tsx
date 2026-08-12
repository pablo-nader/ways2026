import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Existencias } from './Existencias'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { Existencias as ExistenciasRespuesta, FilaExistencia, PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

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
}

function filaFixture(sobrescribir: Partial<FilaExistencia> = {}): FilaExistencia {
  return { idArticulo: 100, nombre: 'Yerba mate 1kg', cantidad: 42.5, ...sobrescribir }
}

function existenciasFixture(filas: FilaExistencia[] = [filaFixture()], idPuntoVenta = 10): ExistenciasRespuesta {
  return { idPuntoVenta, filas }
}

function renderExistencias() {
  return render(<Existencias />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

/** Monta detrás del mismo gate de rol que `App.tsx` usa para `/reportes/existencias`
 * (`Politicas.LecturaDeReportes`). */
function renderExistenciasProtegido() {
  return render(
    <MemoryRouter initialEntries={['/reportes/existencias']}>
      <Routes>
        <Route
          path="/reportes/existencias"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
              <Existencias />
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
    if (ruta.startsWith('/reportes/stock/existencias?')) return Promise.resolve(existenciasFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDescargarMock.mockReset()
  apiDescargarMock.mockResolvedValue(undefined)
  usuarioActual = usuarioFixture()
})

describe('Existencias — reporte (stage-11-exportacion-reportes, Slice 9 — web)', () => {
  it('arranca con el primer punto de venta cargado', async () => {
    mockearRutasBase()
    renderExistencias()

    await screen.findByLabelText('Punto de venta')
    expect(screen.getByLabelText('Punto de venta')).toHaveValue('10')
  })

  it('sin stock cargado muestra un estado vacío, no un re-query', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/existencias?')) return Promise.resolve(existenciasFixture([]))
      return undefined
    })
    renderExistencias()

    expect(await screen.findByText('No hay stock cargado para este punto de venta.')).toBeInTheDocument()
    const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/stock/existencias?'))
    expect(llamadas).toHaveLength(1)
  })

  it('renderiza las filas devueltas por el backend, sin idArticulo faltante en la consulta', async () => {
    const filaUno = filaFixture({ idArticulo: 1, nombre: 'Aceite de girasol 900ml', cantidad: 12 })
    const filaDos = filaFixture({ idArticulo: 2, nombre: 'Fideos guiseros 500g', cantidad: 87.5 })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/existencias?')) return Promise.resolve(existenciasFixture([filaUno, filaDos]))
      return undefined
    })
    renderExistencias()

    await screen.findByText('Aceite de girasol 900ml')
    const filas = screen.getAllByRole('row').slice(1) // sin la fila de encabezado
    expect(within(filas[0]).getByText('Aceite de girasol 900ml')).toBeInTheDocument()
    expect(within(filas[0]).getByText('12')).toBeInTheDocument()
    expect(within(filas[1]).getByText('Fideos guiseros 500g')).toBeInTheDocument()
    expect(within(filas[1]).getByText('87,5')).toBeInTheDocument()

    const llamada = apiGetMock.mock.calls.find((call: unknown[]) => (call[0] as string).startsWith('/reportes/stock/existencias?'))!
    expect(llamada[0] as string).not.toContain('idArticulo')
  })

  it('cambiar el punto de venta dispara una nueva consulta con el idPuntoVenta elegido', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Yerba mate 1kg')
    apiGetMock.mockClear()
    mockearRutasBase()
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/stock/existencias?'))
      expect(llamadas.some((call: unknown[]) => (call[0] as string).includes('idPuntoVenta=11'))).toBe(true)
    })
  })

  it('el botón de descarga apunta a /reportes/stock/existencias/export con el idPuntoVenta elegido', async () => {
    mockearRutasBase()
    apiDescargarMock.mockRejectedValueOnce(new Error('no se pudo descargar'))
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Yerba mate 1kg')
    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))

    expect(await screen.findByText('No se pudo descargar el archivo.')).toBeInTheDocument()
    expect(apiDescargarMock.mock.calls[0][0]).toMatch(/^\/reportes\/stock\/existencias\/export\?idPuntoVenta=10/)
  })

  it('una respuesta desactualizada nunca pisa la más reciente (generación)', async () => {
    let resolverPrimera: (valor: ExistenciasRespuesta) => void = () => {}
    const primera = new Promise<ExistenciasRespuesta>((resolve) => {
      resolverPrimera = resolve
    })
    let cantidadDeLlamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/existencias?')) {
        cantidadDeLlamadas += 1
        if (cantidadDeLlamadas === 1) return primera
        return Promise.resolve(existenciasFixture([filaFixture({ idArticulo: 999, nombre: 'segunda-respuesta' })]))
      }
      return undefined
    })

    const usuario = userEvent.setup()
    renderExistencias()
    await screen.findByLabelText('Punto de venta')

    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')
    expect(await screen.findByText('segunda-respuesta')).toBeInTheDocument()

    // El flush del microtask va DENTRO de act: un waitFor solo pasaría en su primer tick,
    // antes de que el .then stale aterrice, y saldría verde sin probar nada.
    await act(async () => {
      resolverPrimera(existenciasFixture([filaFixture({ idArticulo: 1, nombre: 'primera-respuesta-vieja' })]))
      await primera
    })
    expect(screen.queryByText('primera-respuesta-vieja')).not.toBeInTheDocument()
    expect(screen.getByText('segunda-respuesta')).toBeInTheDocument()
  })
})

describe('Existencias — role gating (spec: A Supervisor Exports Existencias)', () => {
  it('un Supervisor llega a /reportes/existencias', async () => {
    mockearRutasBase()
    renderExistenciasProtegido()

    await screen.findByText('Yerba mate 1kg')
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
  })

  it('un Vendedor nunca llega a /reportes/existencias: redirige a Inicio', async () => {
    usuarioActual = usuarioFixture({ id: 4, usuario: 'vendedor', rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()

    renderExistenciasProtegido()

    expect(await screen.findByText('Inicio (redirigido)')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Existencias')).not.toBeInTheDocument())
  })
})
