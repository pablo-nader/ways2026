/**
 * Lectura de los rechazos del servidor sobre la tecla de atajo del POS de un artículo por importe. Módulo puro, sin
 * React ni fetch (web-descriptor-tests).
 */
import { ErrorApi } from '../../api/cliente'
import type { ErrorDeTecla, Formulario } from './FormularioArticulo'

/**
 * `409 tecla_atajo_duplicada` sobre la tecla que viajó: otro artículo vivo ya la usa. Se elige por `codigo` (el
 * mensaje del servidor menciona al tenant, que no le dice nada al usuario). Sin tecla enviada no hay nada a lo que
 * atribuirlo y devuelve `null`, igual que cualquier otro rechazo.
 */
export function rechazoDeTeclaTomada(e: unknown, enviado: Pick<Formulario, 'unidadVenta' | 'teclaAtajo'>): ErrorDeTecla | null {
  if (!(e instanceof ErrorApi) || e.estado !== 409 || e.codigo !== 'tecla_atajo_duplicada') return null
  if (enviado.unidadVenta !== 'Importe' || enviado.teclaAtajo === '') return null

  return { tecla: enviado.teclaAtajo, mensaje: `La tecla ${enviado.teclaAtajo} ya la usa otro artículo. Elegí otra o dejá este sin atajo.` }
}
