import { describe, expect, it } from 'vitest'
import { construirQueryDeReporte, rangoUltimosSieteDias } from './reportes'
import type { FiltrosDeReporte } from './reportes'

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
