import { importeValidoParaTipo, motivoValido } from '../api/caja'
import { ErrorApi } from '../api/cliente'
import type { FilaDeRetiro } from '../api/tipos'

export type AccionSobreRetiro = 'editar' | 'eliminar'

const MENSAJES_DE_RETIRO: Readonly<Record<string, string>> = {
  retiro_de_cierre_no_editable: 'El retiro de cierre de turno lo genera el sistema al cerrar: no se puede modificar ni eliminar.',
  movimiento_de_caja_importe_invalido: 'El importe tiene que ser mayor a 0.',
  movimiento_de_caja_sin_motivo: 'El motivo tiene que tener al menos 5 caracteres.',
  periodo_conciliado: 'La fecha de este retiro ya está conciliada en tesorería: no se puede modificar ni eliminar.',
  no_encontrado: 'El retiro ya no existe: alguien lo eliminó. Se actualizó el listado.',
}

/** Mensaje de un rechazo de la edición o la baja de un retiro: los códigos propios con texto fijo; el resto con el del servidor. */
export function mensajeDeErrorDeRetiro(e: unknown, accion: AccionSobreRetiro): string {
  if (e instanceof ErrorApi) return MENSAJES_DE_RETIRO[e.codigo] ?? e.message

  return accion === 'editar' ? 'No se pudo guardar el retiro.' : 'No se pudo eliminar el retiro.'
}

/** Un 404 o un 409 dicen que la fila en pantalla quedó vieja (eliminada, conciliada): hay que volver a pedir el listado. */
export function filaQuedoObsoleta(e: unknown): boolean {
  return e instanceof ErrorApi && (e.estado === 404 || e.estado === 409)
}

/** Por qué el retiro no admite editar ni eliminar, o `null` si los admite. El retiro de cierre gana sobre la conciliación. */
export function motivoDeNoModificarRetiro(fila: Pick<FilaDeRetiro, 'esRetiroDeCierre' | 'conciliado'>): string | null {
  if (fila.esRetiroDeCierre) return MENSAJES_DE_RETIRO.retiro_de_cierre_no_editable
  if (fila.conciliado) return MENSAJES_DE_RETIRO.periodo_conciliado

  return null
}

/** Mismas reglas que al registrar el retiro desde la caja (`importeValidoParaTipo`, `motivoValido`). */
export function validarRetiro(importe: number | null, motivo: string): { importe: string; motivo: string } {
  return {
    importe: importe !== null && importeValidoParaTipo('Retiro', importe) ? '' : MENSAJES_DE_RETIRO.movimiento_de_caja_importe_invalido,
    motivo: motivoValido(motivo) ? '' : MENSAJES_DE_RETIRO.movimiento_de_caja_sin_motivo,
  }
}
