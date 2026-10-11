import { describe, expect, it } from 'vitest'
import { hrefDeAdmin, PREFIJO_ADMIN, PREFIJO_ONLINE } from './rutasPublicas'

describe('rutasPublicas', () => {
  it('los prefijos son rutas absolutas sin barra final', () => {
    expect(PREFIJO_ADMIN).toBe('/admin')
    expect(PREFIJO_ONLINE).toBe('/online')
  })

  it('hrefDeAdmin antepone el prefijo del back office', () => {
    expect(hrefDeAdmin('/login')).toBe('/admin/login')
    expect(hrefDeAdmin('/compras/12')).toBe('/admin/compras/12')
  })

  it('hrefDeAdmin tolera una ruta sin barra inicial y conserva el query', () => {
    expect(hrefDeAdmin('login')).toBe('/admin/login')
    expect(hrefDeAdmin('/caja/cierre?idTurno=7')).toBe('/admin/caja/cierre?idTurno=7')
  })
})
