/**
 * Cliente de los endpoints de cobro con QR o con la terminal Point de Mercado Pago que usa el POS (`/api/pos/mercadopago/cobros`, sesión de
 * dispositivo). El punto de venta sale de la sesión, nunca del cuerpo. Las reglas puras de la pantalla viven en
 * `pos/cobroMercadoPago.ts`.
 */
import { api, ErrorApi, ErrorDeRed } from './cliente'

export type EstadoDeCobroMercadoPago = 'creando' | 'pendiente' | 'aprobado' | 'cancelado' | 'expirado' | 'devuelto' | 'sin_venta' | 'fallido'

/** Espejo de `CobroDeMercadoPago`. `qrData` solo viaja en un cobro QR mientras está pendiente y hay un QR para mostrar. */
export type CobroMercadoPago = {
  idCobro: number
  estado: EstadoDeCobroMercadoPago
  qrData: string | null
  expiraEn: string
  importe: number
  importePagado: number | null
  importeDistinto: boolean
  verificandoPago: boolean
  pagoInformadoSinDatos: boolean
  /** Una venta ya quedó ligada al cobro: la reserva local de su número ya no tiene nada que proteger. */
  usadoEnVenta: boolean
  /** `qr` (el cliente escanea) o `point` (el cliente paga en la terminal; nunca hay `qrData`). */
  tipo: 'qr' | 'point'
  /** Solo Point: la terminal a la que se pidió la orden. */
  idTerminal: string | null
  /** Solo Point pendiente: la orden ya llegó a la terminal (ya no se cancela desde el POS). */
  enTerminal: boolean
  /** Solo Point pendiente: Mercado Pago pide mirar el resultado en la terminal. */
  revisarTerminal: boolean
}

export type SolicitudDeCobroMercadoPago = { idMedioPago: number; numeroPreasignado: number; importe: number }

const RUTA = '/pos/mercadopago/cobros'

export const clienteDeCobrosMercadoPago = {
  crear: (solicitud: SolicitudDeCobroMercadoPago) => api.post<CobroMercadoPago>(`${RUTA}/`, solicitud),
  consultar: (idCobro: number) => api.get<CobroMercadoPago>(`${RUTA}/${idCobro}`),
  cancelar: (idCobro: number) => api.post<CobroMercadoPago>(`${RUTA}/${idCobro}/cancelar`),
  /** Aparta un cobro aprobado sin venta: el servidor lo pasa a `sin_venta`, libera el número y lo deja para la conciliación. Idempotente. */
  apartar: (idCobro: number) => api.post<CobroMercadoPago>(`${RUTA}/${idCobro}/apartar`),
}

const MENSAJE_POR_CODIGO: Record<string, string> = {
  mercadopago_caja_no_registrada: 'Este punto de venta no está registrado como caja de Mercado Pago. Pedile a un administrador que lo registre.',
  mercadopago_no_conectada: 'La empresa no está conectada a Mercado Pago. Pedile a un administrador que la conecte.',
  mercadopago_requiere_reconexion: 'La conexión con Mercado Pago venció. Pedile a un administrador que la vuelva a conectar.',
  cobro_mercadopago_en_curso: 'Ya hay un cobro de Mercado Pago en curso para esta venta. Esperá a que se resuelva o cobrá con otro medio.',
  cobro_mercadopago_ya_aprobado: 'Mercado Pago ya aprobó un cobro para esta venta.',
  cobro_mercadopago_ya_usado: 'Una venta ya usó este cobro de Mercado Pago.',
  cobro_mercadopago_pagado_sin_datos:
    'Mercado Pago informa el pago, falta confirmarlo. No se puede cancelar: esperá la confirmación.',
  mercadopago_caja_sin_terminal: 'La caja de este punto de venta no tiene una terminal Point asignada. Pedile a un administrador que la asigne.',
  mercadopago_terminal_ocupada: 'La terminal tiene otra orden esperando. Terminá o cancelá ese cobro en la terminal y probá de nuevo.',
  mercadopago_cancelacion_no_aplicada: 'Mercado Pago no pudo cancelar el cobro y sigue pendiente. Consultá su estado y probá de nuevo.',
  mercadopago_cancelar_en_la_terminal: 'El cobro ya está en la terminal: cancelalo en la terminal y esperá a que aparezca acá.',
  cobro_mercadopago_medio_invalido: 'Este medio de pago no está habilitado para cobrar con Mercado Pago.',
  cobro_mercadopago_importe_invalido: 'El importe no es válido para cobrar con Mercado Pago.',
  cobro_mercadopago_numero_invalido: 'El número de la venta no es válido para el cobro con Mercado Pago.',
  mercadopago_no_disponible: 'Mercado Pago no responde en este momento. Probá de nuevo en unos segundos.',
}

/** El texto para el cajero de un fallo de los endpoints de cobro. Sin conexión no es un rechazo: el cobro puede existir. */
export function mensajeDeErrorDeCobroMercadoPago(error: unknown): string {
  if (error instanceof ErrorDeRed) return 'Sin conexión con el servidor. Mercado Pago necesita conexión.'
  if (error instanceof ErrorApi) return MENSAJE_POR_CODIGO[error.codigo] ?? error.message
  return 'No se pudo completar la operación con Mercado Pago.'
}
