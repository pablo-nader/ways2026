import { describe, expect, it } from 'vitest'
import { colorDePuntoVenta } from './colorDePuntoVenta'

const centro = { id: 5, nombre: 'Local Centro' }
const norte = { id: 12, nombre: 'Local Norte' }
const sur = { id: 30, nombre: 'Local Sur' }

describe('colorDePuntoVenta', () => {
  it('sin punto de venta activo pinta color_1', () => {
    expect(colorDePuntoVenta(null, [centro, norte])).toBe('color_1')
  })

  it('los dos ids más bajos reciben colores distintos, sin importar el orden de la lista', () => {
    expect(colorDePuntoVenta(centro, [norte, centro])).toBe('color_1')
    expect(colorDePuntoVenta(norte, [norte, centro])).toBe('color_2')
  })

  it('renombrar un punto de venta no cambia su color', () => {
    const norteRenombrado = { ...norte, nombre: 'Sucursal Norte' }

    expect(colorDePuntoVenta(norteRenombrado, [centro, norteRenombrado])).toBe(
      colorDePuntoVenta(norte, [centro, norte]),
    )
  })

  it('agregar un punto de venta con id mayor conserva los colores de los existentes', () => {
    expect(colorDePuntoVenta(centro, [centro, norte, sur])).toBe(colorDePuntoVenta(centro, [centro, norte]))
    expect(colorDePuntoVenta(norte, [centro, norte, sur])).toBe(colorDePuntoVenta(norte, [centro, norte]))
    expect(colorDePuntoVenta(sur, [centro, norte, sur])).toBe('color_1')
  })

  it('un punto de venta que no está en la lista cae a color_1', () => {
    expect(colorDePuntoVenta(sur, [centro, norte])).toBe('color_1')
  })

  it('no muta la lista recibida', () => {
    const lista = [sur, centro, norte]

    colorDePuntoVenta(centro, lista)

    expect(lista).toEqual([sur, centro, norte])
  })
})
