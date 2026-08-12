import { describe, expect, it, vi } from 'vitest'
import type {
  FiltrosDeBreakdown,
  FiltrosDeBreakdownConPv,
  FiltrosDeRentabilidad,
  FiltrosDeReporte,
  FiltrosDeTopArticulos,
} from './reportes'

const apiGetMock = vi.fn(() => Promise.resolve(undefined))

vi.mock('./cliente', () => ({
  api: { get: (...args: unknown[]) => apiGetMock(...(args as [])) },
}))

const {
  clienteDeReportes,
  construirQueryDeBreakdown,
  construirQueryDeBreakdownConPv,
  construirQueryDeReporte,
  rangoUltimosSieteDias,
  rutasDeExportacion,
} = await import('./reportes')

function filtrosFixture(sobrescribir: Partial<FiltrosDeReporte> = {}): FiltrosDeReporte {
  return {
    idEmpresa: 1,
    idPuntoVenta: null,
    desde: '2026-08-05',
    hasta: '2026-08-11',
    granularidad: 'Dia',
    ...sobrescribir,
  }
}

function filtrosBreakdownFixture(sobrescribir: Partial<FiltrosDeBreakdown> = {}): FiltrosDeBreakdown {
  return { idEmpresa: 1, desde: '2026-08-05', hasta: '2026-08-11', ...sobrescribir }
}

function filtrosBreakdownConPvFixture(sobrescribir: Partial<FiltrosDeBreakdownConPv> = {}): FiltrosDeBreakdownConPv {
  return { idEmpresa: 1, idPuntoVenta: null, desde: '2026-08-05', hasta: '2026-08-11', ...sobrescribir }
}

function filtrosTopArticulosFixture(sobrescribir: Partial<FiltrosDeTopArticulos> = {}): FiltrosDeTopArticulos {
  return { idEmpresa: 1, idPuntoVenta: null, desde: '2026-08-05', hasta: '2026-08-11', limite: null, ...sobrescribir }
}

function filtrosRentabilidadFixture(sobrescribir: Partial<FiltrosDeRentabilidad> = {}): FiltrosDeRentabilidad {
  return { idEmpresa: 1, idPuntoVenta: null, desde: '2026-08-05', hasta: '2026-08-11', incluirEstimados: false, ...sobrescribir }
}

describe('construirQueryDeReporte', () => {
  it('arma idEmpresa/desde/hasta/granularidad sin idPuntoVenta cuando es null', () => {
    const query = construirQueryDeReporte(filtrosFixture())

    expect(query).toBe('?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11&granularidad=Dia')
  })

  it('agrega idPuntoVenta solo cuando está seteado', () => {
    const query = construirQueryDeReporte(filtrosFixture({ idPuntoVenta: 7 }))

    expect(query).toContain('idPuntoVenta=7')
  })

  it('propaga la granularidad tal cual (nombre del enum de C#, no camelCase)', () => {
    expect(construirQueryDeReporte(filtrosFixture({ granularidad: 'Semana' }))).toContain('granularidad=Semana')
    expect(construirQueryDeReporte(filtrosFixture({ granularidad: 'Mes' }))).toContain('granularidad=Mes')
  })
})

describe('construirQueryDeBreakdown', () => {
  it('arma idEmpresa/desde/hasta, sin granularidad ni idPuntoVenta (el backend no los lee en estas rutas)', () => {
    const query = construirQueryDeBreakdown(filtrosBreakdownFixture())

    expect(query).toBe('?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11')
  })
})

describe('construirQueryDeBreakdownConPv', () => {
  it('omite idPuntoVenta cuando es null', () => {
    const query = construirQueryDeBreakdownConPv(filtrosBreakdownConPvFixture())

    expect(query).toBe('?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11')
  })

  it('agrega idPuntoVenta solo cuando está seteado', () => {
    const query = construirQueryDeBreakdownConPv(filtrosBreakdownConPvFixture({ idPuntoVenta: 7 }))

    expect(query).toContain('idPuntoVenta=7')
  })
})

describe('clienteDeReportes.articulosTop', () => {
  it('reutiliza construirQueryDeBreakdownConPv y omite limite cuando es null', async () => {
    apiGetMock.mockClear()
    await clienteDeReportes.articulosTop(filtrosTopArticulosFixture())

    expect(apiGetMock).toHaveBeenCalledWith('/reportes/articulos/top?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11')
  })

  it('agrega limite solo cuando está seteado, junto con idPuntoVenta', async () => {
    apiGetMock.mockClear()
    await clienteDeReportes.articulosTop(filtrosTopArticulosFixture({ idPuntoVenta: 7, limite: 10 }))

    expect(apiGetMock).toHaveBeenCalledWith('/reportes/articulos/top?idEmpresa=1&idPuntoVenta=7&desde=2026-08-05&hasta=2026-08-11&limite=10')
  })
})

describe('rutasDeExportacion', () => {
  it('ventasResumen reutiliza construirQueryDeReporte y suma formato=xlsx al final', () => {
    const ruta = rutasDeExportacion.ventasResumen(filtrosFixture())

    expect(ruta).toBe('/reportes/ventas/resumen/export?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11&granularidad=Dia&formato=xlsx')
  })

  it('gastosResumen reutiliza construirQueryDeReporte y suma formato=xlsx al final', () => {
    const ruta = rutasDeExportacion.gastosResumen(filtrosFixture({ idPuntoVenta: 7 }))

    expect(ruta).toBe(
      '/reportes/gastos/resumen/export?idEmpresa=1&idPuntoVenta=7&desde=2026-08-05&hasta=2026-08-11&granularidad=Dia&formato=xlsx',
    )
  })

  it('rentabilidad omite incluirEstimados cuando es false, igual que clienteDeReportes.rentabilidad', () => {
    const ruta = rutasDeExportacion.rentabilidad(filtrosRentabilidadFixture())

    expect(ruta).toBe('/reportes/rentabilidad/export?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11&formato=xlsx')
  })

  it('rentabilidad agrega incluirEstimados=true cuando está tildado', () => {
    const ruta = rutasDeExportacion.rentabilidad(filtrosRentabilidadFixture({ incluirEstimados: true }))

    expect(ruta).toBe('/reportes/rentabilidad/export?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11&incluirEstimados=true&formato=xlsx')
  })
})

describe('rangoUltimosSieteDias', () => {
  it('devuelve [hoy - 6 días, hoy] — 7 días inclusive', () => {
    const rango = rangoUltimosSieteDias(new Date(2026, 7, 11)) // 11 de agosto de 2026

    expect(rango).toEqual({ desde: '2026-08-05', hasta: '2026-08-11' })
  })

  it('cruza el límite de mes correctamente', () => {
    const rango = rangoUltimosSieteDias(new Date(2026, 7, 3)) // 3 de agosto de 2026

    expect(rango).toEqual({ desde: '2026-07-28', hasta: '2026-08-03' })
  })

  it('usa la fecha local, no UTC (sin desfasaje de un día)', () => {
    const rango = rangoUltimosSieteDias(new Date(2026, 0, 1, 0, 30)) // 1 de enero, 00:30 local

    expect(rango.hasta).toBe('2026-01-01')
  })
})
