/**
 * Orquestación del cobro con QR de Mercado Pago de una venta del POS, sin React: reserva el número, crea (o retoma) el cobro,
 * espera su resolución en el modal y devuelve si la venta puede cerrarse. La venta se encola recién cuando esto devuelve
 * `aprobado`, con el MISMO número reservado (`numeroReservado`), así que se imprime una sola vez y por el camino de siempre.
 */
import { clienteDeCobrosMercadoPago, mensajeDeErrorDeCobroMercadoPago, type CobroMercadoPago } from '../api/cobrosMercadoPago'
import { formatearImporte } from '../formato/importes'
import { construirNumeroVisible } from './outboxOffline'
import {
  indicacionDeCobroAprobado,
  MENSAJE_SIN_CONEXION_PARA_COBRAR,
  mensajeDeImporteDistinto,
  TEXTO_POR_CIERRE_SIN_APROBAR,
  vistaDelCobro,
} from './cobroMercadoPago'
import type { ResultadoDelCobro } from './ModalDeCobroMercadoPago'
import type { ResultadoDeSincronizacionOffline } from './useSincronizacionOffline'
import type { ReservaLeida } from './reservaDeCobro'

export type ReservaDeCobroEnPos = Pick<
  ResultadoDeSincronizacionOffline,
  'reservarNumeroParaCobro' | 'anotarCobroEnCurso' | 'soltarReservaDeCobro' | 'consumirReservaDeCobro' | 'leerReservaDeCobro'
>

export type DependenciasDeCobroMercadoPago = {
  enLinea: boolean
  idPuntoVenta: number
  reserva: ReservaDeCobroEnPos
  clienteDeCobros?: Pick<typeof clienteDeCobrosMercadoPago, 'crear' | 'consultar' | 'cancelar'>
  /** Abre el modal con el cobro y resuelve cuando termina. */
  esperarCobro: (cobro: CobroMercadoPago, numeroVisible: string) => Promise<ResultadoDelCobro>
  /** Pregunta al cajero si usa un cobro ya aprobado (de una recarga o de un intento anterior) para esta venta. Nunca se reutiliza en silencio. */
  confirmarReuso: (cobro: CobroMercadoPago, numeroVisible: string) => Promise<boolean>
}

/**
 * `aprobado`: la venta puede cerrarse con ese cobro y ese número; `importe` es lo que Mercado Pago cobró. `detenido`: no hay venta;
 * `mensaje` dice por qué.
 */
export type ResultadoDeCobroEnPos =
  | { tipo: 'aprobado'; numero: number; idCobro: number; importe: number }
  | { tipo: 'detenido'; mensaje: string }

const formatear = (valor: number) => formatearImporte(valor, { simbolo: true })

const centavos = (valor: number) => Math.round(valor * 100)

const MENSAJE_POR_MOTIVO_DE_RESERVA = {
  sin_numeracion: 'No quedan números reservados para vender en este dispositivo: recuperá la señal para reponer el bloque.',
  error_al_guardar: 'No se pudo reservar el número de la venta en este dispositivo. Probá de nuevo.',
  cobro_en_curso: 'Hay un cobro de Mercado Pago sin cerrar en este dispositivo. Resolvelo antes de iniciar otro.',
} as const

const detenido = (mensaje: string): ResultadoDeCobroEnPos => ({ tipo: 'detenido', mensaje })

const importeCobrado = (cobro: CobroMercadoPago) => cobro.importePagado ?? cobro.importe

/**
 * Cierra el modal con un resultado: un cobro aprobado cierra la venta; uno cancelado o vencido libera la reserva (el número no se
 * consumió, así que no queda ningún hueco); un importe distinto deja el cobro vivo y la venta sin cerrar.
 */
export async function resolverResultadoDelCobro(
  resultado: ResultadoDelCobro,
  numero: number,
  reserva: Pick<ReservaDeCobroEnPos, 'soltarReservaDeCobro'>,
): Promise<ResultadoDeCobroEnPos> {
  switch (resultado.tipo) {
    case 'aprobado':
      return { tipo: 'aprobado', numero, idCobro: resultado.cobro.idCobro, importe: importeCobrado(resultado.cobro) }
    case 'sin_aprobar':
      await reserva.soltarReservaDeCobro(numero)
      return detenido(TEXTO_POR_CIERRE_SIN_APROBAR[resultado.estado])
    case 'importe_distinto':
      return detenido(mensajeDeImporteDistinto(resultado.cobro.importe, resultado.cobro.importePagado ?? 0, formatear))
  }
}

/**
 * Qué hacer con el cobro vivo que dejó un intento anterior (recarga o error después de aprobarse). `null`: no hay nada que retomar
 * y se sigue con un cobro nuevo. Un cobro de otro punto de venta, o cuyo número ya no es el próximo del bloque, nunca se reutiliza:
 * pendiente se cancela; aprobado se conserva y se pide resolverlo.
 */
async function resolverCobroPrevio(
  deps: DependenciasDeCobroMercadoPago,
  clientes: Pick<typeof clienteDeCobrosMercadoPago, 'consultar' | 'cancelar'>,
  previa: ReservaLeida,
  pago: { idMedioPago: number; importe: number },
): Promise<ResultadoDeCobroEnPos | null> {
  const { reserva } = deps
  const idCobro = previa.idCobro as number
  let actual: CobroMercadoPago
  try {
    actual = await clientes.consultar(idCobro)
  } catch (e) {
    return detenido(mensajeDeErrorDeCobroMercadoPago(e))
  }
  if (actual.usadoEnVenta) {
    // La venta de ese cobro ya se registró: la reserva es un resto, y el bloque tiene que dejar el número atrás.
    await reserva.consumirReservaDeCobro(previa.numero)
    return null
  }
  const vista = vistaDelCobro(actual)
  if (vista.tipo === 'sin_aprobar') {
    await reserva.soltarReservaDeCobro(previa.numero)
    return null
  }
  const aprobado = vista.tipo === 'aprobado' || vista.tipo === 'importe_distinto'
  const numeroVisible = construirNumeroVisible(previa.idPuntoVenta, previa.numero)

  const otroPuntoDeVenta = previa.idPuntoVenta !== deps.idPuntoVenta
  if (otroPuntoDeVenta || previa.fueraDeBloque) {
    if (aprobado) {
      return detenido(
        otroPuntoDeVenta
          ? `Hay un cobro de Mercado Pago aprobado por ${formatear(importeCobrado(actual))} sin venta en otro punto de venta (venta ${numeroVisible}, cobro ${idCobro}). Resolvelo desde ese punto de venta antes de cobrar acá.`
          : `Cobro de Mercado Pago aprobado sin venta: el número ${numeroVisible} ya no está disponible en este dispositivo. Avisá a un supervisor con el cobro ${idCobro} por ${formatear(importeCobrado(actual))} para resolverlo.`,
      )
    }
    try {
      const cancelado = await clientes.cancelar(idCobro)
      const final = vistaDelCobro(cancelado)
      if (final.tipo === 'sin_aprobar') {
        await reserva.soltarReservaDeCobro(previa.numero)
        return null
      }
      if (final.tipo === 'aprobado' || final.tipo === 'importe_distinto') {
        return detenido(
          `El cobro ${idCobro} se aprobó por ${formatear(importeCobrado(cancelado))} mientras se cancelaba y su número ya no está disponible. Avisá a un supervisor para resolverlo.`,
        )
      }
    } catch (e) {
      return detenido(mensajeDeErrorDeCobroMercadoPago(e))
    }
    return detenido(`El cobro ${idCobro} de Mercado Pago sigue sin resolverse. Esperá unos segundos y probá de nuevo.`)
  }

  const cobrado = aprobado ? importeCobrado(actual) : previa.importe
  const coincide = previa.idMedioPago === pago.idMedioPago && cobrado !== undefined && centavos(cobrado) === centavos(pago.importe)
  if (!coincide) {
    return detenido(
      aprobado
        ? `Hay un cobro de Mercado Pago aprobado por ${formatear(cobrado ?? 0)} sin venta (venta ${numeroVisible}). ${indicacionDeCobroAprobado(cobrado ?? 0, formatear)}`
        : `Hay un cobro de Mercado Pago sin cerrar por ${formatear(previa.importe ?? 0)} (venta ${numeroVisible}). Resolvelo antes de cobrar otro importe o con otro medio.`,
    )
  }
  if (aprobado) {
    if (!(await deps.confirmarReuso(actual, numeroVisible))) {
      return detenido(
        `No se usó el cobro aprobado por ${formatear(cobrado ?? 0)} (venta ${numeroVisible}): sigue guardado y no se puede iniciar otro cobro hasta usarlo. Volvé a cobrar para usarlo.`,
      )
    }
    return { tipo: 'aprobado', numero: previa.numero, idCobro, importe: importeCobrado(actual) }
  }
  return resolverResultadoDelCobro(await deps.esperarCobro(actual, numeroVisible), previa.numero, reserva)
}

/**
 * Cobra `pago` con el QR de Mercado Pago. Sin conexión no aparta nada. Si el dispositivo ya tiene un cobro vivo (una recarga o un
 * error después de aprobarse), lo retoma en vez de crear otro: aprobado y con el mismo medio e importe se usa previa confirmación del
 * cajero; pendiente y con el mismo medio e importe se vuelve a mostrar; cerrado sin pagarse libera el número y sigue con uno nuevo;
 * si no coincide con lo que se quiere cobrar ahora, se detiene y pide resolverlo.
 */
export async function cobrarConMercadoPago(
  deps: DependenciasDeCobroMercadoPago,
  pago: { idMedioPago: number; importe: number },
): Promise<ResultadoDeCobroEnPos> {
  if (!deps.enLinea) return detenido(MENSAJE_SIN_CONEXION_PARA_COBRAR)
  const clientes = deps.clienteDeCobros ?? clienteDeCobrosMercadoPago
  const { reserva } = deps

  const previa = await reserva.leerReservaDeCobro()
  if (previa !== null && previa.idCobro !== undefined) {
    const resuelto = await resolverCobroPrevio(deps, clientes, previa, pago)
    if (resuelto !== null) return resuelto
  }

  const apartado = await reserva.reservarNumeroParaCobro(deps.idPuntoVenta)
  if (!apartado.ok) return detenido(MENSAJE_POR_MOTIVO_DE_RESERVA[apartado.motivo])

  let cobro: CobroMercadoPago
  try {
    cobro = await clientes.crear({ idMedioPago: pago.idMedioPago, numeroPreasignado: apartado.numero, importe: pago.importe })
  } catch (e) {
    await reserva.soltarReservaDeCobro(apartado.numero)
    return detenido(mensajeDeErrorDeCobroMercadoPago(e))
  }

  let anotado = false
  try {
    anotado = await reserva.anotarCobroEnCurso({ numero: apartado.numero, idCobro: cobro.idCobro, idMedioPago: pago.idMedioPago, importe: pago.importe })
  } catch {
    anotado = false
  }
  if (!anotado) {
    // Sin el registro local un cobro vivo quedaría sin dueño: se cancela en el servidor y no se abre el modal.
    await reserva.soltarReservaDeCobro(apartado.numero).catch(() => false)
    try {
      const cancelado = await clientes.cancelar(cobro.idCobro)
      if (cancelado.estado === 'aprobado') {
        return detenido(
          `No se pudo guardar el cobro en este dispositivo y Mercado Pago ya lo aprobó (cobro ${cobro.idCobro} por ${formatear(importeCobrado(cancelado))}). Avisá a un supervisor para resolverlo.`,
        )
      }
    } catch {
      return detenido(
        `No se pudo guardar el cobro en este dispositivo y tampoco se pudo cancelar (cobro ${cobro.idCobro}). Avisá a un supervisor para que lo cancele.`,
      )
    }
    return detenido('No se pudo guardar el cobro en este dispositivo: se canceló el cobro. Probá de nuevo.')
  }

  const numeroVisible = construirNumeroVisible(deps.idPuntoVenta, apartado.numero)
  return resolverResultadoDelCobro(await deps.esperarCobro(cobro, numeroVisible), apartado.numero, reserva)
}
