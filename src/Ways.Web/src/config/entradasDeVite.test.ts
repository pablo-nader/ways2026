import { describe, expect, it } from 'vitest'
import { reescribirUrlDeDesarrollo } from './entradasDeVite'

describe('reescribirUrlDeDesarrollo', () => {
  it.each([
    ['/admin', '/admin.html'],
    ['/admin/', '/admin.html'],
    ['/admin/login', '/admin.html'],
    ['/admin/articulos/edit/12', '/admin.html'],
    ['/online', '/online.html'],
    ['/online/producto/3', '/online.html'],
  ])('%s sale de %s', (url, esperado) => {
    expect(reescribirUrlDeDesarrollo(url)).toBe(esperado)
  })

  it('conserva el query y el hash', () => {
    expect(reescribirUrlDeDesarrollo('/admin/caja/cierre?idTurno=7')).toBe('/admin.html?idTurno=7')
    expect(reescribirUrlDeDesarrollo('/online#carrito')).toBe('/online.html#carrito')
  })

  it.each([
    '/',
    '/?x=1',
    '/pos.html',
    '/admin.html',
    '/online.html',
    '/administrador',
    '/onlinex/abc',
    '/api/admin/algo',
    '/src/main.tsx',
    '/@vite/client',
    '/login',
  ])('no toca %s', (url) => {
    expect(reescribirUrlDeDesarrollo(url)).toBe(url)
  })
})
