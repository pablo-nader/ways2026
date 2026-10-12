import { describe, expect, it } from 'vitest'
import {
  aClienteListado,
  aMedioPagoListado,
  buscarArticuloOffline,
  buscarClientesOffline,
  edadDeInstantaneaEnMinutos,
  elegirEscalon,
  elegirCeldaDeFamilia,
  esInstantaneaValida,
  formatearVejezDeInstantanea,
  guardarInstantaneaLocal,
  leerInstantaneaLocal,
  parsearEntradaDeEscaneoOffline,
  precioEnLista,
  purgarInstantaneaLocal,
  preciosVigentesDeLasLineas,
  preciosVigentesOffline,
  resolverPreciosOffline,
  resolverPreviaOffline,
  TOPE_DE_RESULTADOS_DE_CLIENTES,
  todasLasLineasTienenPrecioOffline,
} from './instantaneaOffline'
import type { AlmacenClaveValor } from './almacenPos'
import type {
  ArticuloDeInstantanea,
  ClienteDeInstantanea,
  ComboDeInstantanea,
  EscalonDeCantidad,
  EscalonDeFamilia,
  InstantaneaDePos,
  PrecioDeListaDeInstantanea,
} from '../api/tipos'
import type { LineaCarrito } from '../api/carrito'
import { calcularSubtotalPrevia } from '../api/ventas'

const LISTA = 5
const OTRA_LISTA = 8

function almacenFake(datosIniciales: Record<string, unknown> = {}): AlmacenClaveValor & { datos: Map<string, unknown> } {
  const datos = new Map<string, unknown>(Object.entries(datosIniciales))
  return {
    datos,
    async leer<T>(clave: string) {
      return (datos.has(clave) ? (datos.get(clave) as T) : null) ?? null
    },
    async escribir<T>(clave: string, valor: T) {
      datos.set(clave, valor)
      return true
    },
  }
}

function precioFixture(sobrescribir: Partial<PrecioDeListaDeInstantanea> = {}): PrecioDeListaDeInstantanea {
  return { idListaPrecio: LISTA, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [], ...sobrescribir }
}

function articuloFixture(sobrescribir: Partial<ArticuloDeInstantanea> = {}): ArticuloDeInstantanea {
  return {
    idArticulo: 1,
    codigoInterno: 'A0001',
    nombre: 'Coca Cola 1L',
    codigosBarra: ['7790001234567'],
    idAlicuotaIva: 1,
    porcentajeIva: 21,
    preciosPorLista: [precioFixture()],
    acumulaEnVenta: true,
    ...sobrescribir,
  }
}

function clienteFixture(sobrescribir: Partial<ClienteDeInstantanea> = {}): ClienteDeInstantanea {
  return {
    idCliente: 1,
    numero: 1,
    nombre: 'Consumidor Final',
    apellido: null,
    razonSocial: null,
    tipoDocumento: null,
    numeroDocumento: null,
    idCondicionFiscal: 1,
    idEmpresa: null,
    idListaPrecio: LISTA,
    esConsumidorFinal: true,
    saldo: 0,
    limiteCredito: 0,
    creditoIlimitado: true,
    ...sobrescribir,
  }
}

function instantaneaFixture(sobrescribir: Partial<InstantaneaDePos> = {}): InstantaneaDePos {
  return {
    momento: '2026-09-20T10:00:00.000Z',
    idPuntoVenta: 7,
    articulos: [articuloFixture()],
    clientes: [clienteFixture()],
    mediosDePago: [],
    toleranciaPago: 0,
    ...sobrescribir,
  }
}

function lineaFixture(sobrescribir: Partial<LineaCarrito> = {}): LineaCarrito {
  return {
    idLinea: `l-${sobrescribir.idArticulo ?? 1}`,
    idArticulo: 1,
    codigoInterno: 'A0001',
    nombre: 'Coca Cola 1L',
    codigoBarra: '7790001234567',
    acumulaEnVenta: true,
    cantidad: 1,
    ...sobrescribir,
  }
}

/** Tres tramos con valores TODOS distintos entre sí y distintos del precio plano: cualquier
 * confusión de tramo (el primero, el último del array, el plano) cambia los tres campos a la vez,
 * así que ninguna aserción puede pasar por coincidencia aritmética. */
const ESCALON_3: EscalonDeCantidad = { cantidadDesde: 3, precioFinal: 90, descuentoUnitario: 10, aplicadas: [{ idOferta: 31, nombre: '3 o más', descuentoUnitario: 10 }] }
const ESCALON_6: EscalonDeCantidad = { cantidadDesde: 6, precioFinal: 80, descuentoUnitario: 20, aplicadas: [{ idOferta: 61, nombre: '6 o más', descuentoUnitario: 20 }] }
const ESCALON_12: EscalonDeCantidad = { cantidadDesde: 12, precioFinal: 70, descuentoUnitario: 30, aplicadas: [{ idOferta: 121, nombre: '12 o más', descuentoUnitario: 30 }] }
const ESCALON_FRACCIONARIO: EscalonDeCantidad = { cantidadDesde: 1.5, precioFinal: 85, descuentoUnitario: 15, aplicadas: [{ idOferta: 151, nombre: 'Desde 1,5 kg', descuentoUnitario: 15 }] }

/** Precio con los tres tramos y precio plano 100/100/0. */
function precioConEscalonesFixture(sobrescribir: Partial<PrecioDeListaDeInstantanea> = {}): PrecioDeListaDeInstantanea {
  return precioFixture({ escalones: [ESCALON_3, ESCALON_6, ESCALON_12], ...sobrescribir })
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
    expect(buscarArticuloOffline(instantaneaFixture(), '7790001234567')).toEqual({
      idArticulo: 1,
      codigoInterno: 'A0001',
      nombre: 'Coca Cola 1L',
      codigoBarra: '7790001234567',
      cantidad: 1,
      acumulaEnVenta: true,
    })
  })

  it('encuentra por código interno, con codigoBarra null (mismo criterio que el escaneo online)', () => {
    expect(buscarArticuloOffline(instantaneaFixture(), 'A0001')).toEqual({
      idArticulo: 1,
      codigoInterno: 'A0001',
      nombre: 'Coca Cola 1L',
      codigoBarra: null,
      cantidad: 1,
      acumulaEnVenta: true,
    })
  })

  it('lleva acumulaEnVenta del artículo de la instantánea', () => {
    const instantanea = instantaneaFixture({ articulos: [articuloFixture({ acumulaEnVenta: false })] })
    expect(buscarArticuloOffline(instantanea, 'A0001')?.acumulaEnVenta).toBe(false)
  })

  it('un artículo de una instantánea guardada antes del campo acumula (true)', () => {
    const { acumulaEnVenta: _omitido, ...sinCampo } = articuloFixture()
    expect(buscarArticuloOffline(instantaneaFixture({ articulos: [sinCampo] }), 'A0001')?.acumulaEnVenta).toBe(true)
  })

  it('respeta la sintaxis "cantidad*codigo"', () => {
    expect(buscarArticuloOffline(instantaneaFixture(), '5*7790001234567')?.cantidad).toBe(5)
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

describe('buscarArticuloOffline — unidad de venta', () => {
  it('propaga la unidad de venta del artículo, por código de barra y por código interno', () => {
    const instantanea = instantaneaFixture({ articulos: [articuloFixture({ unidadVenta: 'Peso' })] })

    expect(buscarArticuloOffline(instantanea, '7790001234567')?.unidadVenta).toBe('Peso')
    expect(buscarArticuloOffline(instantanea, 'A0001')?.unidadVenta).toBe('Peso')
    expect(
      buscarArticuloOffline(instantaneaFixture({ articulos: [articuloFixture({ unidadVenta: 'Unidad' })] }), 'A0001')?.unidadVenta,
    ).toBe('Unidad')
  })

  it('una instantánea guardada antes de que existiera el dato sigue siendo válida y no declara unidad (permisivo)', async () => {
    const antigua = instantaneaFixture({ articulos: [articuloFixture()] })
    expect('unidadVenta' in antigua.articulos[0]).toBe(false)
    expect(esInstantaneaValida(antigua)).toBe(true)

    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, { instantanea: antigua, etag: '"viejo"', verificadaEn: '2026-10-02T12:00:00.000Z' })
    const leida = await leerInstantaneaLocal(almacen)

    expect(leida).not.toBeNull()
    expect(buscarArticuloOffline(leida!.instantanea, 'A0001')?.unidadVenta).toBeUndefined()
  })

  it('la unidad de venta sobrevive al guardado y a la lectura del almacén local', async () => {
    const instantanea = instantaneaFixture({ articulos: [articuloFixture({ unidadVenta: 'Unidad' })] })
    const almacen = almacenFake()

    await guardarInstantaneaLocal(almacen, { instantanea, etag: '"nuevo"', verificadaEn: '2026-10-02T12:00:00.000Z' })

    expect(buscarArticuloOffline((await leerInstantaneaLocal(almacen))!.instantanea, 'A0001')?.unidadVenta).toBe('Unidad')
  })
})

describe('precioEnLista', () => {
  it('devuelve el precio de la lista pedida y null si el artículo no tiene precio ahí', () => {
    const enOtra = precioFixture({ idListaPrecio: OTRA_LISTA, precioOriginal: 140 })
    const articulo = articuloFixture({ preciosPorLista: [precioFixture(), enOtra] })

    expect(precioEnLista(articulo, OTRA_LISTA)).toEqual(enOtra)
    expect(precioEnLista(articulo, 99)).toBeNull()
  })
})

describe('elegirEscalon', () => {
  // Cláusula bajo prueba: `escalon.cantidadDesde <= cantidad`. En el umbral EXACTO el tramo tiene
  // que aplicar — un off-by-one a `<` deja al cliente que lleva justo 3 pagando precio pleno.
  it('en la cantidad EXACTA del umbral el tramo ya aplica (cláusula `cantidadDesde <= cantidad`, nunca `<`)', () => {
    expect(elegirEscalon(precioConEscalonesFixture(), 3)).toEqual(ESCALON_3)
  })

  it('una unidad por debajo del primer umbral no elige ningún tramo (rige el precio plano)', () => {
    expect(elegirEscalon(precioConEscalonesFixture(), 2)).toBeNull()
  })

  // Cláusula bajo prueba: gana el tramo con el `cantidadDesde` MÁS ALTO entre los que califican.
  it('con tres tramos, gana el de umbral más alto entre los que califican — nunca el primero', () => {
    expect(elegirEscalon(precioConEscalonesFixture(), 7)).toEqual(ESCALON_6)
  })

  // Con los tramos al revés (12/6/3) y cantidad 7: por umbral gana el de 6, por posición en el
  // array ganaría el de 3.
  it('elige por umbral y no por posición en el array — con los tramos al revés sigue ganando el de umbral más alto', () => {
    expect(elegirEscalon(precioConEscalonesFixture({ escalones: [ESCALON_12, ESCALON_6, ESCALON_3] }), 7)).toEqual(ESCALON_6)
  })

  it('por encima del último umbral gana el último tramo', () => {
    expect(elegirEscalon(precioConEscalonesFixture(), 50)).toEqual(ESCALON_12)
  })

  // La comparación es decimal, no entera: un umbral de 1,5 kg (artículos de balanza) aplica en
  // 1,5 exacto. Truncar a entero lo rompe.
  it('con un umbral fraccionario (peso) el tramo aplica en el umbral exacto y por encima', () => {
    const precio = precioConEscalonesFixture({ escalones: [ESCALON_FRACCIONARIO] })

    expect(elegirEscalon(precio, 1.4)).toBeNull()
    expect(elegirEscalon(precio, 1.5)).toEqual(ESCALON_FRACCIONARIO)
    expect(elegirEscalon(precio, 2.25)).toEqual(ESCALON_FRACCIONARIO)
  })

  it.each([
    ['sin la clave `escalones`', precioFixture()],
    ['con `escalones: null`', precioFixture({ escalones: null })],
    ['con `escalones: []`', precioFixture({ escalones: [] })],
  ])('%s devuelve null a cualquier cantidad', (_titulo, precio) => {
    expect(elegirEscalon(precio, 1)).toBeNull()
    expect(elegirEscalon(precio, 3)).toBeNull()
    expect(elegirEscalon(precio, 999)).toBeNull()
  })
})

describe('preciosVigentesOffline', () => {
  it('el tramo pisa precioFinal/descuentoUnitario/aplicadas', () => {
    expect(preciosVigentesOffline(precioConEscalonesFixture(), 6)).toEqual({
      precioOriginal: 100,
      precioFinal: 80,
      descuentoUnitario: 20,
      aplicadas: [{ idOferta: 61, nombre: '6 o más', descuentoUnitario: 20 }],
    })
  })

  // `precioOriginal` es constante entre cantidades y el tramo a propósito NO lo trae: el bruto de
  // lista tiene que seguir siendo el de la lista (el backend le resta `descuentoUnitario`).
  it('precioOriginal NUNCA lo pisa el tramo — sigue siendo el precio de lista', () => {
    const precio = precioConEscalonesFixture({ precioOriginal: 100 })
    expect(preciosVigentesOffline(precio, 12).precioOriginal).toBe(100)
    expect(preciosVigentesOffline(precio, 1).precioOriginal).toBe(100)
  })

  it('sin tramo vigente devuelve los campos planos tal cual', () => {
    const precio = precioConEscalonesFixture({ precioOriginal: 100, precioFinal: 95, descuentoUnitario: 5, aplicadas: [{ idOferta: 7, nombre: 'Promo suelta', descuentoUnitario: 5 }] })
    expect(preciosVigentesOffline(precio, 2)).toEqual({
      precioOriginal: 100,
      precioFinal: 95,
      descuentoUnitario: 5,
      aplicadas: [{ idOferta: 7, nombre: 'Promo suelta', descuentoUnitario: 5 }],
    })
  })
})

describe('resolverPreciosOffline', () => {
  /** Dos listas con valores distintos en cada campo: tomar la lista equivocada (o la primera) se ve
   * en todos a la vez. */
  const dosListas = () =>
    instantaneaFixture({
      articulos: [
        articuloFixture({
          preciosPorLista: [
            precioFixture({ idListaPrecio: LISTA, precioOriginal: 150, precioFinal: 120, descuentoUnitario: 30, aplicadas: [{ idOferta: 9, nombre: '20% off', descuentoUnitario: 30 }] }),
            precioFixture({ idListaPrecio: OTRA_LISTA, precioOriginal: 110, precioFinal: 99, descuentoUnitario: 11, aplicadas: [{ idOferta: 4, nombre: 'Mayorista', descuentoUnitario: 11 }] }),
          ],
        }),
      ],
    })

  it('resuelve con el precio de la lista del cliente, indexado por idLinea', () => {
    expect(resolverPreciosOffline([lineaFixture()], dosListas(), OTRA_LISTA)['l-1']).toEqual({
      idArticulo: 1,
      idListaPrecio: OTRA_LISTA,
      precioOriginal: 110,
      precioFinal: 99,
      descuentoUnitario: 11,
      aplicadas: [{ idOferta: 4, nombre: 'Mayorista', descuentoUnitario: 11 }],
    })
    expect(resolverPreciosOffline([lineaFixture()], dosListas(), LISTA)['l-1']).toMatchObject({ idListaPrecio: LISTA, precioOriginal: 150, precioFinal: 120 })
  })

  it('una línea sin precio en la lista del cliente queda ausente del índice, aunque tenga precio en otra', () => {
    expect(resolverPreciosOffline([lineaFixture()], dosListas(), 99)['l-1']).toBeUndefined()
  })

  it('una línea sin artículo en la instantánea queda ausente del índice (nunca un precio inventado)', () => {
    expect(resolverPreciosOffline([lineaFixture({ idArticulo: 999 })], instantaneaFixture(), LISTA)['l-999']).toBeUndefined()
  })

  it('la cantidad de la línea elige el tramo de esa lista, y precioOriginal queda el de lista', () => {
    const instantanea = instantaneaFixture({ articulos: [articuloFixture({ preciosPorLista: [precioConEscalonesFixture()] })] })
    expect(resolverPreciosOffline([lineaFixture({ cantidad: 6 })], instantanea, LISTA)['l-1']).toEqual({
      idArticulo: 1,
      idListaPrecio: LISTA,
      precioOriginal: 100,
      precioFinal: 80,
      descuentoUnitario: 20,
      aplicadas: [{ idOferta: 61, nombre: '6 o más', descuentoUnitario: 20 }],
    })
    expect(resolverPreciosOffline([lineaFixture({ cantidad: 2 })], instantanea, LISTA)['l-1']).toMatchObject({ precioFinal: 100, descuentoUnitario: 0 })
  })

  it('cada línea cae en el tramo de SU cantidad, nunca en el de la otra línea', () => {
    const instantanea = instantaneaFixture({
      articulos: [
        articuloFixture({ idArticulo: 1, preciosPorLista: [precioConEscalonesFixture()] }),
        articuloFixture({ idArticulo: 2, codigosBarra: ['7790009999999'], preciosPorLista: [precioConEscalonesFixture()] }),
      ],
    })
    const indice = resolverPreciosOffline([lineaFixture({ idArticulo: 1, cantidad: 3 }), lineaFixture({ idArticulo: 2, cantidad: 12 })], instantanea, LISTA)
    expect(indice['l-1']).toMatchObject({ precioFinal: 90, descuentoUnitario: 10 })
    expect(indice['l-2']).toMatchObject({ precioFinal: 70, descuentoUnitario: 30 })
  })

  it('dos líneas del MISMO artículo resuelven cada una con el tramo de su propia cantidad', () => {
    const instantanea = instantaneaFixture({ articulos: [articuloFixture({ preciosPorLista: [precioConEscalonesFixture()] })] })
    const indice = resolverPreciosOffline(
      [lineaFixture({ idLinea: 'a', cantidad: 3 }), lineaFixture({ idLinea: 'b', cantidad: 12 })],
      instantanea,
      LISTA,
    )
    expect(Object.keys(indice).sort()).toEqual(['a', 'b'])
    expect(indice.a).toMatchObject({ precioFinal: 90, descuentoUnitario: 10 })
    expect(indice.b).toMatchObject({ precioFinal: 70, descuentoUnitario: 30 })
  })
})

describe('todasLasLineasTienenPrecioOffline', () => {
  it('true cuando todas las líneas tienen precio en la lista pedida', () => {
    expect(todasLasLineasTienenPrecioOffline([lineaFixture()], instantaneaFixture(), LISTA)).toBe(true)
  })

  it('false si el artículo tiene precio solo en otra lista', () => {
    expect(todasLasLineasTienenPrecioOffline([lineaFixture()], instantaneaFixture(), OTRA_LISTA)).toBe(false)
  })

  it('false con el carrito vacío (nunca "todas" vacuamente true)', () => {
    expect(todasLasLineasTienenPrecioOffline([], instantaneaFixture(), LISTA)).toBe(false)
  })

  it('false si CUALQUIER línea no está en la instantánea (all-or-nothing)', () => {
    const lineas = [lineaFixture({ idArticulo: 1 }), lineaFixture({ idArticulo: 2 })]
    expect(todasLasLineasTienenPrecioOffline(lineas, instantaneaFixture(), LISTA)).toBe(false)
  })
})

describe('instantánea persistida', () => {
  it('guarda y vuelve a leer la instantánea con su etiqueta y la hora de verificación', async () => {
    const almacen = almacenFake()
    const instantanea = instantaneaFixture()

    await guardarInstantaneaLocal(almacen, { instantanea, etag: '"abc"', verificadaEn: '2026-09-20T11:00:00.000Z' })

    await expect(leerInstantaneaLocal(almacen)).resolves.toEqual({
      version: 2,
      instantanea,
      etag: '"abc"',
      verificadaEn: '2026-09-20T11:00:00.000Z',
    })
  })

  // La instantánea de antes de los precios por lista quedó bajo la clave 'instantanea', con el
  // precio plano en el artículo: nunca se lee, se vuelve a descargar entera.
  it('una instantánea de la forma anterior, bajo la clave anterior, se ignora', async () => {
    const vieja = { momento: '2026-09-20T10:00:00.000Z', idPuntoVenta: 7, articulos: [{ idArticulo: 1, precioOriginal: 100, precioFinal: 100 }], mediosDePago: [], toleranciaPago: 0 }
    await expect(leerInstantaneaLocal(almacenFake({ instantanea: vieja }))).resolves.toBeNull()
  })

  it.each([
    ['sin versión', { instantanea: instantaneaFixture(), etag: null, verificadaEn: '2026-09-20T11:00:00.000Z' }],
    ['con artículos sin precios por lista', { version: 2, instantanea: { ...instantaneaFixture(), articulos: [{ idArticulo: 1, codigoInterno: 'A', codigosBarra: [], porcentajeIva: 21, precioOriginal: 100 }] }, etag: null, verificadaEn: 'x' }],
    ['sin clientes', { version: 2, instantanea: { ...instantaneaFixture(), clientes: undefined }, etag: null, verificadaEn: 'x' }],
    ['sin hora de verificación', { version: 2, instantanea: instantaneaFixture(), etag: null }],
    ['que no es un objeto', 'basura'],
  ])('lo guardado %s se descarta (nunca se cotiza con una forma desconocida)', async (_titulo, guardado) => {
    await expect(leerInstantaneaLocal(almacenFake({ 'instantanea.v2': guardado }))).resolves.toBeNull()
  })

  it('esInstantaneaValida rechaza la forma que responde un servidor anterior a los precios por lista', () => {
    const delServidorViejo = { momento: '2026-09-20T10:00:00.000Z', idPuntoVenta: 7, articulos: [{ idArticulo: 1, codigoInterno: 'A', codigosBarra: [], porcentajeIva: 21, precioOriginal: 100 }], mediosDePago: [], toleranciaPago: 0 }
    expect(esInstantaneaValida(delServidorViejo)).toBe(false)
    expect(esInstantaneaValida(instantaneaFixture())).toBe(true)
  })

  it('esInstantaneaValida acepta artículos sin acumulaEnVenta (instantánea guardada antes del campo)', () => {
    const { acumulaEnVenta: _omitido, ...sinCampo } = articuloFixture()
    expect(esInstantaneaValida(instantaneaFixture({ articulos: [sinCampo] }))).toBe(true)
  })

  it.each([
    ['true', true],
    ['false', false],
  ])('esInstantaneaValida acepta acumulaEnVenta = %s', (_titulo, valor) => {
    expect(esInstantaneaValida(instantaneaFixture({ articulos: [articuloFixture({ acumulaEnVenta: valor })] }))).toBe(true)
  })

  it('esInstantaneaValida rechaza un acumulaEnVenta que no es booleano', () => {
    const conTexto = { ...articuloFixture(), acumulaEnVenta: 'si' }
    expect(esInstantaneaValida({ ...instantaneaFixture(), articulos: [conTexto] })).toBe(false)
  })

  it('una instantánea guardada sin acumulaEnVenta en sus artículos se sigue leyendo', async () => {
    const { acumulaEnVenta: _omitido, ...sinCampo } = articuloFixture()
    const guardada = { version: 2, instantanea: instantaneaFixture({ articulos: [sinCampo] }), etag: null, verificadaEn: '2026-09-20T11:00:00.000Z' }
    await expect(leerInstantaneaLocal(almacenFake({ 'instantanea.v2': guardada }))).resolves.not.toBeNull()
  })
})

describe('buscarClientesOffline', () => {
  const clientes = [
    clienteFixture(),
    clienteFixture({ idCliente: 20, numero: 20, nombre: 'José', apellido: 'Pérez', esConsumidorFinal: false, numeroDocumento: '20-30405060-7', idListaPrecio: OTRA_LISTA }),
    clienteFixture({ idCliente: 21, numero: 21, nombre: 'Ana', apellido: 'Gómez', razonSocial: 'Almacén Ñandú SRL', esConsumidorFinal: false, numeroDocumento: '27111222' }),
  ]
  const instantanea = instantaneaFixture({ clientes })
  const ids = (termino: string) => buscarClientesOffline(instantanea, termino).map((c) => c.idCliente)

  it('busca por nombre sin distinguir mayúsculas ni acentos', () => {
    expect(ids('jose')).toEqual([20])
    expect(ids('PEREZ')).toEqual([20])
    expect(ids('josé pérez')).toEqual([20])
  })

  it('busca por razón social', () => {
    expect(ids('nandu')).toEqual([21])
  })

  it('busca por número de cliente exacto', () => {
    expect(ids('21')).toEqual([21])
  })

  it('busca por documento, con o sin separadores', () => {
    expect(ids('20304050607')).toEqual([20])
    expect(ids('30405060')).toEqual([20])
    expect(ids('27111222')).toEqual([21])
  })

  it('sin coincidencias devuelve vacío', () => {
    expect(ids('zzz')).toEqual([])
  })

  it('el término vacío devuelve los primeros clientes, con el tope de la primera página', () => {
    const muchos = Array.from({ length: TOPE_DE_RESULTADOS_DE_CLIENTES + 5 }, (_, i) =>
      clienteFixture({ idCliente: 100 + i, numero: 100 + i, nombre: `Cliente ${i}`, esConsumidorFinal: false }),
    )
    const resultado = buscarClientesOffline(instantaneaFixture({ clientes: muchos }), '')
    expect(resultado).toHaveLength(TOPE_DE_RESULTADOS_DE_CLIENTES)
    expect(resultado[0].idCliente).toBe(100)
  })
})

describe('aClienteListado / aMedioPagoListado', () => {
  it('lleva cada campo del cliente de la instantánea al ClienteListado', () => {
    const cliente = clienteFixture({
      idCliente: 20, numero: 31, nombre: 'José', apellido: 'Pérez', razonSocial: 'JP SA', tipoDocumento: 'Cuit',
      numeroDocumento: '20-1', idCondicionFiscal: 4, idEmpresa: 3, idListaPrecio: OTRA_LISTA, esConsumidorFinal: false,
      saldo: 1234.5, limiteCredito: 9000, creditoIlimitado: false,
    })

    expect(aClienteListado(cliente)).toEqual({
      id: 20, numero: 31, nombre: 'José', apellido: 'Pérez', razonSocial: 'JP SA', tipoDocumento: 'Cuit', numeroDocumento: '20-1',
      idCondicionFiscal: 4, nacimiento: null, domicilio: null, telefono: null, celular: null, email: null, observaciones: null,
      idListaPrecio: OTRA_LISTA, limiteCredito: 9000, creditoIlimitado: false, saldo: 1234.5, activo: true, idEmpresa: 3, esConsumidorFinal: false,
    })
  })

  // Lista dada de baja (null): un id que ninguna lista tiene, así ningún precio local aplica.
  it('un cliente con la lista dada de baja no resuelve ningún precio local', () => {
    const listado = aClienteListado(clienteFixture({ idListaPrecio: null }))
    expect(resolverPreciosOffline([lineaFixture()], instantaneaFixture(), listado.idListaPrecio)).toEqual({})
  })

  it('lleva el medio de pago con su orden por posición', () => {
    expect(aMedioPagoListado({ idMedioPago: 3, nombre: 'Tarjeta', comportamiento: 'Electronico', admiteVuelto: false, requiereReferencia: true }, 1)).toEqual({
      id: 3, nombre: 'Tarjeta', activo: true, idEmpresa: null, orden: 2, comportamiento: 'Electronico', admiteVuelto: false, requiereReferencia: true, recargoPorcentaje: null,
    })
  })
})

describe('edadDeInstantaneaEnMinutos', () => {
  it('calcula minutos completos transcurridos', () => {
    expect(edadDeInstantaneaEnMinutos('2026-09-20T10:00:00.000Z', new Date('2026-09-20T10:05:30.000Z'))).toBe(5)
  })

  it('nunca negativo — un reloj local atrasado respecto del servidor da 0, no un absurdo negativo', () => {
    expect(edadDeInstantaneaEnMinutos('2026-09-20T10:10:00.000Z', new Date('2026-09-20T10:00:00.000Z'))).toBe(0)
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

describe('purgarInstantaneaLocal', () => {
  it('borra la instantánea guardada y la de la forma anterior', async () => {
    const borradas: string[] = []
    await purgarInstantaneaLocal({
      async eliminar(clave: string) {
        borradas.push(clave)
        return true
      },
    })
    expect(borradas.sort()).toEqual(['instantanea', 'instantanea.v2'])
  })
})

// --- Ofertas de familia: la cantidad de la familia sale de TODO el carrito -----------------------

const FAMILIA_ALFAJORES = 7
const OTRA_FAMILIA = 8

/** Mismos importes que el caso del dueño: lista 2000, "2 x 3500" ⇒ 1750 desde 2 unidades de la
 * familia. Todos los campos de cada celda difieren entre celdas para que elegir mal se vea. */
const CELDA_FAMILIA_2: EscalonDeFamilia = { cantidadDeFamiliaDesde: 2, cantidadDesde: 1, precioFinal: 1750, descuentoUnitario: 250, aplicadas: [{ idOferta: 71, nombre: '2 x 3500', descuentoUnitario: 250 }] }
const CELDA_FAMILIA_2_PROPIA_6: EscalonDeFamilia = { cantidadDeFamiliaDesde: 2, cantidadDesde: 6, precioFinal: 1400, descuentoUnitario: 600, aplicadas: [{ idOferta: 72, nombre: '6 o más', descuentoUnitario: 600 }] }
const CELDA_FAMILIA_4: EscalonDeFamilia = { cantidadDeFamiliaDesde: 4, cantidadDesde: 1, precioFinal: 1500, descuentoUnitario: 500, aplicadas: [{ idOferta: 73, nombre: '4 x 6000', descuentoUnitario: 500 }] }

function alfajorFixture(idArticulo: number, celdas: EscalonDeFamilia[] | undefined = [CELDA_FAMILIA_2], idFamilia: number | null = FAMILIA_ALFAJORES): ArticuloDeInstantanea {
  return articuloFixture({
    idArticulo,
    codigoInterno: `ALF${idArticulo}`,
    codigosBarra: [`77900000${idArticulo}`],
    idFamilia,
    preciosPorLista: [
      precioFixture({
        precioOriginal: 2000,
        precioFinal: 2000,
        escalones: [{ cantidadDesde: 2, precioFinal: 1750, descuentoUnitario: 250, aplicadas: CELDA_FAMILIA_2.aplicadas }],
        escalonesDeFamilia: celdas,
      }),
    ],
  })
}

const A = 1
const B = 2
const C_SIN_FAMILIA = 3
const D_OTRA_FAMILIA = 4

function instantaneaDeFamiliaFixture(celdas: EscalonDeFamilia[] | undefined = [CELDA_FAMILIA_2]) {
  return instantaneaFixture({
    articulos: [
      alfajorFixture(A, celdas),
      alfajorFixture(B, celdas),
      alfajorFixture(C_SIN_FAMILIA, celdas, null),
      alfajorFixture(D_OTRA_FAMILIA, celdas, OTRA_FAMILIA),
    ],
  })
}

function descuentos(lineas: LineaCarrito[], instantanea: InstantaneaDePos) {
  const indice = resolverPreciosOffline(lineas, instantanea, LISTA)
  return lineas.map((l) => indice[l.idLinea]?.descuentoUnitario)
}

describe('elegirCeldaDeFamilia', () => {
  it('sin grilla, o con una cantidad de familia por debajo de todos los umbrales, no elige nada', () => {
    expect(elegirCeldaDeFamilia(precioFixture(), 1, 9)).toBeNull()
    expect(elegirCeldaDeFamilia(precioFixture({ escalonesDeFamilia: null }), 1, 9)).toBeNull()
    expect(elegirCeldaDeFamilia(precioFixture({ escalonesDeFamilia: [CELDA_FAMILIA_2] }), 1, 1)).toBeNull()
  })

  // Cláusula: `cantidadDeFamiliaDesde <= cantidadDeFamilia` — en el umbral exacto entra.
  it('el umbral de familia entra en la cantidad exacta y no en la anterior', () => {
    const precio = precioFixture({ escalonesDeFamilia: [CELDA_FAMILIA_2] })
    expect(elegirCeldaDeFamilia(precio, 1, 2)).toEqual(CELDA_FAMILIA_2)
    expect(elegirCeldaDeFamilia(precio, 1, 1.99)).toBeNull()
  })

  it('toma el umbral de familia MÁS ALTO que entra, aunque la grilla venga desordenada', () => {
    const precio = precioFixture({ escalonesDeFamilia: [CELDA_FAMILIA_4, CELDA_FAMILIA_2] })
    expect(elegirCeldaDeFamilia(precio, 1, 3)).toEqual(CELDA_FAMILIA_2)
    expect(elegirCeldaDeFamilia(precio, 1, 5)).toEqual(CELDA_FAMILIA_4)
  })

  it('dentro del umbral de familia elegido toma la cantidad propia más alta que entra', () => {
    const precio = precioFixture({ escalonesDeFamilia: [CELDA_FAMILIA_2_PROPIA_6, CELDA_FAMILIA_2] })
    expect(elegirCeldaDeFamilia(precio, 5, 8)).toEqual(CELDA_FAMILIA_2)
    expect(elegirCeldaDeFamilia(precio, 6, 8)).toEqual(CELDA_FAMILIA_2_PROPIA_6)
  })

  it('con una cantidad propia por debajo de la base (fracción) cae en la celda de menor cantidad propia del grupo', () => {
    const precio = precioFixture({ escalonesDeFamilia: [CELDA_FAMILIA_2_PROPIA_6, CELDA_FAMILIA_2] })
    expect(elegirCeldaDeFamilia(precio, 0.5, 2.5)).toEqual(CELDA_FAMILIA_2)
  })
})

describe('resolverPreciosOffline — cantidad agregada por familia', () => {
  it('dos artículos de la misma familia con cantidad 1 completan el mínimo: ambos a 1750', () => {
    const lineas = [lineaFixture({ idArticulo: A, cantidad: 1 }), lineaFixture({ idArticulo: B, cantidad: 1 })]
    const indice = resolverPreciosOffline(lineas, instantaneaDeFamiliaFixture(), LISTA)
    expect(indice['l-1']).toMatchObject({ precioOriginal: 2000, precioFinal: 1750, descuentoUnitario: 250 })
    expect(indice['l-2']).toMatchObject({ precioOriginal: 2000, precioFinal: 1750, descuentoUnitario: 250 })
    expect(indice['l-1'].aplicadas).toEqual(CELDA_FAMILIA_2.aplicadas)
  })

  it('un solo artículo con cantidad 1 no llega al mínimo: queda en 2000', () => {
    expect(descuentos([lineaFixture({ idArticulo: A, cantidad: 1 })], instantaneaDeFamiliaFixture())).toEqual([0])
  })

  it('un solo artículo con cantidad 2 llega por sí solo (camino de siempre): 1750', () => {
    expect(descuentos([lineaFixture({ idArticulo: A, cantidad: 2 })], instantaneaDeFamiliaFixture())).toEqual([250])
  })

  it('un artículo sin familia no suma con el de la familia: ambos a 2000', () => {
    const lineas = [lineaFixture({ idArticulo: A, cantidad: 1 }), lineaFixture({ idArticulo: C_SIN_FAMILIA, cantidad: 1 })]
    expect(descuentos(lineas, instantaneaDeFamiliaFixture())).toEqual([0, 0])
  })

  it('dos familias distintas no se suman entre sí', () => {
    const lineas = [lineaFixture({ idArticulo: A, cantidad: 1 }), lineaFixture({ idArticulo: D_OTRA_FAMILIA, cantidad: 1 })]
    expect(descuentos(lineas, instantaneaDeFamiliaFixture())).toEqual([0, 0])
  })

  it('dos líneas del MISMO artículo de la familia se suman entre sí', () => {
    const lineas = [lineaFixture({ idLinea: 'x', idArticulo: A, cantidad: 1 }), lineaFixture({ idLinea: 'y', idArticulo: A, cantidad: 1 })]
    expect(descuentos(lineas, instantaneaDeFamiliaFixture())).toEqual([250, 250])
  })

  it('cada línea toma la celda de SU cantidad propia dentro de la cantidad de familia del carrito', () => {
    const instantanea = instantaneaDeFamiliaFixture([CELDA_FAMILIA_2, CELDA_FAMILIA_2_PROPIA_6])
    const lineas = [lineaFixture({ idArticulo: A, cantidad: 1 }), lineaFixture({ idArticulo: B, cantidad: 6 })]
    expect(descuentos(lineas, instantanea)).toEqual([250, 600])
  })

  it('con varios umbrales de familia, la cantidad total del carrito elige el más alto', () => {
    const instantanea = instantaneaDeFamiliaFixture([CELDA_FAMILIA_2, CELDA_FAMILIA_4])
    expect(descuentos([lineaFixture({ idArticulo: A, cantidad: 1 }), lineaFixture({ idArticulo: B, cantidad: 2 })], instantanea)).toEqual([250, 250])
    expect(descuentos([lineaFixture({ idArticulo: A, cantidad: 1 }), lineaFixture({ idArticulo: B, cantidad: 3 })], instantanea)).toEqual([500, 500])
  })

  it('una instantánea sin los campos nuevos (guardada antes) cotiza como siempre: sin descuento por familia', () => {
    const vieja = instantaneaFixture({
      articulos: [A, B].map((id) => articuloFixture({ idArticulo: id, codigosBarra: [`${id}`], preciosPorLista: [precioFixture({ precioOriginal: 2000, precioFinal: 2000 })] })),
    })
    const lineas = [lineaFixture({ idArticulo: A, cantidad: 1 }), lineaFixture({ idArticulo: B, cantidad: 1 })]
    expect(descuentos(lineas, vieja)).toEqual([0, 0])
  })

  it('una línea cuyo artículo no está en la instantánea queda sin precio y no altera el precio de las demás', () => {
    const lineas = [lineaFixture({ idArticulo: A, cantidad: 1 }), lineaFixture({ idArticulo: 999, cantidad: 1 })]
    expect(descuentos(lineas, instantaneaDeFamiliaFixture())).toEqual([0, undefined])
  })
})

describe('resolverPreciosOffline — la suma por familia es exacta a 3 decimales', () => {
  const celdaDe = (cantidadDeFamiliaDesde: number): EscalonDeFamilia => ({ ...CELDA_FAMILIA_2, cantidadDeFamiliaDesde })
  const instantaneaConUmbral = (umbral: number) =>
    instantaneaFixture({ articulos: [A, B, C_SIN_FAMILIA].map((id) => alfajorFixture(id, [celdaDe(umbral)], id === C_SIN_FAMILIA ? null : FAMILIA_ALFAJORES)) })
  const lineasDe = (...cantidades: number[]) => cantidades.map((cantidad, i) => lineaFixture({ idLinea: `p${i}`, idArticulo: i === 2 ? C_SIN_FAMILIA : i === 0 ? A : B, cantidad }))

  it.each([
    [0.1, 0.7, 0.8],
    [0.3, 0.6, 0.9],
  ])('%s + %s alcanza el umbral %s aunque en punto flotante quede por debajo', (x, y, umbral) => {
    expect(x + y).toBeLessThan(umbral)
    expect(descuentos(lineasDe(x, y), instantaneaConUmbral(umbral))).toEqual([250, 250])
  })

  it('tres líneas de la familia que suman exactamente 2,000 alcanzan el umbral 2', () => {
    const lineas = [
      lineaFixture({ idLinea: 'x', idArticulo: A, cantidad: 0.6 }),
      lineaFixture({ idLinea: 'y', idArticulo: B, cantidad: 0.7 }),
      lineaFixture({ idLinea: 'z', idArticulo: B, cantidad: 0.7 }),
    ]
    expect(0.6 + 0.7 + 0.7).toBeLessThan(2)
    expect(descuentos(lineas, instantaneaConUmbral(2))).toEqual([250, 250, 250])
  })

  it('0,1 + 0,699 queda por debajo del umbral 0,8: el redondeo no empuja una suma que no llega', () => {
    expect(descuentos(lineasDe(0.1, 0.699), instantaneaConUmbral(0.8))).toEqual([0, 0])
  })
})

describe('preciosVigentesDeLasLineas', () => {
  it('devuelve un precio por línea, en el orden de las líneas, y null donde no hay precio en la lista', () => {
    const lineas = [lineaFixture({ idArticulo: A, cantidad: 1 }), lineaFixture({ idArticulo: 999, cantidad: 1 }), lineaFixture({ idArticulo: B, cantidad: 1 })]
    const precios = preciosVigentesDeLasLineas(lineas, instantaneaDeFamiliaFixture(), LISTA)
    expect(precios.map((p) => p?.descuentoUnitario ?? null)).toEqual([250, null, 250])
    expect(preciosVigentesDeLasLineas(lineas, instantaneaDeFamiliaFixture(), 99)).toEqual([null, null, null])
  })
})

describe('esInstantaneaValida — campos de familia', () => {
  it('acepta artículos con y sin idFamilia y precios con y sin grilla', () => {
    expect(esInstantaneaValida(instantaneaDeFamiliaFixture())).toBe(true)
    expect(esInstantaneaValida(instantaneaFixture())).toBe(true)
  })

  it('rechaza una grilla que no es una lista', () => {
    const rota = instantaneaFixture({ articulos: [articuloFixture({ preciosPorLista: [precioFixture({ escalonesDeFamilia: 'x' as unknown as EscalonDeFamilia[] })] })] })
    expect(esInstantaneaValida(rota)).toBe(false)
  })
})

// --- Combos (T5a) -------------------------------------------------------------------------------

const PANCHO = 11
const COCA = 12
const SPRITE = 13
const FAMILIA_BEBIDAS = 40

function articuloDeCombosFixture(idArticulo: number, precio: number, sobrescribir: Partial<ArticuloDeInstantanea> = {}): ArticuloDeInstantanea {
  return articuloFixture({
    idArticulo,
    codigoInterno: `A${idArticulo}`,
    nombre: `Artículo ${idArticulo}`,
    codigosBarra: [`${idArticulo}`],
    unidadVenta: 'Unidad',
    preciosPorLista: [precioFixture({ precioOriginal: precio, precioFinal: precio })],
    ...sobrescribir,
  })
}

const comboPanchoCoca: ComboDeInstantanea = {
  idCombo: 7,
  codigo: 7,
  nombre: 'Pancho + Coca',
  precio: 5000,
  componentes: [
    { idArticulo: PANCHO, cantidad: 1 },
    { idArticulo: COCA, cantidad: 1 },
  ],
}

function instantaneaConCombos(combos: ComboDeInstantanea[] | undefined, articulos?: ArticuloDeInstantanea[]): InstantaneaDePos {
  return instantaneaFixture({
    articulos: articulos ?? [articuloDeCombosFixture(PANCHO, 3000), articuloDeCombosFixture(COCA, 2500), articuloDeCombosFixture(SPRITE, 2000)],
    ...(combos === undefined ? {} : { combos }),
  })
}

const lineasDe = (...pares: [number, number][]) => pares.map(([idArticulo, cantidad]) => lineaFixture({ idLinea: `l-${idArticulo}`, idArticulo, cantidad }))

describe('esInstantaneaValida — combos', () => {
  it('una instantánea guardada antes de los combos (sin la clave) sigue siendo válida', () => {
    expect(esInstantaneaValida(instantaneaConCombos(undefined))).toBe(true)
  })

  it('acepta una lista de combos vacía y combos bien formados, con componentes de artículo o de familia', () => {
    expect(esInstantaneaValida(instantaneaConCombos([]))).toBe(true)
    const conFamilia: ComboDeInstantanea = {
      ...comboPanchoCoca,
      componentes: [
        { idArticulo: PANCHO, cantidad: 1 },
        { idFamilia: FAMILIA_BEBIDAS, cantidad: 2 },
      ],
    }
    expect(esInstantaneaValida(instantaneaConCombos([comboPanchoCoca, conFamilia]))).toBe(true)
  })

  it.each([
    ['combos que no es una lista', 'x'],
    ['un combo que no es un objeto', [3]],
    ['un combo sin precio numérico', [{ ...comboPanchoCoca, precio: '5000' }]],
    ['un combo sin nombre', [{ ...comboPanchoCoca, nombre: undefined }]],
    ['un combo con componentes que no es una lista', [{ ...comboPanchoCoca, componentes: 'x' }]],
    ['un componente sin cantidad numérica', [{ ...comboPanchoCoca, componentes: [{ idArticulo: PANCHO }] }]],
    ['un componente con idArticulo que no es número', [{ ...comboPanchoCoca, componentes: [{ idArticulo: 'a', cantidad: 1 }] }]],
    ['un componente con idFamilia que no es número', [{ ...comboPanchoCoca, componentes: [{ idFamilia: 'a', cantidad: 1 }] }]],
  ])('rechaza %s', (_descripcion, combos) => {
    expect(esInstantaneaValida({ ...instantaneaConCombos(undefined), combos })).toBe(false)
  })

  it('leerInstantaneaLocal devuelve la instantánea vieja sin combos y descarta la de combos malformados', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, { instantanea: instantaneaConCombos(undefined), etag: null, verificadaEn: '2026-09-20T10:00:00.000Z' })
    expect((await leerInstantaneaLocal(almacen))?.instantanea.combos).toBeUndefined()

    const rota = { ...instantaneaConCombos(undefined), combos: 'x' } as unknown as InstantaneaDePos
    await guardarInstantaneaLocal(almacen, { instantanea: rota, etag: null, verificadaEn: '2026-09-20T10:00:00.000Z' })
    expect(await leerInstantaneaLocal(almacen)).toBeNull()
  })
})

describe('resolverPreviaOffline — combos', () => {
  const total = (lineas: LineaCarrito[], instantanea: InstantaneaDePos) => calcularSubtotalPrevia(lineas, resolverPreviaOffline(lineas, instantanea, LISTA).precios)

  it('el ejemplo del dueño: pancho 3000 + coca 2500 con el combo a 5000 totaliza 5000 y reparte 272,73 / 227,27', () => {
    const lineas = lineasDe([PANCHO, 1], [COCA, 1])
    const { precios, combos } = resolverPreviaOffline(lineas, instantaneaConCombos([comboPanchoCoca]), LISTA)

    expect(precios['l-11'].combo).toEqual({ unidadesConsumidas: 1, ahorro: 272.73, idCombo: 7 })
    expect(precios['l-12'].combo).toEqual({ unidadesConsumidas: 1, ahorro: 227.27, idCombo: 7 })
    expect(combos).toEqual([{ idCombo: 7, codigo: 7, nombre: 'Pancho + Coca', veces: 1, ahorro: 500 }])
    expect(total(lineas, instantaneaConCombos([comboPanchoCoca]))).toBe(5000)
  })

  it('una instantánea sin combos cotiza como siempre: ninguna línea trae combo y el total es el de lista', () => {
    const lineas = lineasDe([PANCHO, 1], [COCA, 1])
    const { precios, combos } = resolverPreviaOffline(lineas, instantaneaConCombos(undefined), LISTA)

    expect(precios['l-11']).not.toHaveProperty('combo')
    expect(combos).toEqual([])
    expect(total(lineas, instantaneaConCombos(undefined))).toBe(5500)
  })

  it('un artículo pesable (o con unidad ausente) no entra al combo aunque la cantidad sea entera', () => {
    const lineas = lineasDe([PANCHO, 1], [COCA, 1])
    for (const unidadVenta of ['Peso', undefined] as const) {
      const articulos = [articuloDeCombosFixture(PANCHO, 3000, { unidadVenta }), articuloDeCombosFixture(COCA, 2500)]
      const instantanea = instantaneaConCombos([comboPanchoCoca], articulos)
      expect(resolverPreviaOffline(lineas, instantanea, LISTA).combos).toEqual([])
      expect(total(lineas, instantanea)).toBe(5500)
    }
  })

  it('una cantidad no entera deja la línea fuera del combo', () => {
    const lineas = lineasDe([PANCHO, 1.5], [COCA, 1])
    expect(resolverPreviaOffline(lineas, instantaneaConCombos([comboPanchoCoca]), LISTA).combos).toEqual([])
  })

  it('un componente de familia se cubre con cualquier artículo de la familia, vía idFamilia', () => {
    const articulos = [
      articuloDeCombosFixture(PANCHO, 3000),
      articuloDeCombosFixture(COCA, 2500, { idFamilia: FAMILIA_BEBIDAS }),
      articuloDeCombosFixture(SPRITE, 2000, { idFamilia: FAMILIA_BEBIDAS }),
    ]
    const combo: ComboDeInstantanea = {
      ...comboPanchoCoca,
      precio: 4500,
      componentes: [
        { idArticulo: PANCHO, cantidad: 1 },
        { idFamilia: FAMILIA_BEBIDAS, cantidad: 1 },
      ],
    }
    const lineas = lineasDe([PANCHO, 1], [SPRITE, 1])
    const { precios, combos } = resolverPreviaOffline(lineas, instantaneaConCombos([combo], articulos), LISTA)

    expect(combos).toHaveLength(1)
    expect(precios['l-13'].combo?.idCombo).toBe(7)
    expect(total(lineas, instantaneaConCombos([combo], articulos))).toBe(4500)
  })

  it('la compuerta D1: un combo que no es más barato que la oferta de esas unidades no se aplica', () => {
    const articulos = [
      articuloDeCombosFixture(PANCHO, 3000, { preciosPorLista: [precioFixture({ precioOriginal: 3000, precioFinal: 2000, descuentoUnitario: 1000 })] }),
      articuloDeCombosFixture(COCA, 2500),
    ]
    const lineas = lineasDe([PANCHO, 1], [COCA, 1])

    expect(resolverPreviaOffline(lineas, instantaneaConCombos([{ ...comboPanchoCoca, precio: 4500 }], articulos), LISTA).combos).toEqual([])
    expect(resolverPreviaOffline(lineas, instantaneaConCombos([{ ...comboPanchoCoca, precio: 4499.99 }], articulos), LISTA).combos).toHaveLength(1)
  })

  it('las unidades fuera del combo conservan su oferta y el total suma el combo más lo que sobra', () => {
    const articulos = [
      articuloDeCombosFixture(PANCHO, 3000, { preciosPorLista: [precioFixture({ precioOriginal: 3000, precioFinal: 2500, descuentoUnitario: 500 })] }),
      articuloDeCombosFixture(COCA, 2500),
    ]
    const lineas = lineasDe([PANCHO, 2], [COCA, 1])
    const instantanea = instantaneaConCombos([{ ...comboPanchoCoca, precio: 4800 }], articulos)

    expect(resolverPreviaOffline(lineas, instantanea, LISTA).precios['l-11'].combo).toMatchObject({ unidadesConsumidas: 1, ahorro: 381.82 })
    // combo 4800 + la segunda unidad de pancho con su oferta (3000 − 500).
    expect(total(lineas, instantanea)).toBe(7300)
  })

  it('una línea sin precio en la lista queda fuera y el resto sigue evaluándose', () => {
    const lineas = lineasDe([PANCHO, 1], [999, 1], [COCA, 1])
    const { precios } = resolverPreviaOffline(lineas, instantaneaConCombos([comboPanchoCoca]), LISTA)

    expect(precios['l-999']).toBeUndefined()
    expect(precios['l-11'].combo?.idCombo).toBe(7)
  })

  it('una línea consumida por dos combos lleva el de menor id', () => {
    const articulos = [articuloDeCombosFixture(PANCHO, 3000), articuloDeCombosFixture(COCA, 2500), articuloDeCombosFixture(SPRITE, 2000)]
    const cinco: ComboDeInstantanea = { ...comboPanchoCoca, idCombo: 5, codigo: 5, precio: 5000 }
    const tres: ComboDeInstantanea = {
      ...comboPanchoCoca,
      idCombo: 3,
      codigo: 3,
      precio: 4500,
      componentes: [
        { idArticulo: PANCHO, cantidad: 1 },
        { idArticulo: SPRITE, cantidad: 1 },
      ],
    }
    const lineas = lineasDe([PANCHO, 2], [COCA, 1], [SPRITE, 1])

    const { precios, combos } = resolverPreviaOffline(lineas, instantaneaConCombos([cinco, tres], articulos), LISTA)

    expect(combos.map((c) => c.idCombo)).toEqual([3, 5])
    expect(precios['l-11'].combo?.idCombo).toBe(3)
  })

  it('resolverPreciosOffline devuelve los mismos precios que resolverPreviaOffline', () => {
    const lineas = lineasDe([PANCHO, 1], [COCA, 1])
    const instantanea = instantaneaConCombos([comboPanchoCoca])
    expect(resolverPreciosOffline(lineas, instantanea, LISTA)).toEqual(resolverPreviaOffline(lineas, instantanea, LISTA).precios)
  })
})
