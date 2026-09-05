import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  CLAVE_PUNTO_VENTA_DE_SESION,
  guardarPuntoVentaDeSesion,
  leerPuntoVentaDeSesion,
  olvidarPuntoVentaDeSesion,
} from './almacenDePuntoVenta'

describe('almacenDePuntoVenta', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('guarda el par usuario/punto de venta y lo recupera para el mismo usuario', () => {
    guardarPuntoVentaDeSesion(9, 100)

    expect(JSON.parse(localStorage.getItem(CLAVE_PUNTO_VENTA_DE_SESION) ?? '')).toEqual({
      idUsuario: 9,
      idPuntoVenta: 100,
    })
    expect(leerPuntoVentaDeSesion(9)).toBe(100)
  })

  it('otro usuario no hereda la elección', () => {
    guardarPuntoVentaDeSesion(9, 100)

    expect(leerPuntoVentaDeSesion(10)).toBeNull()
  })

  it('devuelve null cuando no hay nada guardado', () => {
    expect(leerPuntoVentaDeSesion(9)).toBeNull()
  })

  it('devuelve null ante un JSON ilegible', () => {
    localStorage.setItem(CLAVE_PUNTO_VENTA_DE_SESION, '{no es json')

    expect(leerPuntoVentaDeSesion(9)).toBeNull()
  })

  it('devuelve null cuando lo guardado no es un objeto', () => {
    localStorage.setItem(CLAVE_PUNTO_VENTA_DE_SESION, 'null')

    expect(leerPuntoVentaDeSesion(9)).toBeNull()
  })

  it('devuelve null cuando el id guardado no es numérico', () => {
    localStorage.setItem(CLAVE_PUNTO_VENTA_DE_SESION, JSON.stringify({ idUsuario: 9, idPuntoVenta: '100' }))

    expect(leerPuntoVentaDeSesion(9)).toBeNull()
  })

  it('olvidar borra la clave', () => {
    guardarPuntoVentaDeSesion(9, 100)
    olvidarPuntoVentaDeSesion()

    expect(localStorage.getItem(CLAVE_PUNTO_VENTA_DE_SESION)).toBeNull()
    expect(leerPuntoVentaDeSesion(9)).toBeNull()
  })

  it('no propaga cuando el almacenamiento no está disponible', () => {
    const sinAlmacenamiento = () => {
      throw new Error('sin almacenamiento')
    }
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(sinAlmacenamiento)
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(sinAlmacenamiento)
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(sinAlmacenamiento)

    expect(() => guardarPuntoVentaDeSesion(9, 100)).not.toThrow()
    expect(leerPuntoVentaDeSesion(9)).toBeNull()
    expect(() => olvidarPuntoVentaDeSesion()).not.toThrow()
  })
})
