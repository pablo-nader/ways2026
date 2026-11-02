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
 * línea = cantidad × precio ORIGINAL (lista, antes de descuento) al peso entero, neto = cantidad ×
 * (precio − descuentoUnitario) para las unidades fuera de un combo, redondeado al peso, más lo que
 * cobra el combo por las unidades que consumió (`unidades × precio − ahorro`, al peso), descuento =
 * bruto − neto, y el ajuste manual de la línea (porcentaje con signo) se aplica sobre ese neto: total
 * de línea = neto + ajuste (`calcularTotalesDeLinea`). Con una oferta "solo múltiplos" la parte fuera del combo es
 * `unidades × precio − descuentoDeLinea`, al peso. Todos los importes del comprobante son pesos enteros.
 * `subtotal`/`descuentoTotal` del comprobante son la suma de esas mismas columnas;
 * `descuentoManualTotal`/`recargoManualTotal` separan los ajustes por el signo del porcentaje y
 * `total = subtotal − descuentoTotal − descuentoManualTotal + recargoManualTotal` — nunca un
 * cálculo alternativo.
 *
 * El descuento por unidad, la oferta y el combo salen de `resolverDocumentoOffline` sobre el precio de la
 * lista de la venta (no del precio plano), con la cantidad de la línea y la de su familia en el
 * carrito: este módulo lee la instantánea DIRECTO, no el `ResultadoDeResolucion` que ya
 * resolvió la vista previa, así que sin ese lookup el ticket impreso quedaría con el descuento de
 * cantidad 1 mientras la pantalla y el payload encolado cobran el del tramo.
 *
 * Con envío, la línea "Envío" (sin artículo ni área, al 21 %, id de alícuota en `0` por el mismo motivo que abajo) cierra el
 * comprobante y `direccionEntrega` lleva el domicilio del cliente, como el servidor.
 *
 * `idArea`/`idListaPrecio` de cada item quedan en `0` — sentinels deliberados, nunca valores
 * inventados que alguien pudiera leer como reales: el servidor los resuelve del artículo FRESCO
 * recién al sincronizar (mismo motivo documentado en `ArticuloDeInstantanea.IdArea` del backend),
 * y NINGÚN consumidor de este comprobante sintético (`VentaFinalizada`, `ticketDeVenta`) lee esos
 * dos campos — verificado contra ambos archivos antes de fijar este contrato.
 */
import { resolverDocumentoOffline } from './instantaneaOffline'
import { totalesDeAjusteManual } from '../api/ajusteManual'
import { calcularTotalesDeLineaConCombo } from '../api/combos'
import type { LineaCarrito } from '../api/carrito'
import type { ComprobanteEmitido, InstantaneaDePos, ItemEmitido, PagoDeVenta } from '../api/tipos'
import { redondearAPeso } from '../formato/importes'
import { DESCRIPCION_DE_LA_LINEA_DE_ENVIO, PORCENTAJE_IVA_DEL_ENVIO } from './envio'

export type ParametrosDeComprobanteOfflineSintetico = {
  numero: number
  numeroVisible: string
  idPuntoVenta: number
  idCliente: number
  lineas: LineaCarrito[]
  instantanea: InstantaneaDePos
  /** Lista del cliente de la venta: la misma con la que se resolvió la vista previa y se encoló. */
  idListaPrecio: number
  pagos: PagoDeVenta[]
  ahora: Date
  /** Venta con envío: el importe que cobró la caja (sin la línea si no es mayor a 0) y el domicilio al que se entrega. */
  envio?: { importe: number; direccionEntrega: string | null }
}

/**
 * `null` si alguna línea no tiene precio en la lista de la venta dentro de la instantánea — el llamador (`Pos.tsx`) ya validó
 * esto antes de encolar (`admisibilidadDeVentaOffline`), así que en la práctica nunca debería
 * pasar; devolver `null` en vez de tirar es la misma defensa en profundidad que el resto del
 * módulo offline (nunca confiar ciegamente en que un chequeo anterior corrió).
 */
export function construirComprobanteOfflineSintetico(params: ParametrosDeComprobanteOfflineSintetico): ComprobanteEmitido | null {
  const porId = new Map(params.instantanea.articulos.map((a) => [a.idArticulo, a]))

  const vigentes = resolverDocumentoOffline(params.lineas, params.instantanea, params.idListaPrecio).lineas

  let subtotal = 0
  let descuentoTotal = 0
  const items: ItemEmitido[] = []
  const ajustes: { porcentaje: number | null | undefined; ajuste: number }[] = []

  for (let indice = 0; indice < params.lineas.length; indice++) {
    const linea = params.lineas[indice]
    const articulo = porId.get(linea.idArticulo)
    const precios = vigentes[indice]
    if (!articulo || !precios) return null

    const { bruto, descuento, ajuste, total } = calcularTotalesDeLineaConCombo({
      cantidad: linea.cantidad,
      precioOriginal: precios.precioOriginal,
      descuentoUnitario: precios.descuentoUnitario,
      porcentaje: linea.ajusteManualPorcentaje,
      combo: precios.combo,
      descuentoDeLinea: precios.descuentoDeLinea,
    })
    subtotal += bruto
    descuentoTotal += descuento
    ajustes.push({ porcentaje: linea.ajusteManualPorcentaje, ajuste })

    items.push({
      orden: indice + 1,
      idArticulo: linea.idArticulo,
      descripcion: linea.nombre,
      codigoBarra: linea.codigoBarra,
      idArea: 0,
      idListaPrecio: 0,
      // Como el servidor: una línea que los combos consumieron entera no lleva oferta.
      idOferta: precios.combo?.unidadesConsumidas === linea.cantidad ? null : (precios.aplicadas[0]?.idOferta ?? null),
      idCombo: precios.combo?.idCombo ?? null,
      idAlicuotaIva: articulo.idAlicuotaIva,
      porcentajeIva: articulo.porcentajeIva,
      cantidad: linea.cantidad,
      precioUnitario: precios.precioOriginal,
      descuento,
      total,
      idLote: null,
      codigoLote: null,
      loteVencido: false,
      precioDiscrepante: false,
      ajusteManualPorcentaje: linea.ajusteManualPorcentaje ?? null,
      ajusteManual: ajuste,
      porImporte: linea.importe !== undefined,
    })
  }

  // Como el servidor: la línea Envío va al final, sin artículo, área, oferta ni ajuste, al 21 %, y suma al subtotal.
  if (params.envio && params.envio.importe > 0) {
    const totalDelEnvio = redondearAPeso(params.envio.importe)
    subtotal += totalDelEnvio
    items.push({
      orden: items.length + 1,
      idArticulo: null,
      descripcion: DESCRIPCION_DE_LA_LINEA_DE_ENVIO,
      codigoBarra: null,
      idArea: null,
      idListaPrecio: 0,
      idOferta: null,
      idCombo: null,
      idAlicuotaIva: 0,
      porcentajeIva: PORCENTAJE_IVA_DEL_ENVIO,
      cantidad: 1,
      precioUnitario: params.envio.importe,
      descuento: 0,
      total: totalDelEnvio,
      idLote: null,
      codigoLote: null,
      loteVencido: false,
      precioDiscrepante: false,
      ajusteManualPorcentaje: null,
      ajusteManual: 0,
      porImporte: false,
    })
  }

  subtotal = redondearAPeso(subtotal)
  descuentoTotal = redondearAPeso(descuentoTotal)
  const { descuentoManualTotal, recargoManualTotal } = totalesDeAjusteManual(ajustes)

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
    descuentoManualTotal,
    recargoManualTotal,
    total: redondearAPeso(subtotal - descuentoTotal - descuentoManualTotal + recargoManualTotal),
    direccionEntrega: params.envio?.direccionEntrega?.trim() || null,
    observaciones: null,
    items,
    pagos: params.pagos.map((p) => ({
      idMedioPago: p.idMedioPago,
      importe: p.importe,
      referencia: p.referencia,
      vuelto: p.vuelto,
      // Como el servidor: el pago con cobro queda `mercadopago` y la aprobación manual `sin_verificar`.
      ...(p.aprobacionManualSinVerificar ? { verificacionMercadoPago: 'sin_verificar' as const } : {}),
      ...(p.idCobroMercadoPago !== undefined ? { verificacionMercadoPago: 'mercadopago' as const } : {}),
    })),
    idPresupuestoOrigen: null,
  }
}
