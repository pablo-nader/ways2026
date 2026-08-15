import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Reposicion } from './Reposicion'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { FilaDeReposicion, PuntoVentaListado, Reposicion as ReposicionRespuesta, UsuarioAutenticado } from '../api/tipos'

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

function filaFixture(sobrescribir: Partial<FilaDeReposicion> = {}): FilaDeReposicion {
  return {
    idArticulo: 100,
    articulo: 'Yerba mate 1kg',
    cantidad: 3,
    minimo: 10,
    reposicion: 20,
    sugerido: 17,
    idProveedor: 1,
    proveedor: 'Proveedor Uno',
    consumoDiarioPromedio: null,
    diasDeCobertura: null,
    ...sobrescribir,
  }
}

function reposicionFixture(filas: FilaDeReposicion[], idPuntoVenta = 10): ReposicionRespuesta {
  return { idPuntoVenta, hoy: '2026-08-14', diasDeRotacion: 30, zonaHoraria: 'America/Argentina/Buenos_Aires', filas }
}

function mockearRutasBase(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaCentro, puntoVentaNorte])
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderReposicion() {
  return render(<Reposicion />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

/** Monta detrás del mismo gate de rol que `App.tsx` usa para
 * `/reportes/stock/reposicion` (`Politicas.LecturaDeReportes`). */
function renderReposicionProtegido() {
  return render(
    <MemoryRouter initialEntries={['/reportes/stock/reposicion']}>
      <Routes>
        <Route
          path="/reportes/stock/reposicion"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
              <Reposicion />
            </RutaProtegida>
          }
        />
        <Route path="/" element={<div>Inicio (redirigido)</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDescargarMock.mockReset()
  apiDescargarMock.mockResolvedValue(undefined)
  usuarioActual = usuarioFixture()
})

describe('Reposicion (stage-13-stock-inteligente, Slice 6 — web)', () => {
  it('arranca con el primer punto de venta cargado, sin ?dias= en la consulta', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) return Promise.resolve(reposicionFixture([filaFixture()]))
      return undefined
    })
    renderReposicion()

    await screen.findByLabelText('Punto de venta')
    expect(screen.getByLabelText('Punto de venta')).toHaveValue('10')
    const llamada = apiGetMock.mock.calls.find((call: unknown[]) => (call[0] as string).startsWith('/reportes/stock/reposicion?'))!
    expect(llamada[0] as string).toBe('/reportes/stock/reposicion?idPuntoVenta=10')
  })

  it('sugerido renderiza — cuando es null, nunca 0', async () => {
    const filaSinReposicionConfigurada = filaFixture({ idArticulo: 1, sugerido: null })
    const filaConSugerido = filaFixture({ idArticulo: 2, sugerido: 8 })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) {
        return Promise.resolve(reposicionFixture([filaSinReposicionConfigurada, filaConSugerido]))
      }
      return undefined
    })
    renderReposicion()

    const filas = await screen.findAllByRole('row')
    // fila 0 = encabezado, fila 1 = header del grupo de proveedor, filas 2/3 = datos.
    const filaUno = filas[2]
    const filaDos = filas[3]
    expect(within(filaUno).getByText('—')).toBeInTheDocument()
    expect(within(filaDos).queryByText('—')).not.toBeInTheDocument()
    expect(within(filaDos).getByText('8')).toBeInTheDocument()
  })

  it('agrupa por proveedor mostrando el nombre y la cantidad de filas en el encabezado del grupo', async () => {
    const filaUno = filaFixture({ idArticulo: 1, idProveedor: 1, proveedor: 'Proveedor Uno' })
    const filaDos = filaFixture({ idArticulo: 2, idProveedor: 1, proveedor: 'Proveedor Uno' })
    const filaSinProveedor = filaFixture({ idArticulo: 3, idProveedor: null, proveedor: null })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) {
        return Promise.resolve(reposicionFixture([filaUno, filaDos, filaSinProveedor]))
      }
      return undefined
    })
    renderReposicion()

    expect(await screen.findByText('Proveedor Uno (2)')).toBeInTheDocument()
    expect(screen.getByText('Sin proveedor (1)')).toBeInTheDocument()
  })

  it('el botón de descarga apunta a /reportes/stock/reposicion/export con el idPuntoVenta elegido', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) return Promise.resolve(reposicionFixture([filaFixture()]))
      return undefined
    })
    const usuario = userEvent.setup()
    renderReposicion()

    await screen.findByText('Yerba mate 1kg')
    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))

    expect(apiDescargarMock.mock.calls[0][0]).toMatch(/^\/reportes\/stock\/reposicion\/export\?idPuntoVenta=10/)
  })

  it('sin filas bajo el mínimo muestra un estado vacío', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) return Promise.resolve(reposicionFixture([]))
      return undefined
    })
    renderReposicion()

    expect(await screen.findByText('No hay artículos bajo el mínimo para este punto de venta.')).toBeInTheDocument()
  })

  it('una respuesta desactualizada nunca pisa la más reciente (generación)', async () => {
    let resolverPrimera: (valor: ReposicionRespuesta) => void = () => {}
    const primera = new Promise<ReposicionRespuesta>((resolve) => {
      resolverPrimera = resolve
    })
    let cantidadDeLlamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) {
        cantidadDeLlamadas += 1
        if (cantidadDeLlamadas === 1) return primera
        return Promise.resolve(reposicionFixture([filaFixture({ idArticulo: 999, articulo: 'segunda-respuesta' })]))
      }
      return undefined
    })

    const usuario = userEvent.setup()
    renderReposicion()
    await screen.findByLabelText('Punto de venta')

    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')
    expect(await screen.findByText('segunda-respuesta')).toBeInTheDocument()

    // El flush del microtask va DENTRO de act (mutation-proof-tests regla 7): un waitFor solo
    // pasaría en su primer tick, antes de que el .then stale aterrice.
    const { act } = await import('@testing-library/react')
    await act(async () => {
      resolverPrimera(reposicionFixture([filaFixture({ idArticulo: 1, articulo: 'primera-respuesta-vieja' })]))
      await primera
    })
    expect(screen.queryByText('primera-respuesta-vieja')).not.toBeInTheDocument()
    expect(screen.getByText('segunda-respuesta')).toBeInTheDocument()
  })
})

describe('Reposicion — role gating (mismo gate que Vencimientos: Politicas.LecturaDeReportes)', () => {
  it('un Supervisor llega a /reportes/stock/reposicion', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) return Promise.resolve(reposicionFixture([filaFixture()]))
      return undefined
    })
    renderReposicionProtegido()

    await screen.findByText('Yerba mate 1kg')
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
  })

  it('un Vendedor nunca llega a /reportes/stock/reposicion: redirige a Inicio', async () => {
    usuarioActual = usuarioFixture({ id: 4, usuario: 'vendedor', rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) return Promise.resolve(reposicionFixture([filaFixture()]))
      return undefined
    })

    renderReposicionProtegido()

    expect(await screen.findByText('Inicio (redirigido)')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Reposición')).not.toBeInTheDocument())
  })
})
