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
    porcentajeImpuestoInterno: 8,
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
  it('es el porcentaje del artículo sobre el neto de la línea (cantidad × importe − descuento), a 2 decimales', () => {
    expect(impuestoInternoSugerido(linea(), PORCENTAJES, NETO)).toBe(80)
    expect(impuestoInternoSugerido(linea({ descuentoPorcentaje: 10 }), PORCENTAJES, NETO)).toBe(72)
  })

  it('redondea la mitad lejos de cero', () => {
    expect(impuestoInternoSugerido(linea({ cantidad: '1', importe: 1.25, porcentajeImpuestoInterno: 10 }), PORCENTAJES, NETO)).toBe(0.13)
    expect(impuestoInternoSugerido(linea({ cantidad: '1', importe: 0.05, porcentajeImpuestoInterno: 10 }), PORCENTAJES, NETO)).toBe(0.01)
  })

  it('con precios con IVA incluido parte del neto: extrae el IVA del total de la línea', () => {
    expect(impuestoInternoSugerido(linea({ cantidad: '1', importe: 121 }), PORCENTAJES, FINAL)).toBe(8)
  })

  it('es cero sin porcentaje en el artículo', () => {
    expect(impuestoInternoSugerido(linea({ porcentajeImpuestoInterno: 0 }), PORCENTAJES, NETO)).toBe(0)
  })
})

describe('admiteImpuestoInterno / impuestoInternoDeLinea', () => {
  it('no se admite en un concepto ni en un comprobante que no discrimina IVA', () => {
    expect(admiteImpuestoInterno(linea(), NETO)).toBe(true)
    expect(admiteImpuestoInterno(linea({ idArticulo: '' }), NETO)).toBe(false)
    expect(admiteImpuestoInterno(linea(), SIN_DISCRIMINAR)).toBe(false)
  })

  it('mientras no se tipeó nada rige el sugerido y sigue a la cantidad y al costo', () => {
    expect(impuestoInternoDeLinea(linea(), PORCENTAJES, NETO)).toBe(80)
    expect(impuestoInternoDeLinea(linea({ cantidad: '20' }), PORCENTAJES, NETO)).toBe(160)
    expect(impuestoInternoDeLinea(linea({ importe: 50 }), PORCENTAJES, NETO)).toBe(40)
  })

  it('el importe tipeado a mano gana y no lo pisa un cambio de cantidad o de costo', () => {
    const manual = linea({ impuestoInterno: 75.5 })
    expect(impuestoInternoDeLinea(manual, PORCENTAJES, NETO)).toBe(75.5)
    expect(impuestoInternoDeLinea({ ...manual, cantidad: '99', importe: 1 }, PORCENTAJES, NETO)).toBe(75.5)
  })

  it('un cero tipeado a mano también gana sobre el sugerido', () => {
    expect(impuestoInternoDeLinea(linea({ impuestoInterno: 0 }), PORCENTAJES, NETO)).toBe(0)
  })

  it('es cero (aunque haya un importe tipeado) en un concepto o sin discriminar IVA', () => {
    expect(impuestoInternoDeLinea(linea({ impuestoInterno: 10 }), PORCENTAJES, SIN_DISCRIMINAR)).toBe(0)
    expect(impuestoInternoDeLinea(linea({ idArticulo: '', impuestoInterno: 10 }), PORCENTAJES, NETO)).toBe(0)
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

describe('cambiosAlElegirArticulo — porcentaje de impuestos internos', () => {
  it('toma el porcentaje del artículo y vuelve al cálculo automático', () => {
    const cambios = cambiosAlElegirArticulo(
      linea({ idArticulo: '', impuestoInterno: 5, porcentajeImpuestoInterno: 0 }),
      articulo({ porcentajeImpuestoInterno: 12 }),
      alicuotas,
      'busqueda',
      NETO,
    )

    expect(cambios.porcentajeImpuestoInterno).toBe(12)
    expect(cambios.impuestoInterno).toBeNull()
  })

  it('un artículo sin porcentaje (o que no lo informa) deja 0', () => {
    expect(cambiosAlElegirArticulo(linea(), articulo({ porcentajeImpuestoInterno: null }), alicuotas, 'busqueda').porcentajeImpuestoInterno).toBe(0)
    expect(cambiosAlElegirArticulo(linea(), articulo(), alicuotas, 'busqueda').porcentajeImpuestoInterno).toBe(0)
  })

  it('volver a elegir el mismo artículo conserva el importe tipeado', () => {
    const cambios = cambiosAlElegirArticulo(linea({ impuestoInterno: 33 }), articulo({ porcentajeImpuestoInterno: 8 }), alicuotas, 'busqueda')

    expect('impuestoInterno' in cambios).toBe(false)
  })
})

describe('una línea que pasa a ser concepto pierde el impuesto interno', () => {
  it('al escribir el Detalle', () => {
    const cambios = cambiosAlEscribirDetalle(linea({ impuestoInterno: 5 }), 'Flete')

    expect(cambios.impuestoInterno).toBeNull()
    expect(cambios.porcentajeImpuestoInterno).toBe(0)
  })

  it('al desresolver el código', () => {
    const cambios = cambiosAlDesresolver()

    expect(cambios.impuestoInterno).toBeNull()
    expect(cambios.porcentajeImpuestoInterno).toBe(0)
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

  it('completa el porcentaje del artículo sin pisar uno ya conocido', () => {
    expect(completarConArticulo(linea({ porcentajeImpuestoInterno: 0 }), articulo({ porcentajeImpuestoInterno: 8 })).porcentajeImpuestoInterno).toBe(8)
    expect(completarConArticulo(linea({ porcentajeImpuestoInterno: 5 }), articulo({ porcentajeImpuestoInterno: 8 })).porcentajeImpuestoInterno).toBe(5)
  })
})

describe('costoDeArticuloParaCompra — el costo nominal de respaldo sale sin el impuesto interno', () => {
  const sinLista = { costoLista: null, descuentoProveedor: null, costoNominal: 129 }

  it('en modo neto: costoNominal × 100 / (100 + IVA + II)', () => {
    // 129 = 100 × (1 + 8 % + 21 %) -> neto 100
    const costo = costoDeArticuloParaCompra({ ...sinLista, porcentajeImpuestoInterno: 8 }, { delArticulo: 21, deLaFila: 21 }, NETO)

    expect(costo?.importe).toBe(100)
  })

  it('con precios con IVA incluido: costoNominal × (100 + IVA) / (100 + IVA + II)', () => {
    // 129 -> 100 neto -> 121 con IVA
    const costo = costoDeArticuloParaCompra({ ...sinLista, porcentajeImpuestoInterno: 8 }, { delArticulo: 21, deLaFila: 21 }, FINAL)

    expect(costo?.importe).toBe(121)
  })

  it('sin porcentaje del artículo el comportamiento no cambia', () => {
    expect(costoDeArticuloParaCompra({ ...sinLista, costoNominal: 121 }, { delArticulo: 21, deLaFila: 21 }, NETO)?.importe).toBe(100)
    expect(costoDeArticuloParaCompra({ ...sinLista, costoNominal: 121, porcentajeImpuestoInterno: null }, { delArticulo: 21, deLaFila: 21 }, FINAL)?.importe).toBe(121)
  })

  it('en un comprobante que no discrimina IVA no hay impuesto interno: el nominal con IVA se usa tal cual menos el excise', () => {
    // sin discriminar no se admite excise: el costo precargado es el nominal sin el excise, con IVA
    const costo = costoDeArticuloParaCompra({ ...sinLista, porcentajeImpuestoInterno: 8 }, { delArticulo: 21, deLaFila: 21 }, SIN_DISCRIMINAR)

    expect(costo?.importe).toBe(121)
  })
})
