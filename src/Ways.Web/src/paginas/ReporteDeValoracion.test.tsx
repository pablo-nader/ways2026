import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ReporteDeValoracion } from './ReporteDeValoracion'
import { ErrorApi } from '../api/cliente'
import { rangoDeLosUltimosDoceMeses } from '../api/reportes'
import type { EmpresaListado, PuntoDeValoracion, ValoracionDeInventario } from '../api/tipos'

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

// Bajo jsdom `recharts` no dibuja: los gráficos se reemplazan por un stub que expone sus series.
vi.mock('../componentes/graficos/GraficoDeLineasMultiples', () => ({
  GraficoDeLineasMultiples: ({ titulo, data }: { titulo: string; data: unknown[] }) => (
    <div role="img" aria-label={titulo} data-puntos={data.length} />
  ),
}))
vi.mock('../componentes/graficos/GraficoDeBarrasApiladas', () => ({
  GraficoDeBarrasApiladas: ({ titulo, data }: { titulo: string; data: unknown[] }) => (
    <div role="img" aria-label={titulo} data-puntos={data.length} />
  ),
}))

const empresaUno = { id: 1, idTenant: 1, razonSocial: 'Empresa Uno SA', nombreFantasia: null } as EmpresaListado
const empresaDos = { id: 2, idTenant: 1, razonSocial: 'Empresa Dos SA', nombreFantasia: null } as EmpresaListado

function punto(sobrescribir: Partial<PuntoDeValoracion> = {}): PuntoDeValoracion {
  return {
    fecha: '2026-02-01T02:59:59.999999Z',
    mercaderiaCosto: 1000,
    mercaderiaVenta: 1500,
    efectivo: 200,
    cuentas: 300,
    dinero: 500,
    totalACosto: 1500,
    totalAVenta: 2000,
    articulosSinCosto: 0,
    articulosSinPrecio: 0,
    articulosConCostoEstimado: 0,
    valorCostoEstimado: 0,
    ...sobrescribir,
  }
}

function respuestaFixture(sobrescribir: Partial<ValoracionDeInventario> = {}): ValoracionDeInventario {
  return {
    idEmpresa: 1,
    desde: '2026-01-01',
    hasta: '2026-11-15',
    granularidad: 'Mes',
    zonaHoraria: 'America/Argentina/Buenos_Aires',
    puntos: [
      punto(),
      punto({
        fecha: '2026-11-15T13:00:00.000Z',
        mercaderiaCosto: 800,
        mercaderiaVenta: 1300,
        efectivo: 500,
        cuentas: 700,
        dinero: 1200,
        totalACosto: 2000,
        totalAVenta: 2500,
        articulosSinCosto: 2,
        articulosSinPrecio: 1,
      }),
    ],
    actual: {
      porCuenta: [
        { idCuenta: 5, nombre: 'Banco Galicia', saldo: 450 },
        { idCuenta: 6, nombre: 'Mercado Pago', saldo: 250 },
      ],
      porArea: [
        { idArea: 1, nombre: 'Almacén', mercaderiaCosto: 500, mercaderiaVenta: 800 },
        { idArea: null, nombre: 'Sin área', mercaderiaCosto: 300, mercaderiaVenta: 500 },
      ],
      idListaPrecio: 9,
      nombreListaPrecio: 'Minorista',
    },
    ...sobrescribir,
  }
}

const RUTA = '/reportes/valoracion?'

function mockearRutasBase(reporte: (ruta: string) => Promise<unknown> = () => Promise.resolve(respuestaFixture())) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/empresas') return Promise.resolve([empresaUno, empresaDos])
    if (ruta.startsWith(RUTA)) return reporte(ruta)
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function llamadasDeReporte(): string[] {
  return apiGetMock.mock.calls.map((c) => c[0] as string).filter((r) => r.startsWith(RUTA))
}

function parametrosDeLaUltimaLlamada(): URLSearchParams {
  const llamadas = llamadasDeReporte()
  return new URLSearchParams(llamadas[llamadas.length - 1].split('?')[1])
}

function renderReporte() {
  return render(<ReporteDeValoracion />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

/** La frase solo existe cuando llegó la respuesta del reporte: es la señal del dato, no del contenedor. */
function frase(): Promise<HTMLElement> {
  return screen.findByTestId('valoracion-frase')
}

beforeEach(() => {
  apiGetMock.mockReset()
})

describe('ReporteDeValoracion', () => {
  it('pide los últimos 12 meses por mes de la primera empresa', async () => {
    mockearRutasBase()
    renderReporte()

    await frase()
    const esperado = rangoDeLosUltimosDoceMeses()
    const parametros = parametrosDeLaUltimaLlamada()
    expect(parametros.get('idEmpresa')).toBe('1')
    expect(parametros.get('desde')).toBe(esperado.desde)
    expect(parametros.get('hasta')).toBe(esperado.hasta)
    expect(parametros.get('granularidad')).toBe('Mes')
  })

  it('muestra las tarjetas con el último valor y la variación contra el cierre del primer período, y la frase', async () => {
    mockearRutasBase()
    renderReporte()

    expect(await frase()).toHaveTextContent('Tenés más dinero, pero bajó la mercadería.')

    const total = screen.getByTestId('valoracion-tarjeta-totalACosto')
    expect(total).toHaveTextContent('Total a costo')
    expect(total).toHaveTextContent('$ 2.000,00')
    expect(total).toHaveTextContent('+$ 500,00 (+33,3 %) desde el cierre de 01/2026')

    const mercaderia = screen.getByTestId('valoracion-tarjeta-mercaderiaCosto')
    expect(mercaderia).toHaveTextContent('$ 800,00')
    expect(mercaderia).toHaveTextContent('-$ 200,00 (-20,0 %)')

    expect(screen.getByTestId('valoracion-tarjeta-mercaderiaVenta')).toHaveTextContent('$ 1.300,00')
    expect(screen.getByTestId('valoracion-tarjeta-efectivo')).toHaveTextContent('$ 500,00')
    expect(screen.getByTestId('valoracion-tarjeta-cuentas')).toHaveTextContent('$ 700,00')
    expect(screen.getByRole('img', { name: /Evolución/ })).toHaveAttribute('data-puntos', '2')
    expect(screen.getByRole('img', { name: /Composición/ })).toHaveAttribute('data-puntos', '2')
  })

  it('arma las tablas por cuenta y por área con sus totales', async () => {
    mockearRutasBase()
    renderReporte()
    await frase()

    const cuentas = screen.getByRole('table', { name: 'Saldo por cuenta' })
    expect(within(cuentas).getByText('Banco Galicia')).toBeInTheDocument()
    expect(within(cuentas).getByText('$ 450,00')).toBeInTheDocument()
    expect(within(cuentas).getByText('$ 700,00')).toBeInTheDocument()

    const areas = screen.getByRole('table', { name: 'Mercadería por área' })
    expect(within(areas).getByText('Sin área')).toBeInTheDocument()
    const totales = within(within(areas).getAllByRole('row').at(-1) as HTMLElement).getAllByRole('cell')
    expect(totales.map((c) => c.textContent)).toEqual(['Total', '$ 800,00', '$ 1.300,00'])
  })

  it('avisa los artículos sin costo y sin precio y deja las notas fijas del reporte', async () => {
    mockearRutasBase()
    renderReporte()
    await frase()

    expect(screen.getByRole('status')).toHaveTextContent('2 artículos con stock no tienen costo cargado')
    expect(screen.getByRole('status')).toHaveTextContent('1 artículo con stock no tiene precio de venta')
    expect(screen.getByText(/turno abierto no está incluido/)).toBeInTheDocument()
    expect(screen.getByText(/se reconstruye con las compras/)).toBeInTheDocument()
    expect(screen.getByText(/lista «Minorista»/)).toBeInTheDocument()
  })

  it('sin faltantes no muestra el aviso de cobertura', async () => {
    const base = respuestaFixture()
    mockearRutasBase(() =>
      Promise.resolve({ ...base, puntos: base.puntos.map((p) => ({ ...p, articulosSinCosto: 0, articulosSinPrecio: 0 })) }),
    )
    renderReporte()
    await frase()

    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })

  it('cambiar la granularidad vuelve a consultar con ese valor', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()
    await frase()

    await usuario.selectOptions(screen.getByLabelText('Un punto por'), 'Semana')

    await waitFor(() => expect(parametrosDeLaUltimaLlamada().get('granularidad')).toBe('Semana'))
  })

  it('una respuesta vieja no pisa la del filtro que el usuario ya cambió', async () => {
    let resolverPrimera: (v: ValoracionDeInventario) => void = () => {}
    let llamadas = 0
    mockearRutasBase(() => {
      llamadas += 1
      if (llamadas === 1) return new Promise<ValoracionDeInventario>((resolver) => (resolverPrimera = resolver))
      return Promise.resolve(respuestaFixture({ actual: { ...respuestaFixture().actual, porCuenta: [{ idCuenta: 7, nombre: 'Cuenta nueva', saldo: 1 }] } }))
    })
    const usuario = userEvent.setup()
    renderReporte()

    await waitFor(() => expect(llamadasDeReporte()).toHaveLength(1))
    await usuario.selectOptions(await screen.findByLabelText('Empresa'), 'Empresa Dos SA')
    expect(await screen.findByText('Cuenta nueva')).toBeInTheDocument()

    await act(async () => {
      resolverPrimera(respuestaFixture())
    })
    expect(screen.queryByText('Banco Galicia')).not.toBeInTheDocument()
    expect(screen.getByText('Cuenta nueva')).toBeInTheDocument()
  })

  it('al cambiar un filtro no deja a la vista los números anteriores mientras la consulta está pendiente', async () => {
    let llamadas = 0
    mockearRutasBase(() => {
      llamadas += 1
      return llamadas === 1 ? Promise.resolve(respuestaFixture()) : new Promise<ValoracionDeInventario>(() => {})
    })
    const usuario = userEvent.setup()
    renderReporte()
    await frase()

    await usuario.selectOptions(screen.getByLabelText('Empresa'), 'Empresa Dos SA')
    await waitFor(() => expect(llamadasDeReporte()).toHaveLength(2))

    expect(screen.queryByTestId('valoracion-frase')).not.toBeInTheDocument()
    expect(screen.queryByTestId('valoracion-tarjeta-totalACosto')).not.toBeInTheDocument()
  })

  it.each([
    ['rango_en_el_futuro', /después de hoy/],
    ['demasiados_puntos', /máximo 60/],
    ['rango_demasiado_amplio', /366 días/],
  ])('traduce el código %s a un mensaje en español y permite reintentar', async (codigo, patron) => {
    let fallar = true
    mockearRutasBase(() =>
      fallar ? Promise.reject(new ErrorApi(400, codigo, 'mensaje crudo')) : Promise.resolve(respuestaFixture()),
    )
    const usuario = userEvent.setup()
    renderReporte()

    expect(await screen.findByText(patron)).toBeInTheDocument()
    expect(screen.queryByText('mensaje crudo')).not.toBeInTheDocument()
    expect(screen.queryByTestId('valoracion-frase')).not.toBeInTheDocument()

    fallar = false
    await usuario.click(screen.getByRole('button', { name: 'Reintentar' }))

    await frase()
    expect(screen.queryByText(patron)).not.toBeInTheDocument()
  })

  it('un error que no es de la API muestra el texto genérico', async () => {
    mockearRutasBase(() => Promise.reject(new Error('boom')))
    renderReporte()

    expect(await screen.findByText('No se pudo cargar el reporte de valoración.')).toBeInTheDocument()
  })

  it('una fecha inicial vacía avisa y no consulta', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()
    await frase()
    const antes = llamadasDeReporte().length

    await usuario.clear(screen.getByLabelText('Desde'))

    expect(await screen.findByText(/Elegí un rango de fechas válido/)).toBeInTheDocument()
    expect(llamadasDeReporte()).toHaveLength(antes)
  })

  it('sin empresas visibles lo dice y no consulta el reporte', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/empresas' ? Promise.resolve([]) : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    renderReporte()

    expect(await screen.findByText(/No hay empresas visibles/)).toBeInTheDocument()
    expect(llamadasDeReporte()).toHaveLength(0)
  })
})
