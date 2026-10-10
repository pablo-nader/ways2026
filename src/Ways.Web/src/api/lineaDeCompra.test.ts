import { describe, expect, it } from 'vitest'
import {
  aLineaSolicitada,
  alicuotaParaArticulo,
  brutoDeLinea,
  cambiosAlDesresolver,
  cambiosAlElegirArticulo,
  cambiosAlEscribirCodigo,
  cambiosAlEscribirDetalle,
  cambiosAlResolverPorCodigo,
  camposInvalidosDeLinea,
  cantidadDeLinea,
  completarConArticulo,
  costoDeArticuloParaCompra,
  descuentoDesdePorcentaje,
  diferenciaDePrecioPorBulto,
  importeSegunModoDePrecios,
  itemAFormulario,
  lineaCompletaParaEnvio,
  lineaDeCompraVacia,
  lineaDesdeCoberturaDeOrden,
  lineaTieneDatos,
  MENSAJE_DE_BULTOS_CON_DECIMALES_DE_MAS,
  MENSAJE_DE_BULTOS_QUE_FRACCIONAN_UNIDADES,
  mensajeDeCantidadDeBultos,
  normalizarFilas,
  porcentajeDeDescuento,
  tipoDeLinea,
  valoresParaApi,
  type LineaDeCompraFormulario,
} from './lineaDeCompra'
import type { AlicuotaIvaListado, ArticuloListado, ItemDeCompra } from './tipos'

function lineaFixture(sobrescribir: Partial<LineaDeCompraFormulario> = {}): LineaDeCompraFormulario {
  return {
    ...lineaDeCompraVacia(1),
    idArticulo: 10,
    descripcion: 'Fideos 500g',
    cantidad: '10',
    importe: 100,
    descuentoPorcentaje: 5,
    idAlicuotaIva: 3,
    ...sobrescribir,
  }
}

function itemFixture(sobrescribir: Partial<ItemDeCompra> = {}): ItemDeCompra {
  return {
    orden: 1,
    idArticulo: 10,
    descripcion: 'Fideos 500g',
    cantidad: 10,
    bultos: null,
    unidadesPorBulto: null,
    costoUnitario: 100,
    descuento: 50,
    idAlicuotaIva: 3,
    porcentajeIva: 21,
    total: 950,
    actualizaCosto: true,
    precioSugerido: 114.95,
    codigoLote: null,
    fechaVencimiento: null,
    idLote: null,
    codigoProveedor: null,
    ...sobrescribir,
  }
}

function articuloFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    id: 20,
    codigoInterno: 'ART-20',
    nombre: 'Leche en polvo 800g',
    descripcion: null,
    idArea: 1,
    idCategoria: null,
    idMarca: null,
    idGrupo: null,
    idProveedorHabitual: null,
    idAlicuotaIva: 3,
    unidadVenta: 'Unidad',
    unidadesPorBulto: null,
    esProducto: true,
    costoLista: null,
    descuentoProveedor: null,
    costoNominal: null,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    controlaLote: false,
    acumulaEnVenta: true,
    idFamilia: null,
    ...sobrescribir,
  }
}

const ALICUOTAS: AlicuotaIvaListado[] = [
  { id: 3, nombre: '21%', porcentaje: 21, codigoAfip: null, activo: true },
  { id: 4, nombre: '10,5%', porcentaje: 10.5, codigoAfip: null, activo: true },
]

describe('lineaDeCompraVacia / lineaTieneDatos', () => {
  it('una fila nueva está vacía: sin artículo, UM Unidad, sin importe ni IVA', () => {
    expect(lineaDeCompraVacia(7)).toEqual({
      clave: 7,
      idArticulo: '',
      descripcion: '',
      codigoProveedor: '',
      resueltaPorCodigo: false,
      codigoConsultado: '',
      unidadDeMedida: 'Unidad',
      cantidad: '',
      importe: null,
      descuentoPorcentaje: null,
      idAlicuotaIva: '',
      controlaLote: false,
      codigoLote: '',
      fechaVencimiento: '',
      unidadesPorBulto: null,
      actualizaCosto: true,
      guardada: null,
    })
    expect(lineaTieneDatos(lineaDeCompraVacia(1))).toBe(false)
  })

  it.each<[string, Partial<LineaDeCompraFormulario>]>([
    ['un código', { codigoProveedor: 'AB-1' }],
    ['un detalle', { descripcion: 'Flete' }],
    ['un artículo', { idArticulo: 4 }],
    ['una cantidad', { cantidad: '1' }],
    ['un importe, aunque sea cero', { importe: 0 }],
    ['un descuento, aunque sea cero', { descuentoPorcentaje: 0 }],
    ['una alícuota', { idAlicuotaIva: 3 }],
  ])('con %s la fila tiene datos', (_nombre, cambios) => {
    expect(lineaTieneDatos({ ...lineaDeCompraVacia(1), ...cambios })).toBe(true)
  })

  it('el espacio en blanco no cuenta como dato', () => {
    expect(lineaTieneDatos({ ...lineaDeCompraVacia(1), codigoProveedor: '  ', descripcion: ' ', cantidad: ' ' })).toBe(false)
  })

  it('tipoDeLinea: con artículo es artículo; sin artículo es concepto', () => {
    expect(tipoDeLinea(lineaFixture())).toBe('articulo')
    expect(tipoDeLinea(lineaFixture({ idArticulo: '' }))).toBe('concepto')
  })
})

describe('normalizarFilas', () => {
  const vacia = (clave: number) => lineaDeCompraVacia(clave)
  const conDatos = (clave: number) => lineaFixture({ clave })

  it('sin filas devuelve una fila vacía con la clave nueva', () => {
    expect(normalizarFilas([], 9).map((l) => l.clave)).toEqual([9])
  })

  it('cuando la última fila tiene datos suma una vacía al final', () => {
    const filas = normalizarFilas([conDatos(1), conDatos(2)], 9)
    expect(filas.map((l) => l.clave)).toEqual([1, 2, 9])
    expect(lineaTieneDatos(filas[2])).toBe(false)
  })

  it('con la última ya vacía no suma otra: la deja como está, con su misma clave', () => {
    expect(normalizarFilas([conDatos(1), vacia(2)], 9).map((l) => l.clave)).toEqual([1, 2])
  })

  it('de varias vacías finales conserva solo la primera (la que pudo quedar con el foco)', () => {
    expect(normalizarFilas([conDatos(1), vacia(2), vacia(3), vacia(4)], 9).map((l) => l.clave)).toEqual([1, 2])
  })

  it('una fila vacía en el medio no se toca', () => {
    expect(normalizarFilas([conDatos(1), vacia(2), conDatos(3), vacia(4)], 9).map((l) => l.clave)).toEqual([1, 2, 3, 4])
  })
})

describe('camposInvalidosDeLinea / lineaCompletaParaEnvio', () => {
  it('una fila sin datos no es inválida: simplemente se ignora', () => {
    expect(camposInvalidosDeLinea(lineaDeCompraVacia(1))).toEqual([])
    expect(lineaCompletaParaEnvio(lineaDeCompraVacia(1))).toBe(false)
  })

  it('una fila con artículo, cantidad, importe e IVA está completa', () => {
    expect(camposInvalidosDeLinea(lineaFixture())).toEqual([])
    expect(lineaCompletaParaEnvio(lineaFixture())).toBe(true)
  })

  it('con solo un código tipeado faltan el detalle, la cantidad, el importe y el IVA', () => {
    expect(camposInvalidosDeLinea({ ...lineaDeCompraVacia(1), codigoProveedor: 'AB-1' })).toEqual([
      'detalle',
      'cantidad',
      'importe',
      'iva',
    ])
  })

  it.each<[string, Partial<LineaDeCompraFormulario>, string]>([
    ['sin cantidad', { cantidad: '' }, 'cantidad'],
    ['con cantidad cero', { cantidad: '0' }, 'cantidad'],
    ['con cantidad negativa', { cantidad: '-2' }, 'cantidad'],
    ['sin importe', { importe: null }, 'importe'],
    ['sin IVA', { idAlicuotaIva: '' }, 'iva'],
    ['con descuento mayor a 100', { descuentoPorcentaje: 100.01 }, 'descuento'],
    ['con descuento negativo', { descuentoPorcentaje: -1 }, 'descuento'],
    ['con lote sin vencimiento', { controlaLote: true, fechaVencimiento: '' }, 'vencimiento'],
  ])('una línea %s no está completa', (_nombre, cambios, campo) => {
    expect(camposInvalidosDeLinea(lineaFixture(cambios))).toContain(campo)
    expect(lineaCompletaParaEnvio(lineaFixture(cambios))).toBe(false)
  })

  it('un descuento de exactamente 100 % es válido; cero también', () => {
    expect(camposInvalidosDeLinea(lineaFixture({ descuentoPorcentaje: 100 }))).toEqual([])
    expect(camposInvalidosDeLinea(lineaFixture({ descuentoPorcentaje: 0 }))).toEqual([])
  })

  it('un artículo con lote y vencimiento (el código se deriva si falta) está completo', () => {
    expect(lineaCompletaParaEnvio(lineaFixture({ controlaLote: true, codigoLote: '', fechaVencimiento: '2026-12-01' }))).toBe(true)
  })

  it('un artículo que no controla lote nunca exige vencimiento', () => {
    expect(camposInvalidosDeLinea(lineaFixture({ controlaLote: false, fechaVencimiento: '' }))).toEqual([])
  })

  it('un concepto necesita descripción y no se juzga por la unidad de venta', () => {
    const concepto = lineaFixture({ idArticulo: '', descripcion: 'Flete', cantidad: '1.5', unidadVenta: 'Unidad' })
    expect(camposInvalidosDeLinea(concepto)).toEqual([])
    expect(camposInvalidosDeLinea({ ...concepto, descripcion: '   ' })).toEqual(['detalle'])
  })

  it('cantidad fraccionaria: inválida para un artículo por unidad, válida por peso o con unidad desconocida', () => {
    expect(camposInvalidosDeLinea(lineaFixture({ cantidad: '1.5', unidadVenta: 'Unidad' }))).toEqual(['cantidad'])
    expect(camposInvalidosDeLinea(lineaFixture({ cantidad: '1.5', unidadVenta: 'Peso' }))).toEqual([])
    expect(camposInvalidosDeLinea(lineaFixture({ cantidad: '1.5' }))).toEqual([])
  })

  it('con UM Bulto la cantidad son bultos y no se juzga por la unidad de venta del artículo', () => {
    expect(
      camposInvalidosDeLinea(lineaFixture({ cantidad: '1.5', unidadVenta: 'Unidad', unidadDeMedida: 'Bulto', unidadesPorBulto: 12 })),
    ).toEqual([])
  })

  it('UM Bulto sin unidades por bulto del artículo no es válida', () => {
    expect(camposInvalidosDeLinea(lineaFixture({ unidadDeMedida: 'Bulto', unidadesPorBulto: null }))).toEqual(['cantidad'])
  })
})

describe('cuenta exacta de la línea', () => {
  it('cantidadDeLinea suma unidades y bultos × unidades por bulto a 3 decimales', () => {
    expect(cantidadDeLinea(2, 3, 6)).toBe(20)
    expect(cantidadDeLinea(0, 0.1, 3)).toBe(0.3)
    expect(cantidadDeLinea(5, null, null)).toBe(5)
  })

  it('brutoDeLinea es cantidad × costo a 2 decimales con empate hacia arriba (0,7 × 1,15 = 0,805)', () => {
    expect(brutoDeLinea(1, null, null, 100)).toBe(100)
    expect(brutoDeLinea(0.7, null, null, 1.15)).toBe(0.81)
  })

  it('descuentoDesdePorcentaje redondea el empate hacia arriba: 15 % de 4,10 es 0,615 → 0,62 (el flotante daría 0,61)', () => {
    expect(descuentoDesdePorcentaje(4.1, 15)).toBe(0.62)
    expect(descuentoDesdePorcentaje(1000, 5)).toBe(50)
    expect(descuentoDesdePorcentaje(1000, null)).toBe(0)
    expect(descuentoDesdePorcentaje(333.33, 100)).toBe(333.33)
  })

  it('porcentajeDeDescuento devuelve el porcentaje a 2 decimales, y null sin descuento o sin bruto', () => {
    expect(porcentajeDeDescuento(50, 1000)).toBe(5)
    expect(porcentajeDeDescuento(1, 3)).toBe(33.33)
    expect(porcentajeDeDescuento(0, 1000)).toBeNull()
    expect(porcentajeDeDescuento(null, 1000)).toBeNull()
    expect(porcentajeDeDescuento(10, 0)).toBeNull()
  })
})

describe('valoresParaApi — línea nueva', () => {
  it('UM Unidad: unidades = cantidad, costoUnitario = importe y descuento = bruto × % redondeado', () => {
    expect(valoresParaApi(lineaFixture({ cantidad: '10', importe: 100, descuentoPorcentaje: 5 }))).toEqual({
      unidades: 10,
      bultos: null,
      unidadesPorBulto: null,
      costoUnitario: 100,
      descuento: 50,
    })
  })

  it('UM Bulto: bultos = cantidad, unidades por bulto del artículo, unidades 0 y costo = importe por bulto / unidades por bulto', () => {
    expect(
      valoresParaApi(lineaFixture({ unidadDeMedida: 'Bulto', unidadesPorBulto: 12, cantidad: '2', importe: 120, descuentoPorcentaje: 10 })),
    ).toEqual({ unidades: 0, bultos: 2, unidadesPorBulto: 12, costoUnitario: 10, descuento: 24 })
  })

  it('UM Bulto: el costo por unidad se redondea a 4 decimales (la precisión de la columna)', () => {
    const v = valoresParaApi(lineaFixture({ unidadDeMedida: 'Bulto', unidadesPorBulto: 3, cantidad: '1', importe: 100, descuentoPorcentaje: null }))
    expect(v.costoUnitario).toBe(33.3333)
  })

  it('el descuento se calcula sobre el bruto que va a calcular el servidor, así que 100 % nunca lo supera', () => {
    const v = valoresParaApi(lineaFixture({ unidadDeMedida: 'Bulto', unidadesPorBulto: 3, cantidad: '3', importe: 100, descuentoPorcentaje: 100 }))
    const bruto = brutoDeLinea(v.unidades, v.bultos, v.unidadesPorBulto, v.costoUnitario)
    expect(bruto).toBe(300)
    expect(v.descuento).toBe(bruto)
  })

  it('un concepto con UM Bulto residual viaja siempre por unidades', () => {
    const v = valoresParaApi(lineaFixture({ idArticulo: '', unidadDeMedida: 'Bulto', unidadesPorBulto: 12, cantidad: '2', importe: 50 }))
    expect(v).toMatchObject({ unidades: 2, bultos: null, unidadesPorBulto: null, costoUnitario: 50 })
  })

  it('sin descuento viaja 0, y un importe vacío viaja 0, nunca NaN', () => {
    expect(valoresParaApi(lineaFixture({ descuentoPorcentaje: null })).descuento).toBe(0)
    expect(valoresParaApi(lineaFixture({ importe: null })).costoUnitario).toBe(0)
  })
})

describe('itemAFormulario y reenvío de una línea guardada', () => {
  it('una línea sin bultos se muestra como UM Unidad con su cantidad, el importe y el % derivado', () => {
    const l = itemAFormulario(1, itemFixture({ cantidad: 10, costoUnitario: 100, descuento: 50 }))
    expect(l).toMatchObject({ unidadDeMedida: 'Unidad', cantidad: '10', importe: 100, descuentoPorcentaje: 5, unidadesPorBulto: null })
  })

  it('una línea solo de bultos se muestra como UM Bulto con la cantidad de bultos y el precio por bulto', () => {
    const l = itemAFormulario(1, itemFixture({ cantidad: 24, bultos: 2, unidadesPorBulto: 12, costoUnitario: 10, descuento: 0 }))
    expect(l).toMatchObject({ unidadDeMedida: 'Bulto', cantidad: '2', importe: 120, descuentoPorcentaje: null, unidadesPorBulto: 12 })
  })

  it('una línea que mezcla unidades sueltas y bultos se muestra como UM Unidad con la cantidad total', () => {
    const l = itemAFormulario(1, itemFixture({ cantidad: 20, bultos: 3, unidadesPorBulto: 6, costoUnitario: 10, descuento: 0 }))
    expect(l).toMatchObject({ unidadDeMedida: 'Unidad', cantidad: '20', importe: 10, unidadesPorBulto: 6 })
  })

  it('la mezcla sin tocar viaja EXACTAMENTE como estaba guardada (unidades sueltas y bultos)', () => {
    const l = itemAFormulario(1, itemFixture({ cantidad: 20, bultos: 3, unidadesPorBulto: 6, costoUnitario: 10, descuento: 0 }))
    expect(aLineaSolicitada(l)).toMatchObject({ unidades: 2, bultos: 3, unidadesPorBulto: 6, costoUnitario: 10, descuento: 0 })
  })

  it('la mezcla con la cantidad editada pasa a viajar por unidades, sin bultos', () => {
    const l = { ...itemAFormulario(1, itemFixture({ cantidad: 20, bultos: 3, unidadesPorBulto: 6, costoUnitario: 10, descuento: 0 })), cantidad: '25' }
    expect(aLineaSolicitada(l)).toMatchObject({ unidades: 25, bultos: null, unidadesPorBulto: null, costoUnitario: 10 })
  })

  it('un descuento guardado cuyo % no se reconstruye exacto vuelve idéntico mientras no se toque la línea', () => {
    // 0,07 sobre 1.000 es 0,007 %: se muestra 0,01 % y recalcularlo daría 0,10.
    const l = itemAFormulario(1, itemFixture({ cantidad: 10, costoUnitario: 100, descuento: 0.07 }))
    expect(l.descuentoPorcentaje).toBe(0.01)
    expect(descuentoDesdePorcentaje(1000, 0.01)).toBe(0.1)
    expect(aLineaSolicitada(l).descuento).toBe(0.07)
  })

  it('al tocar un valor visible el descuento se recalcula desde el %', () => {
    const l = itemAFormulario(1, itemFixture({ cantidad: 10, costoUnitario: 100, descuento: 0.07 }))
    expect(aLineaSolicitada({ ...l, cantidad: '10.5' }).descuento).toBe(0.11)
    expect(aLineaSolicitada({ ...l, importe: 100.5 }).descuento).toBe(0.1)
    expect(aLineaSolicitada({ ...l, descuentoPorcentaje: 0.02 }).descuento).toBe(0.2)
  })

  it('volver a escribir los valores originales devuelve la línea guardada', () => {
    const l = itemAFormulario(1, itemFixture({ cantidad: 10, costoUnitario: 100, descuento: 0.07 }))
    const tocada = { ...l, cantidad: '11' }
    expect(aLineaSolicitada(tocada).descuento).not.toBe(0.07)
    expect(aLineaSolicitada({ ...tocada, cantidad: '10' }).descuento).toBe(0.07)
  })

  it('el precio por bulto y el costo por unidad guardados vuelven idénticos si no se toca nada', () => {
    const l = itemAFormulario(1, itemFixture({ cantidad: 36, bultos: 3, unidadesPorBulto: 12, costoUnitario: 0.8333, descuento: 1.5 }))
    expect(l.importe).toBe(9.9996)
    expect(aLineaSolicitada(l)).toMatchObject({ unidades: 0, bultos: 3, unidadesPorBulto: 12, costoUnitario: 0.8333, descuento: 1.5 })
  })

  it('un item sin costo visible (rol sin permiso) no inventa importe ni línea guardada', () => {
    const l = itemAFormulario(1, itemFixture({ costoUnitario: null, descuento: null, total: null }))
    expect(l).toMatchObject({ importe: null, descuentoPorcentaje: null, guardada: null })
  })

  it('infiere controlaLote de un dato de lote ya persistido', () => {
    const conLote = itemAFormulario(1, itemFixture({ codigoLote: 'L-01', fechaVencimiento: '2026-12-01' }))
    expect(conLote).toMatchObject({ controlaLote: true, codigoLote: 'L-01', fechaVencimiento: '2026-12-01' })
    const sinLote = itemAFormulario(1, itemFixture())
    expect(sinLote).toMatchObject({ controlaLote: false, codigoLote: '', fechaVencimiento: '' })
  })

  it('un item sin artículo se reabre como concepto, con su código', () => {
    const l = itemAFormulario(1, itemFixture({ idArticulo: null, descripcion: 'Flete', actualizaCosto: false, codigoProveedor: 'FLETE-9' }))
    expect(tipoDeLinea(l)).toBe('concepto')
    expect(l).toMatchObject({ idArticulo: '', descripcion: 'Flete', codigoProveedor: 'FLETE-9', codigoConsultado: 'FLETE-9' })
  })

  it('un código guardado no está resuelto por código: queda pendiente y editable', () => {
    expect(itemAFormulario(1, itemFixture({ codigoProveedor: 'AB-12' }))).toMatchObject({ codigoProveedor: 'AB-12', resueltaPorCodigo: false })
    expect(itemAFormulario(1, itemFixture({ codigoProveedor: null })).codigoProveedor).toBe('')
  })
})

describe('aLineaSolicitada', () => {
  it('una línea guardada con actualizaCosto false lo reenvía false mientras conserve el artículo, aunque se editen cantidad e importe', () => {
    const l = itemAFormulario(1, itemFixture({ actualizaCosto: false }))
    expect(aLineaSolicitada(l).actualizaCosto).toBe(false)
    expect(aLineaSolicitada({ ...l, cantidad: '99', importe: 7 }).actualizaCosto).toBe(false)
  })

  it('una línea guardada con actualizaCosto true lo reenvía true', () => {
    expect(aLineaSolicitada(itemAFormulario(1, itemFixture({ actualizaCosto: true }))).actualizaCosto).toBe(true)
  })

  it('un artículo elegido, creado o resuelto por código en la grilla manda true aunque la fila viniera de una línea con false', () => {
    const guardadaSinActualizar = itemAFormulario(1, itemFixture({ actualizaCosto: false }))
    for (const origen of ['busqueda', 'alta', 'codigo'] as const) {
      const cambios = cambiosAlElegirArticulo(guardadaSinActualizar, articuloFixture({ id: 21 }), ALICUOTAS, origen)
      expect(cambios.actualizaCosto).toBe(true)
      expect(aLineaSolicitada({ ...guardadaSinActualizar, ...cambios }).actualizaCosto).toBe(true)
    }
  })

  it('un concepto manda false (lo exige el servidor) aunque la línea guardada dijera true', () => {
    const concepto = itemAFormulario(1, itemFixture({ idArticulo: null, descripcion: 'Flete', actualizaCosto: true }))
    expect(aLineaSolicitada(concepto).actualizaCosto).toBe(false)
  })

  it('mapea todos los campos de un artículo', () => {
    expect(aLineaSolicitada(lineaFixture())).toEqual({
      idArticulo: 10,
      descripcion: 'Fideos 500g',
      unidades: 10,
      bultos: null,
      unidadesPorBulto: null,
      costoUnitario: 100,
      descuento: 50,
      idAlicuotaIva: 3,
      actualizaCosto: true,
      codigoLote: null,
      fechaVencimiento: null,
      codigoProveedor: null,
    })
  })

  it('un concepto viaja sin artículo, sin bultos ni lote y sin actualizar costo, con la descripción recortada', () => {
    const concepto = lineaFixture({
      idArticulo: '',
      descripcion: '  Flete  ',
      cantidad: '1',
      importe: 500,
      descuentoPorcentaje: null,
      codigoLote: 'L-1',
      fechaVencimiento: '2026-12-01',
      controlaLote: true,
    })
    expect(aLineaSolicitada(concepto)).toEqual({
      idArticulo: null,
      descripcion: 'Flete',
      unidades: 1,
      bultos: null,
      unidadesPorBulto: null,
      costoUnitario: 500,
      descuento: 0,
      idAlicuotaIva: 3,
      actualizaCosto: false,
      codigoLote: null,
      fechaVencimiento: null,
      codigoProveedor: null,
    })
  })

  it('el código viaja recortado y null cuando está vacío, en un artículo y en un concepto', () => {
    expect(aLineaSolicitada(lineaFixture({ codigoProveedor: '  AB-12  ' })).codigoProveedor).toBe('AB-12')
    expect(aLineaSolicitada(lineaFixture({ codigoProveedor: '   ' })).codigoProveedor).toBeNull()
    expect(aLineaSolicitada(lineaFixture({ idArticulo: '', codigoProveedor: ' FLETE-9 ' })).codigoProveedor).toBe('FLETE-9')
  })

  it('reabrir un borrador y volver a guardarlo no pierde el código de ninguna línea', () => {
    const items = [
      itemFixture({ orden: 1, codigoProveedor: 'AB-12' }),
      itemFixture({ orden: 2, idArticulo: null, actualizaCosto: false, codigoProveedor: 'FLETE-9' }),
      itemFixture({ orden: 3, codigoProveedor: null }),
    ]
    expect(items.map((item, i) => aLineaSolicitada(itemAFormulario(i + 1, item)).codigoProveedor)).toEqual(['AB-12', 'FLETE-9', null])
  })

  it('codigoLote y fechaVencimiento vacíos viajan null; tipeados viajan recortados', () => {
    expect(aLineaSolicitada(lineaFixture()).codigoLote).toBeNull()
    const l = aLineaSolicitada(lineaFixture({ codigoLote: '  L-01  ', fechaVencimiento: '2026-12-01' }))
    expect(l.codigoLote).toBe('L-01')
    expect(l.fechaVencimiento).toBe('2026-12-01')
  })

  it('UM Bulto viaja con bultos, unidades por bulto y unidades en cero', () => {
    const l = aLineaSolicitada(lineaFixture({ unidadDeMedida: 'Bulto', unidadesPorBulto: 12, cantidad: '2', importe: 120, descuentoPorcentaje: null }))
    expect(l).toMatchObject({ unidades: 0, bultos: 2, unidadesPorBulto: 12, costoUnitario: 10 })
  })
})

describe('lineaDesdeCoberturaDeOrden', () => {
  it('arranca con el pendiente como cantidad y el costo estimado como importe', () => {
    const l = lineaDesdeCoberturaDeOrden(
      5,
      { idArticulo: 10, pendiente: 7, costoEstimado: 12.5 } as Parameters<typeof lineaDesdeCoberturaDeOrden>[1],
      [{ idArticulo: 10, descripcion: 'Fideos' } as Parameters<typeof lineaDesdeCoberturaDeOrden>[2][number]],
    )
    expect(l).toMatchObject({ clave: 5, idArticulo: 10, descripcion: 'Fideos', cantidad: '7', importe: 12.5, idAlicuotaIva: '' })
  })

  it('un artículo nunca cotizado deja el importe vacío, no 0', () => {
    const l = lineaDesdeCoberturaDeOrden(
      5,
      { idArticulo: 10, pendiente: 7, costoEstimado: null } as Parameters<typeof lineaDesdeCoberturaDeOrden>[1],
      [],
    )
    expect(l.importe).toBeNull()
    expect(l.descripcion).toBe('Artículo #10')
  })
})

describe('precarga desde un artículo', () => {
  it('costoDeArticuloParaCompra: lista con el descuento del proveedor (un porcentaje); sin lista, el nominal sin descuento; sin ninguno, null', () => {
    expect(costoDeArticuloParaCompra({ costoLista: 100, descuentoProveedor: 12.5, costoNominal: 80 })).toEqual({
      importe: 100,
      descuentoPorcentaje: 12.5,
    })
    expect(costoDeArticuloParaCompra({ costoLista: null, descuentoProveedor: 12.5, costoNominal: 80 })).toEqual({
      importe: 80,
      descuentoPorcentaje: null,
    })
    expect(costoDeArticuloParaCompra({ costoLista: null, descuentoProveedor: null, costoNominal: null })).toBeNull()
  })

  it('alicuotaParaArticulo: la del artículo si existe; si no, la del 21 %; si tampoco hay, vacío', () => {
    expect(alicuotaParaArticulo({ idAlicuotaIva: 4 }, ALICUOTAS)).toBe(4)
    expect(alicuotaParaArticulo({ idAlicuotaIva: 99 }, ALICUOTAS)).toBe(3)
    expect(alicuotaParaArticulo({ idAlicuotaIva: 99 }, [ALICUOTAS[1]])).toBe('')
  })

  it('elegir un artículo por código precarga Detalle, UM Unidad, importe, descuento, IVA y marca la fila como resuelta por código', () => {
    const base = lineaFixture({ idArticulo: '', descripcion: '', cantidad: '3', importe: null, descuentoPorcentaje: null, idAlicuotaIva: '', unidadDeMedida: 'Bulto' })
    const cambios = cambiosAlElegirArticulo(
      base,
      articuloFixture({ costoLista: 200, descuentoProveedor: 10, idAlicuotaIva: 4, unidadesPorBulto: 12, controlaLote: true }),
      ALICUOTAS,
      'codigo',
    )
    expect(cambios).toMatchObject({
      idArticulo: 20,
      descripcion: 'Leche en polvo 800g',
      resueltaPorCodigo: true,
      controlaLote: true,
      unidadVenta: 'Unidad',
      unidadesPorBulto: 12,
      unidadDeMedida: 'Unidad',
      importe: 200,
      descuentoPorcentaje: 10,
      idAlicuotaIva: 4,
      guardada: null,
    })
    expect(cambios).not.toHaveProperty('cantidad')
  })

  it('elegir por búsqueda no marca la fila como resuelta por código', () => {
    expect(cambiosAlElegirArticulo(lineaDeCompraVacia(1), articuloFixture(), ALICUOTAS, 'busqueda').resueltaPorCodigo).toBe(false)
  })

  it('un artículo sin unidades por bulto (o en 0) no habilita Bulto', () => {
    expect(cambiosAlElegirArticulo(lineaDeCompraVacia(1), articuloFixture({ unidadesPorBulto: 0 }), ALICUOTAS, 'busqueda').unidadesPorBulto).toBeNull()
  })

  it('un artículo sin costos visibles respeta el importe y el descuento ya tipeados', () => {
    const cambios = cambiosAlElegirArticulo(lineaFixture({ importe: 77, descuentoPorcentaje: 3 }), articuloFixture(), ALICUOTAS, 'busqueda')
    expect(cambios).toMatchObject({ importe: 77, descuentoPorcentaje: 3 })
  })

  it('un artículo sin lista usa el costo nominal y sin descuento', () => {
    const cambios = cambiosAlElegirArticulo(lineaDeCompraVacia(1), articuloFixture({ costoNominal: 80 }), ALICUOTAS, 'busqueda')
    expect(cambios).toMatchObject({ importe: 80, descuentoPorcentaje: null })
  })

  it('cambiar de artículo invalida el lote cargado; elegir el mismo no lo borra', () => {
    const conLote = lineaFixture({ idArticulo: 20, controlaLote: true, codigoLote: 'L-1', fechaVencimiento: '2026-12-01' })
    expect(cambiosAlElegirArticulo(conLote, articuloFixture({ id: 21 }), ALICUOTAS, 'busqueda')).toMatchObject({ codigoLote: '', fechaVencimiento: '' })
    expect(cambiosAlElegirArticulo(conLote, articuloFixture({ id: 20 }), ALICUOTAS, 'busqueda')).not.toHaveProperty('codigoLote')
  })

  it('un artículo recién creado desde la fila conserva el importe, el descuento y el IVA ya tipeados', () => {
    const cambios = cambiosAlElegirArticulo(
      lineaFixture({ idArticulo: '', importe: 123.4567, descuentoPorcentaje: 2, idAlicuotaIva: 4 }),
      articuloFixture({ costoLista: 123.46, descuentoProveedor: null, idAlicuotaIva: 3 }),
      ALICUOTAS,
      'alta',
    )
    expect(cambios).toMatchObject({ importe: 123.4567, descuentoPorcentaje: 2, idAlicuotaIva: 4, resueltaPorCodigo: false })
  })

  it('un artículo recién creado sobre una fila sin importe toma su costo', () => {
    const cambios = cambiosAlElegirArticulo(lineaDeCompraVacia(1), articuloFixture({ costoLista: 50 }), ALICUOTAS, 'alta')
    expect(cambios).toMatchObject({ importe: 50, idAlicuotaIva: 3 })
  })

  it('completarConArticulo completa las unidades por bulto solo cuando la línea no las trae', () => {
    const articulo = { controlaLote: false, idAlicuotaIva: 3 }
    expect(completarConArticulo(lineaFixture({ unidadesPorBulto: null }), { ...articulo, unidadesPorBulto: 12 })).toMatchObject({ unidadesPorBulto: 12 })
    expect(completarConArticulo(lineaFixture({ unidadesPorBulto: 6 }), { ...articulo, unidadesPorBulto: 12 })).toMatchObject({ unidadesPorBulto: 6 })
    expect(completarConArticulo(lineaFixture({ unidadesPorBulto: null }), { ...articulo, unidadesPorBulto: 0 })).toMatchObject({ unidadesPorBulto: null })
  })

  it('completarConArticulo completa si controla lote y no baja un lote ya inferido', () => {
    const base = { unidadesPorBulto: null, idAlicuotaIva: 3 }
    expect(completarConArticulo(lineaFixture({ controlaLote: false }), { ...base, controlaLote: true }).controlaLote).toBe(true)
    expect(completarConArticulo(lineaFixture({ controlaLote: true }), { ...base, controlaLote: false }).controlaLote).toBe(true)
    expect(completarConArticulo(lineaFixture({ controlaLote: false }), { ...base, controlaLote: false }).controlaLote).toBe(false)
  })

  it('completarConArticulo pone la alícuota del artículo solo donde la línea no tiene ninguna: nunca pisa una guardada o tipeada', () => {
    const base = { unidadesPorBulto: null, controlaLote: false }
    expect(completarConArticulo(lineaFixture({ idAlicuotaIva: '' }), { ...base, idAlicuotaIva: 4 }).idAlicuotaIva).toBe(4)
    expect(completarConArticulo(lineaFixture({ idAlicuotaIva: 3 }), { ...base, idAlicuotaIva: 4 }).idAlicuotaIva).toBe(3)
  })
})

describe('edición del Detalle y del Código', () => {
  it('escribir en el Detalle de una fila con artículo la vuelve concepto y conserva importe, descuento e IVA', () => {
    const l = lineaFixture({ resueltaPorCodigo: false, controlaLote: true, codigoLote: 'L', unidadDeMedida: 'Bulto', unidadesPorBulto: 12 })
    const cambios = cambiosAlEscribirDetalle(l, 'Flete')
    expect(cambios).toMatchObject({
      descripcion: 'Flete',
      idArticulo: '',
      controlaLote: false,
      codigoLote: '',
      unidadesPorBulto: null,
      unidadDeMedida: 'Unidad',
      guardada: null,
    })
    expect(cambios).not.toHaveProperty('importe')
    expect(cambios).not.toHaveProperty('idAlicuotaIva')
  })

  it('escribir en el Detalle de un concepto solo cambia el texto', () => {
    expect(cambiosAlEscribirDetalle(lineaFixture({ idArticulo: '' }), 'Flete')).toEqual({ descripcion: 'Flete' })
  })

  it('editar el código de una fila resuelta por código le saca el artículo: vuelve a ser concepto con Detalle vacío y el código sin consultar', () => {
    const resuelta = lineaFixture({ resueltaPorCodigo: true, codigoProveedor: 'AB-1', codigoConsultado: 'AB-1', controlaLote: true, unidadesPorBulto: 12 })
    const cambios = cambiosAlEscribirCodigo(resuelta, 'AB-2')
    expect(cambios).toMatchObject({
      codigoProveedor: 'AB-2',
      idArticulo: '',
      descripcion: '',
      resueltaPorCodigo: false,
      codigoConsultado: '',
      controlaLote: false,
      unidadesPorBulto: null,
      guardada: null,
    })
    expect(tipoDeLinea({ ...resuelta, ...cambios })).toBe('concepto')
    expect(cambios).not.toHaveProperty('importe')
  })

  it('editar el código de una fila cuyo artículo se eligió buscando o se creó con "+" conserva el artículo: el código es el pendiente de asociar', () => {
    expect(cambiosAlEscribirCodigo(lineaFixture({ resueltaPorCodigo: false }), 'X')).toEqual({ codigoProveedor: 'X' })
  })

  it('cambiosAlDesresolver deja la fila sin el artículo que le dio un código', () => {
    expect(cambiosAlDesresolver()).toMatchObject({ idArticulo: '', descripcion: '', resueltaPorCodigo: false, codigoConsultado: '', actualizaCosto: true })
  })
})

describe('diferencia de precio por bulto', () => {
  const porBulto = (sobrescribir: Partial<LineaDeCompraFormulario> = {}) =>
    lineaFixture({ unidadDeMedida: 'Bulto', unidadesPorBulto: 3, cantidad: '1000', importe: 100, descuentoPorcentaje: null, ...sobrescribir })

  it('1000 bultos de 3 unidades a 100,00 por bulto se registran por 99.999,90: diferencia de 0,10', () => {
    const l = porBulto()
    expect(valoresParaApi(l).costoUnitario).toBe(33.3333)
    expect(diferenciaDePrecioPorBulto(l)).toEqual({ registrado: 99999.9, diferencia: 0.1, unidadesPorBulto: 3 })
  })

  it('un bulto que divide exacto entre sus unidades no tiene diferencia', () => {
    expect(diferenciaDePrecioPorBulto(porBulto({ unidadesPorBulto: 4, importe: 100 }))).toBeNull()
    expect(diferenciaDePrecioPorBulto(porBulto({ unidadesPorBulto: 12, cantidad: '2', importe: 120 }))).toBeNull()
  })

  it('un bulto que no divide exacto pero cuyo redondeo final coincide con cantidad × importe no tiene diferencia', () => {
    // 1 bulto de 3 unidades a 100: 33,3333 × 3 = 99,9999 → 100,00
    expect(diferenciaDePrecioPorBulto(porBulto({ cantidad: '1' }))).toBeNull()
  })

  it('un redondeo hacia arriba también se informa, con la diferencia negativa (1000 bultos de 3 a 2,00 se registran de más: -0,10)', () => {
    expect(diferenciaDePrecioPorBulto(porBulto({ importe: 2 }))).toMatchObject({ registrado: 2000.1, diferencia: -0.1 })
  })

  it('una fila por unidades, un concepto o una línea mezclada guardada nunca tienen diferencia', () => {
    expect(diferenciaDePrecioPorBulto(lineaFixture({ cantidad: '1000', importe: 33.3333 }))).toBeNull()
    expect(diferenciaDePrecioPorBulto(porBulto({ idArticulo: '', descripcion: 'Flete' }))).toBeNull()
    const mezcla = itemAFormulario(1, itemFixture({ cantidad: 25, bultos: 2, unidadesPorBulto: 12, costoUnitario: 3 }))
    expect(diferenciaDePrecioPorBulto(mezcla)).toBeNull()
  })

  it('una línea guardada solo de bultos que no se toca no inventa diferencia', () => {
    const guardada = itemAFormulario(1, itemFixture({ cantidad: 3000, bultos: 1000, unidadesPorBulto: 3, costoUnitario: 33.3333, descuento: 0 }))
    expect(guardada.importe).toBe(99.9999)
    expect(diferenciaDePrecioPorBulto(guardada)).toBeNull()
  })
})

describe('precarga según el modo de precios', () => {
  const NETO: Parameters<typeof importeSegunModoDePrecios>[2] = { discriminaIva: true, preciosIncluyenIva: false }

  it('con un comprobante que discrimina y precios netos, el costo (con IVA) se carga sin IVA a 4 decimales', () => {
    expect(importeSegunModoDePrecios(121, 21, NETO)).toBe(100)
    expect(importeSegunModoDePrecios(100, 21, NETO)).toBe(82.6446)
    expect(importeSegunModoDePrecios(110.5, 10.5, NETO)).toBe(100)
  })

  it('con precios con IVA incluido, sin discriminar o sin modo, el costo se carga tal cual', () => {
    expect(importeSegunModoDePrecios(121, 21, { discriminaIva: true, preciosIncluyenIva: true })).toBe(121)
    expect(importeSegunModoDePrecios(121, 21, { discriminaIva: false, preciosIncluyenIva: false })).toBe(121)
    expect(importeSegunModoDePrecios(121, 21, undefined)).toBe(121)
    expect(importeSegunModoDePrecios(121, null, NETO)).toBe(121)
  })

  it('elegir un artículo usa la alícuota que le queda a la fila para extraer el IVA', () => {
    const articulo = articuloFixture({ costoLista: 110.5, idAlicuotaIva: 4, descuentoProveedor: 5 })
    expect(cambiosAlElegirArticulo(lineaDeCompraVacia(1), articulo, ALICUOTAS, 'busqueda', NETO)).toMatchObject({ importe: 100, descuentoPorcentaje: 5 })
    expect(cambiosAlElegirArticulo(lineaDeCompraVacia(1), articulo, ALICUOTAS, 'busqueda')).toMatchObject({ importe: 110.5 })
  })

  it('el costo nominal (costo efectivo con IVA) se trata igual que el de lista', () => {
    const articulo = articuloFixture({ costoNominal: 121, idAlicuotaIva: 3 })
    expect(cambiosAlElegirArticulo(lineaDeCompraVacia(1), articulo, ALICUOTAS, 'busqueda', NETO).importe).toBe(100)
  })

  it('un artículo recién creado conserva el importe tipeado aunque el modo sea neto', () => {
    const cambios = cambiosAlElegirArticulo(lineaFixture({ idArticulo: '', importe: 77 }), articuloFixture({ costoLista: 121 }), ALICUOTAS, 'alta', NETO)
    expect(cambios.importe).toBe(77)
  })
})

describe('cantidad de bultos', () => {
  const porBulto = (sobrescribir: Partial<LineaDeCompraFormulario> = {}) =>
    lineaFixture({ unidadDeMedida: 'Bulto', unidadesPorBulto: 3, unidadVenta: 'Unidad', cantidad: '2', importe: 30, descuentoPorcentaje: null, ...sobrescribir })

  it('bultos enteros dan unidades enteras: válido', () => {
    expect(mensajeDeCantidadDeBultos(porBulto())).toBeNull()
    expect(lineaCompletaParaEnvio(porBulto())).toBe(true)
  })

  it('bultos fraccionados que dan unidades enteras en un artículo por unidad: válido (0,5 bulto de 4)', () => {
    expect(mensajeDeCantidadDeBultos(porBulto({ cantidad: '0.5', unidadesPorBulto: 4 }))).toBeNull()
  })

  it('bultos fraccionados que dan unidades fraccionadas en un artículo por unidad: inválido, con mensaje', () => {
    const l = porBulto({ cantidad: '1.5' })
    expect(mensajeDeCantidadDeBultos(l)).toBe(MENSAJE_DE_BULTOS_QUE_FRACCIONAN_UNIDADES)
    expect(camposInvalidosDeLinea(l)).toContain('cantidad')
  })

  it('un artículo por peso admite bultos fraccionados que den unidades con fracción', () => {
    expect(mensajeDeCantidadDeBultos(porBulto({ cantidad: '1.5', unidadVenta: 'Peso' }))).toBeNull()
  })

  it('más de 2 decimales de bultos es inválido (la columna es numeric(10,2))', () => {
    const l = porBulto({ cantidad: '1.234', unidadVenta: 'Peso' })
    expect(mensajeDeCantidadDeBultos(l)).toBe(MENSAJE_DE_BULTOS_CON_DECIMALES_DE_MAS)
    expect(camposInvalidosDeLinea(l)).toContain('cantidad')
    expect(mensajeDeCantidadDeBultos(porBulto({ cantidad: '1.23', unidadVenta: 'Peso' }))).toBeNull()
  })

  it('sin UM Bulto, sin cantidad o en un concepto no hay mensaje', () => {
    expect(mensajeDeCantidadDeBultos(lineaFixture({ cantidad: '1.5', unidadVenta: 'Unidad' }))).toBeNull()
    expect(mensajeDeCantidadDeBultos(porBulto({ cantidad: '' }))).toBeNull()
    expect(mensajeDeCantidadDeBultos(porBulto({ idArticulo: '' }))).toBeNull()
  })
})

describe('cambiosAlResolverPorCodigo respeta lo tipeado mientras la consulta estaba en vuelo', () => {
  const alLanzar = () => lineaFixture({ idArticulo: '', descripcion: '', importe: null, descuentoPorcentaje: null, idAlicuotaIva: '', cantidad: '' })
  const articulo = articuloFixture({ costoLista: 200, descuentoProveedor: 10, idAlicuotaIva: 4 })

  it('las celdas que no se tocaron se precargan con el artículo', () => {
    const lanzada = alLanzar()
    expect(cambiosAlResolverPorCodigo(lanzada, lanzada, articulo, ALICUOTAS)).toMatchObject({ importe: 200, descuentoPorcentaje: 10, idAlicuotaIva: 4 })
  })

  it('las celdas tipeadas entretanto se conservan, una por una', () => {
    const lanzada = alLanzar()
    const actual = { ...lanzada, importe: 150, idAlicuotaIva: 3 as const, cantidad: '7' }
    const cambios = cambiosAlResolverPorCodigo(lanzada, actual, articulo, ALICUOTAS)
    expect(cambios).not.toHaveProperty('importe')
    expect(cambios).not.toHaveProperty('idAlicuotaIva')
    expect(cambios).toMatchObject({ idArticulo: 20, descuentoPorcentaje: 10 })
  })
})
