import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Vencimientos } from './Vencimientos'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { FilaDeVencimiento, PuntoVentaListado, UsuarioAutenticado, Vencimientos as VencimientosRespuesta } from '../api/tipos'

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

function filaFixture(sobrescribir: Partial<FilaDeVencimiento> = {}): FilaDeVencimiento {
  return {
    idArticulo: 100,
    articulo: 'Yogur bebible 1L',
    idLote: 41,
    codigoLote: '2026-08-20',
    fechaVencimiento: '2026-08-20',
    cantidad: 12,
    estado: 'Vigente',
    ...sobrescribir,
  }
}

function vencimientosFixture(filas: FilaDeVencimiento[] = [filaFixture()], idPuntoVenta = 10): VencimientosRespuesta {
  return { idPuntoVenta, hoy: '2026-08-14', diasDeAlerta: 30, zonaHoraria: 'America/Argentina/Buenos_Aires', filas }
}

function renderVencimientos() {
  return render(<Vencimientos />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

/** Monta detrás del mismo gate de rol que `App.tsx` usa para
 * `/reportes/stock/vencimientos` (`Politicas.LecturaDeReportes`). */
function renderVencimientosProtegido() {
  return render(
    <MemoryRouter initialEntries={['/reportes/stock/vencimientos']}>
      <Routes>
        <Route
          path="/reportes/stock/vencimientos"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
              <Vencimientos />
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
    if (ruta.startsWith('/reportes/stock/vencimientos?')) return Promise.resolve(vencimientosFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDescargarMock.mockReset()
  apiDescargarMock.mockResolvedValue(undefined)
  usuarioActual = usuarioFixture()
})

describe('Vencimientos — reporte (stage-12-lotes-vencimientos, Slice 15 — web)', () => {
  it('arranca con el primer punto de venta cargado y sin dias forzado (el servidor resuelve el default)', async () => {
    mockearRutasBase()
    renderVencimientos()

    await screen.findByLabelText('Punto de venta')
    expect(screen.getByLabelText('Punto de venta')).toHaveValue('10')

    const llamada = apiGetMock.mock.calls.find((call: unknown[]) => (call[0] as string).startsWith('/reportes/stock/vencimientos?'))!
    expect(llamada[0] as string).not.toContain('dias=')
  })

  it('un valor tipeado en "Días de alerta" viaja como dias en la query', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderVencimientos()

    await screen.findByLabelText('Punto de venta')
    apiGetMock.mockClear()
    mockearRutasBase()
    await usuario.type(screen.getByLabelText('Días de alerta'), '45')

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/stock/vencimientos?'))
      expect(llamadas.some((call: unknown[]) => (call[0] as string).includes('dias=45'))).toBe(true)
    })
  })

  it('renderiza las cuatro badges de estado, incluido sin_fecha para el lote sin identificar', async () => {
    const filaVencida = filaFixture({ idLote: 1, articulo: 'Art A', estado: 'Vencido' })
    const filaPorVencer = filaFixture({ idLote: 2, articulo: 'Art B', estado: 'PorVencer' })
    const filaVigente = filaFixture({ idLote: 3, articulo: 'Art C', estado: 'Vigente' })
    const filaSinFecha = filaFixture({ idLote: 4, articulo: 'Art D', estado: 'SinFecha', fechaVencimiento: null, codigoLote: 'SIN-IDENTIFICAR' })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/vencimientos?')) {
        return Promise.resolve(vencimientosFixture([filaVencida, filaPorVencer, filaVigente, filaSinFecha]))
      }
      return undefined
    })
    renderVencimientos()

    await screen.findByText('Art A')
    const filas = screen.getAllByRole('row').slice(1)
    expect(within(filas[0]).getByText('Vencido')).toBeInTheDocument()
    expect(within(filas[1]).getByText('Por vencer')).toBeInTheDocument()
    expect(within(filas[2]).getByText('Vigente')).toBeInTheDocument()
    expect(within(filas[3]).getByText('Sin fecha')).toBeInTheDocument()
    // el lote sin identificar SE INCLUYE en el reporte — nunca "—" silencioso en el código.
    expect(within(filas[3]).getByText('SIN-IDENTIFICAR')).toBeInTheDocument()
    expect(within(filas[3]).getByText('—')).toBeInTheDocument() // fecha de vencimiento null
  })

  it('sin lotes con saldo positivo muestra un estado vacío, no un re-query', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/vencimientos?')) return Promise.resolve(vencimientosFixture([]))
      return undefined
    })
    renderVencimientos()

    expect(await screen.findByText('No hay lotes con saldo positivo para este punto de venta.')).toBeInTheDocument()
    const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/stock/vencimientos?'))
    expect(llamadas).toHaveLength(1)
  })

  it('cambiar el punto de venta dispara una nueva consulta con el idPuntoVenta elegido', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderVencimientos()

    await screen.findByText('Yogur bebible 1L')
    apiGetMock.mockClear()
    mockearRutasBase()
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/stock/vencimientos?'))
      expect(llamadas.some((call: unknown[]) => (call[0] as string).includes('idPuntoVenta=11'))).toBe(true)
    })
  })

  it('el botón de descarga apunta a /reportes/stock/vencimientos/export con el idPuntoVenta elegido', async () => {
    mockearRutasBase()
    apiDescargarMock.mockRejectedValueOnce(new Error('no se pudo descargar'))
    const usuario = userEvent.setup()
    renderVencimientos()

    await screen.findByText('Yogur bebible 1L')
    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))

    expect(await screen.findByText('No se pudo descargar el archivo.')).toBeInTheDocument()
    expect(apiDescargarMock.mock.calls[0][0]).toMatch(/^\/reportes\/stock\/vencimientos\/export\?idPuntoVenta=10/)
  })

  it('una respuesta desactualizada nunca pisa la más reciente (generación)', async () => {
    let resolverPrimera: (valor: VencimientosRespuesta) => void = () => {}
    const primera = new Promise<VencimientosRespuesta>((resolve) => {
      resolverPrimera = resolve
    })
    let cantidadDeLlamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/vencimientos?')) {
        cantidadDeLlamadas += 1
        if (cantidadDeLlamadas === 1) return primera
        return Promise.resolve(vencimientosFixture([filaFixture({ idLote: 999, articulo: 'segunda-respuesta' })]))
      }
      return undefined
    })

    const usuario = userEvent.setup()
    renderVencimientos()
    await screen.findByLabelText('Punto de venta')

    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')
    expect(await screen.findByText('segunda-respuesta')).toBeInTheDocument()

    // El flush del microtask va DENTRO de act (mutation-proof-tests regla 7): un waitFor solo
    // pasaría en su primer tick, antes de que el .then stale aterrice.
    const { act } = await import('@testing-library/react')
    await act(async () => {
      resolverPrimera(vencimientosFixture([filaFixture({ idLote: 1, articulo: 'primera-respuesta-vieja' })]))
      await primera
    })
    expect(screen.queryByText('primera-respuesta-vieja')).not.toBeInTheDocument()
    expect(screen.getByText('segunda-respuesta')).toBeInTheDocument()
  })
})

describe('Vencimientos — role gating (mismo gate que Existencias: Politicas.LecturaDeReportes)', () => {
  it('un Supervisor llega a /reportes/stock/vencimientos', async () => {
    mockearRutasBase()
    renderVencimientosProtegido()

    await screen.findByText('Yogur bebible 1L')
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
  })

  it('un Vendedor nunca llega a /reportes/stock/vencimientos: redirige a Inicio', async () => {
    usuarioActual = usuarioFixture({ id: 4, usuario: 'vendedor', rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()

    renderVencimientosProtegido()

    expect(await screen.findByText('Inicio (redirigido)')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Vencimientos')).not.toBeInTheDocument())
  })
})
