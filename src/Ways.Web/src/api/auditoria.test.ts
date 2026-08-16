import { describe, expect, it } from 'vitest'
import type { FiltrosDeExportacionDeAuditoria } from './auditoria'

const { rutasDeExportacion } = await import('./auditoria')

function offsetEsperado(anio: number, mes: number, dia: number): string {
  const minutos = new Date(anio, mes - 1, dia).getTimezoneOffset()
  const signo = minutos > 0 ? '-' : '+'
  const minutosAbsolutos = Math.abs(minutos)
  const horas = String(Math.floor(minutosAbsolutos / 60)).padStart(2, '0')
  const restoMinutos = String(minutosAbsolutos % 60).padStart(2, '0')
  return `${signo}${horas}:${restoMinutos}`
}

function filtrosFixture(sobrescribir: Partial<FiltrosDeExportacionDeAuditoria> = {}): FiltrosDeExportacionDeAuditoria {
  return {
    desde: '2026-08-05',
    hasta: '2026-08-11',
    accion: null,
    idActor: null,
    entidad: null,
    idEntidad: null,
    idPuntoVenta: null,
    ...sobrescribir,
  }
}

describe('rutasDeExportacion.auditoria', () => {
  it('aplica el mismo offset local que /cajas y /tesoreria, y suma formato=xlsx al final', () => {
    const offsetDesde = offsetEsperado(2026, 8, 5)
    const offsetHasta = offsetEsperado(2026, 8, 11)

    const ruta = decodeURIComponent(rutasDeExportacion.auditoria(filtrosFixture()))

    expect(ruta).toBe(
      `/auditoria/export?desde=2026-08-05T00:00:00${offsetDesde}&hasta=2026-08-11T23:59:59.999${offsetHasta}&formato=xlsx`,
    )
  })

  it('omite los 5 filtros opcionales cuando son null', () => {
    const ruta = rutasDeExportacion.auditoria(filtrosFixture())

    expect(ruta).not.toContain('accion=')
    expect(ruta).not.toContain('idActor=')
    expect(ruta).not.toContain('entidad=')
    expect(ruta).not.toContain('idEntidad=')
    expect(ruta).not.toContain('idPuntoVenta=')
  })

  it('agrega los 5 filtros opcionales solo cuando están seteados', () => {
    const ruta = rutasDeExportacion.auditoria(
      filtrosFixture({ accion: 'precio.cambio', idActor: 3, entidad: 'articulo', idEntidad: 41, idPuntoVenta: 7 }),
    )

    expect(ruta).toContain('accion=precio.cambio')
    expect(ruta).toContain('idActor=3')
    expect(ruta).toContain('entidad=articulo')
    expect(ruta).toContain('idEntidad=41')
    expect(ruta).toContain('idPuntoVenta=7')
  })
})
