import { describe, expect, it } from 'vitest'

import type { SucursalPublica } from '../api/tipos'
import { enlaceFacebook, enlaceInstagram, enlaceWeb, enlaceWhatsapp, enlaceWhatsappDelComercio } from './enlaces'

function sucursal(whatsapp: string | null): SucursalPublica {
  return { nombre: 'Centro', domicilio: null, horario: null, whatsapp, instagram: null, facebook: null, web: null }
}

describe('enlaceWhatsapp', () => {
  it.each([
    ['+54 9 11 5555-1234', 'https://wa.me/5491155551234'],
    ['+54 9 223 555 1234', 'https://wa.me/5492235551234'],
    ['  +54 9 11 5555 1234  ', 'https://wa.me/5491155551234'],
    ['+351 912 345 678', 'https://wa.me/351912345678'],
    ['00 44 20 7946 0958', 'https://wa.me/442079460958'],
    ['5491155551234', 'https://wa.me/5491155551234'],
    ['542235551234', 'https://wa.me/542235551234'],
    ['54 9 223 555-1234', 'https://wa.me/5492235551234'],
  ])('%s ya trae el código de país y se usa tal cual', (entrada, esperado) => {
    expect(enlaceWhatsapp(entrada)).toBe(esperado)
  })

  it.each([
    ['011 4444 5678', 'https://wa.me/5491144445678'],
    ['(011) 4444 5678', 'https://wa.me/5491144445678'],
    ['351 612-3456', 'https://wa.me/5493516123456'],
    ['11 5555-1234', 'https://wa.me/5491155551234'],
  ])('%s es local argentino: sin el 0 inicial y con el prefijo 549', (entrada, esperado) => {
    expect(enlaceWhatsapp(entrada)).toBe(esperado)
  })

  it.each([
    [null],
    [undefined],
    [''],
    ['   '],
    ['sin número'],
    ['12345'],
    ['4444 5678'],
    ['223 15 555 1234'],
    ['11 5555-1234 / 11 6666-0000'],
    ['54115555123'],
    ['54911555512345'],
    ['+54 9 11'],
    ['+54 9 11 5555-1234 / 11 6666-0000'],
  ])('%s no es un número usable', (entrada) => {
    expect(enlaceWhatsapp(entrada)).toBeNull()
  })
})

describe('enlaceInstagram', () => {
  it.each([
    ['ferreteriasur', 'https://www.instagram.com/ferreteriasur'],
    ['@ferreteria.sur_1', 'https://www.instagram.com/ferreteria.sur_1'],
    ['  @sur  ', 'https://www.instagram.com/sur'],
    ['https://www.instagram.com/sur', 'https://www.instagram.com/sur'],
    ['instagram.com/sur', 'https://instagram.com/sur'],
    ['instagram.com', 'https://instagram.com/'],
    ['www.instagram.com', 'https://www.instagram.com/'],
    ['@ferreteria.sur', 'https://www.instagram.com/ferreteria.sur'],
  ])('%s', (entrada, esperado) => {
    expect(enlaceInstagram(entrada)).toBe(esperado)
  })

  it.each([[null], [''], ['   '], ['dos palabras'], ['https://otro-sitio.com/sur'], ['javascript:alert(1)'], ['sur.com.ar']])(
    '%s no genera enlace',
    (entrada) => {
      expect(enlaceInstagram(entrada)).toBeNull()
    },
  )
})

describe('enlaceFacebook', () => {
  it.each([
    ['https://www.facebook.com/FerreteriaSur', 'https://www.facebook.com/FerreteriaSur'],
    ['facebook.com/FerreteriaSur', 'https://facebook.com/FerreteriaSur'],
    ['https://fb.com/sur.ferreteria', 'https://fb.com/sur.ferreteria'],
    ['FerreteriaSur', 'https://www.facebook.com/FerreteriaSur'],
    ['www.facebook.com', 'https://www.facebook.com/'],
    ['fb.com/sur', 'https://fb.com/sur'],
    ['@ferreteria.sur', 'https://www.facebook.com/ferreteria.sur'],
    ['@FerreteriaSur', 'https://www.facebook.com/FerreteriaSur'],
  ])('%s', (entrada, esperado) => {
    expect(enlaceFacebook(entrada)).toBe(esperado)
  })

  it.each([[null], [''], ['Ferretería Sur'], ['https://otro-sitio.com/sur'], ['data:text/html,x'], ['sur.com.ar'], ['ferreteria.sur'], ['@'], ['@dos palabras'], ['@sur']])(
    '%s no genera enlace',
    (entrada) => {
      expect(enlaceFacebook(entrada)).toBeNull()
    },
  )
})

describe('enlaceWeb', () => {
  it.each([
    ['https://sur.com.ar', 'https://sur.com.ar/'],
    ['http://sur.com.ar/tienda?x=1', 'http://sur.com.ar/tienda?x=1'],
    ['www.sur.com.ar', 'https://www.sur.com.ar/'],
    ['  sur.com.ar/contacto ', 'https://sur.com.ar/contacto'],
  ])('%s', (entrada, esperado) => {
    expect(enlaceWeb(entrada)).toBe(esperado)
  })

  it.each([[null], [undefined], [''], ['   '], ['sin punto'], ['javascript:alert(1)'], ['ftp://sur.com.ar']])(
    '%s no genera enlace',
    (entrada) => {
      expect(enlaceWeb(entrada)).toBeNull()
    },
  )
})

describe('enlaceWhatsappDelComercio', () => {
  it('toma el primer punto de venta con un número usable', () => {
    const enlace = enlaceWhatsappDelComercio([sucursal(null), sucursal('abc'), sucursal('11 5555-1234'), sucursal('11 6666-0000')])

    expect(enlace).toBe('https://wa.me/5491155551234')
  })

  it('sin ningún número usable no hay enlace', () => {
    expect(enlaceWhatsappDelComercio([sucursal(null), sucursal(' '), sucursal('12')])).toBeNull()
    expect(enlaceWhatsappDelComercio([])).toBeNull()
  })
})
