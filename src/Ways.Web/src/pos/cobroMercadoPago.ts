/**
 * Reglas puras del cobro con QR o con la terminal Point de Mercado Pago en el POS: qué muestra el modal según el cobro, cada cuánto se consulta, la
 * cuenta regresiva y qué filas de pago disparan el cobro. Sin I/O: el cliente HTTP está en `api/cobrosMercadoPago.ts` y el
 * número de venta se reserva en `reservaDeCobro.ts`.
 */
import type { CobroMercadoPago, EstadoDeCobroMercadoPago } from '../api/cobrosMercadoPago'
import type { MedioPagoListado } from '../api/tipos'

export const PROVEEDOR_MERCADOPAGO_QR = 'mercadopago_qr'

export const PROVEEDOR_MERCADOPAGO_POINT = 'mercadopago_point'

/** Cada cuánto se vuelve a consultar el cobro mientras el modal está abierto. */
export const INTERVALO_DE_CONSULTA_MS = 2_000

/** Tras un fallo de la consulta (sin conexión, Mercado Pago no disponible) se espera más para no martillar. */
export const INTERVALO_TRAS_FALLO_MS = 5_000

export const MENSAJE_SIN_CONEXION_PARA_COBRAR = 'Mercado Pago necesita conexión: sin conexión no se puede cobrar con QR ni con la terminal Point. Cobrá con otro medio.'

export const MENSAJE_DE_VARIOS_COBROS =
  'Una venta admite un solo cobro con Mercado Pago. Dejá una sola fila con ese medio o cobrá el resto con otro.'

export function medioConCobroIntegrado(medio: Pick<MedioPagoListado, 'proveedor'> | undefined): boolean {
  return medio?.proveedor === PROVEEDOR_MERCADOPAGO_QR || medio?.proveedor === PROVEEDOR_MERCADOPAGO_POINT
}

export function esperaHastaLaProximaConsulta(fallosSeguidos: number): number {
  return fallosSeguidos > 0 ? INTERVALO_TRAS_FALLO_MS : INTERVALO_DE_CONSULTA_MS
}

export type PlanDeCobroMercadoPago<T> = { tipo: 'ninguno' } | { tipo: 'uno'; pago: T } | { tipo: 'varios' }

/** Qué pagos de la venta se cobran con Mercado Pago (QR o Point): ninguno (venta de siempre), uno, o más de uno (se rechaza: una venta admite un solo cobro). */
export function planDeCobroMercadoPago<T extends { idMedioPago: number }>(
  pagos: readonly T[],
  medioPorId: Record<number, MedioPagoListado>,
): PlanDeCobroMercadoPago<T> {
  const conQr = pagos.filter((p) => medioConCobroIntegrado(medioPorId[p.idMedioPago]))
  if (conQr.length === 0) return { tipo: 'ninguno' }
  if (conQr.length > 1) return { tipo: 'varios' }
  return { tipo: 'uno', pago: conQr[0] }
}

/** Los estados en que el cobro terminó sin aprobarse: la venta no se cierra y el cajero vuelve al paso de pago. */
export type EstadoSinAprobar = Extract<EstadoDeCobroMercadoPago, 'cancelado' | 'expirado' | 'devuelto' | 'sin_venta' | 'fallido'>

export type VistaDelCobro =
  | { tipo: 'qr'; qrData: string }
  | { tipo: 'verificando' }
  | { tipo: 'informado_sin_datos' }
  | { tipo: 'aprobado' }
  | { tipo: 'importe_distinto'; importe: number; importePagado: number }
  | { tipo: 'sin_aprobar'; estado: EstadoSinAprobar }
  | { tipo: 'cobrando_en_terminal'; idTerminal: string | null; enTerminal: boolean }
  | { tipo: 'revisar_terminal' }
  | { tipo: 'esperando' }

/**
 * Lo que el modal muestra. Un cobro aprobado con un importe pagado distinto del cobrado nunca cierra solo: el cajero lo revisa.
 * Un QR solo se muestra mientras el cobro está pendiente, hay QR y el servidor no avisa que está verificando o que el pago ya se informó.
 */
export function vistaDelCobro(cobro: CobroMercadoPago): VistaDelCobro {
  switch (cobro.estado) {
    case 'aprobado':
      if (cobro.importeDistinto || (cobro.importePagado !== null && cobro.importePagado !== cobro.importe)) {
        return { tipo: 'importe_distinto', importe: cobro.importe, importePagado: cobro.importePagado ?? 0 }
      }
      return { tipo: 'aprobado' }
    case 'cancelado':
    case 'expirado':
    case 'devuelto':
    case 'sin_venta':
    case 'fallido':
      return { tipo: 'sin_aprobar', estado: cobro.estado }
    case 'pendiente':
      if (cobro.pagoInformadoSinDatos) return { tipo: 'informado_sin_datos' }
      if (cobro.tipo === 'point') {
        // Un cobro Point nunca trae QR: la espera es la terminal, no un QR que falta.
        if (cobro.revisarTerminal) return { tipo: 'revisar_terminal' }
        if (cobro.verificandoPago) return { tipo: 'verificando' }
        return { tipo: 'cobrando_en_terminal', idTerminal: cobro.idTerminal, enTerminal: cobro.enTerminal }
      }
      if (cobro.verificandoPago || cobro.qrData === null || cobro.qrData === '') return { tipo: 'verificando' }
      return { tipo: 'qr', qrData: cobro.qrData }
    default:
      return { tipo: 'esperando' }
  }
}

/** Segundos que faltan para `expiraEn` (nunca negativo); `null` si la fecha no se puede leer. */
export function segundosParaExpirar(expiraEn: string, ahora: number): number | null {
  const fin = Date.parse(expiraEn)
  if (Number.isNaN(fin)) return null
  return Math.max(0, Math.ceil((fin - ahora) / 1000))
}

export function formatearCuentaRegresiva(segundos: number): string {
  const minutos = Math.floor(segundos / 60)
  const resto = segundos % 60
  return `${String(minutos).padStart(2, '0')}:${String(resto).padStart(2, '0')}`
}

export const TEXTO_POR_CIERRE_SIN_APROBAR: Record<EstadoSinAprobar, string> = {
  cancelado: 'El cobro con Mercado Pago se canceló. Podés intentarlo de nuevo o cobrar con otro medio.',
  expirado: 'El cobro con Mercado Pago venció sin pagarse. Podés intentarlo de nuevo o cobrar con otro medio.',
  devuelto: 'Mercado Pago devolvió el cobro. Podés intentarlo de nuevo o cobrar con otro medio.',
  fallido: 'El pago fue rechazado en la terminal: probá de nuevo u otro medio.',
  sin_venta: 'El cobro se apartó sin venta y un supervisor lo resuelve desde la conciliación. Podés intentarlo de nuevo o cobrar con otro medio.',
}

/** Qué hacer con un cobro aprobado por un importe distinto del pedido: nunca se cierra solo, pero tampoco queda sin salida. */
export function mensajeDeImporteDistinto(importe: number, importePagado: number, formatear: (valor: number) => string): string {
  return `Mercado Pago aprobó ${formatear(importePagado)} y la venta pide ${formatear(importe)}. La venta no se cerró. ${indicacionDeCobroAprobado(importePagado, formatear)}`
}

/** El cobro aprobado se conserva: la fila de Mercado Pago tiene que valer lo que se cobró y se vuelve a cobrar, que lo reutiliza. */
export function indicacionDeCobroAprobado(importePagado: number, formatear: (valor: number) => string): string {
  return `El cobro aprobado se conserva: ajustá la fila de Mercado Pago a ${formatear(importePagado)} (lo que se cobró) y volvé a cobrar; se usa ese cobro.`
}
