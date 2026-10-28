import { describe, expect, it } from 'vitest'
import {
  aLineaSolicitada,
  admiteImpuestoInterno,
  cambiosAlDesresolver,
  cambiosAlElegirArticulo,
  cambiosAlEscribirDetalle,
  completarConArticulo,
  costoDeArticuloParaCompra,
  impuestoInternoDeLinea,
  impuestoInternoPorUnidadDelArticulo,
  impuestoInternoSugerido,
  itemAFormulario,
  lineaDeCompraVacia,
  type LineaDeCompraFormulario,
  type ModoDePreciosDeCompra,
} from './lineaDeCompra'
import { lineaFormularioACalculo } from './compras'
import type { AlicuotaIvaListado, ArticuloListado, ItemDeCompra } from './tipos'

const NETO: ModoDePreciosDeCompra = { discriminaIva: true, preciosIncluyenIva: false }
const FINAL: ModoDePreciosDeCompra = { discriminaIva: true, preciosIncluyenIva: true }
const SIN_DISCRIMINAR: ModoDePreciosDeCompra = { discriminaIva: false, preciosIncluyenIva: false }
const PORCENTAJES = { 3: 21 }

function linea(sobrescribir: Partial<LineaDeCompraFormulario> = {}): LineaDeCompraFormulario {
  return {
    ...lineaDeCompraVacia(1),
    idArticulo: 10,
    descripcion: 'Gaseosa 2L',
    cantidad: '10',
    importe: 100,
    idAlicuotaIva: 3,
    impuestoInternoPorUnidad: 8,
    ...sobrescribir,
  }
}

const alicuotas: AlicuotaIvaListado[] = [{ id: 3, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true }]

function articulo(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    id: 10,
    codigoInterno: 'A-10',
    nombre: 'Gaseosa 2L',
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
    costoLista: 100,
    costoListaPor: 'Unidad',
    costoListaIncluyeIva: false,
    descuentoProveedor: 0,
    impuestoInterno: 8,
    costoNominal: 129,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    controlaLote: false,
    acumulaEnVenta: true,
    idFamilia: null,
    disponibleOnline: true,
    disponibleVenta: true,
    tieneImagen: false,
    ...sobrescribir,
  }
}

describe('impuestoInternoSugerido', () => {
  it('es el importe por unidad del artículo por las unidades de la línea, sin depender del costo ni del descuento', () => {
    expect(impuestoInternoSugerido(linea())).toBe(80)
    expect(impuestoInternoSugerido(linea({ descuentoPorcentaje: 10, importe: 1 }))).toBe(80)
  })

  it('redondea la mitad lejos de cero', () => {
    expect(impuestoInternoSugerido(linea({ cantidad: '1', impuestoInternoPorUnidad: 0.125 }))).toBe(0.13)
    expect(impuestoInternoSugerido(linea({ cantidad: '1', impuestoInternoPorUnidad: 0.005 }))).toBe(0.01)
  })

  it('en una línea por bulto multiplica por las unidades del bulto', () => {
    // 50 por bulto de 12 unidades = 4,1666... por unidad; 2 bultos = 24 unidades = 100
    const porBulto = { unidadDeMedida: 'Bulto' as const, unidadesPorBulto: 12, cantidad: '2', impuestoInternoPorUnidad: 50 / 12 }
    expect(impuestoInternoSugerido(linea(porBulto))).toBe(100)
  })

  it('es cero sin importe en el artículo', () => {
    expect(impuestoInternoSugerido(linea({ impuestoInternoPorUnidad: 0 }))).toBe(0)
  })
})

describe('impuestoInternoPorUnidadDelArticulo', () => {
  it('por unidad es el importe del artículo', () => {
    expect(impuestoInternoPorUnidadDelArticulo({ impuestoInterno: 8, costoListaPor: 'Unidad' })).toBe(8)
    expect(impuestoInternoPorUnidadDelArticulo({ impuestoInterno: 8 })).toBe(8)
  })

  it('por bulto se divide entre las unidades por bulto', () => {
    expect(impuestoInternoPorUnidadDelArticulo({ impuestoInterno: 96, costoListaPor: 'Bulto', unidadesPorBulto: 12 })).toBe(8)
  })

  it('sin importe, sin visibilidad de costos o por bulto sin unidades es cero', () => {
    expect(impuestoInternoPorUnidadDelArticulo({ impuestoInterno: 0 })).toBe(0)
    expect(impuestoInternoPorUnidadDelArticulo({ impuestoInterno: null })).toBe(0)
    expect(impuestoInternoPorUnidadDelArticulo({})).toBe(0)
    expect(impuestoInternoPorUnidadDelArticulo({ impuestoInterno: 96, costoListaPor: 'Bulto', unidadesPorBulto: null })).toBe(0)
  })
})

describe('admiteImpuestoInterno / impuestoInternoDeLinea', () => {
  it('no se admite en un concepto ni en un comprobante que no discrimina IVA', () => {
    expect(admiteImpuestoInterno(linea(), NETO)).toBe(true)
    expect(admiteImpuestoInterno(linea({ idArticulo: '' }), NETO)).toBe(false)
    expect(admiteImpuestoInterno(linea(), SIN_DISCRIMINAR)).toBe(false)
  })

  it('mientras no se tipeó nada rige el sugerido y sigue a la cantidad', () => {
    expect(impuestoInternoDeLinea(linea(), NETO)).toBe(80)
    expect(impuestoInternoDeLinea(linea({ cantidad: '20' }), NETO)).toBe(160)
    expect(impuestoInternoDeLinea(linea({ importe: 50 }), NETO)).toBe(80)
  })

  it('el importe tipeado a mano gana y no lo pisa un cambio de cantidad o de costo', () => {
    const manual = linea({ impuestoInterno: 75.5 })
    expect(impuestoInternoDeLinea(manual, NETO)).toBe(75.5)
    expect(impuestoInternoDeLinea({ ...manual, cantidad: '99', importe: 1 }, NETO)).toBe(75.5)
  })

  it('un cero tipeado a mano también gana sobre el sugerido', () => {
    expect(impuestoInternoDeLinea(linea({ impuestoInterno: 0 }), NETO)).toBe(0)
  })

  it('es cero (aunque haya un importe tipeado) en un concepto o sin discriminar IVA', () => {
    expect(impuestoInternoDeLinea(linea({ impuestoInterno: 10 }), SIN_DISCRIMINAR)).toBe(0)
    expect(impuestoInternoDeLinea(linea({ idArticulo: '', impuestoInterno: 10 }), NETO)).toBe(0)
  })
})

describe('aLineaSolicitada — impuestoInterno', () => {
  it('envía el importe de la línea que se le pasa', () => {
    expect(aLineaSolicitada(linea(), 80).impuestoInterno).toBe(80)
  })

  it('un concepto envía siempre 0', () => {
    expect(aLineaSolicitada(linea({ idArticulo: '', descripcion: 'Flete' }), 80).impuestoInterno).toBe(0)
  })

  it('sin importe explícito envía 0', () => {
    expect(aLineaSolicitada(linea()).impuestoInterno).toBe(0)
  })
})

describe('lineaFormularioACalculo — impuestoInterno', () => {
  it('lleva el importe efectivo de la línea según el modo del comprobante', () => {
    expect(lineaFormularioACalculo(linea(), PORCENTAJES, NETO).impuestoInterno).toBe(80)
    expect(lineaFormularioACalculo(linea({ impuestoInterno: 12 }), PORCENTAJES, NETO).impuestoInterno).toBe(12)
    expect(lineaFormularioACalculo(linea(), PORCENTAJES, SIN_DISCRIMINAR).impuestoInterno).toBe(0)
  })

  it('sin modo no hay impuesto interno', () => {
    expect(lineaFormularioACalculo(linea(), PORCENTAJES).impuestoInterno).toBe(0)
  })
})

describe('cambiosAlElegirArticulo — impuestos internos del artículo', () => {
  it('toma el importe por unidad del artículo y vuelve al cálculo automático', () => {
    const cambios = cambiosAlElegirArticulo(
      linea({ idArticulo: '', impuestoInterno: 5, impuestoInternoPorUnidad: 0 }),
      articulo({ impuestoInterno: 12 }),
      alicuotas,
      'busqueda',
      NETO,
    )

    expect(cambios.impuestoInternoPorUnidad).toBe(12)
    expect(cambios.impuestoInterno).toBeNull()
  })

  it('con un costo de lista por bulto lo divide entre las unidades por bulto', () => {
    const cambios = cambiosAlElegirArticulo(
      linea({ idArticulo: '' }),
      articulo({ impuestoInterno: 96, costoListaPor: 'Bulto', unidadesPorBulto: 12 }),
      alicuotas,
      'busqueda',
    )

    expect(cambios.impuestoInternoPorUnidad).toBe(8)
  })

  it('un artículo sin importe (o que no lo informa) deja 0', () => {
    expect(cambiosAlElegirArticulo(linea(), articulo({ impuestoInterno: null }), alicuotas, 'busqueda').impuestoInternoPorUnidad).toBe(0)
    expect(cambiosAlElegirArticulo(linea(), articulo({ impuestoInterno: 0 }), alicuotas, 'busqueda').impuestoInternoPorUnidad).toBe(0)
  })

  it('volver a elegir el mismo artículo conserva el importe tipeado', () => {
    const cambios = cambiosAlElegirArticulo(linea({ impuestoInterno: 33 }), articulo({ impuestoInterno: 8 }), alicuotas, 'busqueda')

    expect('impuestoInterno' in cambios).toBe(false)
  })
})

describe('una línea que pasa a ser concepto pierde el impuesto interno', () => {
  it('al escribir el Detalle', () => {
    const cambios = cambiosAlEscribirDetalle(linea({ impuestoInterno: 5 }), 'Flete')

    expect(cambios.impuestoInterno).toBeNull()
    expect(cambios.impuestoInternoPorUnidad).toBe(0)
  })

  it('al desresolver el código', () => {
    const cambios = cambiosAlDesresolver()

    expect(cambios.impuestoInterno).toBeNull()
    expect(cambios.impuestoInternoPorUnidad).toBe(0)
  })
})

describe('itemAFormulario / completarConArticulo — impuesto interno', () => {
  function item(sobrescribir: Partial<ItemDeCompra> = {}): ItemDeCompra {
    return {
      orden: 1,
      idArticulo: 10,
      descripcion: 'Gaseosa 2L',
      cantidad: 10,
      bultos: null,
      unidadesPorBulto: null,
      costoUnitario: 100,
      descuento: 0,
      idAlicuotaIva: 3,
      porcentajeIva: 21,
      total: 1000,
      impuestoInterno: 80,
      actualizaCosto: true,
      precioSugerido: null,
      codigoLote: null,
      fechaVencimiento: null,
      idLote: null,
      codigoProveedor: null,
      ...sobrescribir,
    }
  }

  it('una línea guardada vuelve con su importe impreso como valor tipeado', () => {
    expect(itemAFormulario(1, item()).impuestoInterno).toBe(80)
    expect(itemAFormulario(1, item({ impuestoInterno: 0 })).impuestoInterno).toBe(0)
  })

  it('un importe que el rol no ve (null) deja la línea en automático', () => {
    expect(itemAFormulario(1, item({ impuestoInterno: null })).impuestoInterno).toBeNull()
  })

  it('completa el importe por unidad del artículo sin pisar uno ya conocido', () => {
    expect(completarConArticulo(linea({ impuestoInternoPorUnidad: 0 }), articulo({ impuestoInterno: 8 })).impuestoInternoPorUnidad).toBe(8)
    expect(completarConArticulo(linea({ impuestoInternoPorUnidad: 5 }), articulo({ impuestoInterno: 8 })).impuestoInternoPorUnidad).toBe(5)
  })
})

describe('costoDeArticuloParaCompra — el costo nominal de respaldo sale sin el impuesto interno', () => {
  const sinLista = { costoLista: null, descuentoProveedor: null, costoNominal: 129 }

  it('en modo neto: (nominal − impuesto por unidad) / (1 + IVA)', () => {
    // 129 = 100 × 1,21 + 8 -> neto 100
    const costo = costoDeArticuloParaCompra({ ...sinLista, impuestoInterno: 8 }, { delArticulo: 21, deLaFila: 21 }, NETO)

    expect(costo?.importe).toBe(100)
  })

  it('con precios con IVA incluido: nominal − impuesto por unidad', () => {
    // 129 -> 121 con IVA y sin el excise
    const costo = costoDeArticuloParaCompra({ ...sinLista, impuestoInterno: 8 }, { delArticulo: 21, deLaFila: 21 }, FINAL)

    expect(costo?.importe).toBe(121)
  })

  it('sin importe del artículo el comportamiento no cambia', () => {
    expect(costoDeArticuloParaCompra({ ...sinLista, costoNominal: 121 }, { delArticulo: 21, deLaFila: 21 }, NETO)?.importe).toBe(100)
    expect(costoDeArticuloParaCompra({ ...sinLista, costoNominal: 121, impuestoInterno: null }, { delArticulo: 21, deLaFila: 21 }, FINAL)?.importe).toBe(121)
  })

  it('en un comprobante que no discrimina IVA el excise va dentro del costo: el nominal se usa tal cual', () => {
    const costo = costoDeArticuloParaCompra({ ...sinLista, impuestoInterno: 8 }, { delArticulo: 21, deLaFila: 21 }, SIN_DISCRIMINAR)

    expect(costo?.importe).toBe(129)
  })
})

describe('costoDeArticuloParaCompra — impuesto interno por origen del costo y modo del comprobante', () => {
  const alicuotasDe21 = { delArticulo: 21, deLaFila: 21 }
  // Neto 100 con IVA 21 % e impuesto interno de 8 por unidad: sin IVA 100, con IVA 121, todo incluido 129.
  const listaSinIva = { costoLista: 100, costoListaIncluyeIva: false, descuentoProveedor: null, costoNominal: 129, impuestoInterno: 8 }
  const listaConIva = { costoLista: 129, costoListaIncluyeIva: true, descuentoProveedor: null, costoNominal: 129, impuestoInterno: 8 }
  const soloNominal = { costoLista: null, descuentoProveedor: null, costoNominal: 129, impuestoInterno: 8 }
  // El mismo artículo con el costo de lista y el impuesto por bulto de 12 unidades.
  const bulto = { costoListaPor: 'Bulto' as const, unidadesPorBulto: 12 }
  const listaSinIvaPorBulto = { ...listaSinIva, ...bulto, costoLista: 1200, impuestoInterno: 96 }
  const listaConIvaPorBulto = { ...listaConIva, ...bulto, costoLista: 1548, impuestoInterno: 96 }
  const soloNominalPorBulto = { ...soloNominal, ...bulto, impuestoInterno: 96 }

  const casos = [
    ['lista sin IVA', listaSinIva],
    ['lista con IVA', listaConIva],
    ['nominal de respaldo', soloNominal],
    ['lista sin IVA por bulto', listaSinIvaPorBulto],
    ['lista con IVA por bulto', listaConIvaPorBulto],
    ['nominal de respaldo con la base por bulto', soloNominalPorBulto],
  ] as const

  it.each(casos)('%s, comprobante neto: el importe es el neto y el excise va aparte', (_origen, datos) => {
    expect(costoDeArticuloParaCompra(datos, alicuotasDe21, NETO)?.importe).toBe(100)
  })

  it.each(casos)('%s, precios finales: el importe es el costo con IVA sin el excise', (_origen, datos) => {
    expect(costoDeArticuloParaCompra(datos, alicuotasDe21, FINAL)?.importe).toBe(121)
  })

  it.each(casos)('%s, sin discriminar IVA: el costo todo incluido conserva el excise', (_origen, datos) => {
    expect(costoDeArticuloParaCompra(datos, alicuotasDe21, SIN_DISCRIMINAR)?.importe).toBe(129)
  })

  it.each(casos)('%s: el importe precargado más el excise automático reproduce el costo nominal del servidor', (_origen, datos) => {
    const importe = costoDeArticuloParaCompra(datos, alicuotasDe21, NETO)?.importe ?? 0
    const cambios = cambiosAlElegirArticulo(linea({ idArticulo: '' }), articulo({ ...datos, idAlicuotaIva: 3 }), alicuotas, 'busqueda', NETO)
    const sugerido = impuestoInternoSugerido(linea({ importe, cantidad: '6', impuestoInternoPorUnidad: cambios.impuestoInternoPorUnidad }))

    // (100 neto × 1,21 × 6 unidades + excise de la línea) / 6 unidades = 129
    expect((importe * 1.21 * 6 + sugerido) / 6).toBeCloseTo(129, 2)
  })
})
