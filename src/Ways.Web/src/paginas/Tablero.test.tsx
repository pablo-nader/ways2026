import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Tablero } from './Tablero'
import { rangoUltimosSieteDias } from '../api/reportes'
import { ROL } from '../api/tipos'
import type {
  EmpresaListado,
  MedioPagoListado,
  PuntoVentaListado,
  ResumenDeGastos,
  ResumenDeVentas,
  UsuarioAutenticado,
  VentasPorMedioPago,
  VentasPorPuntoVenta,
  VentasPorVendedor,
} from '../api/tipos'
import { RutaProtegida } from '../auth/RutaProtegida'

const apiGetMock = vi.fn()

// `recharts` se mockea igual que en los tests de los wrappers: bajo jsdom no renderiza
// nada observable, y sin el stub el mapeo de datos al grafico queda sin asercion posible.
// `BarChart`/`Bar` (Slice 8, paneles de desglose) se agregan al mismo mock que `LineChart`.
vi.mock('recharts', () => ({
  ResponsiveContainer: ({ children }: { children: import('react').ReactNode }) => (
    <div data-testid="responsive-container">{children}</div>
  ),
  LineChart: ({ data, children }: { data: unknown[]; children: import('react').ReactNode }) => (
    <div data-testid="line-chart" data-serie={JSON.stringify(data)}>
      {children}
    </div>
  ),
  Line: () => null,
  BarChart: ({ data, children }: { data: unknown[]; children: import('react').ReactNode }) => (
    <div data-testid="bar-chart" data-serie={JSON.stringify(data)}>
      {children}
    </div>
  ),
  Bar: () => null,
  XAxis: () => null,
  YAxis: () => null,
  Tooltip: () => null,
  CartesianGrid: () => null,
}))

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

function ventasPorPuntoVentaFixture(sobrescribir: Partial<VentasPorPuntoVenta> = {}): VentasPorPuntoVenta {
  return {
    desde: '2026-08-05',
    hasta: '2026-08-11',
    zonaHoraria: 'America/Argentina/Buenos_Aires',
    filas: [{ idPuntoVenta: 10, neto: 1500, cantidadTx: 5, ticketPromedio: 500 }],
    ...sobrescribir,
  }
}

function ventasPorVendedorFixture(sobrescribir: Partial<VentasPorVendedor> = {}): VentasPorVendedor {
  return {
    desde: '2026-08-05',
    hasta: '2026-08-11',
    zonaHoraria: 'America/Argentina/Buenos_Aires',
    filas: [{ idEmpleado: 9, neto: 800, cantidadTx: 3, ticketPromedio: 266.67 }],
    ...sobrescribir,
  }
}

function ventasPorMedioPagoFixture(sobrescribir: Partial<VentasPorMedioPago> = {}): VentasPorMedioPago {
  return {
    desde: '2026-08-05',
    hasta: '2026-08-11',
    zonaHoraria: 'America/Argentina/Buenos_Aires',
    filas: [{ idMedioPago: 1, neto: 1200, cantidadPagos: 4 }],
    ...sobrescribir,
  }
}

function mockearRutasBase(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/empresas') return Promise.resolve([empresaUno])
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaCentro])
    if (ruta === '/catalogos/medios-pago') return Promise.resolve([medioEfectivo])
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    if (ruta.startsWith('/reportes/ventas/resumen?')) return Promise.resolve(ventasFixture())
    if (ruta.startsWith('/reportes/gastos/resumen?')) return Promise.resolve(gastosFixture())
    if (ruta.startsWith('/reportes/ventas/por-punto-venta?')) return Promise.resolve(ventasPorPuntoVentaFixture())
    if (ruta.startsWith('/reportes/ventas/por-vendedor?')) return Promise.resolve(ventasPorVendedorFixture())
    if (ruta.startsWith('/reportes/ventas/por-medio-pago?')) return Promise.resolve(ventasPorMedioPagoFixture())
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

  // Prueba la guarda `if (generacionRef.current !== miGeneracion) return` del `.then` de
  // `cargar` en Tablero.tsx (mutation-proof-tests): quitando esa línea este test falla
  // (verificado — $1,00 de la respuesta obsoleta queda en pantalla en vez de $9.999,00),
  // revertida vuelve a pasar.
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

  it('mapea neto e importe como valor de las series de ventas y gastos', async () => {
    mockearRutasBase()
    renderTablero()

    await waitFor(() => {
      const charts = screen.getAllByTestId('line-chart')
      expect(charts).toHaveLength(2)
      expect(JSON.parse(charts[0].dataset.serie ?? '[]')).toEqual([
        { etiqueta: '05/08', valor: 1000 },
      ])
      expect(JSON.parse(charts[1].dataset.serie ?? '[]')).toEqual([
        { etiqueta: '05/08', valor: 300 },
      ])
    })
  })
})

describe('Tablero — Paneles de desglose por dimensión (stage-10-agregacion-dashboard, Slice 8)', () => {
  // Prueba el mapeo `valor: f.neto` de los tres paneles (mutation-proof-tests): mismo precedente
  // que la slice 7 (`neto` → `cantidadTx`). Mutación aplicada a los tres paneles (`Tablero.tsx`,
  // `data={datos.filas.map(...)}`) → cambiado `f.neto` por `f.cantidadTx`/`f.cantidadPagos` →
  // esta aserción falló (300/5/4 en vez de 1500/800/1200) → revertido → vuelve a pasar.
  it('cada panel manda su GraficoDeBarras con neto como valor, etiquetado por su propia dimensión', async () => {
    mockearRutasBase()
    renderTablero()

    await screen.findByText('Empresa Uno SA')

    await waitFor(() => {
      expect(screen.getAllByTestId('bar-chart')).toHaveLength(3)
    })

    const barras = screen.getAllByTestId('bar-chart')
    expect(JSON.parse(barras[0].dataset.serie ?? '[]')).toEqual([{ etiqueta: 'PV Centro', valor: 1500 }])
    expect(JSON.parse(barras[1].dataset.serie ?? '[]')).toEqual([{ etiqueta: 'Vendedor #9', valor: 800 }])
    expect(JSON.parse(barras[2].dataset.serie ?? '[]')).toEqual([{ etiqueta: 'Efectivo', valor: 1200 }])
  })

  // Prueba la guarda de generación DE CADA PANEL (`usePanelDeReporte`, react-async-state regla 2):
  // no es el par compartido de la card G1 (deviation registrada en tasks.md antes de la 7.6).
  // Mutación aplicada UNA VEZ en el hook compartido (`if (generacionRef.current !== miGeneracion)
  // return` borrada de las tres ramas) → los tres tests de esta sección fallaron (la respuesta
  // obsoleta de cada panel pisó el valor ya re-scopeado) → revertido → los tres vuelven a pasar.
  it('el panel por punto de venta descarta una respuesta obsoleta cuando "Hasta" cambia antes de que resuelva', async () => {
    let resolverPrimera: (valor: VentasPorPuntoVenta) => void = () => {}
    const primera = new Promise<VentasPorPuntoVenta>((resolve) => {
      resolverPrimera = resolve
    })
    let llamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/ventas/por-punto-venta?')) {
        llamadas += 1
        if (llamadas === 1) return primera
        return Promise.resolve(ventasPorPuntoVentaFixture({ filas: [{ idPuntoVenta: 10, neto: 9999, cantidadTx: 1, ticketPromedio: 9999 }] }))
      }
      return undefined
    })

    renderTablero()
    await screen.findByText('Empresa Uno SA')

    fireEvent.change(screen.getByLabelText('Hasta'), { target: { value: '2026-08-20' } })

    await waitFor(() => {
      const barras = screen.getAllByTestId('bar-chart')
      expect(JSON.parse(barras[0].dataset.serie ?? '[]')).toEqual([{ etiqueta: 'PV Centro', valor: 9999 }])
    })

    resolverPrimera(ventasPorPuntoVentaFixture({ filas: [{ idPuntoVenta: 10, neto: 1, cantidadTx: 1, ticketPromedio: 1 }] }))
    await new Promise((resolve) => setTimeout(resolve, 0))

    const barras = screen.getAllByTestId('bar-chart')
    expect(JSON.parse(barras[0].dataset.serie ?? '[]')).toEqual([{ etiqueta: 'PV Centro', valor: 9999 }])
  })

  it('el panel por vendedor descarta una respuesta obsoleta cuando "Hasta" cambia antes de que resuelva', async () => {
    let resolverPrimera: (valor: VentasPorVendedor) => void = () => {}
    const primera = new Promise<VentasPorVendedor>((resolve) => {
      resolverPrimera = resolve
    })
    let llamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/ventas/por-vendedor?')) {
        llamadas += 1
        if (llamadas === 1) return primera
        return Promise.resolve(ventasPorVendedorFixture({ filas: [{ idEmpleado: 9, neto: 9999, cantidadTx: 1, ticketPromedio: 9999 }] }))
      }
      return undefined
    })

    renderTablero()
    await screen.findByText('Empresa Uno SA')

    fireEvent.change(screen.getByLabelText('Hasta'), { target: { value: '2026-08-20' } })

    await waitFor(() => {
      const barras = screen.getAllByTestId('bar-chart')
      expect(JSON.parse(barras[1].dataset.serie ?? '[]')).toEqual([{ etiqueta: 'Vendedor #9', valor: 9999 }])
    })

    resolverPrimera(ventasPorVendedorFixture({ filas: [{ idEmpleado: 9, neto: 1, cantidadTx: 1, ticketPromedio: 1 }] }))
    await new Promise((resolve) => setTimeout(resolve, 0))

    const barras = screen.getAllByTestId('bar-chart')
    expect(JSON.parse(barras[1].dataset.serie ?? '[]')).toEqual([{ etiqueta: 'Vendedor #9', valor: 9999 }])
  })

  it('el panel por medio de pago descarta una respuesta obsoleta cuando "Hasta" cambia antes de que resuelva', async () => {
    let resolverPrimera: (valor: VentasPorMedioPago) => void = () => {}
    const primera = new Promise<VentasPorMedioPago>((resolve) => {
      resolverPrimera = resolve
    })
    let llamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/ventas/por-medio-pago?')) {
        llamadas += 1
        if (llamadas === 1) return primera
        return Promise.resolve(ventasPorMedioPagoFixture({ filas: [{ idMedioPago: 1, neto: 9999, cantidadPagos: 1 }] }))
      }
      return undefined
    })

    renderTablero()
    await screen.findByText('Empresa Uno SA')

    fireEvent.change(screen.getByLabelText('Hasta'), { target: { value: '2026-08-20' } })

    await waitFor(() => {
      const barras = screen.getAllByTestId('bar-chart')
      expect(JSON.parse(barras[2].dataset.serie ?? '[]')).toEqual([{ etiqueta: 'Efectivo', valor: 9999 }])
    })

    resolverPrimera(ventasPorMedioPagoFixture({ filas: [{ idMedioPago: 1, neto: 1, cantidadPagos: 1 }] }))
    await new Promise((resolve) => setTimeout(resolve, 0))

    const barras = screen.getAllByTestId('bar-chart')
    expect(JSON.parse(barras[2].dataset.serie ?? '[]')).toEqual([{ etiqueta: 'Efectivo', valor: 9999 }])
  })

  // Prueba `construirQueryDeBreakdown` vs `construirQueryDeBreakdownConPv` end-to-end (design:
  // Endpoints — "idPuntoVenta en todo menos por-punto-venta, sería una contradicción"): filtrar
  // por PV debe llegar a por-vendedor/por-medio-pago pero jamás a por-punto-venta.
  it('el filtro de punto de venta viaja a por-vendedor y por-medio-pago, nunca a por-punto-venta', async () => {
    mockearRutasBase()
    renderTablero()
    await screen.findByText('Empresa Uno SA')
    await waitFor(() => expect(screen.getAllByTestId('bar-chart')).toHaveLength(3))

    fireEvent.change(screen.getByLabelText('Punto de venta'), { target: { value: '10' } })

    await waitFor(() => {
      const rutas = apiGetMock.mock.calls.map((llamada) => llamada[0] as string)
      expect(rutas.some((r) => r.startsWith('/reportes/ventas/por-vendedor?') && r.includes('idPuntoVenta=10'))).toBe(true)
      expect(rutas.some((r) => r.startsWith('/reportes/ventas/por-medio-pago?') && r.includes('idPuntoVenta=10'))).toBe(true)
    })

    const rutasPorPv = apiGetMock.mock.calls.map((llamada) => llamada[0] as string).filter((r) => r.startsWith('/reportes/ventas/por-punto-venta?'))
    expect(rutasPorPv.every((r) => !r.includes('idPuntoVenta'))).toBe(true)
  })
})
