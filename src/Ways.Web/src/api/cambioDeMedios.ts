/**
 * Reglas puras de "Cambiar medio de pago" de una venta ya emitida (`PUT /api/ventas/{id}/medios`): espejo cliente de lo que el
 * servidor vuelve a validar (`ReglaDeCambioDeMedios`, `ValidadorDePagos`), nunca autoritativo. El modal las usa para guiar
 * al cajero y para mapear los 409/400 del servidor a mensajes claros.
 */
import { ErrorApi } from './cliente'
import { errorDeLineasDePago, type LineaDePagoEditable } from './lineasDePago'
import { filaPagoVacia, filasAPagosConVuelto, sumarImportes, sumarVueltos, validarPagosLocal } from './pagos'
import type { FilaPago } from './pagos'
import type { EstadoComprobante, EstadoFiscalDeVenta, MedioPagoListado, PagoDeVenta, PagoEmitido } from './tipos'
import { redondearImporte } from '../formato/importes'

export const MENSAJES_DE_CAMBIO_DE_MEDIOS: Readonly<Record<string, string>> = {
  venta_turno_cerrado: 'El turno de esta venta ya está cerrado: el medio de pago lo corrige la administración.',
  venta_con_cae: 'La venta tiene factura con CAE: anulala y volvé a cargarla para cambiar el medio de pago.',
  venta_con_factura_en_tramite: 'La factura de esta venta está en trámite en ARCA; probá de nuevo en unos minutos.',
  venta_anulada: 'La venta está anulada: no se puede cambiar su medio de pago.',
  comprobante_sin_cambio_de_medios: 'Este comprobante no cobra con medios de pago, no hay nada para cambiar.',
  cambio_de_medios_con_cuenta_corriente: 'No se puede cambiar el medio de una venta cobrada con cuenta corriente, ni pasarla a cuenta corriente.',
  total_de_cobros_distinto: 'Lo cobrado con los medios nuevos tiene que ser igual a lo que se cobró originalmente.',
  cambio_de_medios_sin_diferencia: 'Los medios indicados son los que la venta ya tiene: no hay nada para cambiar.',
  periodo_conciliado: 'La fecha de esta venta ya está conciliada en tesorería: no se puede cambiar su medio de pago.',
}

/** Mensaje de un rechazo del cambio de medios: los códigos propios del cambio con texto fijo; el resto (reglas de pago como
 * `referencia_de_pago_requerida`, 403, etc.) con el mensaje del servidor. */
export function mensajeDeErrorDeCambioDeMedios(e: unknown): string {
  if (e instanceof ErrorApi) return MENSAJES_DE_CAMBIO_DE_MEDIOS[e.codigo] ?? e.message
  return 'No se pudo cambiar el medio de pago.'
}

/** Lo cobrado de verdad: importe entregado menos vuelto, como lo compara el servidor. */
export function cobradoDePagos(pagos: readonly Pick<PagoEmitido, 'importe' | 'vuelto'>[]): number {
  return redondearImporte(sumarImportes([...pagos]) - sumarVueltos([...pagos]))
}

type VentaParaBloqueo = {
  estado: EstadoComprobante
  estadoFiscal?: EstadoFiscalDeVenta
  mediosDePago: readonly { idMedioPago: number }[]
}

/** Por qué una venta del listado no ofrece "Cambiar medio de pago", o `null` si se puede intentar. Solo con lo que el listado
 * sabe (estado, factura, medios); el servidor decide siempre (turno, conciliación, comprobante). */
export function motivoDeNoCambiarMedios(venta: VentaParaBloqueo, medios: readonly MedioPagoListado[]): string | null {
  if (venta.estado !== 'Emitido') return MENSAJES_DE_CAMBIO_DE_MEDIOS.venta_anulada
  if (venta.estadoFiscal === 'aprobado') return MENSAJES_DE_CAMBIO_DE_MEDIOS.venta_con_cae
  if (venta.estadoFiscal === 'pendiente') return MENSAJES_DE_CAMBIO_DE_MEDIOS.venta_con_factura_en_tramite
  const conCuentaCorriente = venta.mediosDePago.some(
    (m) => medios.find((candidato) => candidato.id === m.idMedioPago)?.comportamiento === 'CuentaCorriente',
  )
  return conCuentaCorriente ? MENSAJES_DE_CAMBIO_DE_MEDIOS.cambio_de_medios_con_cuenta_corriente : null
}

/** Medios que el selector ofrece: la cuenta corriente nunca (ni de origen ni de destino de la corrección). */
export function mediosParaCambiar(medios: readonly MedioPagoListado[]): MedioPagoListado[] {
  return medios.filter((m) => m.comportamiento !== 'CuentaCorriente')
}

/** Filas del formulario precargadas con los cobros que tiene la venta. El vuelto original viaja como el manual de su fila; sin
 * vuelto la fila queda en "sugerido", que sigue al importe que se tipee. */
export function filasDesdePagos(pagos: readonly PagoEmitido[]): FilaPago[] {
  return pagos.map((p, indice) => ({
    ...filaPagoVacia(indice + 1),
    idMedioPago: p.idMedioPago,
    importe: p.importe,
    referencia: p.referencia ?? '',
    vueltoManual: p.vuelto > 0 ? p.vuelto : null,
  }))
}

function firmaDeReparto(pagos: readonly PagoDeVenta[]): string {
  return pagos
    .map((p) => `${p.idMedioPago}|${p.importe}|${p.vuelto}|${(p.referencia ?? '').trim()}`)
    .sort()
    .join(';')
}

/** `true` si los dos repartos cobran con los mismos medios, importes, vueltos y referencias (sin importar el orden): guardar
 * eso no cambia nada y el servidor lo rechazaría con `cambio_de_medios_sin_diferencia`. */
export function repartoIgual(a: readonly PagoDeVenta[], b: readonly PagoDeVenta[]): boolean {
  return firmaDeReparto(a) === firmaDeReparto(b)
}

export type EvaluacionDeCambioDeMedios = {
  /** Pagos listos para enviar (solo filas completas). */
  pagos: PagoDeVenta[]
  /** Vuelto resuelto por fila (id de fila → vuelto). */
  vueltoPorFila: ReadonlyMap<number, number>
  /** Lo que falta (positivo) o sobra (negativo) de lo cobrado originalmente. */
  diferencia: number
  /** Primer motivo por el que todavía no se puede guardar, o `null`. */
  error: string | null
}

/**
 * Evalúa el formulario completo. `totalDeLaVenta` y `cobradoOriginal` salen de la venta que leyó el servidor. La tolerancia de
 * pago no se conoce acá: se toma la que la venta original ya usó (`total − cobrado`), así una venta cobrada dentro de la
 * tolerancia no se rechaza de entrada.
 */
export function evaluarCambioDeMedios(params: {
  filas: readonly FilaPago[]
  medios: readonly MedioPagoListado[]
  totalDeLaVenta: number
  cobradoOriginal: number
}): EvaluacionDeCambioDeMedios {
  const { filas, medios, totalDeLaVenta, cobradoOriginal } = params
  const medioPorId = Object.fromEntries(medios.map((m) => [m.id, m]))
  const pagosConVuelto = filasAPagosConVuelto([...filas], medioPorId, totalDeLaVenta)
  const vueltoPorFila = new Map(pagosConVuelto.map((p) => [p.idFila, p.vuelto]))
  const diferencia = redondearImporte(cobradoOriginal - (sumarImportes(pagosConVuelto) - sumarVueltos(pagosConVuelto)))
  const pagos = pagosConVuelto.map((p) => ({ idMedioPago: p.idMedioPago, importe: p.importe, referencia: p.referencia, vuelto: p.vuelto }))

  const resultado = (error: string | null): EvaluacionDeCambioDeMedios => ({ pagos, vueltoPorFila, diferencia, error })

  const lineas: LineaDePagoEditable[] = filas.map((f) => ({ clave: f.id, idMedioPago: f.idMedioPago, importe: f.importe, referencia: f.referencia }))
  const errorDeLineas = errorDeLineasDePago(lineas)
  if (errorDeLineas !== null) return resultado(errorDeLineas)

  const indiceSinMedio = filas.findIndex((f) => f.idMedioPago !== '' && !medioPorId[f.idMedioPago])
  if (indiceSinMedio >= 0) return resultado(`Línea ${indiceSinMedio + 1}: elegí un medio de pago disponible.`)

  const rechazo = validarPagosLocal({
    total: totalDeLaVenta,
    pagos: pagosConVuelto,
    toleranciaPago: Math.max(0, redondearImporte(totalDeLaVenta - cobradoOriginal)),
    esConsumidorFinal: false,
    saldoCliente: 0,
    limiteCredito: 0,
    creditoIlimitado: true,
    exigirLimiteDeCredito: false,
  })
  if (rechazo !== null) return resultado(rechazo.mensaje)

  if (diferencia !== 0) return resultado(MENSAJES_DE_CAMBIO_DE_MEDIOS.total_de_cobros_distinto)

  return resultado(null)
}
