/**
 * Orquestación del cobro con QR de Mercado Pago de una venta del POS, sin React: reserva el número, crea (o retoma) el cobro,
 * espera su resolución en el modal y devuelve si la venta puede cerrarse. La venta se encola recién cuando esto devuelve
 * `aprobado`, con el MISMO número reservado (`numeroReservado`), así que se imprime una sola vez y por el camino de siempre.
 */
import { clienteDeCobrosMercadoPago, mensajeDeErrorDeCobroMercadoPago, type CobroMercadoPago } from '../api/cobrosMercadoPago'
import { formatearImporte } from '../formato/importes'
import { construirNumeroVisible } from './outboxOffline'
import {
  habilitaAprobacionManual,
  indicacionDeCobroAprobado,
  MENSAJE_SIN_CONEXION_PARA_COBRAR,
  MENSAJE_SIN_CONEXION_PARA_COBRAR_O_APROBAR_A_MANO,
  mensajeDeImporteDistinto,
  TEXTO_POR_CIERRE_SIN_APROBAR,
  vistaDelCobro,
} from './cobroMercadoPago'
import type { ResultadoDelCobro } from './ModalDeCobroMercadoPago'
import type { ResultadoDeSincronizacionOffline } from './useSincronizacionOffline'
import type { CobroSinVenta, ReservaDeCobro, ReservaLeida } from './reservaDeCobro'

export type ReservaDeCobroEnPos = Pick<
  ResultadoDeSincronizacionOffline,
  'reservarNumeroParaCobro' | 'anotarCobroEnCurso' | 'soltarReservaDeCobro' | 'consumirReservaDeCobro' | 'leerReservaDeCobro' | 'sincronizarApartados'
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
 * `mensaje` dice por qué. `aprobableAMano`: el cobro no se pudo crear (sin red, o Mercado Pago no responde) y el dispositivo no tiene
 * ningún cobro sin resolver, así que el cajero puede aprobar el pago a mano (queda sin verificar).
 */
export type ResultadoDeCobroEnPos =
  | { tipo: 'aprobado'; numero: number; idCobro: number; importe: number; dueno?: string }
  | { tipo: 'detenido'; mensaje: string; apartable?: CobroAAPartar; aprobableAMano?: true }

/** Un cobro aprobado sin venta que el cajero puede apartar (`apartarCobroSinVenta`) para liberar la caja. */
export type CobroAAPartar = { cobro: Omit<CobroSinVenta, 'apartadoEn'>; dueno?: string }

/** Contexto de la reserva con la que se resuelve un resultado: su dueño y el punto de venta, para poder apartar un cobro aprobado. */
export type ContextoDeResolucion = { idPuntoVenta: number; dueno?: string; idMedioPago?: number }

/** Arma lo que hace falta para apartar un cobro aprobado: el dueño de la reserva y los datos que quedan en la lista. */
export function cobroAAPartar(cobro: Pick<CobroMercadoPago, 'idCobro' | 'importe' | 'importePagado'>, numero: number, contexto: ContextoDeResolucion): CobroAAPartar {
  return {
    cobro: {
      idCobro: cobro.idCobro,
      numero,
      idPuntoVenta: contexto.idPuntoVenta,
      ...(contexto.idMedioPago !== undefined ? { idMedioPago: contexto.idMedioPago } : {}),
      importe: cobro.importe,
      importePagado: cobro.importePagado,
    },
    ...(contexto.dueno !== undefined ? { dueno: contexto.dueno } : {}),
  }
}

const conDueno = (dueno: string | undefined): [] | [string] => (dueno === undefined ? [] : [dueno])

/**
 * Si la venta de la reserva ya se registró (el servidor marca el cobro `usadoEnVenta`), la reserva es un resto: el bloque deja el
 * número atrás y se da de baja. Devuelve `'sanada'`, el cobro vivo (para seguir con él) o `null` si no se pudo consultar.
 */
export async function sanarReservaYaVendida(
  reserva: Pick<ReservaDeCobro, 'idCobro' | 'numero' | 'dueno'>,
  deps: {
    consultar: (idCobro: number) => Promise<CobroMercadoPago>
    consumirReservaDeCobro: (numero: number, ...dueno: [] | [string]) => Promise<boolean>
  },
): Promise<'sanada' | CobroMercadoPago | null> {
  if (reserva.idCobro === undefined) return null
  let actual: CobroMercadoPago
  try {
    actual = await deps.consultar(reserva.idCobro)
  } catch {
    return null
  }
  if (!actual.usadoEnVenta) return actual
  await deps.consumirReservaDeCobro(reserva.numero, ...conDueno(reserva.dueno))
  return 'sanada'
}

const formatear = (valor: number) => formatearImporte(valor, { simbolo: true })

const centavos = (valor: number) => Math.round(valor * 100)

const MENSAJE_POR_MOTIVO_DE_RESERVA = {
  sin_numeracion: 'No quedan números reservados para vender en este dispositivo: recuperá la señal para reponer el bloque.',
  error_al_guardar: 'No se pudo reservar el número de la venta en este dispositivo. Probá de nuevo.',
  cobro_en_curso: 'Hay un cobro de Mercado Pago sin cerrar en este dispositivo. Resolvelo antes de iniciar otro.',
} as const

export const MENSAJE_CON_APARTADOS_SIN_SINCRONIZAR =
  'Hay cobros de Mercado Pago apartados que todavía no se avisaron al servidor. Se reintenta solo cuando hay conexión: hasta entonces no se pueden iniciar cobros con Mercado Pago.'

const detenido = (mensaje: string, apartable?: CobroAAPartar, aprobableAMano = false): ResultadoDeCobroEnPos => ({
  tipo: 'detenido',
  mensaje,
  ...(apartable === undefined ? {} : { apartable }),
  ...(aprobableAMano ? { aprobableAMano: true as const } : {}),
})

const importeCobrado = (cobro: CobroMercadoPago) => cobro.importePagado ?? cobro.importe

/**
 * Cierra el modal con un resultado: un cobro aprobado cierra la venta; uno cancelado o vencido libera la reserva (el número no se
 * consumió, así que no queda ningún hueco); un importe distinto deja el cobro vivo y la venta sin cerrar.
 */
export async function resolverResultadoDelCobro(
  resultado: ResultadoDelCobro,
  numero: number,
  reserva: Pick<ReservaDeCobroEnPos, 'soltarReservaDeCobro'>,
  contexto?: ContextoDeResolucion,
): Promise<ResultadoDeCobroEnPos> {
  switch (resultado.tipo) {
    case 'aprobado':
      return {
        tipo: 'aprobado',
        numero,
        idCobro: resultado.cobro.idCobro,
        importe: importeCobrado(resultado.cobro),
        ...(contexto?.dueno !== undefined ? { dueno: contexto.dueno } : {}),
      }
    case 'sin_aprobar':
      await reserva.soltarReservaDeCobro(numero, ...conDueno(contexto?.dueno))
      return detenido(TEXTO_POR_CIERRE_SIN_APROBAR[resultado.estado])
    case 'en_curso':
      return detenido(
        'El cobro con Mercado Pago sigue en curso en la terminal. Cuando termine, volvé a cobrar para retomarlo: el número queda reservado.',
      )
    case 'importe_distinto':
      return {
        ...detenido(mensajeDeImporteDistinto(resultado.cobro.importe, resultado.cobro.importePagado ?? 0, formatear)),
        ...(contexto === undefined ? {} : { apartable: cobroAAPartar(resultado.cobro, numero, contexto) }),
      }
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
    await reserva.consumirReservaDeCobro(previa.numero, ...conDueno(previa.dueno))
    return null
  }
  const vista = vistaDelCobro(actual)
  if (vista.tipo === 'sin_aprobar') {
    await reserva.soltarReservaDeCobro(previa.numero, ...conDueno(previa.dueno))
    return null
  }
  const aprobado = vista.tipo === 'aprobado' || vista.tipo === 'importe_distinto'
  const numeroVisible = construirNumeroVisible(previa.idPuntoVenta, previa.numero)
  const apartable = cobroAAPartar(actual, previa.numero, { idPuntoVenta: previa.idPuntoVenta, dueno: previa.dueno, idMedioPago: previa.idMedioPago })

  const otroPuntoDeVenta = previa.idPuntoVenta !== deps.idPuntoVenta
  if (otroPuntoDeVenta || previa.fueraDeBloque) {
    if (aprobado) {
      return detenido(
        otroPuntoDeVenta
          ? `Hay un cobro de Mercado Pago aprobado por ${formatear(importeCobrado(actual))} sin venta en otro punto de venta (venta ${numeroVisible}, cobro ${idCobro}). Resolvelo desde ese punto de venta o apartalo para seguir vendiendo.`
          : `Cobro de Mercado Pago aprobado sin venta: el número ${numeroVisible} ya no está disponible en este dispositivo. Apartalo para seguir vendiendo y avisá a un supervisor con el cobro ${idCobro} por ${formatear(importeCobrado(actual))}.`,
        apartable,
      )
    }
    try {
      const cancelado = await clientes.cancelar(idCobro)
      const final = vistaDelCobro(cancelado)
      if (final.tipo === 'sin_aprobar') {
        await reserva.soltarReservaDeCobro(previa.numero, ...conDueno(previa.dueno))
        return null
      }
      if (final.tipo === 'aprobado' || final.tipo === 'importe_distinto') {
        return detenido(
          `El cobro ${idCobro} se aprobó por ${formatear(importeCobrado(cancelado))} mientras se cancelaba y su número ya no está disponible. Apartalo para seguir vendiendo y avisá a un supervisor.`,
          cobroAAPartar(cancelado, previa.numero, { idPuntoVenta: previa.idPuntoVenta, dueno: previa.dueno, idMedioPago: previa.idMedioPago }),
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
        ? `Hay un cobro de Mercado Pago aprobado por ${formatear(cobrado ?? 0)} sin venta (venta ${numeroVisible}). ${indicacionDeCobroAprobado(cobrado ?? 0, formatear)} O apartalo para seguir vendiendo.`
        : `Hay un cobro de Mercado Pago sin cerrar por ${formatear(previa.importe ?? 0)} (venta ${numeroVisible}). Resolvelo antes de cobrar otro importe o con otro medio.`,
      aprobado ? apartable : undefined,
    )
  }
  if (aprobado) {
    if (!(await deps.confirmarReuso(actual, numeroVisible))) {
      return detenido(
        `No se usó el cobro aprobado por ${formatear(cobrado ?? 0)} (venta ${numeroVisible}): sigue guardado. Volvé a cobrar para usarlo o apartalo para seguir vendiendo.`,
        apartable,
      )
    }
    return { tipo: 'aprobado', numero: previa.numero, idCobro, importe: importeCobrado(actual), ...(previa.dueno !== undefined ? { dueno: previa.dueno } : {}) }
  }
  return resolverResultadoDelCobro(await deps.esperarCobro(actual, numeroVisible), previa.numero, reserva, {
    idPuntoVenta: previa.idPuntoVenta,
    dueno: previa.dueno,
    idMedioPago: previa.idMedioPago,
  })
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
  const clientes = deps.clienteDeCobros ?? clienteDeCobrosMercadoPago
  const { reserva } = deps

  // Sin red no se crea ningún cobro. Aprobar a mano solo se ofrece si el dispositivo no tiene un cobro vivo: ese cobro pudo quedar
  // pagado y aprobar a mano la venta lo cobraría dos veces.
  if (!deps.enLinea) {
    return (await reserva.leerReservaDeCobro()) === null
      ? detenido(MENSAJE_SIN_CONEXION_PARA_COBRAR_O_APROBAR_A_MANO, undefined, true)
      : detenido(MENSAJE_SIN_CONEXION_PARA_COBRAR)
  }

  // Un cobro apartado que el servidor todavía tiene aprobado seguiría ocupando su número: primero se sincroniza y, si no se puede, no se cobra.
  if ((await reserva.sincronizarApartados()) > 0) return detenido(MENSAJE_CON_APARTADOS_SIN_SINCRONIZAR)

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
    await reserva.soltarReservaDeCobro(apartado.numero, ...conDueno(apartado.dueno))
    return detenido(mensajeDeErrorDeCobroMercadoPago(e), undefined, habilitaAprobacionManual(e))
  }

  let anotado = false
  try {
    anotado = await reserva.anotarCobroEnCurso({
      numero: apartado.numero,
      idCobro: cobro.idCobro,
      idMedioPago: pago.idMedioPago,
      importe: pago.importe,
      dueno: apartado.dueno,
    })
  } catch {
    anotado = false
  }
  if (!anotado) {
    // Sin el registro local un cobro vivo quedaría sin dueño: se cancela en el servidor y no se abre el modal.
    await reserva.soltarReservaDeCobro(apartado.numero, ...conDueno(apartado.dueno)).catch(() => false)
    try {
      const cancelado = await clientes.cancelar(cobro.idCobro)
      if (cancelado.estado === 'aprobado') {
        return detenido(
          `No se pudo guardar el cobro en este dispositivo y Mercado Pago ya lo aprobó (cobro ${cobro.idCobro} por ${formatear(importeCobrado(cancelado))}). Apartalo para dejar registro y avisá a un supervisor para resolverlo.`,
          cobroAAPartar(cancelado, apartado.numero, { idPuntoVenta: deps.idPuntoVenta, dueno: apartado.dueno, idMedioPago: pago.idMedioPago }),
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
  return resolverResultadoDelCobro(await deps.esperarCobro(cobro, numeroVisible), apartado.numero, reserva, {
    idPuntoVenta: deps.idPuntoVenta,
    dueno: apartado.dueno,
    idMedioPago: pago.idMedioPago,
  })
}
