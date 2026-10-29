import { ErrorApi } from '../../api/cliente'

export const AVISO_DE_EDICION_DE_COMPRA =
  'Los cambios ajustan el stock, la cuenta del proveedor y los costos por diferencia. Si el total baja de lo pagado, se liberan pagos aplicados (vuelven a quedar a cuenta). Si cambia el total, se revierte el redondeo.'

export const MENSAJE_DE_CAMBIO_CONCURRENTE = 'La compra cambió mientras la editabas; recargala.'

const MENSAJE_POR_DEFECTO = 'No se pudieron guardar los cambios de la compra.'

/**
 * Texto de un rechazo de `PUT /api/compras/{id}` sobre una compra confirmada. El servidor ya redacta el motivo
 * (`compra_edicion_stock_negativo` nombra el artículo y el lote; `compra_duplicada`, `lote_vencido_en_recepcion` y
 * `compra_no_editable` explican el suyo) y se muestra tal cual; solo el cambio concurrente tiene copia propia,
 * porque ahí lo útil es decir qué hacer.
 */
export function mensajeDeErrorDeEdicion(e: unknown): string {
  if (!(e instanceof ErrorApi)) return MENSAJE_POR_DEFECTO
  if (e.codigo === 'compra_cambio_concurrente') return MENSAJE_DE_CAMBIO_CONCURRENTE
  return e.message || MENSAJE_POR_DEFECTO
}
