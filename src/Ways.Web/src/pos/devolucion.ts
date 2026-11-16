/**
 * Lógica pura de la devolución en el POS: cantidades por línea, armado de lo que se manda al servidor y copia de cada rechazo.
 * Nada acá es autoritativo: el servidor re-juzga todo bajo lock al emitir la nota de crédito.
 */
import { ErrorApi, ErrorDeRed } from '../api/cliente'
import { MENSAJE_DE_CANTIDAD_ENTERA, DECIMALES_DE_PESO, respetaGranularidad } from '../api/cantidadPorUnidad'
import { CODIGO_RESULTADO_INCIERTO } from '../api/bajas'
import type { LineaDevolvible, TicketDevolvible } from '../api/devoluciones'
import type { LineaDeVenta, SolicitudDeVenta, UnidadVenta, VentaDeTurnoListado } from '../api/tipos'
import { formatearNumeroDecimal, parsearNumeroDecimal } from '../formato/numeros'

const COPIA_GENERICA_DE_MOTIVO = 'Este ticket no admite devoluciones.'

const COPIA_POR_MOTIVO: Record<string, string> = {
  anulado: 'El ticket está anulado: no se puede devolver.',
  con_factura_fiscal: 'El ticket tiene factura fiscal: todavía no se puede devolver desde el POS.',
  sin_unidades_por_devolver: 'Ya se devolvió todo lo de este ticket.',
  no_es_un_ticket_de_venta: 'Ese número no es un ticket de venta: no se puede devolver.',
}

/** El motivo por el que el servidor marcó un ticket como no devolvible, en copia para el cajero. */
export function copiaDelMotivo(motivo: string | null): string {
  return (motivo !== null ? COPIA_POR_MOTIVO[motivo] : undefined) ?? COPIA_GENERICA_DE_MOTIVO
}

/** La devolución no trae la unidad de venta: solo un artículo por importe se reconoce (lleva `importe`). El resto se trata como un
 * peso (hasta tres decimales) y el servidor, que sí la conoce, rechaza una fracción de una unidad. */
export function unidadDeLinea(linea: LineaDevolvible): UnidadVenta | undefined {
  return linea.importe !== null ? 'Importe' : undefined
}

export type CantidadEvaluada = { cantidad: number; error: string | null }

/** El texto tipeado de la cantidad a devolver de una línea. Vacío o cero es "no se devuelve"; cualquier otro valor tiene que ser un
 * número positivo con la granularidad de la unidad y no pasar de lo que se puede devolver. Nunca corrige en silencio lo tipeado. */
export function evaluarCantidad(linea: LineaDevolvible, texto: string): CantidadEvaluada {
  if (texto.trim() === '') return { cantidad: 0, error: null }
  const cantidad = parsearNumeroDecimal(texto)
  if (cantidad === null) return { cantidad: 0, error: 'Ingresá un número.' }
  if (cantidad === 0) return { cantidad: 0, error: null }
  const unidad = unidadDeLinea(linea)
  if (!respetaGranularidad(unidad, cantidad)) {
    return { cantidad: 0, error: unidad === 'Importe' ? MENSAJE_DE_CANTIDAD_ENTERA : `Hasta ${DECIMALES_DE_PESO} decimales.` }
  }
  if (cantidad > linea.cantidadDevolvible) {
    return { cantidad: 0, error: `Se puede devolver hasta ${formatearNumeroDecimal(linea.cantidadDevolvible)}.` }
  }
  return { cantidad, error: null }
}

/** Un escaneo suma uno a la cantidad de la línea sin pasar del tope. Devuelve el texto nuevo y, si ya no entra más, el aviso. */
export function sumarPorEscaneo(linea: LineaDevolvible, texto: string): { texto: string; aviso: string | null } {
  const actual = evaluarCantidad(linea, texto).cantidad
  const siguiente = Math.round((actual + 1) * 10 ** DECIMALES_DE_PESO) / 10 ** DECIMALES_DE_PESO
  if (siguiente > linea.cantidadDevolvible) {
    return { texto, aviso: `Ya se agregó todo lo que se puede devolver de ${linea.descripcion}.` }
  }
  return { texto: formatearNumeroDecimal(siguiente), aviso: null }
}

const NUMERO_VISIBLE = /^\d+-(\d+)$/
const NUMERO_SOLO = /^\d+$/

/** El número de ticket tipeado: el correlativo solo ("12") o el visible ("0001-00000012", donde manda lo de después del guion). */
export function numeroDeTicketTipeado(texto: string): number | null {
  const limpio = texto.trim()
  const correlativo = NUMERO_VISIBLE.exec(limpio)?.[1] ?? (NUMERO_SOLO.test(limpio) ? limpio : null)
  if (correlativo === null) return null
  const numero = Number(correlativo)
  return Number.isSafeInteger(numero) && numero > 0 ? numero : null
}

/** Posición de la línea cuyo código de barras es `codigo`, o -1. */
export function lineaPorCodigoDeBarra(lineas: readonly LineaDevolvible[], codigo: string): number {
  const limpio = codigo.trim()
  if (limpio === '') return -1
  return lineas.findIndex((l) => l.codigoBarra !== null && l.codigoBarra === limpio)
}

/** Las líneas de la nota de crédito: solo las que tienen cantidad, con su posición en el ticket. `null` si no hay ninguna o si alguna
 * cantidad es inválida (la selección no se manda a medias). */
export function lineasDeLaDevolucion(
  lineas: readonly LineaDevolvible[],
  textos: Readonly<Record<number, string>>,
): { posiciones: number[]; lineas: LineaDeVenta[] } | null {
  const posiciones: number[] = []
  const armadas: LineaDeVenta[] = []
  for (let i = 0; i < lineas.length; i += 1) {
    const { cantidad, error } = evaluarCantidad(lineas[i], textos[i] ?? '')
    const importe = lineas[i].importe
    if (error !== null) return null
    if (cantidad === 0) continue
    posiciones.push(i)
    armadas.push({
      idArticulo: lineas[i].idArticulo,
      cantidad,
      codigoBarra: lineas[i].codigoBarra,
      idLote: null,
      ...(importe !== null ? { importe } : {}),
    })
  }
  return armadas.length === 0 ? null : { posiciones, lineas: armadas }
}

/** Identifica una selección: la vista previa solo vale para la selección que la pidió. */
export function firmaDeLaSeleccion(idTicket: number, lineas: readonly LineaDeVenta[]): string {
  return JSON.stringify([idTicket, lineas.map((l) => [l.idArticulo, l.cantidad, l.importe ?? null])])
}

/** `POST /api/ventas` de una devolución: nota de crédito asociada al ticket, y el reintegro como único pago en efectivo, sin vuelto. */
export function solicitudDeDevolucion(params: {
  ticket: TicketDevolvible
  lineas: LineaDeVenta[]
  reintegro: number
  idMedioEfectivo: number
}): SolicitudDeVenta & { idCliente: number; lineas: LineaDeVenta[] } {
  return {
    idPuntoVenta: params.ticket.idPuntoVenta,
    idCliente: params.ticket.idCliente,
    codigoTipoComprobante: 'NCX',
    idComprobanteAsociado: params.ticket.id,
    lineas: params.lineas,
    pagos: [{ idMedioPago: params.idMedioEfectivo, importe: params.reintegro, referencia: null, vuelto: 0 }],
    direccionEntrega: null,
    observaciones: null,
  }
}

export type ErrorDeDevolucion = {
  mensaje: string
  /** El ticket cambió o el cálculo quedó viejo: hay que volver a leerlo y revisar las cantidades. */
  releer: boolean
  /** No se sabe si la devolución se registró. */
  incierto: boolean
}

const MENSAJE_DE_RELECTURA = 'Cambió lo que se puede devolver de este ticket. Revisá las cantidades y confirmá de nuevo.'

const ERRORES_POR_CODIGO: Record<string, { mensaje: string; releer?: boolean }> = {
  comprobante_asociado_requerido: { mensaje: 'Falta elegir el ticket de origen de la devolución.' },
  comprobante_asociado_invalido: { mensaje: 'El ticket elegido no admite devoluciones desde este punto de venta.' },
  devolucion_excede_lo_vendido: { mensaje: MENSAJE_DE_RELECTURA, releer: true },
  devolucion_desactualizada: { mensaje: MENSAJE_DE_RELECTURA, releer: true },
  devolucion_reintegro_invalido: { mensaje: 'El efectivo a reintegrar cambió. Revisá el importe y confirmá de nuevo.', releer: true },
  devolucion_requiere_nota_de_credito_fiscal: {
    mensaje: 'El ticket tiene factura fiscal: la devolución necesita una nota de crédito fiscal y todavía no se puede hacer desde el POS.',
  },
  devolucion_sin_conexion: { mensaje: 'Una devolución necesita conexión con el servidor.' },
  devolucion_solo_en_efectivo: { mensaje: 'Una devolución se reintegra solo en efectivo.' },
  devolucion_con_vuelto: { mensaje: 'Una devolución no admite vuelto.' },
  turno_no_abierto: { mensaje: 'No hay un turno abierto: el efectivo de la devolución sale de la caja del turno.' },
}

const MENSAJE_DE_RESULTADO_INCIERTO =
  'No se pudo confirmar si la devolución se registró. Revisá Ventas del turno para ver si quedó emitida antes de volver a intentarla. Desde esta pantalla ya no se puede confirmar de nuevo: cerrala.'

const MENSAJE_DE_VISTA_PREVIA_SIN_RESPUESTA = 'No se pudo calcular el reintegro. Revisá la conexión y volvé a intentar.'

/** Lo que el cajero ve cuando `POST /api/ventas` rechaza o no responde. */
export function errorDeEmisionDeDevolucion(e: unknown): ErrorDeDevolucion {
  if (e instanceof ErrorDeRed || (e instanceof ErrorApi && e.estado === 503 && e.codigo === CODIGO_RESULTADO_INCIERTO)) {
    return { mensaje: MENSAJE_DE_RESULTADO_INCIERTO, releer: false, incierto: true }
  }
  if (e instanceof ErrorApi) {
    const conocido = ERRORES_POR_CODIGO[e.codigo]
    if (conocido) return { mensaje: conocido.mensaje, releer: conocido.releer === true, incierto: false }
    return { mensaje: e.message, releer: false, incierto: false }
  }
  return { mensaje: 'No se pudo registrar la devolución.', releer: false, incierto: false }
}

/** Lo que el cajero ve cuando la vista previa (solo lectura) no responde: no hay nada que se haya registrado, así que no es incierto. */
export function errorDeVistaPreviaDeDevolucion(e: unknown): ErrorDeDevolucion {
  if (e instanceof ErrorDeRed || (e instanceof ErrorApi && e.estado === 503 && e.codigo === CODIGO_RESULTADO_INCIERTO)) {
    return { mensaje: MENSAJE_DE_VISTA_PREVIA_SIN_RESPUESTA, releer: false, incierto: false }
  }
  return errorDeEmisionDeDevolucion(e)
}

/** Lo que el cajero ve cuando no se pudo leer un ticket (por número o de la lista). */
export function errorDeLecturaDeTicket(e: unknown): string {
  if (e instanceof ErrorApi) return e.estado === 404 ? 'No se encontró ese ticket en este punto de venta.' : e.message
  if (e instanceof ErrorDeRed) return 'No se pudo leer el ticket: revisá la conexión.'
  return 'No se pudo leer el ticket.'
}

const CANTIDAD_DE_TICKETS_RECIENTES = 10

/** Los últimos tickets de venta del turno que se pueden ofrecer para devolver: emitidos, de total positivo (sin notas de crédito),
 * del más nuevo al más viejo. */
export function ticketsRecientes(ventas: readonly VentaDeTurnoListado[]): VentaDeTurnoListado[] {
  return ventas
    .filter((v) => v.estado === 'Emitido' && v.total > 0)
    .sort((a, b) => b.fecha.localeCompare(a.fecha))
    .slice(0, CANTIDAD_DE_TICKETS_RECIENTES)
}

export const MOTIVO_DEVOLUCION_SIN_CONEXION = 'Sin conexión: las devoluciones necesitan conexión con el servidor.'
export const MOTIVO_DEVOLUCION_SIN_TURNO = 'Abrí la caja para registrar una devolución: el efectivo sale de la caja del turno.'
const MOTIVO_DEVOLUCION = 'Devolver mercadería de un ticket anterior'

/** La devolución solo existe con conexión y con el turno abierto (el reintegro sale de su caja). `inerte` agrupa los demás bloqueos de la pantalla. */
export function devolucionDisponible(estado: { enLinea: boolean; turnoAbierto: boolean; inerte: boolean }): { habilitado: boolean; motivo: string } {
  if (!estado.enLinea) return { habilitado: false, motivo: MOTIVO_DEVOLUCION_SIN_CONEXION }
  if (!estado.turnoAbierto) return { habilitado: false, motivo: MOTIVO_DEVOLUCION_SIN_TURNO }
  return { habilitado: !estado.inerte, motivo: MOTIVO_DEVOLUCION }
}
