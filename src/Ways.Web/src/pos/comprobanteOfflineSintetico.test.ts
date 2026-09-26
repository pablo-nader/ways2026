import { describe, expect, it } from 'vitest'
import { construirComprobanteOfflineSintetico } from './comprobanteOfflineSintetico'
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

function instantaneaFixture(articulos: ArticuloDeInstantanea[]): InstantaneaDePos {
  return { momento: '2026-09-20T10:00:00.000Z', idPuntoVenta: 7, articulos, mediosDePago: [], toleranciaPago: 0 }
}

function lineaFixture(sobrescribir: Partial<LineaCarrito> = {}): LineaCarrito {
  return { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', cantidad: 1, ...sobrescribir }
}

/** Tramos con valores todos distintos entre sí y del precio plano — mismo criterio que
 * `instantaneaOffline.test.ts`. */
const ESCALON_3: EscalonDeCantidad = { cantidadDesde: 3, precioFinal: 90, descuentoUnitario: 10, aplicadas: [{ idOferta: 31, nombre: '3 o más', descuentoUnitario: 10 }] }
const ESCALON_6: EscalonDeCantidad = { cantidadDesde: 6, precioFinal: 80, descuentoUnitario: 20, aplicadas: [{ idOferta: 61, nombre: '6 o más', descuentoUnitario: 20 }] }

function articuloConEscalonesFixture(sobrescribir: Partial<ArticuloDeInstantanea> = {}): ArticuloDeInstantanea {
  return articuloFixture({ precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [], escalones: [ESCALON_3, ESCALON_6], ...sobrescribir })
}

describe('construirComprobanteOfflineSintetico', () => {
  it('sin descuento: total de línea y del comprobante = cantidad × precio', () => {
    const comprobante = construirComprobanteOfflineSintetico({
      numero: 5,
      numeroVisible: '0007-00000005',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture({ cantidad: 2 })],
      instantanea: instantaneaFixture([articuloFixture({ precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0 })]),
      pagos: [{ idMedioPago: 1, importe: 200, referencia: null, vuelto: 0 }],
      ahora: new Date('2026-09-20T10:05:00.000Z'),
    })

    expect(comprobante).not.toBeNull()
    expect(comprobante?.items[0]).toMatchObject({ cantidad: 2, precioUnitario: 100, descuento: 0, total: 200 })
    expect(comprobante).toMatchObject({ subtotal: 200, descuentoTotal: 0, total: 200 })
  })

  it('con descuento: espeja CalculadorDeTotales.Calcular — bruto = cantidad×original, descuento = descuentoUnitario×cantidad, total = bruto−descuento', () => {
    const comprobante = construirComprobanteOfflineSintetico({
      numero: 6,
      numeroVisible: '0007-00000006',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture({ cantidad: 3 })],
      instantanea: instantaneaFixture([articuloFixture({ precioOriginal: 150, precioFinal: 120, descuentoUnitario: 30 })]),
      pagos: [{ idMedioPago: 1, importe: 360, referencia: null, vuelto: 0 }],
      ahora: new Date('2026-09-20T10:05:00.000Z'),
    })

    // bruto = 3 * 150 = 450; descuento = 30 * 3 = 90; total = 450 - 90 = 360
    expect(comprobante?.items[0]).toMatchObject({ precioUnitario: 150, descuento: 90, total: 360 })
    expect(comprobante).toMatchObject({ subtotal: 450, descuentoTotal: 90, total: 360 })
  })

  it('suma correctamente varias líneas con valores discriminantes (nunca confunde una línea con otra)', () => {
    const comprobante = construirComprobanteOfflineSintetico({
      numero: 7,
      numeroVisible: '0007-00000007',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture({ idArticulo: 1, cantidad: 1 }), lineaFixture({ idArticulo: 2, cantidad: 4, nombre: 'Fanta 1.5L', codigoBarra: '7790009999999' })],
      instantanea: instantaneaFixture([
        articuloFixture({ idArticulo: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0 }),
        articuloFixture({ idArticulo: 2, codigosBarra: ['7790009999999'], precioOriginal: 50, precioFinal: 45, descuentoUnitario: 5 }),
      ]),
      pagos: [{ idMedioPago: 1, importe: 280, referencia: null, vuelto: 0 }],
      ahora: new Date('2026-09-20T10:05:00.000Z'),
    })

    expect(comprobante?.items).toHaveLength(2)
    expect(comprobante?.items[0]).toMatchObject({ orden: 1, idArticulo: 1, descripcion: 'Coca Cola 1L', total: 100 })
    expect(comprobante?.items[1]).toMatchObject({ orden: 2, idArticulo: 2, descripcion: 'Fanta 1.5L', total: 180 }) // 4*(50-5)
    expect(comprobante).toMatchObject({ subtotal: 300, descuentoTotal: 20, total: 280 })
  })

  it('null cuando alguna línea no tiene artículo en la instantánea (defensa en profundidad)', () => {
    const comprobante = construirComprobanteOfflineSintetico({
      numero: 8,
      numeroVisible: '0007-00000008',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture({ idArticulo: 999 })],
      instantanea: instantaneaFixture([articuloFixture()]),
      pagos: [],
      ahora: new Date(),
    })
    expect(comprobante).toBeNull()
  })

  it('idArea/idListaPrecio quedan en 0 (sentinels) — nunca leídos por ticketDeVenta/VentaFinalizada', async () => {
    const comprobante = construirComprobanteOfflineSintetico({
      numero: 9,
      numeroVisible: '0007-00000009',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture()],
      instantanea: instantaneaFixture([articuloFixture()]),
      pagos: [],
      ahora: new Date(),
    })
    expect(comprobante?.items[0].idArea).toBe(0)
    expect(comprobante?.items[0].idListaPrecio).toBe(0)
  })

  it('idOferta refleja la primera oferta aplicada, o null sin ninguna', () => {
    const conOferta = construirComprobanteOfflineSintetico({
      numero: 10,
      numeroVisible: '0007-00000010',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture()],
      instantanea: instantaneaFixture([articuloFixture({ aplicadas: [{ idOferta: 42, nombre: 'Promo', descuentoUnitario: 10 }] })]),
      pagos: [],
      ahora: new Date(),
    })
    expect(conOferta?.items[0].idOferta).toBe(42)

    const sinOferta = construirComprobanteOfflineSintetico({
      numero: 11,
      numeroVisible: '0007-00000011',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture()],
      instantanea: instantaneaFixture([articuloFixture({ aplicadas: [] })]),
      pagos: [],
      ahora: new Date(),
    })
    expect(sinOferta?.items[0].idOferta).toBeNull()
  })

  it('el comprobante nunca marca loteVencido/precioDiscrepante (offline no resuelve lotes)', () => {
    const comprobante = construirComprobanteOfflineSintetico({
      numero: 12,
      numeroVisible: '0007-00000012',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture()],
      instantanea: instantaneaFixture([articuloFixture()]),
      pagos: [],
      ahora: new Date(),
    })
    expect(comprobante?.items[0].loteVencido).toBe(false)
    expect(comprobante?.items[0].precioDiscrepante).toBe(false)
  })

  // Este módulo lee la instantánea DIRECTO (no el `ResultadoDeResolucion` de la vista previa), así
  // que necesita su propio lookup de tramo: sin él, el ticket impreso llevaría el descuento de
  // cantidad 1 mientras la pantalla y el payload encolado ya cobran el del tramo. Cantidad 6 cruza
  // los dos umbrales; el descuento del tramo es 20, el plano 0 y el del primer tramo 10.
  it('con una cantidad que cruza un umbral, el ticket usa el descuento y la oferta del TRAMO', () => {
    const comprobante = construirComprobanteOfflineSintetico({
      numero: 14,
      numeroVisible: '0007-00000014',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture({ cantidad: 6 })],
      instantanea: instantaneaFixture([articuloConEscalonesFixture()]),
      pagos: [],
      ahora: new Date('2026-09-20T10:05:00.000Z'),
    })

    // bruto = 6 × 100 = 600; descuento = 20 × 6 = 120; total = 480
    expect(comprobante?.items[0]).toMatchObject({ cantidad: 6, precioUnitario: 100, descuento: 120, total: 480, idOferta: 61 })
    expect(comprobante).toMatchObject({ subtotal: 600, descuentoTotal: 120, total: 480 })
  })

  it('con la cantidad por debajo del primer umbral el ticket usa el precio plano — nunca el primer tramo', () => {
    const comprobante = construirComprobanteOfflineSintetico({
      numero: 15,
      numeroVisible: '0007-00000015',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture({ cantidad: 2 })],
      instantanea: instantaneaFixture([articuloConEscalonesFixture()]),
      pagos: [],
      ahora: new Date('2026-09-20T10:05:00.000Z'),
    })

    expect(comprobante?.items[0]).toMatchObject({ cantidad: 2, precioUnitario: 100, descuento: 0, total: 200, idOferta: null })
    expect(comprobante).toMatchObject({ subtotal: 200, descuentoTotal: 0, total: 200 })
  })

  it('encabezado: numero/numeroVisible/idPuntoVenta/idCliente/estado/fecha/pagos vienen tal cual se pasaron', () => {
    const ahora = new Date('2026-09-20T10:05:00.000Z')
    const comprobante = construirComprobanteOfflineSintetico({
      numero: 13,
      numeroVisible: '0007-00000013',
      idPuntoVenta: 7,
      idCliente: 3,
      lineas: [lineaFixture()],
      instantanea: instantaneaFixture([articuloFixture()]),
      pagos: [{ idMedioPago: 1, importe: 100, referencia: 'ref-1', vuelto: 5 }],
      ahora,
    })
    expect(comprobante).toMatchObject({
      numero: 13,
      numeroVisible: '0007-00000013',
      idPuntoVenta: 7,
      idCliente: 3,
      estado: 'Emitido',
      fecha: ahora.toISOString(),
      idComprobanteAsociado: null,
      idPresupuestoOrigen: null,
    })
    expect(comprobante?.pagos).toEqual([{ idMedioPago: 1, importe: 100, referencia: 'ref-1', vuelto: 5 }])
  })
})
