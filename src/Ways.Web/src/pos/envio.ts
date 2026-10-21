import type { ClienteListado } from '../api/tipos'
import { redondearAPeso } from '../formato/importes'

export const MENSAJE_DE_ENVIO_SIN_CLIENTE = 'Elegí un cliente con domicilio para el envío.'
export const MENSAJE_DE_ENVIO_SIN_DOMICILIO = 'El cliente no tiene domicilio cargado: cargalo o elegí otro cliente para el envío.'
export const MENSAJE_DE_ENVIO_SIN_CONEXION =
  'La venta con envío necesita conexión: destildá "Con envío" para vender sin conexión.'
export const MENSAJE_DE_ENVIO_SIN_IMPORTE = 'Calculando el importe del envío…'
export const MENSAJE_DE_ENVIO_IMPORTE_NO_DISPONIBLE =
  'No se pudo leer el importe del envío. Destildá y volvé a tildar "Con envío" para reintentar.'

type ClienteParaEnvio = Pick<ClienteListado, 'esConsumidorFinal' | 'domicilio'>

/** Motivo por el que una venta con envío no puede cerrarse con este cliente (misma regla que el servidor: cliente
 * identificado y con domicilio), o `null` si puede. */
export function motivoDeEnvioBloqueado(cliente: ClienteParaEnvio | null): string | null {
  if (cliente === null || cliente.esConsumidorFinal) return MENSAJE_DE_ENVIO_SIN_CLIENTE
  if (!cliente.domicilio?.trim()) return MENSAJE_DE_ENVIO_SIN_DOMICILIO
  return null
}

/** Importe de la línea Envío que muestra el carrito: solo con envío tildado, importe conocido y mayor a 0 y al menos
 * una línea de artículo. El servidor es quien lo calcula al cobrar; esto es la previa del mismo importe, redondeado al
 * peso como toda línea. */
export function importeDeEnvioAplicado(params: { conEnvio: boolean; importe: number | null; hayLineas: boolean }): number {
  if (!params.conEnvio || !params.hayLineas || params.importe === null || !(params.importe > 0)) return 0
  return redondearAPeso(params.importe)
}

const MENSAJES_DE_RECHAZO_DE_ENVIO: Record<string, string> = {
  envio_requiere_cliente: MENSAJE_DE_ENVIO_SIN_CLIENTE,
  envio_requiere_domicilio: MENSAJE_DE_ENVIO_SIN_DOMICILIO,
  envio_no_admitido: 'El servidor no admite un envío en esta venta. Destildá "Con envío".',
}

/** Mensaje para el cajero cuando el servidor rechaza al cobrar una venta con envío; `null` si el código no es de este
 * tema (el llamador sigue con su mensaje habitual). */
export function mensajeDeRechazoDeEnvio(codigo: string): string | null {
  return MENSAJES_DE_RECHAZO_DE_ENVIO[codigo] ?? null
}

/** Teléfono que se imprime para quien entrega: el fijo y, si no hay, el celular. */
export function telefonoDeContacto(cliente: Pick<ClienteListado, 'telefono' | 'celular'>): string | null {
  return cliente.telefono?.trim() || cliente.celular?.trim() || null
}
