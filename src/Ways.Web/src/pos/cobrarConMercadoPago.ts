/**
 * Orquestación del cobro con QR de Mercado Pago de una venta del POS, sin React: reserva el número, crea (o retoma) el cobro,
 * espera su resolución en el modal y devuelve si la venta puede cerrarse. La venta se encola recién cuando esto devuelve
 * `aprobado`, con el MISMO número reservado (`numeroReservado`), así que se imprime una sola vez y por el camino de siempre.
 */
import { clienteDeCobrosMercadoPago, mensajeDeErrorDeCobroMercadoPago, type CobroMercadoPago } from '../api/cobrosMercadoPago'
import { formatearImporte } from '../formato/importes'
import { construirNumeroVisible } from './outboxOffline'
import {
  MENSAJE_SIN_CONEXION_PARA_COBRAR,
  mensajeDeImporteDistinto,
  TEXTO_POR_CIERRE_SIN_APROBAR,
  vistaDelCobro,
} from './cobroMercadoPago'
import type { ResultadoDelCobro } from './ModalDeCobroMercadoPago'
import type { ResultadoDeSincronizacionOffline } from './useSincronizacionOffline'

export type ReservaDeCobroEnPos = Pick<
  ResultadoDeSincronizacionOffline,
  'reservarNumeroParaCobro' | 'anotarCobroEnCurso' | 'soltarReservaDeCobro' | 'leerReservaDeCobro'
>

export type DependenciasDeCobroMercadoPago = {
  enLinea: boolean
  idPuntoVenta: number
  reserva: ReservaDeCobroEnPos
  clienteDeCobros?: Pick<typeof clienteDeCobrosMercadoPago, 'crear' | 'consultar'>
  /** Abre el modal con el cobro y resuelve cuando termina. */
  esperarCobro: (cobro: CobroMercadoPago, numeroVisible: string) => Promise<ResultadoDelCobro>
}

/** `aprobado`: la venta puede cerrarse con ese cobro y ese número. `detenido`: no hay venta; `mensaje` dice por qué. */
export type ResultadoDeCobroEnPos = { tipo: 'aprobado'; numero: number; idCobro: number } | { tipo: 'detenido'; mensaje: string }

const formatear = (valor: number) => formatearImporte(valor, { simbolo: true })

const centavos = (valor: number) => Math.round(valor * 100)

const MENSAJE_POR_MOTIVO_DE_RESERVA = {
  sin_numeracion: 'No quedan números reservados para vender en este dispositivo: recuperá la señal para reponer el bloque.',
  error_al_guardar: 'No se pudo reservar el número de la venta en este dispositivo. Probá de nuevo.',
  cobro_en_curso: 'Hay un cobro de Mercado Pago sin cerrar en este dispositivo. Resolvelo antes de iniciar otro.',
} as const

/**
 * Cierra el modal con un resultado: un cobro aprobado cierra la venta; uno cancelado o vencido deja el número libre para
 * el próximo intento (la reserva se conserva, nunca se pierde); un importe distinto deja el cobro vivo y la venta sin cerrar.
 */
export async function resolverResultadoDelCobro(
  resultado: ResultadoDelCobro,
  numero: number,
  reserva: Pick<ReservaDeCobroEnPos, 'soltarReservaDeCobro'>,
): Promise<ResultadoDeCobroEnPos> {
  switch (resultado.tipo) {
    case 'aprobado':
      return { tipo: 'aprobado', numero, idCobro: resultado.cobro.idCobro }
    case 'sin_aprobar':
      await reserva.soltarReservaDeCobro(numero)
      return { tipo: 'detenido', mensaje: TEXTO_POR_CIERRE_SIN_APROBAR[resultado.estado] }
    case 'importe_distinto':
      return {
        tipo: 'detenido',
        mensaje: mensajeDeImporteDistinto(resultado.cobro.importe, resultado.cobro.importePagado ?? 0, formatear),
      }
  }
}

/**
 * Cobra `pago` con el QR de Mercado Pago. Sin conexión no reserva nada. Si el dispositivo ya tiene un cobro vivo (una recarga o un
 * error después de aprobarse), lo retoma en vez de crear otro: aprobado y con el mismo medio e importe se usa tal cual; pendiente
 * y con el mismo medio e importe se vuelve a mostrar; cerrado sin pagarse libera el número y sigue con uno nuevo; si no coincide
 * con lo que se quiere cobrar ahora, se detiene y pide resolverlo.
 */
export async function cobrarConMercadoPago(
  deps: DependenciasDeCobroMercadoPago,
  pago: { idMedioPago: number; importe: number },
): Promise<ResultadoDeCobroEnPos> {
  if (!deps.enLinea) return { tipo: 'detenido', mensaje: MENSAJE_SIN_CONEXION_PARA_COBRAR }
  const clientes = deps.clienteDeCobros ?? clienteDeCobrosMercadoPago
  const { reserva } = deps

  const previa = await reserva.leerReservaDeCobro()
  if (previa !== null && previa.estado === 'en_curso' && previa.idCobro !== undefined) {
    let actual: CobroMercadoPago
    try {
      actual = await clientes.consultar(previa.idCobro)
    } catch (e) {
      return { tipo: 'detenido', mensaje: mensajeDeErrorDeCobroMercadoPago(e) }
    }
    const vista = vistaDelCobro(actual)
    const coincide = previa.idMedioPago === pago.idMedioPago && previa.importe !== undefined && centavos(previa.importe) === centavos(pago.importe)
    if (vista.tipo === 'sin_aprobar') {
      await reserva.soltarReservaDeCobro(previa.numero)
    } else if (!coincide) {
      return {
        tipo: 'detenido',
        mensaje: `Hay un cobro de Mercado Pago sin cerrar por ${formatear(previa.importe ?? 0)} (venta ${construirNumeroVisible(previa.idPuntoVenta, previa.numero)}). Resolvelo antes de cobrar otro importe o con otro medio.`,
      }
    } else {
      const numeroVisible = construirNumeroVisible(previa.idPuntoVenta, previa.numero)
      return resolverResultadoDelCobro(await deps.esperarCobro(actual, numeroVisible), previa.numero, reserva)
    }
  }

  const reservado = await reserva.reservarNumeroParaCobro(deps.idPuntoVenta)
  if (!reservado.ok) return { tipo: 'detenido', mensaje: MENSAJE_POR_MOTIVO_DE_RESERVA[reservado.motivo] }

  let cobro: CobroMercadoPago
  try {
    cobro = await clientes.crear({ idMedioPago: pago.idMedioPago, numeroPreasignado: reservado.numero, importe: pago.importe })
  } catch (e) {
    return { tipo: 'detenido', mensaje: mensajeDeErrorDeCobroMercadoPago(e) }
  }
  await reserva.anotarCobroEnCurso({ numero: reservado.numero, idCobro: cobro.idCobro, idMedioPago: pago.idMedioPago, importe: pago.importe })

  const numeroVisible = construirNumeroVisible(deps.idPuntoVenta, reservado.numero)
  return resolverResultadoDelCobro(await deps.esperarCobro(cobro, numeroVisible), reservado.numero, reserva)
}
