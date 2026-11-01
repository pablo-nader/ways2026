import { MENSAJES_DE_CAMBIO_DE_MEDIOS } from '../api/cambioDeMedios'
import type { EstadoFiscalDeGrilla, FilaDeVentaDeAdministracion } from '../api/tipos'

export const SIN_DATO = '(no disponible)'

export const TITULO_DE_CAMBIAR_MEDIO = 'Cambiar medio de pago'
const MOTIVO_GENERICO = 'No se puede cambiar el medio de pago de esta venta.'

/** Por qué el servidor marcó la venta como no editable, en castellano: el código conocido con su texto fijo, un texto libre tal
 * cual y, sin motivo, uno genérico. */
export function motivoLegibleDeMedioNoEditable(motivo: string | null): string {
  if (motivo === null || motivo.trim() === '') return MOTIVO_GENERICO

  return MENSAJES_DE_CAMBIO_DE_MEDIOS[motivo] ?? motivo
}

export const ETIQUETA_DE_ESTADO_FISCAL: Readonly<Record<EstadoFiscalDeGrilla, string>> = {
  sin_factura: 'Sin factura',
  en_tramite: 'En trámite',
  con_cae: 'Con CAE',
}

/** Estado del turno de la venta: `null` = la venta no tiene turno. */
export function etiquetaDeEstadoDeTurno(turnoCerrado: boolean | null): string {
  if (turnoCerrado === null) return '—'

  return turnoCerrado ? 'Cerrado' : 'Abierto'
}

/** Título del botón de la fila: el motivo cuando la venta no admite el cambio, y si no, la acción. */
export function tituloDeCambiarMedio(fila: Pick<FilaDeVentaDeAdministracion, 'medioEditable' | 'motivoMedioNoEditable'>): string {
  return fila.medioEditable ? TITULO_DE_CAMBIAR_MEDIO : motivoLegibleDeMedioNoEditable(fila.motivoMedioNoEditable)
}

/** Valor de un select de filtro booleano: '' = sin filtro. */
export function valorDeSelectBooleano(valor: boolean | null): string {
  return valor === null ? '' : String(valor)
}

export function booleanoDeSelect(valor: string): boolean | null {
  return valor === '' ? null : valor === 'true'
}
