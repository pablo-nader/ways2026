/**
 * Comprobante sintético de una venta offline (stage-pos-venta-offline-web, Parte B/C) — arma el
 * mismo shape `ComprobanteEmitido` que devuelve `POST /api/ventas`, para que el modal "Venta
 * finalizada" y el ticket ESC/POS (`ticketDeVenta`) sigan funcionando SIN CAMBIOS sobre una venta
 * que en verdad todavía no tocó el servidor — nunca se sabe si el número, el cliente o el subtotal
 * cambiarían al sincronizar (no deberían: el servidor solo vuelve a resolver como auditoría,
 * `AccionAuditada.VentaDiscrepanciaDePrecio`, nunca pisa lo cobrado), así que mostrarle al cajero
 * y al ticket EXACTAMENTE lo que se decidió offline es lo honesto.
 *
 * Espeja pixel a pixel la fórmula de `Ways.Domain.Ventas.CalculadorDeTotales.Calcular`: bruto de
 * línea = cantidad × precio ORIGINAL (lista, antes de descuento), descuento = descuentoUnitario ×
 * cantidad, total de línea = bruto − descuento (neto). `subtotal`/`descuentoTotal`/`total` del
 * comprobante son la suma de esas mismas columnas — nunca un cálculo alternativo.
 *
 * El descuento por unidad y la oferta salen de `preciosVigentesOffline` (no de los campos planos
 * del artículo): este módulo lee la instantánea DIRECTO, no el `ResultadoDeResolucion` que ya
 * resolvió la vista previa, así que sin ese lookup el ticket impreso quedaría con el descuento de
 * cantidad 1 mientras la pantalla y el payload encolado cobran el del tramo.
 *
 * `idArea`/`idListaPrecio` de cada item quedan en `0` — sentinels deliberados, nunca valores
 * inventados que alguien pudiera leer como reales: el servidor los resuelve del artículo FRESCO
 * recién al sincronizar (mismo motivo documentado en `ArticuloDeInstantanea.IdArea` del backend),
 * y NINGÚN consumidor de este comprobante sintético (`VentaFinalizada`, `ticketDeVenta`) lee esos
 * dos campos — verificado contra ambos archivos antes de fijar este contrato.
 */
import { preciosVigentesOffline } from './instantaneaOffline'
import type { LineaCarrito } from '../api/carrito'
import type { ComprobanteEmitido, InstantaneaDePos, ItemEmitido, PagoDeVenta } from '../api/tipos'

function redondear(valor: number): number {
  return Math.round((valor + Number.EPSILON) * 100) / 100
}

export type ParametrosDeComprobanteOfflineSintetico = {
  numero: number
  numeroVisible: string
  idPuntoVenta: number
  idCliente: number
  lineas: LineaCarrito[]
  instantanea: InstantaneaDePos
  pagos: PagoDeVenta[]
  ahora: Date
}

/**
 * `null` si alguna línea no tiene artículo en la instantánea — el llamador (`Pos.tsx`) ya validó
 * esto antes de encolar (`admisibilidadDeVentaOffline`), así que en la práctica nunca debería
 * pasar; devolver `null` en vez de tirar es la misma defensa en profundidad que el resto del
 * módulo offline (nunca confiar ciegamente en que un chequeo anterior corrió).
 */
export function construirComprobanteOfflineSintetico(params: ParametrosDeComprobanteOfflineSintetico): ComprobanteEmitido | null {
  const porId = new Map(params.instantanea.articulos.map((a) => [a.idArticulo, a]))

  let subtotal = 0
  let descuentoTotal = 0
  const items: ItemEmitido[] = []

  for (let indice = 0; indice < params.lineas.length; indice++) {
    const linea = params.lineas[indice]
    const articulo = porId.get(linea.idArticulo)
    if (!articulo) return null

    const precios = preciosVigentesOffline(articulo, linea.cantidad)
    const bruto = redondear(linea.cantidad * precios.precioOriginal)
    const descuento = redondear(precios.descuentoUnitario * linea.cantidad)
    subtotal += bruto
    descuentoTotal += descuento

    items.push({
      orden: indice + 1,
      idArticulo: linea.idArticulo,
      descripcion: linea.nombre,
      codigoBarra: linea.codigoBarra,
      idArea: 0,
      idListaPrecio: 0,
      idOferta: precios.aplicadas[0]?.idOferta ?? null,
      idAlicuotaIva: articulo.idAlicuotaIva,
      porcentajeIva: articulo.porcentajeIva,
      cantidad: linea.cantidad,
      precioUnitario: precios.precioOriginal,
      descuento,
      total: redondear(bruto - descuento),
      idLote: null,
      codigoLote: null,
      loteVencido: false,
      precioDiscrepante: false,
    })
  }

  subtotal = redondear(subtotal)
  descuentoTotal = redondear(descuentoTotal)

  return {
    id: 0,
    numero: params.numero,
    numeroVisible: params.numeroVisible,
    estado: 'Emitido',
    fecha: params.ahora.toISOString(),
    idPuntoVenta: params.idPuntoVenta,
    idCliente: params.idCliente,
    idComprobanteAsociado: null,
    subtotal,
    descuentoTotal,
    total: redondear(subtotal - descuentoTotal),
    direccionEntrega: null,
    observaciones: null,
    items,
    pagos: params.pagos.map((p) => ({ idMedioPago: p.idMedioPago, importe: p.importe, referencia: p.referencia, vuelto: p.vuelto })),
    idPresupuestoOrigen: null,
  }
}
