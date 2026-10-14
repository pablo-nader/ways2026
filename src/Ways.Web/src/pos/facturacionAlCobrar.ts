import type { ComprobanteEmitido, MedioPagoListado } from '../api/tipos'

/** Tope del dueño para esperar la factura de una venta que seguro se factura. */
export const ESPERA_DE_LA_FACTURA_MS = 5_000

/** Una venta seguro se factura si el cajero lo forzó o si algún pago usa un medio que factura el 100 %. */
export function ventaSeguroSeFactura(params: {
  forzarFactura: boolean
  pagos: readonly { idMedioPago: number }[]
  medioPorId: Readonly<Record<number, Pick<MedioPagoListado, 'porcentajeFacturacion'>>>
}): boolean {
  if (params.forzarFactura) return true
  return params.pagos.some((pago) => (params.medioPorId[pago.idMedioPago]?.porcentajeFacturacion ?? 0) >= 100)
}

/** Deja el estado fiscal en el comprobante solo si la venta seguro se facturaba y la factura está aprobada con sus
 * datos: con porcentajes parciales la factura nunca reemplaza al ticket, aunque el servidor ya la haya emitido. */
export function conFiscalSoloSiSeguroSeFactura(comprobante: ComprobanteEmitido, seguroSeFactura: boolean): ComprobanteEmitido {
  const fiscal = comprobante.fiscal
  if (!fiscal) return comprobante
  if (seguroSeFactura && fiscal?.estado === 'aprobado' && fiscal.comprobante) return comprobante
  return { ...comprobante, fiscal: undefined }
}
