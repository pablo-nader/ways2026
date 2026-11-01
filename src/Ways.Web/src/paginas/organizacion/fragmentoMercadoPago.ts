import { leerRetornoDeMercadoPago, type RetornoDeMercadoPago } from '../../api/mercadoPago'

/** Quita el fragmento de la barra sin recargar ni sumar una entrada al historial: el code no debe quedar visible ni volver con "Atrás". */
export function limpiarFragmento() {
  window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search)
}

/**
 * Toma el retorno de Mercado Pago del fragmento y lo borra de la barra en el acto, sea válido o no: el `code` y el `state` no
 * deben quedar en la URL aunque nadie los vaya a canjear (retorno incompleto, usuario sin Admin, carga fallida). Devuelve el
 * retorno interpretado, o `null` si no lo hay o no es canjeable. Un fragmento ajeno a Mercado Pago no se toca.
 */
export function tomarRetornoDeMercadoPago(): RetornoDeMercadoPago | null {
  const hash = window.location.hash
  if (!new URLSearchParams(hash.startsWith('#') ? hash.slice(1) : hash).has('mercadopago')) return null

  const retorno = leerRetornoDeMercadoPago(hash)
  limpiarFragmento()

  return retorno
}
