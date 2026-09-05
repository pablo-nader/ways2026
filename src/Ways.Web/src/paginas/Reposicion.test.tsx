import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
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

  it('sugerido renderiza 0 cuando es un sugerido genuino de 0 — nunca —', async () => {
    // Regression guard: un `!valor ? '—' : ...` en formatearCantidadNullable trataría
    // sugerido: 0 como falsy y renderizaría '—' (lectura "comprar nada" que la spec prohíbe).
    const filaConSugeridoCero = filaFixture({ idArticulo: 3, sugerido: 0 })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) {
        return Promise.resolve(reposicionFixture([filaConSugeridoCero]))
      }
      return undefined
    })
    renderReposicion()

    const filas = await screen.findAllByRole('row')
    // fila 0 = encabezado, fila 1 = header del grupo de proveedor, fila 2 = dato.
    const filaDato = filas[2]
    expect(within(filaDato).queryByText('—')).not.toBeInTheDocument()
    expect(within(filaDato).getByText('0')).toBeInTheDocument()
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

// stage-16-ordenes-de-compra, Slice 6 (design decisión 16, tasks.md decisión 24; mutation target
// #34c): el botón "Generar OC" por grupo — gateado a Admin, ausente en "Sin proveedor", y el
// mapeo reposición→OC probado de punta a punta contra la pantalla destino real (nunca un
// `useNavigate` mockeado: la lección del Link de la 15 es que el destino lee `location.state`, no
// un fetch propio).
function ProbeDeOrdenDeCompraNueva() {
  const location = useLocation()
  const state = location.state as { idProveedor: number; idPuntoVenta: number; items: { idArticulo: number; cantidadPedida: number }[] } | null
  if (state === null) return <div>Sin precarga</div>
  return (
    <div>
      <div>idProveedor={state.idProveedor}</div>
      <div>idPuntoVenta={state.idPuntoVenta}</div>
      <div>items={JSON.stringify(state.items)}</div>
    </div>
  )
}

function renderReposicionConDestino() {
  return render(
    <MemoryRouter initialEntries={['/reportes/stock/reposicion']}>
      <Routes>
        <Route path="/reportes/stock/reposicion" element={<Reposicion />} />
        <Route path="/ordenes-compra/nueva" element={<ProbeDeOrdenDeCompraNueva />} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('Reposicion — "Generar OC" (stage-16-ordenes-de-compra, Slice 6)', () => {
  it('un Admin ve el botón por grupo con proveedor; "Sin proveedor" nunca lo ofrece (mutation target #34c, parte 1)', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Admin, rol: 'Admin' })
    const filaConProveedor = filaFixture({ idArticulo: 1, idProveedor: 1, proveedor: 'Proveedor Uno' })
    const filaSinProveedor = filaFixture({ idArticulo: 2, idProveedor: null, proveedor: null })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) return Promise.resolve(reposicionFixture([filaConProveedor, filaSinProveedor]))
      return undefined
    })
    renderReposicionConDestino()

    const filaProveedorUno = (await screen.findByText('Proveedor Uno (1)')).closest('tr')!
    expect(within(filaProveedorUno).getByRole('button', { name: 'Generar OC' })).toBeInTheDocument()

    const filaSinProveedorHeader = screen.getByText('Sin proveedor (1)').closest('tr')!
    expect(within(filaSinProveedorHeader).queryByRole('button', { name: 'Generar OC' })).not.toBeInTheDocument()
  })

  it('un Supervisor no ve ningún botón "Generar OC" (mutation target #34c, parte 2) — la pantalla sigue igual', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Supervisor, rol: 'Supervisor' })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) {
        return Promise.resolve(reposicionFixture([filaFixture({ idProveedor: 1, proveedor: 'Proveedor Uno' })]))
      }
      return undefined
    })
    renderReposicionConDestino()

    await screen.findByText('Yerba mate 1kg')
    expect(screen.queryByRole('button', { name: 'Generar OC' })).not.toBeInTheDocument()
  })

  it('al click navega a /ordenes-compra/nueva con idProveedor/idPuntoVenta/items ya resueltos, excluyendo sugerido = null (mutation target #34c, parte 3)', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Admin, rol: 'Admin' })
    const filaConSugerido = filaFixture({ idArticulo: 1, idProveedor: 5, proveedor: 'Proveedor Cinco', sugerido: 17 })
    const filaSinSugerido = filaFixture({ idArticulo: 2, idProveedor: 5, proveedor: 'Proveedor Cinco', sugerido: null })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) return Promise.resolve(reposicionFixture([filaConSugerido, filaSinSugerido]))
      return undefined
    })
    const usuario = userEvent.setup()
    renderReposicionConDestino()

    await usuario.click(await screen.findByRole('button', { name: 'Generar OC' }))

    expect(await screen.findByText('idProveedor=5')).toBeInTheDocument()
    expect(screen.getByText('idPuntoVenta=10')).toBeInTheDocument()
    const items = JSON.parse(screen.getByText(/^items=/).textContent!.replace('items=', ''))
    expect(items).toHaveLength(1)
    expect(items[0].idArticulo).toBe(1)
  })
})
