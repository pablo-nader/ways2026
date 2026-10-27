import { describe, expect, it } from 'vitest'
import {
  aSolicitudDeCompra,
  calcularTotalesDeCompra,
  lineaDeCompraVacia,
  type EncabezadoDeCompraFormulario,
  type LineaDeCalculo,
} from './compras'

function linea(sobrescribir: Partial<LineaDeCalculo> = {}): LineaDeCalculo {
  return {
    idAlicuotaIva: 1,
    unidades: 10,
    bultos: 0,
    unidadesPorBulto: 0,
    costoUnitario: 100,
    descuento: 0,
    porcentajeIva: 21,
    impuestoInterno: 0,
    ...sobrescribir,
  }
}

function encabezado(sobrescribir: Partial<EncabezadoDeCompraFormulario> = {}): EncabezadoDeCompraFormulario {
  return {
    idProveedor: 1,
    idTipoComprobante: 5,
    idPuntoVenta: 2,
    numeroExterno: '',
    fechaComprobante: '',
    observaciones: '',
    idOrdenCompra: null,
    discriminaIva: null,
    ivaImpreso: {},
    preciosIncluyenIva: false,
    percepciones: [],
    percepcionesDescartadas: [],
    ...sobrescribir,
  }
}

describe('calcularTotalesDeCompra — impuestos internos (espejo de CalculadorDeCompra)', () => {
  it('precios netos: el total es subtotal − descuento + IVA + impuestos internos y el IVA no los grava', () => {
    const totales = calcularTotalesDeCompra([linea({ impuestoInterno: 80 })], true)

    expect(totales.ivaTotal).toBe(210)
    expect(totales.impuestosInternos).toBe(80)
    expect(totales.total).toBe(1290)
  })

  it('precios netos: el costo efectivo es (total con IVA + impuesto interno) / cantidad', () => {
    const totales = calcularTotalesDeCompra([linea({ impuestoInterno: 80 })], true)

    expect(totales.items[0].costoEfectivo).toBe(129)
  })

  it('precios con IVA incluido: el total suma el impuesto interno una sola vez y el costo efectivo es (total + impuesto) / cantidad', () => {
    const totales = calcularTotalesDeCompra([linea({ costoUnitario: 121, impuestoInterno: 80 })], true, {}, true)

    expect(totales.ivaTotal).toBe(210)
    expect(totales.total).toBe(1290)
    expect(totales.items[0].costoEfectivo).toBe(129)
  })

  it('suma el impuesto interno de todas las líneas y lo lleva aparte de las percepciones', () => {
    const totales = calcularTotalesDeCompra(
      [linea({ impuestoInterno: 80 }), linea({ idAlicuotaIva: 2, porcentajeIva: 10.5, impuestoInterno: 20.25 })],
      true,
      {},
      false,
      [{ importe: 30 }],
    )

    expect(totales.impuestosInternos).toBe(100.25)
    expect(totales.percepcionesTotal).toBe(30)
    expect(totales.total).toBe(2445.25)
  })

  it('el impuesto interno no cambia la base del IVA', () => {
    const sin = calcularTotalesDeCompra([linea()], true)
    const con = calcularTotalesDeCompra([linea({ impuestoInterno: 500 })], true)

    expect(con.alicuotas).toEqual(sin.alicuotas)
    expect(con.ivaTotal).toBe(sin.ivaTotal)
  })

  it('sin discriminar IVA no hay impuestos internos aunque la línea los traiga', () => {
    const totales = calcularTotalesDeCompra([linea({ impuestoInterno: 80 })], false)

    expect(totales.impuestosInternos).toBe(0)
    expect(totales.total).toBe(1000)
    expect(totales.items[0].costoEfectivo).toBe(100)
  })

  it('sin impuestos internos el resultado es el de siempre', () => {
    const totales = calcularTotalesDeCompra([linea()], true)

    expect(totales.impuestosInternos).toBe(0)
    expect(totales.total).toBe(1210)
    expect(totales.items[0].costoEfectivo).toBe(121)
  })
})

describe('aSolicitudDeCompra — impuestos internos', () => {
  function lineaDeFormulario(sobrescribir = {}) {
    return {
      ...lineaDeCompraVacia(1),
      idArticulo: 10,
      descripcion: 'Gaseosa',
      cantidad: '10',
      importe: 100,
      idAlicuotaIva: 3,
      porcentajeImpuestoInterno: 8,
      ...sobrescribir,
    }
  }

  it('envía el sugerido mientras la línea está en automático y el tipeado cuando lo hay', () => {
    const automatica = aSolicitudDeCompra(encabezado(), [lineaDeFormulario()], true, true, { 3: 21 })
    const manual = aSolicitudDeCompra(encabezado(), [lineaDeFormulario({ impuestoInterno: 75.5 })], true, true, { 3: 21 })

    expect(automatica.items[0].impuestoInterno).toBe(80)
    expect(manual.items[0].impuestoInterno).toBe(75.5)
  })

  it('envía 0 cuando el comprobante no discrimina IVA o la línea es un concepto', () => {
    const sinDiscriminar = aSolicitudDeCompra(encabezado(), [lineaDeFormulario({ impuestoInterno: 5 })], false, true, { 3: 21 })
    const concepto = aSolicitudDeCompra(encabezado(), [lineaDeFormulario({ idArticulo: '', impuestoInterno: 5 })], true, true, { 3: 21 })

    expect(sinDiscriminar.items[0].impuestoInterno).toBe(0)
    expect(concepto.items[0].impuestoInterno).toBe(0)
  })

  it('con precios con IVA incluido sugiere sobre el neto', () => {
    const solicitud = aSolicitudDeCompra(
      encabezado({ preciosIncluyenIva: true }),
      [lineaDeFormulario({ cantidad: '1', importe: 121 })],
      true,
      true,
      { 3: 21 },
    )

    expect(solicitud.items[0].impuestoInterno).toBe(8)
  })
})
