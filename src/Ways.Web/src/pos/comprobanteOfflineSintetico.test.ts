import { describe, expect, it } from 'vitest'
import { construirComprobanteOfflineSintetico, type ParametrosDeComprobanteOfflineSintetico } from './comprobanteOfflineSintetico'
import type { ArticuloDeInstantanea, EscalonDeCantidad, EscalonDeFamilia, InstantaneaDePos, PrecioDeListaDeInstantanea } from '../api/tipos'
import type { LineaCarrito } from '../api/carrito'

const LISTA = 5

/** Artículo con UN precio, en `LISTA` salvo que se pida otra: los campos de precio se pasan planos
 * para que cada caso se lea igual que el ticket que produce. */
type ArticuloConPrecio = Omit<ArticuloDeInstantanea, 'preciosPorLista'> & Omit<PrecioDeListaDeInstantanea, 'idListaPrecio'> & { idListaPrecio: number }

function articuloFixture(sobrescribir: Partial<ArticuloConPrecio> = {}): ArticuloDeInstantanea {
  const { precioOriginal = 100, precioFinal = 100, descuentoUnitario = 0, aplicadas = [], escalones, escalonesDeFamilia, idListaPrecio = LISTA, ...resto } = sobrescribir
  return {
    idArticulo: 1,
    codigoInterno: 'A0001',
    nombre: 'Coca Cola 1L',
    codigosBarra: ['7790001234567'],
    idAlicuotaIva: 1,
    porcentajeIva: 21,
    ...resto,
    preciosPorLista: [{ idListaPrecio, precioOriginal, precioFinal, descuentoUnitario, aplicadas, escalones, escalonesDeFamilia }],
  }
}

function instantaneaFixture(articulos: ArticuloDeInstantanea[]): InstantaneaDePos {
  return { momento: '2026-09-20T10:00:00.000Z', idPuntoVenta: 7, articulos, clientes: [], mediosDePago: [], toleranciaPago: 0 }
}

function construir(params: Omit<ParametrosDeComprobanteOfflineSintetico, 'idListaPrecio'> & { idListaPrecio?: number }) {
  return construirComprobanteOfflineSintetico({ idListaPrecio: LISTA, ...params })
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

/** Tramos con valores todos distintos entre sí y del precio plano — mismo criterio que
 * `instantaneaOffline.test.ts`. */
const ESCALON_3: EscalonDeCantidad = { cantidadDesde: 3, precioFinal: 90, descuentoUnitario: 10, aplicadas: [{ idOferta: 31, nombre: '3 o más', descuentoUnitario: 10 }] }
const ESCALON_6: EscalonDeCantidad = { cantidadDesde: 6, precioFinal: 80, descuentoUnitario: 20, aplicadas: [{ idOferta: 61, nombre: '6 o más', descuentoUnitario: 20 }] }

function articuloConEscalonesFixture(sobrescribir: Partial<ArticuloConPrecio> = {}): ArticuloDeInstantanea {
  return articuloFixture({ precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [], escalones: [ESCALON_3, ESCALON_6], ...sobrescribir })
}

const CELDA_FAMILIA_2: EscalonDeFamilia = { cantidadDeFamiliaDesde: 2, cantidadDesde: 1, precioFinal: 1750, descuentoUnitario: 250, aplicadas: [{ idOferta: 71, nombre: '2 x 3500', descuentoUnitario: 250 }] }

function alfajorFixture(idArticulo: number, idFamilia: number | null): ArticuloDeInstantanea {
  return articuloFixture({ idArticulo, codigosBarra: [`${idArticulo}`], precioOriginal: 2000, precioFinal: 2000, idFamilia, escalonesDeFamilia: [CELDA_FAMILIA_2] })
}

describe('construirComprobanteOfflineSintetico', () => {
  // La cantidad de la familia sale de TODO el carrito, igual que en la vista previa y en el payload
  // (`preciosVigentesDeLasLineas`): el ticket impreso no puede cobrar 2000 mientras la pantalla 1750.
  it('dos artículos de la misma familia con cantidad 1 se cobran a 1750 cada uno, con la oferta de la celda', () => {
    const comprobante = construir({
      numero: 8,
      numeroVisible: '0007-00000008',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture({ idArticulo: 1, cantidad: 1 }), lineaFixture({ idArticulo: 2, cantidad: 1 })],
      instantanea: instantaneaFixture([alfajorFixture(1, 7), alfajorFixture(2, 7)]),
      pagos: [{ idMedioPago: 1, importe: 3500, referencia: null, vuelto: 0 }],
      ahora: new Date('2026-09-20T10:05:00.000Z'),
    })

    expect(comprobante?.items.map((i) => ({ descuento: i.descuento, total: i.total, idOferta: i.idOferta }))).toEqual([
      { descuento: 250, total: 1750, idOferta: 71 },
      { descuento: 250, total: 1750, idOferta: 71 },
    ])
    expect(comprobante).toMatchObject({ subtotal: 4000, descuentoTotal: 500, total: 3500 })
  })

  it('un artículo de la familia con cantidad 1 junto a uno sin familia se cobra a 2000', () => {
    const comprobante = construir({
      numero: 9,
      numeroVisible: '0007-00000009',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture({ idArticulo: 1, cantidad: 1 }), lineaFixture({ idArticulo: 3, cantidad: 1 })],
      instantanea: instantaneaFixture([alfajorFixture(1, 7), alfajorFixture(3, null)]),
      pagos: [{ idMedioPago: 1, importe: 4000, referencia: null, vuelto: 0 }],
      ahora: new Date('2026-09-20T10:05:00.000Z'),
    })

    expect(comprobante?.items.map((i) => i.descuento)).toEqual([0, 0])
    expect(comprobante).toMatchObject({ subtotal: 4000, descuentoTotal: 0, total: 4000 })
  })

  it('sin descuento: total de línea y del comprobante = cantidad × precio', () => {
    const comprobante = construir({
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
    const comprobante = construir({
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

  it('un bruto que es empate exacto de medio peso sube como en el servidor: 0,7 × 45 = 32, no 31', () => {
    const comprobante = construir({
      numero: 7,
      numeroVisible: '0007-00000007',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture({ cantidad: 0.7 })],
      instantanea: instantaneaFixture([articuloFixture({ precioOriginal: 45, precioFinal: 45, descuentoUnitario: 0 })]),
      pagos: [{ idMedioPago: 1, importe: 32, referencia: null, vuelto: 0 }],
      ahora: new Date('2026-09-20T10:05:00.000Z'),
    })

    expect(comprobante?.items[0]).toMatchObject({ cantidad: 0.7, precioUnitario: 45, descuento: 0, total: 32 })
    expect(comprobante).toMatchObject({ subtotal: 32, descuentoTotal: 0, total: 32 })
  })

  it('la oferta x3 por 3500 (1166,66 por unidad) cobra 3500 en el comprobante: línea, subtotal, descuento y total enteros', () => {
    const comprobante = construir({
      numero: 7,
      numeroVisible: '0007-00000007',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture({ cantidad: 3 })],
      instantanea: instantaneaFixture([articuloFixture({ precioOriginal: 1500, precioFinal: 1166.66, descuentoUnitario: 333.34 })]),
      pagos: [{ idMedioPago: 1, importe: 3500, referencia: null, vuelto: 0 }],
      ahora: new Date('2026-09-20T10:05:00.000Z'),
    })

    expect(comprobante?.items[0]).toMatchObject({ cantidad: 3, precioUnitario: 1500, descuento: 1000, total: 3500 })
    expect(comprobante).toMatchObject({ subtotal: 4500, descuentoTotal: 1000, total: 3500 })
  })

  // Mismos importes que los tramos de la curva del servidor (EscalonesDeCantidad): el cajero
  // offline elige el último tramo que entra y cada uno cobra el neto verdadero redondeado al peso.
  it.each([
    [1, 1500, 0, 1500],
    [2, 3000, 0, 3000],
    [3, 4500, 1000, 3500],
    [5, 7500, 1667, 5833],
    [7, 10500, 2333, 8167],
  ])('cantidad %s sobre una curva con 1166,66 en el tramo de 3: bruto %s, descuento %s, total %s', (cantidad, bruto, descuento, total) => {
    const tramo: EscalonDeCantidad = {
      cantidadDesde: 3,
      precioFinal: 1166.66,
      descuentoUnitario: 333.34,
      aplicadas: [{ idOferta: 9, nombre: 'x3 por 3500', descuentoUnitario: 333.34 }],
    }
    const comprobante = construir({
      numero: 7,
      numeroVisible: '0007-00000007',
      idPuntoVenta: 7,
      idCliente: 1,
      lineas: [lineaFixture({ cantidad })],
      instantanea: instantaneaFixture([articuloFixture({ precioOriginal: 1500, precioFinal: 1500, descuentoUnitario: 0, aplicadas: [], escalones: [tramo] })]),
      pagos: [],
      ahora: new Date('2026-09-20T10:05:00.000Z'),
    })

    expect(comprobante?.items[0]).toMatchObject({ descuento, total })
    expect(comprobante).toMatchObject({ subtotal: bruto, descuentoTotal: descuento, total })
  })

  it('suma correctamente varias líneas con valores discriminantes (nunca confunde una línea con otra)', () => {
    const comprobante = construir({
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
    const comprobante = construir({
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
    const comprobante = construir({
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
    const conOferta = construir({
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

    const sinOferta = construir({
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
    const comprobante = construir({
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
    const comprobante = construir({
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
    const comprobante = construir({
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
    const comprobante = construir({
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

  it('cobra con el precio de la lista de la venta, no con el de otra lista del mismo artículo', () => {
    const articulo: ArticuloDeInstantanea = {
      ...articuloFixture(),
      preciosPorLista: [
        { idListaPrecio: LISTA, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] },
        { idListaPrecio: 8, precioOriginal: 70, precioFinal: 63, descuentoUnitario: 7, aplicadas: [{ idOferta: 4, nombre: 'Mayorista', descuentoUnitario: 7 }] },
      ],
    }
    const comprobante = construir({
      numero: 14,
      numeroVisible: '0007-00000014',
      idPuntoVenta: 7,
      idCliente: 20,
      lineas: [lineaFixture({ cantidad: 2 })],
      instantanea: instantaneaFixture([articulo]),
      idListaPrecio: 8,
      pagos: [{ idMedioPago: 1, importe: 126, referencia: null, vuelto: 0 }],
      ahora: new Date('2026-09-20T10:05:00.000Z'),
    })

    expect(comprobante?.items[0]).toMatchObject({ precioUnitario: 70, descuento: 14, total: 126, idOferta: 4 })
    expect(comprobante).toMatchObject({ subtotal: 140, descuentoTotal: 14, total: 126 })
  })

  describe('ajuste manual por línea', () => {
    const AHORA = new Date('2026-09-20T10:05:00.000Z')

    it('una línea sin ajuste lleva porcentaje null y monto 0, y el comprobante no tiene totales manuales', () => {
      const comprobante = construir({
        numero: 20,
        numeroVisible: '0007-00000020',
        idPuntoVenta: 7,
        idCliente: 1,
        lineas: [lineaFixture({ cantidad: 2 })],
        instantanea: instantaneaFixture([articuloFixture()]),
        pagos: [],
        ahora: AHORA,
      })

      expect(comprobante?.items[0]).toMatchObject({ ajusteManualPorcentaje: null, ajusteManual: 0, total: 200 })
      expect(comprobante).toMatchObject({ descuentoManualTotal: 0, recargoManualTotal: 0, total: 200 })
    })

    it('un descuento manual baja el total de la línea y se informa en positivo en el encabezado', () => {
      const comprobante = construir({
        numero: 21,
        numeroVisible: '0007-00000021',
        idPuntoVenta: 7,
        idCliente: 1,
        lineas: [lineaFixture({ cantidad: 2, ajusteManualPorcentaje: -10 })],
        instantanea: instantaneaFixture([articuloFixture()]),
        pagos: [],
        ahora: AHORA,
      })

      expect(comprobante?.items[0]).toMatchObject({ ajusteManualPorcentaje: -10, ajusteManual: -20, total: 180 })
      expect(comprobante).toMatchObject({ subtotal: 200, descuentoTotal: 0, descuentoManualTotal: 20, recargoManualTotal: 0, total: 180 })
    })

    it('un recargo manual sube el total de la línea y se informa aparte', () => {
      const comprobante = construir({
        numero: 22,
        numeroVisible: '0007-00000022',
        idPuntoVenta: 7,
        idCliente: 1,
        lineas: [lineaFixture({ cantidad: 2, ajusteManualPorcentaje: 15 })],
        instantanea: instantaneaFixture([articuloFixture()]),
        pagos: [],
        ahora: AHORA,
      })

      expect(comprobante?.items[0]).toMatchObject({ ajusteManualPorcentaje: 15, ajusteManual: 30, total: 230 })
      expect(comprobante).toMatchObject({ subtotal: 200, descuentoManualTotal: 0, recargoManualTotal: 30, total: 230 })
    })

    it('el ajuste se aplica sobre el neto posterior a la oferta, no sobre el bruto', () => {
      // bruto = 3 × 150 = 450; oferta = 30 × 3 = 90; neto = 360; descuento manual 10 % = 36
      const comprobante = construir({
        numero: 23,
        numeroVisible: '0007-00000023',
        idPuntoVenta: 7,
        idCliente: 1,
        lineas: [lineaFixture({ cantidad: 3, ajusteManualPorcentaje: -10 })],
        instantanea: instantaneaFixture([articuloFixture({ precioOriginal: 150, precioFinal: 120, descuentoUnitario: 30 })]),
        pagos: [],
        ahora: AHORA,
      })

      expect(comprobante?.items[0]).toMatchObject({ descuento: 90, ajusteManual: -36, total: 324 })
      expect(comprobante).toMatchObject({ subtotal: 450, descuentoTotal: 90, descuentoManualTotal: 36, total: 324 })
    })

    it('un recargo no oculta un descuento: el encabezado lleva los dos y el total los combina', () => {
      const comprobante = construir({
        numero: 24,
        numeroVisible: '0007-00000024',
        idPuntoVenta: 7,
        idCliente: 1,
        lineas: [
          lineaFixture({ idArticulo: 1, cantidad: 2, ajusteManualPorcentaje: -10 }),
          lineaFixture({ idArticulo: 2, cantidad: 1, nombre: 'Fanta 1.5L', codigoBarra: '7790009999999', ajusteManualPorcentaje: 15 }),
        ],
        instantanea: instantaneaFixture([
          articuloFixture({ idArticulo: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0 }),
          articuloFixture({ idArticulo: 2, codigosBarra: ['7790009999999'], precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0 }),
        ]),
        pagos: [],
        ahora: AHORA,
      })

      // 300 − 0 − 20 + 15
      expect(comprobante).toMatchObject({ subtotal: 300, descuentoManualTotal: 20, recargoManualTotal: 15, total: 295 })
      expect(comprobante?.items.map((i) => i.total)).toEqual([180, 115])
    })

    it('redondea el ajuste half-away-from-zero con ambos signos', () => {
      const instantanea = instantaneaFixture([articuloFixture({ precioOriginal: 105, precioFinal: 105, descuentoUnitario: 0 })])
      const descuento = construir({
        numero: 25,
        numeroVisible: '0007-00000025',
        idPuntoVenta: 7,
        idCliente: 1,
        lineas: [lineaFixture({ cantidad: 1, ajusteManualPorcentaje: -10 })],
        instantanea,
        pagos: [],
        ahora: AHORA,
      })
      const recargo = construir({
        numero: 26,
        numeroVisible: '0007-00000026',
        idPuntoVenta: 7,
        idCliente: 1,
        lineas: [lineaFixture({ cantidad: 1, ajusteManualPorcentaje: 10 })],
        instantanea,
        pagos: [],
        ahora: AHORA,
      })

      expect(descuento?.items[0]).toMatchObject({ ajusteManual: -11, total: 94 })
      expect(recargo?.items[0]).toMatchObject({ ajusteManual: 11, total: 116 })
    })

    it('con una cantidad que cruza un umbral, el ajuste va sobre el neto del TRAMO', () => {
      // bruto = 6 × 100 = 600; oferta del tramo = 20 × 6 = 120; neto = 480; recargo 5 % = 24
      const comprobante = construir({
        numero: 27,
        numeroVisible: '0007-00000027',
        idPuntoVenta: 7,
        idCliente: 1,
        lineas: [lineaFixture({ cantidad: 6, ajusteManualPorcentaje: 5 })],
        instantanea: instantaneaFixture([articuloConEscalonesFixture()]),
        pagos: [],
        ahora: AHORA,
      })

      expect(comprobante?.items[0]).toMatchObject({ descuento: 120, ajusteManual: 24, total: 504 })
      expect(comprobante).toMatchObject({ subtotal: 600, descuentoTotal: 120, recargoManualTotal: 24, total: 504 })
    })

    it('el total del comprobante es la suma de los totales de línea (nunca un cálculo alternativo)', () => {
      const comprobante = construir({
        numero: 28,
        numeroVisible: '0007-00000028',
        idPuntoVenta: 7,
        idCliente: 1,
        lineas: [
          lineaFixture({ idArticulo: 1, cantidad: 3, ajusteManualPorcentaje: -33.33 }),
          lineaFixture({ idArticulo: 2, cantidad: 7, nombre: 'Fanta 1.5L', codigoBarra: '7790009999999', ajusteManualPorcentaje: 12.5 }),
        ],
        instantanea: instantaneaFixture([
          articuloFixture({ idArticulo: 1, precioOriginal: 99.99, precioFinal: 89.99, descuentoUnitario: 10 }),
          articuloFixture({ idArticulo: 2, codigosBarra: ['7790009999999'], precioOriginal: 17.35, precioFinal: 17.35, descuentoUnitario: 0 }),
        ]),
        pagos: [],
        ahora: AHORA,
      })

      const sumaDeLineas = Math.round((comprobante?.items.reduce((acumulado, i) => acumulado + i.total, 0) ?? 0) * 100) / 100
      expect(comprobante?.total).toBe(sumaDeLineas)
    })
  })

  it('sin precio en la lista de la venta no arma comprobante', () => {
    const comprobante = construir({
      numero: 15,
      numeroVisible: '0007-00000015',
      idPuntoVenta: 7,
      idCliente: 20,
      lineas: [lineaFixture()],
      instantanea: instantaneaFixture([articuloFixture()]),
      idListaPrecio: 8,
      pagos: [],
      ahora: new Date('2026-09-20T10:05:00.000Z'),
    })

    expect(comprobante).toBeNull()
  })
})

describe('construirComprobanteOfflineSintetico — combos (T5a)', () => {
  const PANCHO = 2
  const COCA = 1
  const combo = {
    idCombo: 7,
    codigo: 7,
    nombre: 'Pancho + Coca',
    precio: 5000,
    componentes: [{ idArticulo: PANCHO, cantidad: 1 }, { idArticulo: COCA, cantidad: 1 }],
  }
  const instantaneaConCombo = (conCombos = true): InstantaneaDePos => ({
    ...instantaneaFixture([
      articuloFixture({ idArticulo: COCA, precioOriginal: 2500, precioFinal: 2500, unidadVenta: 'Unidad' }),
      articuloFixture({ idArticulo: PANCHO, nombre: 'Pancho', codigosBarra: ['2'], precioOriginal: 3000, precioFinal: 3000, unidadVenta: 'Unidad' }),
    ]),
    ...(conCombos ? { combos: [combo] } : {}),
  })
  const base = {
    numero: 20,
    numeroVisible: '0007-00000020',
    idPuntoVenta: 7,
    idCliente: 1,
    pagos: [{ idMedioPago: 1, importe: 5000, referencia: null, vuelto: 0 }],
    ahora: new Date('2026-09-20T10:05:00.000Z'),
  }
  const lineas = [lineaFixture({ idArticulo: COCA, cantidad: 1 }), lineaFixture({ idArticulo: PANCHO, nombre: 'Pancho', cantidad: 1 })]

  it('el descuento de cada línea es su parte del combo, el total del comprobante es el precio del combo y los items traen idCombo', () => {
    const comprobante = construir({ ...base, lineas, instantanea: instantaneaConCombo() })

    expect(comprobante?.items.map((i) => ({ precioUnitario: i.precioUnitario, descuento: i.descuento, total: i.total, idCombo: i.idCombo }))).toEqual([
      { precioUnitario: 2500, descuento: 227, total: 2273, idCombo: 7 },
      { precioUnitario: 3000, descuento: 273, total: 2727, idCombo: 7 },
    ])
    expect(comprobante).toMatchObject({ subtotal: 5500, descuentoTotal: 500, total: 5000 })
  })

  it('sin combos en la instantánea el comprobante es el de siempre: sin descuento y con idCombo nulo', () => {
    const comprobante = construir({ ...base, lineas, instantanea: instantaneaConCombo(false) })

    expect(comprobante?.items.map((i) => [i.descuento, i.idCombo])).toEqual([[0, null], [0, null]])
    expect(comprobante).toMatchObject({ subtotal: 5500, descuentoTotal: 0, total: 5500 })
  })

  it('el ajuste manual de una línea consumida se aplica sobre el neto que ya incluye el combo', () => {
    const conAjuste = [lineas[0], { ...lineas[1], ajusteManualPorcentaje: -10 }]
    const comprobante = construir({ ...base, lineas: conAjuste, instantanea: instantaneaConCombo() })

    // neto 3000 − 273 = 2727; 10 % = 272,7 → 273 al peso.
    expect(comprobante?.items[1]).toMatchObject({ ajusteManual: -273, total: 2454 })
    expect(comprobante).toMatchObject({ descuentoManualTotal: 273, total: 4727 })
  })

  describe('idOferta de un artículo con oferta cuando un combo consume sus unidades (el servidor la persiste igual)', () => {
    const OFERTA = 91
    const conOferta = (): InstantaneaDePos => ({
      ...instantaneaConCombo(),
      articulos: [
        articuloFixture({ idArticulo: COCA, precioOriginal: 2500, precioFinal: 2500, unidadVenta: 'Unidad' }),
        articuloFixture({
          idArticulo: PANCHO,
          nombre: 'Pancho',
          codigosBarra: ['2'],
          precioOriginal: 3000,
          precioFinal: 2500,
          descuentoUnitario: 500,
          aplicadas: [{ idOferta: OFERTA, nombre: 'Oferta pancho', descuentoUnitario: 500 }],
          unidadVenta: 'Unidad',
        }),
      ],
      combos: [{ ...combo, precio: 4800 }],
    })

    it('si el combo consumió todas las unidades de la línea, el item no lleva idOferta', () => {
      const comprobante = construir({ ...base, lineas, instantanea: conOferta() })

      expect(comprobante?.items[1]).toMatchObject({ idCombo: 7, idOferta: null })
    })

    it('si le quedan unidades fuera del combo, esas unidades tuvieron su oferta y el item la conserva', () => {
      const dosPanchos = [lineas[0], { ...lineas[1], cantidad: 2 }]
      const comprobante = construir({ ...base, lineas: dosPanchos, instantanea: conOferta() })

      expect(comprobante?.items[1]).toMatchObject({ idCombo: 7, idOferta: OFERTA })
    })

    it('sin combo la oferta viaja como siempre', () => {
      const comprobante = construir({ ...base, lineas, instantanea: { ...conOferta(), combos: undefined } })

      expect(comprobante?.items[1]).toMatchObject({ idCombo: null, idOferta: OFERTA })
    })
  })

  it('un artículo pesable en el carrito deja el combo sin aplicar en el ticket', () => {
    const instantanea: InstantaneaDePos = {
      ...instantaneaConCombo(),
      articulos: instantaneaConCombo().articulos.map((a) => (a.idArticulo === PANCHO ? { ...a, unidadVenta: 'Peso' as const } : a)),
    }
    const comprobante = construir({ ...base, lineas, instantanea })

    expect(comprobante?.items.map((i) => i.idCombo)).toEqual([null, null])
    expect(comprobante).toMatchObject({ descuentoTotal: 0, total: 5500 })
  })
})
