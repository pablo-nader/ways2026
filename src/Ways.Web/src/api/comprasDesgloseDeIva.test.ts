import { describe, expect, it } from 'vitest'
import {
  aSolicitudDeCompra,
  calcularTotalesDeCompra,
  discriminaIvaEfectivo,
  ivaImpresoDesdeDetalle,
  type EncabezadoDeCompraFormulario,
  type LineaDeCalculo,
  type LineaDeCompraFormulario,
} from './compras'
import type { CompraDetalle, TipoComprobanteListado } from './tipos'

function linea(sobrescribir: Partial<LineaDeCalculo> = {}): LineaDeCalculo {
  return {
    idAlicuotaIva: 1,
    unidades: 1,
    bultos: 0,
    unidadesPorBulto: 0,
    costoUnitario: 100,
    descuento: 0,
    porcentajeIva: 21,
    ...sobrescribir,
  }
}

function tipo(sobrescribir: Partial<TipoComprobanteListado> = {}): TipoComprobanteListado {
  return {
    id: 1,
    clase: 'Compra',
    codigo: 'C-FA',
    nombre: 'Factura A de compra',
    letra: 'A',
    signo: 1,
    discriminaIva: true,
    esFiscal: false,
    afectaStock: true,
    codigoAfip: null,
    activo: true,
    registraLibroIva: true,
    ...sobrescribir,
  }
}

function lineaDeFormulario(sobrescribir: Partial<LineaDeCompraFormulario> = {}): LineaDeCompraFormulario {
  return {
    clave: 1,
    tipo: 'concepto',
    idArticulo: '',
    descripcion: 'Total',
    unidades: '1',
    bultos: '',
    unidadesPorBulto: '',
    costoUnitario: 1000,
    descuento: null,
    idAlicuotaIva: 3,
    actualizaCosto: false,
    controlaLote: false,
    codigoLote: '',
    fechaVencimiento: '',
    ...sobrescribir,
  }
}

function encabezado(sobrescribir: Partial<EncabezadoDeCompraFormulario> = {}): EncabezadoDeCompraFormulario {
  return {
    idProveedor: 1,
    idTipoComprobante: 7,
    idPuntoVenta: 2,
    numeroExterno: '',
    fechaComprobante: '',
    observaciones: '',
    idOrdenCompra: null,
    discriminaIva: true,
    ivaImpreso: {},
    preciosIncluyenIva: false,
    percepciones: [],
    percepcionesDescartadas: [],
    ...sobrescribir,
  }
}

describe('calcularTotalesDeCompra — desglose de IVA', () => {
  it('agrupa el neto por alícuota y calcula el IVA de cada una sobre ese neto', () => {
    const totales = calcularTotalesDeCompra(
      [
        linea({ idAlicuotaIva: 1, costoUnitario: 100 }),
        linea({ idAlicuotaIva: 1, costoUnitario: 200 }),
        linea({ idAlicuotaIva: 2, costoUnitario: 100, porcentajeIva: 10.5 }),
      ],
      true,
    )

    expect(totales.alicuotas).toEqual([
      { idAlicuotaIva: 1, porcentaje: 21, neto: 300, ivaCalculado: 63, iva: 63, fueraDeTolerancia: false },
      { idAlicuotaIva: 2, porcentaje: 10.5, neto: 100, ivaCalculado: 10.5, iva: 10.5, fueraDeTolerancia: false },
    ])
    expect(totales.ivaTotal).toBe(73.5)
    expect(totales.total).toBe(473.5)
  })

  it('redondea el IVA una sola vez por alícuota, como el servidor', () => {
    const tresLineas = [0.5, 0.5, 0.5].map((costoUnitario) => linea({ costoUnitario }))

    const totales = calcularTotalesDeCompra(tresLineas, true)

    expect(totales.alicuotas[0].iva).toBe(0.32)
    expect(totales.ivaTotal).toBe(0.32)
  })

  it('exento y no gravado salen con IVA cero', () => {
    const totales = calcularTotalesDeCompra(
      [linea({ idAlicuotaIva: 1 }), linea({ idAlicuotaIva: 5, porcentajeIva: 0, costoUnitario: 50 })],
      true,
    )

    expect(totales.alicuotas[1]).toMatchObject({ idAlicuotaIva: 5, neto: 50, iva: 0 })
    expect(totales.ivaTotal).toBe(21)
  })

  it('sin discriminar no hay desglose', () => {
    const totales = calcularTotalesDeCompra([linea()], false)

    expect(totales.alicuotas).toEqual([])
    expect(totales.ivaTotal).toBeNull()
  })

  it('el IVA impreso reemplaza al calculado y recomputa el IVA total y el total', () => {
    const totales = calcularTotalesDeCompra([linea({ costoUnitario: 300 })], true, { 1: 63.5 })

    expect(totales.alicuotas[0]).toMatchObject({ ivaCalculado: 63, iva: 63.5, fueraDeTolerancia: false })
    expect(totales.ivaTotal).toBe(63.5)
    expect(totales.total).toBe(363.5)
  })

  it.each([
    [64, false],
    [62, false],
    [64.01, true],
    [61.99, true],
    [-0.5, true],
  ])('un IVA impreso de %s respecto de 63 calculado: fuera de tolerancia = %s', (impreso, fuera) => {
    const totales = calcularTotalesDeCompra([linea({ costoUnitario: 300 })], true, { 1: impreso })

    expect(totales.alicuotas[0].fueraDeTolerancia).toBe(fuera)
  })

  it('un override nulo equivale a no tenerlo', () => {
    const totales = calcularTotalesDeCompra([linea({ costoUnitario: 300 })], true, { 1: null })

    expect(totales.alicuotas[0].iva).toBe(63)
  })
})

describe('discriminaIvaEfectivo', () => {
  it('una factura fija el valor del tipo e ignora la elección', () => {
    expect(discriminaIvaEfectivo(tipo({ discriminaIva: true }), false)).toBe(true)
    expect(discriminaIvaEfectivo(tipo({ discriminaIva: false }), true)).toBe(false)
  })

  it('un tipo que no registra libro IVA toma la elección, y sin elección el valor del tipo', () => {
    const remito = tipo({ registraLibroIva: false, discriminaIva: false })

    expect(discriminaIvaEfectivo(remito, true)).toBe(true)
    expect(discriminaIvaEfectivo(remito, false)).toBe(false)
    expect(discriminaIvaEfectivo(remito, null)).toBe(false)
  })

  it('sin tipo elegido no discrimina', () => {
    expect(discriminaIvaEfectivo(null, true)).toBe(false)
  })
})

describe('aSolicitudDeCompra — IVA impreso y discrimina IVA', () => {
  it('manda discriminaIva tal cual lo eligió el encabezado', () => {
    expect(aSolicitudDeCompra(encabezado({ discriminaIva: true }), [], true).discriminaIva).toBe(true)
    expect(aSolicitudDeCompra(encabezado({ discriminaIva: null }), [], false).discriminaIva).toBeNull()
  })

  it('manda solo los IVA impresos con valor de alícuotas presentes en líneas completas', () => {
    const solicitud = aSolicitudDeCompra(
      encabezado({ ivaImpreso: { 3: 210.5, 4: 7, 9: null } }),
      [lineaDeFormulario({ idAlicuotaIva: 3 }), lineaDeFormulario({ clave: 2, idAlicuotaIva: 5, descripcion: '' })],
      true,
    )

    // 4 no tiene línea completa; 9 no tiene valor: ninguno viaja.
    expect(solicitud.ivaImpreso).toEqual([{ idAlicuotaIva: 3, iva: 210.5 }])
  })

  it('no manda IVA impreso si el comprobante no discrimina, aunque haya valores guardados', () => {
    const solicitud = aSolicitudDeCompra(encabezado({ ivaImpreso: { 3: 210.5 } }), [lineaDeFormulario()], false)

    expect(solicitud.ivaImpreso).toBeNull()
  })

  it('sin overrides manda null, nunca una lista vacía', () => {
    expect(aSolicitudDeCompra(encabezado(), [lineaDeFormulario()], true).ivaImpreso).toBeNull()
  })
})

describe('ivaImpresoDesdeDetalle', () => {
  const detalle = (alicuotas: CompraDetalle['alicuotas'], preciosIncluyenIva = false) =>
    ({ alicuotas, preciosIncluyenIva }) as CompraDetalle

  it('devuelve solo las alícuotas cuyo IVA guardado difiere del que sale de su neto', () => {
    const overrides = ivaImpresoDesdeDetalle(
      detalle([
        { idAlicuotaIva: 1, porcentaje: 21, neto: 950, iva: 200 },
        { idAlicuotaIva: 2, porcentaje: 10.5, neto: 100, iva: 10.5 },
      ]),
    )

    expect(overrides).toEqual({ 1: 200 })
  })

  it('un desglose sin diferencias no tiene overrides', () => {
    expect(ivaImpresoDesdeDetalle(detalle([{ idAlicuotaIva: 1, porcentaje: 21, neto: 1000, iva: 210 }]))).toEqual({})
  })

  // Con precios finales el neto guardado es final − iva: 100 final al 21% da iva 17,36 y neto 82,64,
  // y 82,64 × 21% = 17,35 — comparar contra el neto solo inventaba un override que después fallaba
  // la tolerancia al volver a guardar.
  it('con precios finales el IVA calculado sale del final de la alícuota, no del neto', () => {
    const alicuotas = [{ idAlicuotaIva: 1, porcentaje: 21, neto: 82.64, iva: 17.36 }]

    expect(ivaImpresoDesdeDetalle(detalle(alicuotas, true))).toEqual({})
    expect(ivaImpresoDesdeDetalle(detalle(alicuotas, false))).toEqual({ 1: 17.36 })
  })

  it('con precios finales un IVA que difiere del extraído del final sí es un override', () => {
    const overrides = ivaImpresoDesdeDetalle(
      detalle(
        [
          { idAlicuotaIva: 1, porcentaje: 21, neto: 99.5, iva: 21.5 },
          { idAlicuotaIva: 2, porcentaje: 10.5, neto: 100, iva: 10.5 },
        ],
        true,
      ),
    )

    expect(overrides).toEqual({ 1: 21.5 })
  })
})
