import { describe, expect, it } from 'vitest'
import {
  buscarArticuloOffline,
  edadDeInstantaneaEnMinutos,
  formatearVejezDeInstantanea,
  parsearEntradaDeEscaneoOffline,
  resolverPreciosOffline,
  todasLasLineasTienenPrecioOffline,
} from './instantaneaOffline'
import type { ArticuloDeInstantanea, InstantaneaDePos } from '../api/tipos'
import type { LineaCarrito } from '../api/carrito'

function articuloFixture(sobrescribir: Partial<ArticuloDeInstantanea> = {}): ArticuloDeInstantanea {
  return {
    idArticulo: 1,
    codigoInterno: 'A0001',
    nombre: 'Coca Cola 1L',
    codigosBarra: ['7790001234567'],
    precioOriginal: 100,
    precioFinal: 100,
    descuentoUnitario: 0,
    aplicadas: [],
    idAlicuotaIva: 1,
    porcentajeIva: 21,
    ...sobrescribir,
  }
}

function instantaneaFixture(sobrescribir: Partial<InstantaneaDePos> = {}): InstantaneaDePos {
  return {
    momento: '2026-09-20T10:00:00.000Z',
    idPuntoVenta: 7,
    articulos: [articuloFixture()],
    mediosDePago: [],
    toleranciaPago: 0,
    ...sobrescribir,
  }
}

function lineaFixture(sobrescribir: Partial<LineaCarrito> = {}): LineaCarrito {
  return { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', cantidad: 1, ...sobrescribir }
}

describe('parsearEntradaDeEscaneoOffline — espejo de ParserDeEscaneo (regla I.2)', () => {
  it('un código de 7+ dígitos resuelve a CodigoBarra', () => {
    expect(parsearEntradaDeEscaneoOffline('7790001234567')).toEqual({ cantidad: 1, codigo: '7790001234567', objetivo: 'CodigoBarra' })
  })

  it('un código de menos de 7 caracteres resuelve a CodigoInterno', () => {
    expect(parsearEntradaDeEscaneoOffline('A0001')).toEqual({ cantidad: 1, codigo: 'A0001', objetivo: 'CodigoInterno' })
  })

  it.each([
    ['exactamente 7 caracteres es CodigoBarra (mutation target: el corte es < 7, no <= 7)', '1234567', 'CodigoBarra'],
    ['6 caracteres es CodigoInterno', '123456', 'CodigoInterno'],
  ] as const)('%s', (_titulo, codigo, objetivoEsperado) => {
    expect(parsearEntradaDeEscaneoOffline(codigo)?.objetivo).toBe(objetivoEsperado)
  })

  it('sintaxis "cantidad*codigo" separa cantidad y código', () => {
    expect(parsearEntradaDeEscaneoOffline('3*7790001234567')).toEqual({ cantidad: 3, codigo: '7790001234567', objetivo: 'CodigoBarra' })
  })

  it.each([
    ['prefijo ausente', '7790001234567', 1],
    ['prefijo "0"', '0*7790001234567', 1],
    ['prefijo negativo', '-2*7790001234567', 1],
    ['prefijo no numérico', 'x*7790001234567', 1],
    ['prefijo vacío antes del *', '*7790001234567', 1],
  ])('%s cae a cantidad 1 (nunca invalida el escaneo)', (_titulo, entrada, cantidadEsperada) => {
    expect(parsearEntradaDeEscaneoOffline(entrada)?.cantidad).toBe(cantidadEsperada)
  })

  it('una entrada vacía (o solo espacios) devuelve null', () => {
    expect(parsearEntradaDeEscaneoOffline('')).toBeNull()
    expect(parsearEntradaDeEscaneoOffline('   ')).toBeNull()
  })

  it('un código vacío después del separador ("3*") devuelve null', () => {
    expect(parsearEntradaDeEscaneoOffline('3*')).toBeNull()
  })
})

describe('buscarArticuloOffline', () => {
  it('encuentra por código de barra', () => {
    const instantanea = instantaneaFixture()
    expect(buscarArticuloOffline(instantanea, '7790001234567')).toEqual({
      idArticulo: 1,
      codigoInterno: 'A0001',
      nombre: 'Coca Cola 1L',
      codigoBarra: '7790001234567',
      cantidad: 1,
    })
  })

  it('encuentra por código interno, con codigoBarra null (mismo criterio que el escaneo online)', () => {
    const instantanea = instantaneaFixture()
    expect(buscarArticuloOffline(instantanea, 'A0001')).toEqual({
      idArticulo: 1,
      codigoInterno: 'A0001',
      nombre: 'Coca Cola 1L',
      codigoBarra: null,
      cantidad: 1,
    })
  })

  it('respeta la sintaxis "cantidad*codigo"', () => {
    const instantanea = instantaneaFixture()
    expect(buscarArticuloOffline(instantanea, '5*7790001234567')?.cantidad).toBe(5)
  })

  it('un código que no está en la instantánea devuelve null', () => {
    expect(buscarArticuloOffline(instantaneaFixture(), '9999999999999')).toBeNull()
  })

  it('una entrada vacía devuelve null', () => {
    expect(buscarArticuloOffline(instantaneaFixture(), '')).toBeNull()
  })

  it('nunca confunde un artículo de OTRO id con el mismo código interno de otra fixture (identidad, no solo presencia)', () => {
    const instantanea = instantaneaFixture({
      articulos: [articuloFixture({ idArticulo: 1, codigoInterno: 'A0001' }), articuloFixture({ idArticulo: 2, codigoInterno: 'A0002', codigosBarra: ['7790009999999'] })],
    })
    expect(buscarArticuloOffline(instantanea, 'A0002')?.idArticulo).toBe(2)
    expect(buscarArticuloOffline(instantanea, '7790009999999')?.idArticulo).toBe(2)
  })
})

describe('resolverPreciosOffline', () => {
  it('resuelve precio/descuento/aplicadas desde la instantánea, indexado por idArticulo', () => {
    const instantanea = instantaneaFixture({
      articulos: [articuloFixture({ precioOriginal: 150, precioFinal: 120, descuentoUnitario: 30, aplicadas: [{ idOferta: 9, nombre: '20% off', descuentoUnitario: 30 }] })],
    })
    const resultado = resolverPreciosOffline([lineaFixture()], instantanea, 5)
    expect(resultado[1]).toEqual({
      idArticulo: 1,
      idListaPrecio: 5,
      precioOriginal: 150,
      precioFinal: 120,
      descuentoUnitario: 30,
      aplicadas: [{ idOferta: 9, nombre: '20% off', descuentoUnitario: 30 }],
    })
  })

  it('una línea sin artículo en la instantánea queda ausente del índice (nunca un precio inventado)', () => {
    const resultado = resolverPreciosOffline([lineaFixture({ idArticulo: 999 })], instantaneaFixture(), 5)
    expect(resultado[999]).toBeUndefined()
  })
})

describe('todasLasLineasTienenPrecioOffline', () => {
  it('true cuando todas las líneas están en la instantánea', () => {
    expect(todasLasLineasTienenPrecioOffline([lineaFixture()], instantaneaFixture())).toBe(true)
  })

  it('false con el carrito vacío (nunca "todas" vacuamente true)', () => {
    expect(todasLasLineasTienenPrecioOffline([], instantaneaFixture())).toBe(false)
  })

  it('false si CUALQUIER línea no está en la instantánea (all-or-nothing)', () => {
    const lineas = [lineaFixture({ idArticulo: 1 }), lineaFixture({ idArticulo: 2 })]
    expect(todasLasLineasTienenPrecioOffline(lineas, instantaneaFixture())).toBe(false)
  })
})

describe('edadDeInstantaneaEnMinutos', () => {
  it('calcula minutos completos transcurridos', () => {
    const momento = '2026-09-20T10:00:00.000Z'
    const ahora = new Date('2026-09-20T10:05:30.000Z')
    expect(edadDeInstantaneaEnMinutos(momento, ahora)).toBe(5)
  })

  it('nunca negativo — un reloj local atrasado respecto del servidor da 0, no un absurdo negativo', () => {
    const momento = '2026-09-20T10:10:00.000Z'
    const ahora = new Date('2026-09-20T10:00:00.000Z')
    expect(edadDeInstantaneaEnMinutos(momento, ahora)).toBe(0)
  })
})

describe('formatearVejezDeInstantanea', () => {
  it.each([
    ['hace instantes', '2026-09-20T10:00:00.000Z', '2026-09-20T10:00:30.000Z'],
    ['hace 1 minuto', '2026-09-20T10:00:00.000Z', '2026-09-20T10:01:00.000Z'],
    ['hace 5 minutos', '2026-09-20T10:00:00.000Z', '2026-09-20T10:05:00.000Z'],
    ['hace 1 hora', '2026-09-20T10:00:00.000Z', '2026-09-20T11:00:00.000Z'],
    ['hace 3 horas', '2026-09-20T10:00:00.000Z', '2026-09-20T13:00:00.000Z'],
    ['hace 1 día', '2026-09-20T10:00:00.000Z', '2026-09-21T10:00:00.000Z'],
    ['hace 2 días', '2026-09-20T10:00:00.000Z', '2026-09-22T10:00:00.000Z'],
  ])('%s', (esperado, momento, ahoraIso) => {
    expect(formatearVejezDeInstantanea(momento, new Date(ahoraIso))).toBe(esperado)
  })
})
