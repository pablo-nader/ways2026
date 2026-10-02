import { describe, expect, it } from 'vitest'
import {
  aClienteListado,
  aMedioPagoListado,
  buscarArticuloOffline,
  buscarClientesOffline,
  edadDeInstantaneaEnMinutos,
  elegirEscalon,
  esInstantaneaValida,
  formatearVejezDeInstantanea,
  guardarInstantaneaLocal,
  leerInstantaneaLocal,
  parsearEntradaDeEscaneoOffline,
  precioEnLista,
  purgarInstantaneaLocal,
  preciosVigentesOffline,
  resolverPreciosOffline,
  TOPE_DE_RESULTADOS_DE_CLIENTES,
  todasLasLineasTienenPrecioOffline,
} from './instantaneaOffline'
import type { AlmacenClaveValor } from './almacenPos'
import type {
  ArticuloDeInstantanea,
  ClienteDeInstantanea,
  EscalonDeCantidad,
  InstantaneaDePos,
  PrecioDeListaDeInstantanea,
} from '../api/tipos'
import type { LineaCarrito } from '../api/carrito'

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
  return { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', cantidad: 1, ...sobrescribir }
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
    })
  })

  it('encuentra por código interno, con codigoBarra null (mismo criterio que el escaneo online)', () => {
    expect(buscarArticuloOffline(instantaneaFixture(), 'A0001')).toEqual({
      idArticulo: 1,
      codigoInterno: 'A0001',
      nombre: 'Coca Cola 1L',
      codigoBarra: null,
      cantidad: 1,
    })
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

  it('resuelve con el precio de la lista del cliente, indexado por idArticulo', () => {
    expect(resolverPreciosOffline([lineaFixture()], dosListas(), OTRA_LISTA)[1]).toEqual({
      idArticulo: 1,
      idListaPrecio: OTRA_LISTA,
      precioOriginal: 110,
      precioFinal: 99,
      descuentoUnitario: 11,
      aplicadas: [{ idOferta: 4, nombre: 'Mayorista', descuentoUnitario: 11 }],
    })
    expect(resolverPreciosOffline([lineaFixture()], dosListas(), LISTA)[1]).toMatchObject({ idListaPrecio: LISTA, precioOriginal: 150, precioFinal: 120 })
  })

  it('una línea sin precio en la lista del cliente queda ausente del índice, aunque tenga precio en otra', () => {
    expect(resolverPreciosOffline([lineaFixture()], dosListas(), 99)[1]).toBeUndefined()
  })

  it('una línea sin artículo en la instantánea queda ausente del índice (nunca un precio inventado)', () => {
    expect(resolverPreciosOffline([lineaFixture({ idArticulo: 999 })], instantaneaFixture(), LISTA)[999]).toBeUndefined()
  })

  it('la cantidad de la línea elige el tramo de esa lista, y precioOriginal queda el de lista', () => {
    const instantanea = instantaneaFixture({ articulos: [articuloFixture({ preciosPorLista: [precioConEscalonesFixture()] })] })
    expect(resolverPreciosOffline([lineaFixture({ cantidad: 6 })], instantanea, LISTA)[1]).toEqual({
      idArticulo: 1,
      idListaPrecio: LISTA,
      precioOriginal: 100,
      precioFinal: 80,
      descuentoUnitario: 20,
      aplicadas: [{ idOferta: 61, nombre: '6 o más', descuentoUnitario: 20 }],
    })
    expect(resolverPreciosOffline([lineaFixture({ cantidad: 2 })], instantanea, LISTA)[1]).toMatchObject({ precioFinal: 100, descuentoUnitario: 0 })
  })

  it('cada línea cae en el tramo de SU cantidad, nunca en el de la otra línea', () => {
    const instantanea = instantaneaFixture({
      articulos: [
        articuloFixture({ idArticulo: 1, preciosPorLista: [precioConEscalonesFixture()] }),
        articuloFixture({ idArticulo: 2, codigosBarra: ['7790009999999'], preciosPorLista: [precioConEscalonesFixture()] }),
      ],
    })
    const indice = resolverPreciosOffline([lineaFixture({ idArticulo: 1, cantidad: 3 }), lineaFixture({ idArticulo: 2, cantidad: 12 })], instantanea, LISTA)
    expect(indice[1]).toMatchObject({ precioFinal: 90, descuentoUnitario: 10 })
    expect(indice[2]).toMatchObject({ precioFinal: 70, descuentoUnitario: 30 })
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
