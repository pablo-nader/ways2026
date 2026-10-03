import { describe, expect, it } from 'vitest'
import {
  aSolicitudDeCompra,
  calcularTotalesDeCompra,
  lineaDeConceptoVacia,
  percepcionesAplicables,
  type EncabezadoDeCompraFormulario,
  type LineaDeCalculo,
  type PercepcionFormulario,
} from './compras'

function linea(sobrescribir: Partial<LineaDeCalculo> = {}): LineaDeCalculo {
  return {
    idAlicuotaIva: 1,
    unidades: 1,
    bultos: 0,
    unidadesPorBulto: 0,
    costoUnitario: 121,
    descuento: 0,
    porcentajeIva: 21,
    ...sobrescribir,
  }
}

function percepcion(sobrescribir: Partial<PercepcionFormulario> = {}): PercepcionFormulario {
  return { tipo: 'iibb', baseImponible: 1000, alicuota: 3, importe: 30, automatica: false, ...sobrescribir }
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

function lineaFormulario() {
  return { ...lineaDeConceptoVacia(1), descripcion: 'Total', costoUnitario: 1210, idAlicuotaIva: 3 as const }
}

describe('calcularTotalesDeCompra — precios con IVA incluido', () => {
  it('extrae el IVA del final y el neto es el resto: 121 al 21% da neto 100 e IVA 21', () => {
    const totales = calcularTotalesDeCompra([linea()], true, {}, true)

    expect(totales.alicuotas[0]).toMatchObject({ neto: 100, iva: 21, ivaCalculado: 21 })
    expect(totales.ivaTotal).toBe(21)
    expect(totales.total).toBe(121)
  })

  it('alícuotas mezcladas suman exactamente lo tipeado (mismas cifras que el servidor)', () => {
    const totales = calcularTotalesDeCompra(
      [
        linea({ costoUnitario: 121 }),
        linea({ costoUnitario: 60.5 }),
        linea({ idAlicuotaIva: 2, porcentajeIva: 10.5, costoUnitario: 110.5 }),
        linea({ idAlicuotaIva: 5, porcentajeIva: 0, costoUnitario: 50 }),
      ],
      true,
      {},
      true,
    )

    expect(totales.alicuotas.map((a) => [a.neto, a.iva])).toEqual([
      [150, 31.5],
      [100, 10.5],
      [50, 0],
    ])
    expect(totales.ivaTotal).toBe(42)
    expect(totales.total).toBe(342)
  })

  it('redondea una vez por alícuota: tres finales de 0,50 dan IVA 0,26 y neto 1,24', () => {
    const totales = calcularTotalesDeCompra(
      [linea({ costoUnitario: 0.5 }), linea({ costoUnitario: 0.5 }), linea({ costoUnitario: 0.5 })],
      true,
      {},
      true,
    )

    expect(totales.alicuotas[0]).toMatchObject({ neto: 1.24, iva: 0.26 })
    expect(totales.total).toBe(1.5)
  })

  it('con un IVA impreso el neto se mueve en sentido contrario y el total no cambia', () => {
    const totales = calcularTotalesDeCompra([linea()], true, { 1: 21.5 }, true)

    expect(totales.alicuotas[0]).toMatchObject({ neto: 99.5, iva: 21.5, ivaCalculado: 21, fueraDeTolerancia: false })
    expect(totales.ivaTotal).toBe(21.5)
    expect(totales.total).toBe(121)
  })

  it('avisa fuera de tolerancia contra el IVA calculado del modo precio final', () => {
    const totales = calcularTotalesDeCompra([linea()], true, { 1: 22.01 }, true)

    expect(totales.alicuotas[0].fueraDeTolerancia).toBe(true)
  })

  it('un IVA impreso que dejaría el neto negativo avisa antes de guardar', () => {
    const totales = calcularTotalesDeCompra([linea({ costoUnitario: 0.5 })], true, { 1: 1.09 }, true)

    expect(totales.alicuotas[0].fueraDeTolerancia).toBe(true)
  })

  it('sin el flag el mismo costo sigue siendo neto y el IVA se suma', () => {
    const totales = calcularTotalesDeCompra([linea()], true, {}, false)

    expect(totales.alicuotas[0]).toMatchObject({ neto: 121, iva: 25.41 })
    expect(totales.total).toBe(146.41)
  })

  it('en un comprobante que no discrimina el flag no cambia nada: ya es un precio final', () => {
    const totales = calcularTotalesDeCompra([linea()], false, {}, true)

    expect(totales.alicuotas).toEqual([])
    expect(totales.ivaTotal).toBeNull()
    expect(totales.total).toBe(121)
  })
})

describe('calcularTotalesDeCompra — percepciones', () => {
  it('suman al total con precios netos con el importe de la factura', () => {
    const totales = calcularTotalesDeCompra([linea({ costoUnitario: 100 })], true, {}, false, [
      { importe: 3 },
      { importe: 1.5 },
    ])

    expect(totales.ivaTotal).toBe(21)
    expect(totales.percepcionesTotal).toBe(4.5)
    expect(totales.total).toBe(125.5)
  })

  it('suman al total con precios finales sin contar el IVA dos veces', () => {
    const totales = calcularTotalesDeCompra([linea()], true, {}, true, [{ importe: 3.63 }])

    expect(totales.total).toBe(124.63)
  })

  it('suman también en un comprobante que no discrimina', () => {
    const totales = calcularTotalesDeCompra([linea({ costoUnitario: 1000 })], false, {}, false, [{ importe: 30 }])

    expect(totales.total).toBe(1030)
  })

  it('una percepción sin importe todavía no suma', () => {
    const totales = calcularTotalesDeCompra([linea({ costoUnitario: 100 })], false, {}, false, [{ importe: null }])

    expect(totales.percepcionesTotal).toBe(0)
    expect(totales.total).toBe(100)
  })
})

describe('percepcionesAplicables', () => {
  const todas = [percepcion(), percepcion({ tipo: 'iva', importe: 15 })]

  it('un tipo que no registra libro IVA no admite ninguna', () => {
    expect(percepcionesAplicables(todas, false, true)).toEqual([])
  })

  it('una factura que no discrimina deja afuera la de IVA', () => {
    expect(percepcionesAplicables(todas, true, false).map((p) => p.tipo)).toEqual(['iibb'])
  })

  it('una factura que discrimina admite las dos', () => {
    expect(percepcionesAplicables(todas, true, true).map((p) => p.tipo)).toEqual(['iibb', 'iva'])
  })
})

describe('aSolicitudDeCompra — precio final y percepciones', () => {
  it('manda preciosIncluyenIva solo si el comprobante discrimina', () => {
    const e = encabezado({ preciosIncluyenIva: true })

    expect(aSolicitudDeCompra(e, [lineaFormulario()], true, true).preciosIncluyenIva).toBe(true)
    expect(aSolicitudDeCompra(e, [lineaFormulario()], false, true).preciosIncluyenIva).toBe(false)
  })

  it('manda las percepciones con importe tal cual y base/alícuota informativas', () => {
    const solicitud = aSolicitudDeCompra(
      encabezado({ percepciones: [percepcion({ baseImponible: 1000, alicuota: 3, importe: 31.07 })] }),
      [lineaFormulario()],
      true,
      true,
    )

    expect(solicitud.percepciones).toEqual([{ tipo: 'iibb', baseImponible: 1000, alicuota: 3, importe: 31.07 }])
  })

  it('no manda las percepciones de un tipo que no registra libro IVA', () => {
    const solicitud = aSolicitudDeCompra(encabezado({ percepciones: [percepcion()] }), [lineaFormulario()], true, false)

    expect(solicitud.percepciones).toBeNull()
  })

  it('no manda la de IVA si el comprobante no discrimina', () => {
    const solicitud = aSolicitudDeCompra(
      encabezado({ percepciones: [percepcion(), percepcion({ tipo: 'iva', importe: 15 })] }),
      [lineaFormulario()],
      false,
      true,
    )

    expect(solicitud.percepciones?.map((p) => p.tipo)).toEqual(['iibb'])
  })

  it('una fila sin importe no viaja a medio llenar', () => {
    const solicitud = aSolicitudDeCompra(
      encabezado({ percepciones: [percepcion({ importe: null })] }),
      [lineaFormulario()],
      true,
      true,
    )

    expect(solicitud.percepciones).toBeNull()
  })

  it('base o alícuota vacías viajan como cero, nunca como null', () => {
    const solicitud = aSolicitudDeCompra(
      encabezado({ percepciones: [percepcion({ baseImponible: null, alicuota: null, importe: 12 })] }),
      [lineaFormulario()],
      true,
      true,
    )

    expect(solicitud.percepciones).toEqual([{ tipo: 'iibb', baseImponible: 0, alicuota: 0, importe: 12 }])
  })

  it('una percepción automática en cero no se persiste', () => {
    const solicitud = aSolicitudDeCompra(
      encabezado({
        percepciones: [
          percepcion({ automatica: true, baseImponible: 0, importe: 0 }),
          percepcion({ tipo: 'iva', automatica: true, importe: 15 }),
        ],
      }),
      [lineaFormulario()],
      true,
      true,
    )

    expect(solicitud.percepciones?.map((p) => p.tipo)).toEqual(['iva'])
  })

  it('una percepción en cero que el operador tocó o agregó sí viaja', () => {
    const solicitud = aSolicitudDeCompra(
      encabezado({ percepciones: [percepcion({ automatica: false, importe: 0 })] }),
      [lineaFormulario()],
      true,
      true,
    )

    expect(solicitud.percepciones).toEqual([{ tipo: 'iibb', baseImponible: 1000, alicuota: 3, importe: 0 }])
  })

  it('sin percepciones manda null', () => {
    expect(aSolicitudDeCompra(encabezado(), [lineaFormulario()], true, true).percepciones).toBeNull()
  })
})
