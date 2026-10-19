import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ReporteDeVentasPorHora } from './ReporteDeVentasPorHora'
import { ErrorApi } from '../api/cliente'
import { rangoDeLasUltimasCuatroSemanas } from '../api/reportes'
import type { EmpresaListado, PuntoVentaListado, VentasPorHora } from '../api/tipos'

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

const empresaUno = { id: 1, idTenant: 1, razonSocial: 'Empresa Uno SA', nombreFantasia: null } as EmpresaListado
const empresaDos = { id: 2, idTenant: 1, razonSocial: 'Empresa Dos SA', nombreFantasia: null } as EmpresaListado
const pvUno = { id: 11, idEmpresa: 1, nombre: 'Sucursal Centro' } as PuntoVentaListado
const pvDos = { id: 22, idEmpresa: 2, nombre: 'Sucursal Norte' } as PuntoVentaListado

function respuestaFixture(sobrescribir: Partial<VentasPorHora> = {}): VentasPorHora {
  const celdas = Array.from({ length: 7 * 24 }, (_, i) => ({
    diaDeLaSemana: Math.floor(i / 24) + 1,
    hora: i % 24,
    total: 0,
    tickets: 0,
    promedioTotal: 0,
    promedioTickets: 0,
  }))
  celdas[10] = { diaDeLaSemana: 1, hora: 10, total: 800, tickets: 6, promedioTotal: 200, promedioTickets: 1.5 }
  celdas[24 + 18] = { diaDeLaSemana: 2, hora: 18, total: 400, tickets: 4, promedioTotal: 100, promedioTickets: 1 }
  return {
    desde: '2026-09-21',
    hasta: '2026-10-18',
    zonaHoraria: 'America/Argentina/Buenos_Aires',
    total: 1200,
    tickets: 10,
    dias: [1, 2, 3, 4, 5, 6, 7].map((d) => ({ diaDeLaSemana: d, ocurrencias: 4 })),
    celdas,
    ...sobrescribir,
  }
}

function mockearRutasBase(reporte: (ruta: string) => Promise<unknown> = () => Promise.resolve(respuestaFixture())) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/empresas') return Promise.resolve([empresaUno, empresaDos])
    if (ruta === '/puntos-venta') return Promise.resolve([pvUno, pvDos])
    if (ruta.startsWith('/reportes/ventas/por-hora?')) return reporte(ruta)
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function llamadasDeReporte(): string[] {
  return apiGetMock.mock.calls.map((c) => c[0] as string).filter((r) => r.startsWith('/reportes/ventas/por-hora?'))
}

function parametrosDeLaUltimaLlamada(): URLSearchParams {
  const llamadas = llamadasDeReporte()
  return new URLSearchParams(llamadas[llamadas.length - 1].split('?')[1])
}

function renderReporte() {
  return render(<ReporteDeVentasPorHora />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

/** La tabla solo existe cuando llegó la respuesta del reporte: es la señal del dato, no del contenedor. */
function tablaDelMapa(): Promise<HTMLElement> {
  return screen.findByRole('table', { name: /promedio por día de la semana y hora/ })
}

beforeEach(() => {
  apiGetMock.mockReset()
})

describe('ReporteDeVentasPorHora', () => {
  it('pide las últimas 4 semanas de la primera empresa y dibuja la grilla con los importes promedio', async () => {
    mockearRutasBase()
    renderReporte()

    const tabla = await tablaDelMapa()
    expect(within(tabla).getByRole('cell', { name: 'Lunes 10:00–11:00: $ 200,00' })).toHaveTextContent('200')
    expect(within(tabla).getByRole('cell', { name: 'Martes 18:00–19:00: $ 100,00' })).toHaveTextContent('100')

    const rango = rangoDeLasUltimasCuatroSemanas()
    const parametros = parametrosDeLaUltimaLlamada()
    expect(parametros.get('idEmpresa')).toBe('1')
    expect(parametros.get('desde')).toBe(rango.desde)
    expect(parametros.get('hasta')).toBe(rango.hasta)
    expect(parametros.has('idPuntoVenta')).toBe(false)
  })

  it('resume total, tickets, zona y las veces que cae cada día', async () => {
    mockearRutasBase()
    renderReporte()

    await tablaDelMapa()
    expect(screen.getByText('$ 1.200,00')).toBeInTheDocument()
    expect(screen.getByText('America/Argentina/Buenos_Aires')).toBeInTheDocument()
    const dias = within(screen.getByRole('list', { name: 'Días incluidos en el rango' })).getAllByRole('listitem')
    expect(dias.map((d) => d.textContent)).toEqual([
      'Lunes ×4',
      'Martes ×4',
      'Miércoles ×4',
      'Jueves ×4',
      'Viernes ×4',
      'Sábado ×4',
      'Domingo ×4',
    ])
  })

  it('el toggle pasa a tickets promedio con hasta 2 decimales sin volver a consultar', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    await tablaDelMapa()
    const consultas = llamadasDeReporte().length
    expect(screen.getByRole('button', { name: 'Importe promedio' })).toHaveAttribute('aria-pressed', 'true')

    await usuario.click(screen.getByRole('button', { name: 'Tickets promedio' }))

    const tabla = screen.getByRole('table', { name: /Tickets promedio por día de la semana y hora/ })
    expect(within(tabla).getByRole('cell', { name: 'Lunes 10:00–11:00: 1,5' })).toHaveTextContent('1,5')
    expect(within(tabla).getByRole('cell', { name: 'Martes 18:00–19:00: 1' })).toHaveTextContent('1')
    expect(screen.getByRole('button', { name: 'Tickets promedio' })).toHaveAttribute('aria-pressed', 'true')
    expect(llamadasDeReporte()).toHaveLength(consultas)
  })

  it('el punto de venta elegido viaja en la consulta y cambiar de empresa lo limpia', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    await tablaDelMapa()
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), 'Sucursal Centro')
    await waitFor(() => expect(parametrosDeLaUltimaLlamada().get('idPuntoVenta')).toBe('11'))

    await usuario.selectOptions(screen.getByLabelText('Empresa'), 'Empresa Dos SA')
    await waitFor(() => {
      const parametros = parametrosDeLaUltimaLlamada()
      expect(parametros.get('idEmpresa')).toBe('2')
      expect(parametros.has('idPuntoVenta')).toBe(false)
    })
    expect(screen.queryByRole('option', { name: pvUno.nombre })).not.toBeInTheDocument()
    expect(screen.getByRole('option', { name: pvDos.nombre })).toBeInTheDocument()
  })

  it('una respuesta vieja no pisa la del filtro que el usuario ya cambió', async () => {
    let resolverPrimera: (v: VentasPorHora) => void = () => {}
    let llamadas = 0
    mockearRutasBase(() => {
      llamadas += 1
      if (llamadas === 1) return new Promise<VentasPorHora>((resolver) => (resolverPrimera = resolver))
      return Promise.resolve(respuestaFixture({ total: 777 }))
    })
    const usuario = userEvent.setup()
    renderReporte()

    await waitFor(() => expect(llamadasDeReporte()).toHaveLength(1))
    await usuario.selectOptions(await screen.findByLabelText('Punto de venta'), 'Sucursal Centro')
    expect(await screen.findByText('$ 777,00')).toBeInTheDocument()

    resolverPrimera(respuestaFixture({ total: 999 }))
    await waitFor(() => expect(screen.queryByText('$ 999,00')).not.toBeInTheDocument())
    expect(screen.getByText('$ 777,00')).toBeInTheDocument()
  })

  it('muestra el mensaje del servidor si falla y reintenta', async () => {
    let fallar = true
    mockearRutasBase(() =>
      fallar ? Promise.reject(new ErrorApi(400, 'rango_invalido', 'El rango no puede superar 366 días.')) : Promise.resolve(respuestaFixture()),
    )
    const usuario = userEvent.setup()
    renderReporte()

    expect(await screen.findByText('El rango no puede superar 366 días.')).toBeInTheDocument()
    expect(screen.queryByRole('table', { name: /promedio por día de la semana y hora/ })).not.toBeInTheDocument()

    fallar = false
    await usuario.click(screen.getByRole('button', { name: 'Reintentar' }))

    await tablaDelMapa()
    expect(screen.queryByText('El rango no puede superar 366 días.')).not.toBeInTheDocument()
  })

  it('un error que no es de la API muestra el texto genérico', async () => {
    mockearRutasBase(() => Promise.reject(new Error('boom')))
    renderReporte()

    expect(await screen.findByText('No se pudo cargar el reporte de ventas por hora.')).toBeInTheDocument()
  })

  it('un rango invertido avisa y no consulta', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    await tablaDelMapa()
    const consultas = llamadasDeReporte().length
    const desde = screen.getByLabelText('Desde')
    await usuario.clear(desde)

    expect(await screen.findByText(/Elegí un rango de fechas válido/)).toBeInTheDocument()
    expect(llamadasDeReporte()).toHaveLength(consultas)
    expect(screen.queryByRole('table', { name: /promedio por día de la semana y hora/ })).not.toBeInTheDocument()
  })

  it('sin empresas visibles lo dice y no consulta el reporte', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/empresas' || ruta === '/puntos-venta') return Promise.resolve([])
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    renderReporte()

    expect(await screen.findByText('No hay empresas visibles para mostrar el reporte.')).toBeInTheDocument()
    expect(llamadasDeReporte()).toHaveLength(0)
  })
})
