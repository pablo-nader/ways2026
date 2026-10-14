import { describe, expect, it } from 'vitest'
import type { ComprobanteEmitido, DatosDeFacturaFiscal, FiscalDeVenta } from '../api/tipos'
import { ESPERA_DE_LA_FACTURA_MS, conFiscalSoloSiSeguroSeFactura, ventaSeguroSeFactura } from './facturacionAlCobrar'

const medios = { 1: { porcentajeFacturacion: 0 }, 2: { porcentajeFacturacion: 100 }, 3: { porcentajeFacturacion: 40 }, 4: { porcentajeFacturacion: 150 } }

describe('ventaSeguroSeFactura', () => {
  it('con el forzado encendido siempre se factura, aunque ningún medio facture', () => {
    expect(ventaSeguroSeFactura({ forzarFactura: true, pagos: [{ idMedioPago: 1 }], medioPorId: medios })).toBe(true)
  })

  it('un medio al 100 % factura sin forzado, y el borde exacto cuenta', () => {
    expect(ventaSeguroSeFactura({ forzarFactura: false, pagos: [{ idMedioPago: 2 }], medioPorId: medios })).toBe(true)
  })

  it('un medio por encima del 100 % también factura', () => {
    expect(ventaSeguroSeFactura({ forzarFactura: false, pagos: [{ idMedioPago: 4 }], medioPorId: medios })).toBe(true)
  })

  it('basta una fila de pago con medio al 100 % entre varias', () => {
    expect(ventaSeguroSeFactura({ forzarFactura: false, pagos: [{ idMedioPago: 1 }, { idMedioPago: 2 }], medioPorId: medios })).toBe(true)
  })

  it('un porcentaje parcial no es seguro', () => {
    expect(ventaSeguroSeFactura({ forzarFactura: false, pagos: [{ idMedioPago: 3 }], medioPorId: medios })).toBe(false)
  })

  it('sin pagos ni forzado no es seguro', () => {
    expect(ventaSeguroSeFactura({ forzarFactura: false, pagos: [], medioPorId: medios })).toBe(false)
  })

  it('un medio que no está en el catálogo no factura', () => {
    expect(ventaSeguroSeFactura({ forzarFactura: false, pagos: [{ idMedioPago: 99 }], medioPorId: medios })).toBe(false)
  })
})

const factura: DatosDeFacturaFiscal = {
  idComprobante: 5,
  codigoTipoComprobante: 'FA',
  letra: 'B',
  puntoDeVentaFiscal: 3,
  numero: 12,
  fecha: '2026-10-14',
  cae: '70123456789012',
  caeVencimiento: '2026-10-24',
  payloadQr: null,
  observaciones: [],
}

function comprobante(fiscal: FiscalDeVenta | null | undefined): ComprobanteEmitido {
  return { id: 1, numeroVisible: '0001-00000001', total: 100, fiscal } as ComprobanteEmitido
}

const aprobado: FiscalDeVenta = { estado: 'aprobado', motivo: 'forzada', idFacturacion: 9, error: null, comprobante: factura }

describe('conFiscalSoloSiSeguroSeFactura', () => {
  it('conserva el estado fiscal aprobado cuando la venta seguro se facturaba', () => {
    expect(conFiscalSoloSiSeguroSeFactura(comprobante(aprobado), true).fiscal).toBe(aprobado)
  })

  it('quita la factura aprobada de una venta que no era segura: nunca se imprime sola', () => {
    expect(conFiscalSoloSiSeguroSeFactura(comprobante(aprobado), false).fiscal).toBeUndefined()
  })

  it('quita cualquier estado que no sea aprobado, aunque la venta fuera segura', () => {
    const pendiente: FiscalDeVenta = { estado: 'pendiente', motivo: null, idFacturacion: 9, error: null, comprobante: factura }
    expect(conFiscalSoloSiSeguroSeFactura(comprobante(pendiente), true).fiscal).toBeUndefined()
  })

  it('un aprobado sin datos de la factura no se imprime como factura', () => {
    expect(conFiscalSoloSiSeguroSeFactura(comprobante({ ...aprobado, comprobante: null }), true).fiscal).toBeUndefined()
  })

  it('un comprobante sin fiscal queda igual, sin copiarlo', () => {
    const sinFiscal = comprobante(undefined)
    expect(conFiscalSoloSiSeguroSeFactura(sinFiscal, true)).toBe(sinFiscal)
    expect(conFiscalSoloSiSeguroSeFactura(sinFiscal, false)).toBe(sinFiscal)
  })
})

describe('ESPERA_DE_LA_FACTURA_MS', () => {
  it('es el tope de 5 s del dueño', () => {
    expect(ESPERA_DE_LA_FACTURA_MS).toBe(5_000)
  })
})
