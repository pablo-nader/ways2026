/**
 * Cliente de los tres endpoints de cobro con QR de Mercado Pago que usa el POS (`/api/pos/mercadopago/cobros`, sesión de
 * dispositivo). El punto de venta sale de la sesión, nunca del cuerpo. Las reglas puras de la pantalla viven en
 * `pos/cobroMercadoPago.ts`.
 */
import { api, ErrorApi, ErrorDeRed } from './cliente'

export type EstadoDeCobroMercadoPago = 'creando' | 'pendiente' | 'aprobado' | 'cancelado' | 'expirado' | 'devuelto'

/** Espejo de `CobroDeMercadoPago`. `qrData` solo viaja mientras el cobro está pendiente y hay un QR para mostrar. */
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
}

export type SolicitudDeCobroMercadoPago = { idMedioPago: number; numeroPreasignado: number; importe: number }

const RUTA = '/pos/mercadopago/cobros'

export const clienteDeCobrosMercadoPago = {
  crear: (solicitud: SolicitudDeCobroMercadoPago) => api.post<CobroMercadoPago>(`${RUTA}/`, solicitud),
  consultar: (idCobro: number) => api.get<CobroMercadoPago>(`${RUTA}/${idCobro}`),
  cancelar: (idCobro: number) => api.post<CobroMercadoPago>(`${RUTA}/${idCobro}/cancelar`),
}

const MENSAJE_POR_CODIGO: Record<string, string> = {
  mercadopago_caja_no_registrada: 'Este punto de venta no está registrado como caja de Mercado Pago. Pedile a un administrador que lo registre.',
  mercadopago_no_conectada: 'La empresa no está conectada a Mercado Pago. Pedile a un administrador que la conecte.',
  mercadopago_requiere_reconexion: 'La conexión con Mercado Pago venció. Pedile a un administrador que la vuelva a conectar.',
  cobro_mercadopago_en_curso: 'Ya hay un cobro de Mercado Pago en curso para esta venta. Esperá a que se resuelva o cobrá con otro medio.',
  cobro_mercadopago_ya_aprobado: 'Mercado Pago ya aprobó un cobro para esta venta.',
  cobro_mercadopago_pagado_sin_datos:
    'Mercado Pago informa el pago, falta confirmarlo. No se puede cancelar: esperá la confirmación.',
  cobro_mercadopago_medio_invalido: 'Este medio de pago no está habilitado para cobrar con QR de Mercado Pago.',
  cobro_mercadopago_importe_invalido: 'El importe no es válido para cobrar con QR de Mercado Pago.',
  cobro_mercadopago_numero_invalido: 'El número de la venta no es válido para el cobro con Mercado Pago.',
  mercadopago_no_disponible: 'Mercado Pago no responde en este momento. Probá de nuevo en unos segundos.',
}

/** El texto para el cajero de un fallo de los endpoints de cobro. Sin conexión no es un rechazo: el cobro puede existir. */
export function mensajeDeErrorDeCobroMercadoPago(error: unknown): string {
  if (error instanceof ErrorDeRed) return 'Sin conexión con el servidor. Mercado Pago necesita conexión.'
  if (error instanceof ErrorApi) return MENSAJE_POR_CODIGO[error.codigo] ?? error.message
  return 'No se pudo completar la operación con Mercado Pago.'
}
