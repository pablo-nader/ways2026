import { describe, expect, it } from 'vitest'
import { construirQueryDeBreakdown, construirQueryDeBreakdownConPv, construirQueryDeReporte, rangoUltimosSieteDias } from './reportes'
import type { FiltrosDeBreakdown, FiltrosDeBreakdownConPv, FiltrosDeReporte } from './reportes'

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
