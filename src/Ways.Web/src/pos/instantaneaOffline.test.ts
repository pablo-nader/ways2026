import { describe, expect, it } from 'vitest'
import {
  buscarArticuloOffline,
  edadDeInstantaneaEnMinutos,
  elegirEscalon,
  formatearVejezDeInstantanea,
  parsearEntradaDeEscaneoOffline,
  preciosVigentesOffline,
  resolverPreciosOffline,
  todasLasLineasTienenPrecioOffline,
} from './instantaneaOffline'
import type { ArticuloDeInstantanea, EscalonDeCantidad, InstantaneaDePos } from '../api/tipos'
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

/** Tres tramos con valores TODOS distintos entre sí y distintos del precio plano: cualquier
 * confusión de tramo (el primero, el último del array, el plano) cambia los tres campos a la vez,
 * así que ninguna aserción puede pasar por coincidencia aritmética. */
const ESCALON_3: EscalonDeCantidad = { cantidadDesde: 3, precioFinal: 90, descuentoUnitario: 10, aplicadas: [{ idOferta: 31, nombre: '3 o más', descuentoUnitario: 10 }] }
const ESCALON_6: EscalonDeCantidad = { cantidadDesde: 6, precioFinal: 80, descuentoUnitario: 20, aplicadas: [{ idOferta: 61, nombre: '6 o más', descuentoUnitario: 20 }] }
const ESCALON_12: EscalonDeCantidad = { cantidadDesde: 12, precioFinal: 70, descuentoUnitario: 30, aplicadas: [{ idOferta: 121, nombre: '12 o más', descuentoUnitario: 30 }] }
const ESCALON_FRACCIONARIO: EscalonDeCantidad = { cantidadDesde: 1.5, precioFinal: 85, descuentoUnitario: 15, aplicadas: [{ idOferta: 151, nombre: 'Desde 1,5 kg', descuentoUnitario: 15 }] }

/** Artículo con los tres tramos y precio plano 100/100/0 — la forma que trae la instantánea nueva
 * para un artículo con oferta por volumen. */
function articuloConEscalonesFixture(sobrescribir: Partial<ArticuloDeInstantanea> = {}): ArticuloDeInstantanea {
  return articuloFixture({ precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [], escalones: [ESCALON_3, ESCALON_6, ESCALON_12], ...sobrescribir })
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

describe('elegirEscalon', () => {
  // Cláusula bajo prueba: `escalon.cantidadDesde <= cantidad`. En el umbral EXACTO el tramo tiene
  // que aplicar — un off-by-one a `<` deja al cliente que lleva justo 3 pagando precio pleno.
  it('en la cantidad EXACTA del umbral el tramo ya aplica (cláusula `cantidadDesde <= cantidad`, nunca `<`)', () => {
    expect(elegirEscalon(articuloConEscalonesFixture(), 3)).toEqual(ESCALON_3)
  })

  it('una unidad por debajo del primer umbral no elige ningún tramo (rige el precio plano)', () => {
    expect(elegirEscalon(articuloConEscalonesFixture(), 2)).toBeNull()
  })

  // Cláusula bajo prueba: gana el tramo con el `cantidadDesde` MÁS ALTO entre los que califican.
  // Con tres tramos (3/6/12) y cantidad 7 califican dos (3 y 6): quedarse con el PRIMERO que
  // califica devuelve el de 3 y este test muere.
  it('con tres tramos, gana el de umbral más alto entre los que califican — nunca el primero', () => {
    expect(elegirEscalon(articuloConEscalonesFixture(), 7)).toEqual(ESCALON_6)
  })

  // Misma cláusula, otro confound: elegir "el último del array que califica" da la respuesta
  // correcta mientras el array venga ascendente. Acá llega DESCENDENTE (12/6/3) con cantidad 7:
  // por umbral gana el de 6, por posición en el array ganaría el de 3.
  it('elige por umbral y no por posición en el array — con los tramos al revés sigue ganando el de umbral más alto', () => {
    const articulo = articuloConEscalonesFixture({ escalones: [ESCALON_12, ESCALON_6, ESCALON_3] })
    expect(elegirEscalon(articulo, 7)).toEqual(ESCALON_6)
  })

  it('por encima del último umbral gana el último tramo', () => {
    expect(elegirEscalon(articuloConEscalonesFixture(), 50)).toEqual(ESCALON_12)
  })

  // Cláusula bajo prueba: la comparación es decimal, no entera. `cantidad_minima` es
  // `numeric(12,3)` server-side y acá se vende por peso (artículos de balanza), así que un umbral
  // de 1,5 kg es representable y la cantidad del carrito es fraccionaria. Cualquier truncamiento a
  // entero (comparar `Math.floor(cantidad)`, o parsear el umbral como int) deja al cliente que
  // lleva 1,5 kg pagando precio pleno: con umbral 1,5 y cantidad 1,5, `floor(1,5) = 1 >= 1,5` es
  // falso y este test muere.
  it('con un umbral fraccionario (peso) el tramo aplica en el umbral exacto y por encima', () => {
    const articulo = articuloConEscalonesFixture({ escalones: [ESCALON_FRACCIONARIO] })

    expect(elegirEscalon(articulo, 1.4)).toBeNull()
    expect(elegirEscalon(articulo, 1.5)).toEqual(ESCALON_FRACCIONARIO)
    expect(elegirEscalon(articulo, 2.25)).toEqual(ESCALON_FRACCIONARIO)
  })

  // Camino de compatibilidad con la instantánea VIEJA: la persistida en IndexedDB no tiene versión
  // de esquema ni validación (`almacenPos.ts` castea lo que haya), así que un dispositivo que
  // atraviese el deploy sin conexión lee artículos SIN la clave `escalones`. Eso es el caso
  // normal, nunca un error.
  it.each([
    ['sin la clave `escalones` (instantánea vieja, de antes del deploy)', articuloFixture()],
    ['con `escalones: null`', articuloFixture({ escalones: null })],
    ['con `escalones: []`', articuloFixture({ escalones: [] })],
  ])('%s devuelve null a cualquier cantidad', (_titulo, articulo) => {
    expect(elegirEscalon(articulo, 1)).toBeNull()
    expect(elegirEscalon(articulo, 3)).toBeNull()
    expect(elegirEscalon(articulo, 999)).toBeNull()
  })
})

describe('preciosVigentesOffline', () => {
  it('el tramo pisa precioFinal/descuentoUnitario/aplicadas', () => {
    expect(preciosVigentesOffline(articuloConEscalonesFixture(), 6)).toEqual({
      precioOriginal: 100,
      precioFinal: 80,
      descuentoUnitario: 20,
      aplicadas: [{ idOferta: 61, nombre: '6 o más', descuentoUnitario: 20 }],
    })
  })

  // `precioOriginal` es constante entre cantidades y el tramo a propósito NO lo trae: el bruto de
  // lista tiene que seguir siendo el del artículo (el backend le resta `descuentoUnitario`, ver
  // `enriquecerLineasConPrecioOffline` — pisarlo con el neto del tramo restaría el descuento dos
  // veces).
  it('precioOriginal NUNCA lo pisa el tramo — sigue siendo el precio de lista del artículo', () => {
    const articulo = articuloConEscalonesFixture({ precioOriginal: 100 })
    expect(preciosVigentesOffline(articulo, 12).precioOriginal).toBe(100)
    expect(preciosVigentesOffline(articulo, 1).precioOriginal).toBe(100)
  })

  it('sin tramo vigente devuelve los campos planos del artículo tal cual', () => {
    const articulo = articuloConEscalonesFixture({ precioOriginal: 100, precioFinal: 95, descuentoUnitario: 5, aplicadas: [{ idOferta: 7, nombre: 'Promo suelta', descuentoUnitario: 5 }] })
    expect(preciosVigentesOffline(articulo, 2)).toEqual({
      precioOriginal: 100,
      precioFinal: 95,
      descuentoUnitario: 5,
      aplicadas: [{ idOferta: 7, nombre: 'Promo suelta', descuentoUnitario: 5 }],
    })
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

  // La cantidad de la línea es lo que elige el tramo: sin esto, una oferta "3 o más" nunca aplicaba
  // offline (la instantánea congelaba el precio de cantidad 1) y el cliente que llevaba 3 pagaba
  // pleno.
  it('la cantidad de la línea elige el tramo, y precioOriginal queda el de lista', () => {
    const instantanea = instantaneaFixture({ articulos: [articuloConEscalonesFixture()] })
    expect(resolverPreciosOffline([lineaFixture({ cantidad: 6 })], instantanea, 5)[1]).toEqual({
      idArticulo: 1,
      idListaPrecio: 5,
      precioOriginal: 100,
      precioFinal: 80,
      descuentoUnitario: 20,
      aplicadas: [{ idOferta: 61, nombre: '6 o más', descuentoUnitario: 20 }],
    })
  })

  it('con la cantidad por debajo del primer umbral resuelve el precio plano, no el primer tramo', () => {
    const instantanea = instantaneaFixture({ articulos: [articuloConEscalonesFixture()] })
    expect(resolverPreciosOffline([lineaFixture({ cantidad: 2 })], instantanea, 5)[1]).toEqual({
      idArticulo: 1,
      idListaPrecio: 5,
      precioOriginal: 100,
      precioFinal: 100,
      descuentoUnitario: 0,
      aplicadas: [],
    })
  })

  // Compatibilidad hacia atrás: una instantánea vieja (sin la clave `escalones` en ningún
  // artículo) tiene que seguir vendiendo al precio plano a CUALQUIER cantidad — nunca `undefined`
  // ni `NaN` en un importe.
  it('una instantánea vieja (sin `escalones`) resuelve el precio plano a cualquier cantidad', () => {
    const instantanea = instantaneaFixture({ articulos: [articuloFixture({ precioOriginal: 150, precioFinal: 120, descuentoUnitario: 30, aplicadas: [{ idOferta: 9, nombre: '20% off', descuentoUnitario: 30 }] })] })
    for (const cantidad of [1, 3, 10]) {
      expect(resolverPreciosOffline([lineaFixture({ cantidad })], instantanea, 5)[1]).toEqual({
        idArticulo: 1,
        idListaPrecio: 5,
        precioOriginal: 150,
        precioFinal: 120,
        descuentoUnitario: 30,
        aplicadas: [{ idOferta: 9, nombre: '20% off', descuentoUnitario: 30 }],
      })
    }
  })

  // Un tramo por línea, no uno por carrito: dos líneas del MISMO artículo no existen (el carrito
  // agrupa por `idArticulo`), pero dos artículos con tablas distintas y cantidades distintas sí —
  // cada uno tiene que caer en SU tramo.
  it('cada línea cae en el tramo de SU cantidad, nunca en el de la otra línea', () => {
    const instantanea = instantaneaFixture({
      articulos: [
        articuloConEscalonesFixture({ idArticulo: 1 }),
        articuloConEscalonesFixture({ idArticulo: 2, codigosBarra: ['7790009999999'] }),
      ],
    })
    const indice = resolverPreciosOffline([lineaFixture({ idArticulo: 1, cantidad: 3 }), lineaFixture({ idArticulo: 2, cantidad: 12 })], instantanea, 5)
    expect(indice[1]).toMatchObject({ precioFinal: 90, descuentoUnitario: 10 })
    expect(indice[2]).toMatchObject({ precioFinal: 70, descuentoUnitario: 30 })
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
