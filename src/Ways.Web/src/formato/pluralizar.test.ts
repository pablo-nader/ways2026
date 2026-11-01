import { describe, expect, it } from 'vitest'
import { pluralizar, pluralDe } from './pluralizar'

describe('pluralDe', () => {
  it.each([
    ['movimiento', 'movimientos'],
    ['cierre', 'cierres'],
    ['turno', 'turnos'],
    ['usuario', 'usuarios'],
    ['día', 'días'],
    ['proveedor', 'proveedores'],
    ['mes', 'meses'],
    ['vez', 'veces'],
    ['transacción', 'transacciones'],
    ['sofá', 'sofás'],
  ])('%s -> %s', (singular, plural) => {
    expect(pluralDe(singular)).toBe(plural)
  })
})

describe('pluralizar', () => {
  it('usa el singular solo con exactamente uno', () => {
    expect(pluralizar(1, 'artículo')).toBe('1 artículo')
  })

  it('usa el plural con cero y con más de uno', () => {
    expect(pluralizar(0, 'artículo')).toBe('0 artículos')
    expect(pluralizar(912, 'artículo')).toBe('912 artículos')
  })

  it('no agrupa miles, igual que los paginadores que reemplaza', () => {
    expect(pluralizar(1500, 'pago')).toBe('1500 pagos')
  })

  it('acepta un plural explícito para las excepciones', () => {
    expect(pluralizar(2, 'orden', 'órdenes')).toBe('2 órdenes')
    expect(pluralizar(1, 'orden', 'órdenes')).toBe('1 orden')
  })
})
