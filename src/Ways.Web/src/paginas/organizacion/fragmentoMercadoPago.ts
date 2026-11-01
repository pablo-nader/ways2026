/** Quita el fragmento de la barra sin recargar ni sumar una entrada al historial: el code no debe quedar visible ni volver con "Atrás". */
export function limpiarFragmento() {
  window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search)
}

/**
 * Borra de la barra un fragmento de retorno de Mercado Pago que nadie va a canjear (usuario que no es Admin): el `code` y el
 * `state` no deben quedar en la URL aunque la sección no se monte. Un fragmento ajeno a Mercado Pago no se toca.
 */
export function descartarRetornoDeMercadoPago() {
  const hash = window.location.hash
  if (new URLSearchParams(hash.startsWith('#') ? hash.slice(1) : hash).has('mercadopago')) limpiarFragmento()
}
