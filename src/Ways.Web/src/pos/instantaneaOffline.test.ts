import { describe, expect, it } from 'vitest'
import {
  aClienteListado,
  aMedioPagoListado,
  articuloDeAtajoOffline,
  atajosDeImporteOffline,
  buscarArticuloOffline,
  buscarClientesOffline,
  edadDeInstantaneaEnMinutos,
  elegirEscalon,
  elegirCeldaDeFamilia,
  esInstantaneaValida,
  formatearVejezDeInstantanea,
  guardarInstantaneaLocal,
  leerInstantaneaLocal,
  lineasConImporteDeArticuloOrdinario,
  parsearEntradaDeEscaneoOffline,
  precioEnLista,
  purgarInstantaneaLocal,
  preciosVigentesDeLasLineas,
  preciosVigentesOffline,
  resolverPreciosOffline,
  resolverDocumentoOffline,
  resolverAtajoOffline,
  resolverEscaneoOffline,
  resolverPreviaOffline,
  sanearCombosDeInstantanea,
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
  MedioPagoDeInstantanea,
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
    ['0,25*7790001234567', 0.25],
    ['0.25*7790001234567', 0.25],
    ['2,5*A0001', 2.5],
  ])('el prefijo "%s" acepta coma o punto como separador decimal', (entrada, cantidadEsperada) => {
    expect(parsearEntradaDeEscaneoOffline(entrada)?.cantidad).toBe(cantidadEsperada)
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

  it('lleva el domicilio y los teléfonos de la copia local para la venta con envío; ausentes quedan en null', () => {
    const conContacto = aClienteListado(clienteFixture({ domicilio: 'Mitre 123', telefono: '4444-0000', celular: '11-5555' }))
    expect(conContacto).toMatchObject({ domicilio: 'Mitre 123', telefono: '4444-0000', celular: '11-5555' })

    const sinContacto = aClienteListado(clienteFixture())
    expect(sinContacto).toMatchObject({ domicilio: null, telefono: null, celular: null })
  })

  // Lista dada de baja (null): un id que ninguna lista tiene, así ningún precio local aplica.
  it('un cliente con la lista dada de baja no resuelve ningún precio local', () => {
    const listado = aClienteListado(clienteFixture({ idListaPrecio: null }))
    expect(resolverPreciosOffline([lineaFixture()], instantaneaFixture(), listado.idListaPrecio)).toEqual({})
  })

  it('lleva el medio de pago con su orden por posición', () => {
    expect(aMedioPagoListado({ idMedioPago: 3, nombre: 'Tarjeta', comportamiento: 'Electronico', admiteVuelto: false, requiereReferencia: true }, 1)).toEqual({
      id: 3, nombre: 'Tarjeta', activo: true, idEmpresa: null, orden: 2, comportamiento: 'Electronico', admiteVuelto: false, requiereReferencia: true, recargoPorcentaje: null, porcentajeFacturacion: 0,
    })
  })

  it('lleva el porcentaje de facturación del medio de pago', () => {
    const medio = aMedioPagoListado({ idMedioPago: 3, nombre: 'Transferencia', comportamiento: 'Electronico', admiteVuelto: false, requiereReferencia: false, porcentajeFacturacion: 37.5 }, 0)
    expect(medio.porcentajeFacturacion).toBe(37.5)
  })

  // Una copia guardada antes de este campo no lo trae: se lee como 0 (no se factura), nunca undefined.
  it('una copia guardada sin porcentaje de facturación lo lee como 0', () => {
    const guardado = { idMedioPago: 3, nombre: 'Efectivo', comportamiento: 'Efectivo', admiteVuelto: true, requiereReferencia: false } as MedioPagoDeInstantanea
    expect(aMedioPagoListado(guardado, 0).porcentajeFacturacion).toBe(0)
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

  const combosMalformados: [string, unknown][] = [
    ['un combo que no es un objeto', 3],
    ['un combo sin idCombo numérico', { ...comboPanchoCoca, idCombo: '7' }],
    ['un combo sin precio numérico', { ...comboPanchoCoca, precio: '5000' }],
    ['un combo sin nombre', { ...comboPanchoCoca, nombre: undefined }],
    ['un combo con componentes que no es una lista', { ...comboPanchoCoca, componentes: 'x' }],
    ['un componente sin cantidad numérica', { ...comboPanchoCoca, componentes: [{ idArticulo: PANCHO }] }],
    ['un componente con idArticulo que no es número', { ...comboPanchoCoca, componentes: [{ idArticulo: 'a', cantidad: 1 }] }],
    ['un componente con idFamilia que no es número', { ...comboPanchoCoca, componentes: [{ idFamilia: 'a', cantidad: 1 }] }],
  ]
  const otroCombo: ComboDeInstantanea = { ...comboPanchoCoca, idCombo: 9, codigo: 9, nombre: 'Otro', precio: 5100 }

  it.each(combosMalformados)('%s NO invalida la instantánea: el combo se descarta al sanear y los buenos se conservan', (_descripcion, malo) => {
    const instantanea = { ...instantaneaConCombos(undefined), combos: [comboPanchoCoca, malo, otroCombo] }

    expect(esInstantaneaValida(instantanea)).toBe(true)
    expect(sanearCombosDeInstantanea(instantanea as InstantaneaDePos).combos).toEqual([comboPanchoCoca, otroCombo])
  })

  it('un combos que no es una lista no invalida la instantánea y se trata como ausente', () => {
    const rota = { ...instantaneaConCombos(undefined), combos: 'x' }

    expect(esInstantaneaValida(rota)).toBe(true)
    const saneada = sanearCombosDeInstantanea(rota as unknown as InstantaneaDePos)
    expect(saneada).not.toHaveProperty('combos')
    expect(saneada.articulos).toEqual(rota.articulos)
  })

  it('un combo sin codigo (es un rótulo que el motor no lee) se conserva y se cotiza', () => {
    const { codigo: _codigo, ...sinCodigo } = comboPanchoCoca
    const instantanea = sanearCombosDeInstantanea(instantaneaConCombos([sinCodigo as ComboDeInstantanea]))

    expect(instantanea.combos).toHaveLength(1)
    const { combos } = resolverPreviaOffline(lineasDe([PANCHO, 1], [COCA, 1]), instantanea, LISTA)
    expect(combos).toEqual([{ idCombo: 7, codigo: 0, nombre: 'Pancho + Coca', veces: 1, ahorro: 500 }])
  })

  it('sanear una instantánea sin combos o con todos bien formados devuelve la misma instantánea', () => {
    const sinCombos = instantaneaConCombos(undefined)
    const bien = instantaneaConCombos([comboPanchoCoca])
    expect(sanearCombosDeInstantanea(sinCombos)).toBe(sinCombos)
    expect(sanearCombosDeInstantanea(bien)).toEqual(bien)
  })

  it('leerInstantaneaLocal devuelve la instantánea vieja sin combos', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, { instantanea: instantaneaConCombos(undefined), etag: null, verificadaEn: '2026-09-20T10:00:00.000Z' })
    expect((await leerInstantaneaLocal(almacen))?.instantanea.combos).toBeUndefined()
  })

  it('leerInstantaneaLocal con un combo malformado entre buenos devuelve la instantánea con los buenos', async () => {
    const almacen = almacenFake()
    const guardada = { ...instantaneaConCombos(undefined), combos: [comboPanchoCoca, 3, otroCombo] } as unknown as InstantaneaDePos
    await guardarInstantaneaLocal(almacen, { instantanea: guardada, etag: 'e', verificadaEn: '2026-09-20T10:00:00.000Z' })

    const leida = await leerInstantaneaLocal(almacen)
    expect(leida?.instantanea.combos).toEqual([comboPanchoCoca, otroCombo])
    expect(leida?.etag).toBe('e')
  })

  it('leerInstantaneaLocal con un combos que no es una lista devuelve la instantánea sin combos, no null', async () => {
    const almacen = almacenFake()
    const rota = { ...instantaneaConCombos(undefined), combos: 'x' } as unknown as InstantaneaDePos
    await guardarInstantaneaLocal(almacen, { instantanea: rota, etag: null, verificadaEn: '2026-09-20T10:00:00.000Z' })

    const leida = await leerInstantaneaLocal(almacen)
    expect(leida).not.toBeNull()
    expect(leida?.instantanea).not.toHaveProperty('combos')
  })

  it('una instantánea con un combo malformado igual cotiza: los buenos se aplican y el total baja', () => {
    const lineas = lineasDe([PANCHO, 1], [COCA, 1])
    const instantanea = sanearCombosDeInstantanea({ ...instantaneaConCombos(undefined), combos: [3, comboPanchoCoca] } as unknown as InstantaneaDePos)

    const { precios, combos } = resolverPreviaOffline(lineas, instantanea, LISTA)

    expect(combos.map((c) => c.idCombo)).toEqual([7])
    expect(calcularSubtotalPrevia(lineas, precios)).toBe(5000)
  })
})

describe('resolverPreviaOffline — combos', () => {
  const total = (lineas: LineaCarrito[], instantanea: InstantaneaDePos) => calcularSubtotalPrevia(lineas, resolverPreviaOffline(lineas, instantanea, LISTA).precios)

  it('el ejemplo del dueño: pancho 3000 + coca 2500 con el combo a 5000 totaliza 5000 y reparte 273 / 227', () => {
    const lineas = lineasDe([PANCHO, 1], [COCA, 1])
    const { precios, combos } = resolverPreviaOffline(lineas, instantaneaConCombos([comboPanchoCoca]), LISTA)

    expect(precios['l-11'].combo).toEqual({ unidadesConsumidas: 1, ahorro: 273, idCombo: 7 })
    expect(precios['l-12'].combo).toEqual({ unidadesConsumidas: 1, ahorro: 227, idCombo: 7 })
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

  it.each(['Peso', undefined] as const)('un artículo pesable (unidad %s) entra al combo: la unidad de venta viaja al motor', (unidadVenta) => {
    const lineas = lineasDe([PANCHO, 1.5], [COCA, 1])
    const articulos = [articuloDeCombosFixture(PANCHO, 3000, { unidadVenta }), articuloDeCombosFixture(COCA, 2500)]
    const instantanea = instantaneaConCombos([comboPanchoCoca], articulos)

    const { precios, combos } = resolverPreviaOffline(lineas, instantanea, LISTA)

    expect(combos.map((c) => [c.idCombo, c.veces])).toEqual([[7, 1]])
    expect(precios['l-11'].combo).toEqual({ unidadesConsumidas: 1, ahorro: 273, idCombo: 7 })
    // combo 5000 + el medio kilo que sobra a precio de lista (1500).
    expect(total(lineas, instantanea)).toBe(6500)
  })

  it('un artículo por unidad con cantidad no entera queda fuera del combo', () => {
    const lineas = lineasDe([PANCHO, 1.5], [COCA, 1])
    expect(resolverPreviaOffline(lineas, instantaneaConCombos([comboPanchoCoca]), LISTA).combos).toEqual([])
  })

  describe('componentes pesables', () => {
    const JAMON = 21
    const QUESO = 22
    const fiambres = [
      articuloDeCombosFixture(JAMON, 10000, { unidadVenta: 'Peso' }),
      articuloDeCombosFixture(QUESO, 8000, { unidadVenta: 'Peso' }),
    ]
    const picada: ComboDeInstantanea = {
      idCombo: 9,
      codigo: 9,
      nombre: 'Picada',
      precio: 3000,
      componentes: [
        { idArticulo: JAMON, cantidad: 0.2 },
        { idArticulo: QUESO, cantidad: 0.2 },
      ],
    }

    it('215 g de jamón y 200 g de queso aplican el combo una vez y los 15 g que sobran se cobran a precio de lista', () => {
      const lineas = lineasDe([JAMON, 0.215], [QUESO, 0.2])
      const instantanea = instantaneaConCombos([picada], fiambres)

      const { precios, combos } = resolverPreviaOffline(lineas, instantanea, LISTA)

      expect(precios['l-21'].combo).toEqual({ unidadesConsumidas: 0.2, ahorro: 333, idCombo: 9 })
      expect(precios['l-22'].combo).toEqual({ unidadesConsumidas: 0.2, ahorro: 267, idCombo: 9 })
      expect(combos).toEqual([{ idCombo: 9, codigo: 9, nombre: 'Picada', veces: 1, ahorro: 600 }])
      expect(total(lineas, instantanea)).toBe(3150)
    })

    it('400 g de cada uno lo aplican dos veces y 150 g de jamón no lo aplican', () => {
      const instantanea = instantaneaConCombos([picada], fiambres)

      expect(resolverPreviaOffline(lineasDe([JAMON, 0.4], [QUESO, 0.4]), instantanea, LISTA).combos.map((c) => c.veces)).toEqual([2])
      expect(total(lineasDe([JAMON, 0.4], [QUESO, 0.4]), instantanea)).toBe(6000)
      expect(resolverPreviaOffline(lineasDe([JAMON, 0.15], [QUESO, 0.2]), instantanea, LISTA).combos).toEqual([])
    })

    it('un artículo por unidad no completa un componente de 200 g aunque el combo lo nombre', () => {
      const articulos = [articuloDeCombosFixture(JAMON, 10000), articuloDeCombosFixture(QUESO, 8000, { unidadVenta: 'Peso' })]
      const lineas = lineasDe([JAMON, 1], [QUESO, 0.2])

      expect(resolverPreviaOffline(lineas, instantaneaConCombos([picada], articulos), LISTA).combos).toEqual([])
    })
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
    expect(resolverPreviaOffline(lineas, instantaneaConCombos([{ ...comboPanchoCoca, precio: 4499 }], articulos), LISTA).combos).toHaveLength(1)
    // 4499,99 se cobra 4500 al peso: no es más barato que la oferta.
    expect(resolverPreviaOffline(lineas, instantaneaConCombos([{ ...comboPanchoCoca, precio: 4499.99 }], articulos), LISTA).combos).toEqual([])
  })

  it('las unidades fuera del combo conservan su oferta y el total suma el combo más lo que sobra', () => {
    const articulos = [
      articuloDeCombosFixture(PANCHO, 3000, { preciosPorLista: [precioFixture({ precioOriginal: 3000, precioFinal: 2500, descuentoUnitario: 500 })] }),
      articuloDeCombosFixture(COCA, 2500),
    ]
    const lineas = lineasDe([PANCHO, 2], [COCA, 1])
    const instantanea = instantaneaConCombos([{ ...comboPanchoCoca, precio: 4800 }], articulos)

    expect(resolverPreviaOffline(lineas, instantanea, LISTA).precios['l-11'].combo).toMatchObject({ unidadesConsumidas: 1, ahorro: 382 })
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

  it('una línea con descuento de oferta NEGATIVO (un recargo) queda fuera de la detección, como en el servidor', () => {
    const articulos = [
      articuloDeCombosFixture(PANCHO, 3000, { preciosPorLista: [precioFixture({ precioOriginal: 3000, precioFinal: 3500, descuentoUnitario: -500 })] }),
      articuloDeCombosFixture(COCA, 2500),
    ]
    const lineas = lineasDe([PANCHO, 1], [COCA, 1])

    const { lineas: resueltas, combos } = resolverDocumentoOffline(lineas, instantaneaConCombos([comboPanchoCoca], articulos), LISTA)

    // Sin la exclusión el combo (5000) le ganaría a las unidades con su recargo (3500 + 2500) y se aplicaría.
    expect(combos).toEqual([])
    expect(resueltas.map((r) => r?.combo)).toEqual([undefined, undefined])
  })

  it('con un precio de fracción de centavo el combo se aplica y ninguna parte lleva más de dos decimales (el servidor las rechazaría)', () => {
    // Pancho a 0,005 y coca a 2 con el combo a 1: la lista del pancho es 0,01 y todo el cobro cae en la coca.
    const articulosSubcentavo = [articuloDeCombosFixture(PANCHO, 0.005), articuloDeCombosFixture(COCA, 2)]
    const lineas = lineasDe([PANCHO, 1], [COCA, 1])

    const { lineas: resueltas, combos } = resolverDocumentoOffline(lineas, instantaneaConCombos([{ ...comboPanchoCoca, precio: 1 }], articulosSubcentavo), LISTA)

    expect(resueltas.map((r) => r?.combo)).toEqual([
      { unidadesConsumidas: 1, ahorro: 0.01, idCombo: 7 },
      { unidadesConsumidas: 1, ahorro: 1, idCombo: 7 },
    ])
    expect(combos).toEqual([{ idCombo: 7, codigo: 7, nombre: 'Pancho + Coca', veces: 1, ahorro: 1.01 }])
  })

  it('resolverPreciosOffline devuelve los mismos precios que resolverPreviaOffline', () => {
    const lineas = lineasDe([PANCHO, 1], [COCA, 1])
    const instantanea = instantaneaConCombos([comboPanchoCoca])
    expect(resolverPreciosOffline(lineas, instantanea, LISTA)).toEqual(resolverPreviaOffline(lineas, instantanea, LISTA).precios)
  })
})

// --- Artículos por importe ----------------------------------------------------------------------

const FIAMBRERIA = 500

function articuloPorImporteFixture(sobrescribir: Partial<ArticuloDeInstantanea> = {}): ArticuloDeInstantanea {
  return articuloFixture({
    idArticulo: FIAMBRERIA,
    codigoInterno: '500',
    nombre: 'Fiambrería',
    codigosBarra: [],
    unidadVenta: 'Importe',
    preciosPorLista: [],
    acumulaEnVenta: false,
    teclaAtajo: 'F5',
    ...sobrescribir,
  })
}

function instantaneaConImporte(sobrescribir: Partial<InstantaneaDePos> = {}): InstantaneaDePos {
  return instantaneaFixture({
    articulos: [articuloFixture(), articuloPorImporteFixture()],
    listas: [
      { idListaPrecio: LISTA, modo: 'Fija', porcentaje: null },
      { idListaPrecio: OTRA_LISTA, modo: 'Fija', porcentaje: null },
    ],
    ...sobrescribir,
  })
}

const lineaPorImporte = (importe: number, idLinea = `f-${importe}`) =>
  lineaFixture({ idLinea, idArticulo: FIAMBRERIA, codigoInterno: '500', nombre: 'Fiambrería', codigoBarra: null, unidadVenta: 'Importe', acumulaEnVenta: false, cantidad: 1, importe })

describe('resolverEscaneoOffline — artículo por importe', () => {
  it('"150*500" da cantidad 1 con el importe tipeado', () => {
    expect(resolverEscaneoOffline(instantaneaConImporte(), '150*500')).toEqual({
      tipo: 'articulo',
      articulo: {
        idArticulo: FIAMBRERIA,
        codigoInterno: '500',
        nombre: 'Fiambrería',
        codigoBarra: null,
        cantidad: 1,
        acumulaEnVenta: false,
        unidadVenta: 'Importe',
        teclaAtajo: 'F5',
        importe: 150,
      },
    })
  })

  it('lee la coma decimal del cajero como punto', () => {
    const resultado = resolverEscaneoOffline(instantaneaConImporte(), '150,50*500')

    expect(resultado.tipo === 'articulo' && resultado.articulo.importe).toBe(150.5)
  })

  it('el código a secas devuelve el artículo sin importe (la pantalla lo pide)', () => {
    const resultado = resolverEscaneoOffline(instantaneaConImporte(), '500')

    expect(resultado.tipo).toBe('articulo')
    expect(resultado.tipo === 'articulo' && resultado.articulo).not.toHaveProperty('importe')
    expect(resultado.tipo === 'articulo' && resultado.articulo.cantidad).toBe(1)
  })

  it.each(['abc*500', '0*500', '-5*500', '*500', '150.555*500', '1e3*500', '1.234,50*500'])('el prefijo de "%s" es un importe inválido, no cantidad 1', (entrada) => {
    expect(resolverEscaneoOffline(instantaneaConImporte(), entrada)).toEqual({ tipo: 'importeInvalido' })
  })

  it('también resuelve por código de barra', () => {
    const instantanea = instantaneaConImporte({ articulos: [articuloPorImporteFixture({ codigosBarra: ['7791234000001'] })] })

    const resultado = resolverEscaneoOffline(instantanea, '99*7791234000001')

    expect(resultado.tipo === 'articulo' && resultado.articulo).toMatchObject({ idArticulo: FIAMBRERIA, codigoBarra: '7791234000001', importe: 99 })
  })

  it('un artículo común no cambia: ni importe ni tecla, y un prefijo ilegible sigue cayendo a cantidad 1', () => {
    const conCantidad = resolverEscaneoOffline(instantaneaConImporte(), '3*A0001')
    const ilegible = resolverEscaneoOffline(instantaneaConImporte(), 'abc*A0001')

    expect(conCantidad.tipo === 'articulo' && conCantidad.articulo).toMatchObject({ idArticulo: 1, cantidad: 3 })
    expect(conCantidad.tipo === 'articulo' && conCantidad.articulo).not.toHaveProperty('importe')
    expect(ilegible.tipo === 'articulo' && ilegible.articulo.cantidad).toBe(1)
  })

  it('un código inexistente o vacío no se encuentra', () => {
    expect(resolverEscaneoOffline(instantaneaConImporte(), '150*999')).toEqual({ tipo: 'noEncontrado' })
    expect(resolverEscaneoOffline(instantaneaConImporte(), '')).toEqual({ tipo: 'noEncontrado' })
  })

  it('buscarArticuloOffline devuelve el artículo con importe y null ante un importe inválido', () => {
    expect(buscarArticuloOffline(instantaneaConImporte(), '150*500')?.importe).toBe(150)
    expect(buscarArticuloOffline(instantaneaConImporte(), 'abc*500')).toBeNull()
  })
})

describe('atajosDeImporteOffline / articuloDeAtajoOffline', () => {
  const instantanea = instantaneaConImporte({
    articulos: [
      articuloFixture({ idArticulo: 2, codigoInterno: 'A0002', teclaAtajo: 'F6' }),
      articuloPorImporteFixture({ idArticulo: 502, codigoInterno: '502', nombre: 'Panadería', teclaAtajo: 'F7' }),
      articuloPorImporteFixture({ idArticulo: 501, codigoInterno: '501', nombre: 'Verdulería', teclaAtajo: 'F5' }),
      articuloPorImporteFixture({ idArticulo: 503, codigoInterno: '503', nombre: 'Sin tecla', teclaAtajo: null }),
    ],
  })

  it('lista solo los artículos por importe con tecla, ordenados por tecla', () => {
    expect(atajosDeImporteOffline(instantanea).map((a) => [a.teclaAtajo, a.nombre])).toEqual([
      ['F5', 'Verdulería'],
      ['F7', 'Panadería'],
    ])
  })

  it('busca el artículo de una tecla y null si no hay ninguno', () => {
    expect(articuloDeAtajoOffline(instantanea, 'F7')?.idArticulo).toBe(502)
    expect(articuloDeAtajoOffline(instantanea, 'F8')).toBeNull()
  })

  it('un artículo que no es por importe no ocupa la tecla aunque la traiga', () => {
    expect(articuloDeAtajoOffline(instantanea, 'F6')).toBeNull()
  })

  it('una instantánea guardada antes de las teclas no tiene atajos', () => {
    expect(atajosDeImporteOffline(instantaneaFixture())).toEqual([])
  })
})

describe('esInstantaneaValida — artículo por importe', () => {
  it('acepta un artículo por importe sin precios y con tecla', () => {
    expect(esInstantaneaValida(instantaneaConImporte())).toBe(true)
  })

  it('acepta una tecla nula', () => {
    expect(esInstantaneaValida(instantaneaConImporte({ articulos: [articuloPorImporteFixture({ teclaAtajo: null })] }))).toBe(true)
  })

  it('rechaza una tecla que no es texto', () => {
    const rota = instantaneaConImporte({ articulos: [articuloPorImporteFixture({ teclaAtajo: 5 as unknown as 'F5' })] })

    expect(esInstantaneaValida(rota)).toBe(false)
  })

  it('la tecla sobrevive al guardado y a la lectura del almacén local', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, { instantanea: instantaneaConImporte(), etag: null, verificadaEn: '2026-10-12T12:00:00.000Z' })

    expect(articuloDeAtajoOffline((await leerInstantaneaLocal(almacen))!.instantanea, 'F5')?.idArticulo).toBe(FIAMBRERIA)
  })
})

describe('precio offline de una línea por importe', () => {
  it('cotiza al importe, sin oferta, aunque el artículo no tenga precio en ninguna lista', () => {
    const [precios] = preciosVigentesDeLasLineas([lineaPorImporte(150.5)], instantaneaConImporte(), LISTA)

    expect(precios).toEqual({ precioOriginal: 150.5, precioFinal: 150.5, descuentoUnitario: 0, aplicadas: [] })
  })

  it('una lista derivada aplica su porcentaje al importe: 1500 a -10 % cotiza 1350', () => {
    const instantanea = instantaneaConImporte({ listas: [{ idListaPrecio: LISTA, modo: 'Derivada', porcentaje: -10 }] })

    expect(preciosVigentesDeLasLineas([lineaPorImporte(1500)], instantanea, LISTA)[0]).toEqual({
      precioOriginal: 1350,
      precioFinal: 1350,
      descuentoUnitario: 0,
      aplicadas: [],
    })
  })

  // Espejo de ResolvedorDePrecios.ResolverPrecioDerivado: 2 decimales, el empate se aleja de cero y el producto es decimal exacto.
  it.each([
    [10.05, -10, 9.05],
    [0.05, -10, 0.05],
    [1, -10, 0.9],
    [100, 12.5, 112.5],
    [0.7, 15, 0.81],
    [19.99, 33.33, 26.65],
    [1500, 0, 1500],
    [1000, 11.6666, 1116.67],
    [233.33, -12.35, 204.51],
  ])('el importe %s con el porcentaje %s da %s, como el servidor', (importe, porcentaje, esperado) => {
    const instantanea = instantaneaConImporte({ listas: [{ idListaPrecio: LISTA, modo: 'Derivada', porcentaje }] })

    expect(preciosVigentesDeLasLineas([lineaPorImporte(importe)], instantanea, LISTA)[0]?.precioFinal).toBe(esperado)
  })

  it('una lista que la instantánea no conoce no cotiza el importe: no se cobra sin el porcentaje que el servidor aplicaría', () => {
    const instantanea = instantaneaConImporte({ listas: [{ idListaPrecio: OTRA_LISTA, modo: 'Fija', porcentaje: null }] })

    expect(preciosVigentesDeLasLineas([lineaPorImporte(1500)], instantanea, LISTA)).toEqual([null])
  })

  it('cotiza al importe aunque el artículo tenga un precio de lista (lo ignora)', () => {
    const instantanea = instantaneaConImporte({ articulos: [articuloPorImporteFixture({ preciosPorLista: [precioFixture({ precioOriginal: 999, precioFinal: 999 })] })] })

    expect(preciosVigentesDeLasLineas([lineaPorImporte(150)], instantanea, LISTA)[0]?.precioFinal).toBe(150)
  })

  it('no suma a la cantidad de familia de las demás líneas', () => {
    const celda: EscalonDeFamilia = { cantidadDeFamiliaDesde: 3, cantidadDesde: 1, precioFinal: 50, descuentoUnitario: 50, aplicadas: [{ idOferta: 9, nombre: 'Familia 3', descuentoUnitario: 50 }] }
    const instantanea = instantaneaConImporte({
      articulos: [
        articuloFixture({ idArticulo: 1, idFamilia: 77, preciosPorLista: [precioFixture({ escalonesDeFamilia: [celda] })] }),
        articuloPorImporteFixture({ idFamilia: 77 }),
      ],
    })
    const comun = lineaFixture({ idLinea: 'c', idArticulo: 1, cantidad: 2 })

    const [precioComun] = preciosVigentesDeLasLineas([comun, lineaPorImporte(150)], instantanea, LISTA)

    expect(precioComun?.descuentoUnitario).toBe(0)
  })

  it('resolverPreviaOffline indexa la línea por importe por su idLinea', () => {
    const { precios } = resolverPreviaOffline([lineaPorImporte(150, 'f1')], instantaneaConImporte(), LISTA)

    expect(precios.f1).toMatchObject({ idArticulo: FIAMBRERIA, precioFinal: 150, precioOriginal: 150, descuentoUnitario: 0 })
    expect(calcularSubtotalPrevia([lineaPorImporte(150, 'f1')], precios)).toBe(150)
  })

  it('un combo no consume una línea por importe aunque su artículo sea por unidad en la instantánea', () => {
    const articulos = [articuloDeCombosFixture(PANCHO, 3000), articuloDeCombosFixture(COCA, 2500)]
    const lineas = [
      lineaFixture({ idLinea: 'p', idArticulo: PANCHO, cantidad: 1 }),
      lineaFixture({ idLinea: 'c', idArticulo: COCA, cantidad: 1, importe: 2500 }),
    ]

    expect(resolverDocumentoOffline(lineas, instantaneaConCombos([comboPanchoCoca], articulos), LISTA).combos).toEqual([])
  })
})

describe('todasLasLineasTienenPrecioOffline — artículo por importe', () => {
  it('una línea por importe no necesita precio de lista, pero sí que la instantánea conozca la lista del cliente', () => {
    expect(todasLasLineasTienenPrecioOffline([lineaPorImporte(150)], instantaneaConImporte(), LISTA)).toBe(true)
    expect(todasLasLineasTienenPrecioOffline([lineaPorImporte(150)], instantaneaConImporte({ listas: [] }), LISTA)).toBe(false)
  })

  it('una instantánea guardada sin `listas` manda las líneas por importe al servidor', () => {
    const { listas: _sinListas, ...vieja } = instantaneaConImporte()

    expect(todasLasLineasTienenPrecioOffline([lineaPorImporte(150)], vieja, LISTA)).toBe(false)
    expect(esInstantaneaValida(vieja)).toBe(true)
  })

  it('una lista derivada sin porcentaje numérico no sirve para cotizar', () => {
    const malformada = instantaneaConImporte({ listas: [{ idListaPrecio: LISTA, modo: 'Derivada', porcentaje: null }] })

    expect(todasLasLineasTienenPrecioOffline([lineaPorImporte(150)], malformada, LISTA)).toBe(false)
  })

  it('una venta mixta pasa si las demás líneas tienen precio', () => {
    expect(todasLasLineasTienenPrecioOffline([lineaFixture(), lineaPorImporte(150)], instantaneaConImporte(), LISTA)).toBe(true)
  })

  it('una venta mixta no pasa si falta el precio de una línea común', () => {
    expect(todasLasLineasTienenPrecioOffline([lineaFixture(), lineaPorImporte(150)], instantaneaConImporte(), OTRA_LISTA)).toBe(false)
  })

  it('una línea por importe de un artículo que la instantánea no tiene no pasa', () => {
    expect(todasLasLineasTienenPrecioOffline([lineaPorImporte(150)], instantaneaFixture(), LISTA)).toBe(false)
  })

  it('un artículo por importe SIN importe en la línea no pasa: no tiene precio', () => {
    const sinImporte = lineaFixture({ idArticulo: FIAMBRERIA })

    expect(todasLasLineasTienenPrecioOffline([sinImporte], instantaneaConImporte(), LISTA)).toBe(false)
  })
})

describe('resolverAtajoOffline', () => {
  const instantanea = instantaneaConImporte()

  it('arma el artículo de la tecla con cantidad 1 y el importe tipeado', () => {
    const resultado = resolverAtajoOffline(instantanea, 'F5', '150')

    expect(resultado).toEqual({
      tipo: 'articulo',
      articulo: {
        idArticulo: FIAMBRERIA,
        codigoInterno: '500',
        nombre: 'Fiambrería',
        codigoBarra: null,
        cantidad: 1,
        acumulaEnVenta: false,
        unidadVenta: 'Importe',
        teclaAtajo: 'F5',
        importe: 150,
      },
    })
  })

  it('lee la coma decimal', () => {
    const resultado = resolverAtajoOffline(instantanea, 'F5', '150,50')

    expect(resultado.tipo === 'articulo' && resultado.articulo.importe).toBe(150.5)
  })

  it('una tecla sin artículo no tiene atajo, aunque el importe también sea inválido', () => {
    expect(resolverAtajoOffline(instantanea, 'F8', '150')).toEqual({ tipo: 'sinArticulo' })
    expect(resolverAtajoOffline(instantanea, 'F8', 'abc')).toEqual({ tipo: 'sinArticulo' })
  })

  it.each(['', '   ', 'abc', '0', '-1', '150.555'])('el importe "%s" es inválido', (texto) => {
    expect(resolverAtajoOffline(instantanea, 'F5', texto)).toEqual({ tipo: 'importeInvalido' })
  })

  it('el código del artículo no interviene: se arma desde el registro, sin pasar por el parser de códigos', () => {
    const largo = instantaneaConImporte({ articulos: [articuloPorImporteFixture({ codigoInterno: '7791234567890' })] })

    const resultado = resolverAtajoOffline(largo, 'F5', '150')

    expect(resultado.tipo === 'articulo' && resultado.articulo).toMatchObject({ idArticulo: FIAMBRERIA, codigoInterno: '7791234567890', importe: 150 })
  })
})

describe('lineasConImporteDeArticuloOrdinario', () => {
  const ordinarioEn500 = () => articuloPorImporteFixture({ unidadVenta: 'Unidad' })

  it('señala la línea con importe cuyo artículo la instantánea ya conoce como ordinario', () => {
    const instantanea = instantaneaConImporte({ articulos: [articuloFixture(), ordinarioEn500()] })

    expect(lineasConImporteDeArticuloOrdinario([lineaPorImporte(150, 'a')], instantanea).map((l) => l.idLinea)).toEqual(['a'])
  })

  it('no señala la línea cuyo artículo sigue siendo por importe', () => {
    expect(lineasConImporteDeArticuloOrdinario([lineaPorImporte(150)], instantaneaConImporte())).toEqual([])
  })

  it('no señala la línea cuyo artículo la instantánea no conoce', () => {
    const sinElArticulo = instantaneaConImporte({ articulos: [articuloFixture()] })

    expect(lineasConImporteDeArticuloOrdinario([lineaPorImporte(150)], sinElArticulo)).toEqual([])
  })

  it('no señala una línea ordinaria, aunque el artículo sea ordinario', () => {
    const instantanea = instantaneaConImporte({ articulos: [articuloFixture(), ordinarioEn500()] })

    expect(lineasConImporteDeArticuloOrdinario([lineaFixture({ idArticulo: FIAMBRERIA })], instantanea)).toEqual([])
  })
})
