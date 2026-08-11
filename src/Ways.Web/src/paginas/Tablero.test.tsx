import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Tablero } from './Tablero'
import { rangoUltimosSieteDias } from '../api/reportes'
import { ROL } from '../api/tipos'
import type { EmpresaListado, ResumenDeGastos, ResumenDeVentas, UsuarioAutenticado } from '../api/tipos'
import { RutaProtegida } from '../auth/RutaProtegida'

const apiGetMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
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

const empresaUno: EmpresaListado = { id: 1, idTenant: 1, razonSocial: 'Empresa Uno SA', nombreFantasia: null, cuit: null }

function ventasFixture(sobrescribir: Partial<ResumenDeVentas> = {}): ResumenDeVentas {
  return {
    desde: '2026-08-05',
    hasta: '2026-08-11',
    granularidad: 'Dia',
    zonaHoraria: 'America/Argentina/Buenos_Aires',
    serie: [{ etiqueta: '05/08', inicio: '2026-08-05', neto: 1000, cantidadTx: 4, ticketPromedio: 250 }],
    netoVendido: 1000,
    cantidadTx: 4,
    ticketPromedio: 250,
    cantidadNcx: 0,
    netoNcx: 0,
    ...sobrescribir,
  }
}

function gastosFixture(sobrescribir: Partial<ResumenDeGastos> = {}): ResumenDeGastos {
  return {
    desde: '2026-08-05',
    hasta: '2026-08-11',
    granularidad: 'Dia',
    zonaHoraria: 'America/Argentina/Buenos_Aires',
    serie: [{ etiqueta: '05/08', inicio: '2026-08-05', importe: 300 }],
    importeTotal: 300,
    porCategoria: [],
    ...sobrescribir,
  }
}

function mockearRutasBase(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/empresas') return Promise.resolve([empresaUno])
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    if (ruta.startsWith('/reportes/ventas/resumen?')) return Promise.resolve(ventasFixture())
    if (ruta.startsWith('/reportes/gastos/resumen?')) return Promise.resolve(gastosFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderTablero() {
  return render(<Tablero />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

/** Monta detrás del mismo gate de rol que `App.tsx` usa para `/tablero`
 * (`Politicas.LecturaDeReportes`: Supervisor + Admin). */
function renderTableroProtegido() {
  return render(
    <MemoryRouter initialEntries={['/tablero']}>
      <Routes>
        <Route
          path="/tablero"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
              <Tablero />
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
  usuarioActual = usuarioFixture()
})

describe('Tablero — G1 parity (stage-10-agregacion-dashboard, Slice 7)', () => {
  it('por defecto carga el rango de los últimos 7 días y muestra ventas, gastos y ticket promedio', async () => {
    mockearRutasBase()
    renderTablero()

    const rangoEsperado = rangoUltimosSieteDias()
    await screen.findByText('Empresa Uno SA')
    expect(screen.getByLabelText('Desde')).toHaveValue(rangoEsperado.desde)
    expect(screen.getByLabelText('Hasta')).toHaveValue(rangoEsperado.hasta)

    expect(await screen.findByText('$1.000,00')).toBeInTheDocument() // ventas netas
    expect(screen.getByText('$300,00')).toBeInTheDocument() // gastos
    expect(screen.getByText('$250,00')).toBeInTheDocument() // ticket promedio
    expect(screen.getByRole('img', { name: 'Serie de ventas netas por período' })).toBeInTheDocument()
    expect(screen.getByRole('img', { name: 'Serie de gastos por período' })).toBeInTheDocument()
  })

  it('un ticket promedio null se muestra como "—", nunca como $0,00', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/ventas/resumen?')) {
        return Promise.resolve(ventasFixture({ ticketPromedio: null, cantidadTx: 0 }))
      }
      return undefined
    })
    renderTablero()

    await screen.findByText('Empresa Uno SA')
    expect(await screen.findByText('—')).toBeInTheDocument()
    expect(screen.queryByText('$0,00')).not.toBeInTheDocument()
  })

  it('una respuesta desactualizada nunca pisa el rango ya cambiado (generación)', async () => {
    let resolverPrimeraVentas: (valor: ResumenDeVentas) => void = () => {}
    const primeraVentas = new Promise<ResumenDeVentas>((resolve) => {
      resolverPrimeraVentas = resolve
    })
    let llamadasAVentas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/ventas/resumen?')) {
        llamadasAVentas += 1
        if (llamadasAVentas === 1) return primeraVentas
        return Promise.resolve(ventasFixture({ netoVendido: 9999 }))
      }
      return undefined
    })

    renderTablero()
    await screen.findByText('Empresa Uno SA')
    expect(screen.getByLabelText('Desde')).toBeInTheDocument()

    // Cambia "Hasta" ANTES de que la primera consulta (rango de 7 días por defecto) resuelva.
    fireEvent.change(screen.getByLabelText('Hasta'), { target: { value: '2026-08-20' } })

    expect(await screen.findByText('$9.999,00')).toBeInTheDocument()

    // La primera respuesta, ahora obsoleta, resuelve tarde — no debe pisar el rango ya cambiado.
    resolverPrimeraVentas(ventasFixture({ netoVendido: 1 }))
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(screen.queryByText('$1,00')).not.toBeInTheDocument()
    expect(screen.getByText('$9.999,00')).toBeInTheDocument()
  })

  it('un error del servidor muestra un estado de reintento, no un crash', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/ventas/resumen?')) {
        return Promise.reject(new (class extends Error {
          estado = 500
          codigo = 'error_interno'
        })('No se pudo cargar el tablero.'))
      }
      return undefined
    })
    renderTablero()

    await screen.findByText('Empresa Uno SA')
    expect(await screen.findByText('No se pudo cargar el tablero.')).toBeInTheDocument()
    const botonReintentar = screen.getByRole('button', { name: 'Reintentar' })
    expect(botonReintentar).toBeInTheDocument()

    // El reintento vuelve a pedir el reporte — ya no debe quedar la pantalla rota.
    mockearRutasBase()
    fireEvent.click(botonReintentar)

    expect(await screen.findByText('$1.000,00')).toBeInTheDocument()
    expect(screen.queryByText('No se pudo cargar el tablero.')).not.toBeInTheDocument()
  })

  it('un Supervisor llega a la ruta protegida', async () => {
    mockearRutasBase()
    renderTableroProtegido()

    expect(await screen.findByText('$1.000,00')).toBeInTheDocument()
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
  })

  it('un Vendedor nunca llega a /tablero: redirige a Inicio', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()

    renderTableroProtegido()

    expect(await screen.findByText('Inicio (redirigido)')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Empresa Uno SA')).not.toBeInTheDocument())
  })

  it('un Root nunca llega a /tablero: redirige a Inicio', async () => {
    usuarioActual = usuarioFixture({ id: 1, usuario: 'root', rolId: ROL.Root, rol: 'Root', idTenant: null })
    mockearRutasBase()

    renderTableroProtegido()

    expect(await screen.findByText('Inicio (redirigido)')).toBeInTheDocument()
  })
})
