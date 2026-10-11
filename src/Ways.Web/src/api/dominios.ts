/**
 * Copia de la pantalla de dominios de un tenant (solo plataforma).
 *
 * Módulo PURO: sin React y sin fetch, para que cada rama se pueda probar sin montar la pantalla.
 * Las llamadas a la API viven en `clienteDeOrganizacion` (`organizacion.ts`).
 */
import { ErrorApi } from './cliente'
import { CODIGO_RESULTADO_INCIERTO, COPIA_RESULTADO_INCIERTO } from './bajas'

export const TEXTO_SIN_VERIFICAR = 'Sin verificar'

/** Explica el orden real de la puesta en marcha. La verificación es una marca manual: el servidor
 * no consulta el DNS, así que la ayuda no promete lo contrario. */
export const AYUDA_DE_VERIFICACION =
  'Antes de marcar un dominio como verificado, su DNS debe apuntar al servidor y el dominio debe estar agregado en el panel del hosting. Marcarlo no comprueba el DNS: hasta que se marca como verificado, el dominio no resuelve.'

/** Qué mostrar en la columna de verificación: la fecha de la marca o `Sin verificar`. */
export function textoDeVerificacion(verificadoEn: string | null): string {
  if (verificadoEn === null) return TEXTO_SIN_VERIFICAR

  return `Verificado el ${new Date(verificadoEn).toLocaleDateString('es-AR')}`
}

/**
 * Los dos rechazos de un alta que la pantalla explica por su `codigo`: el contrato es el código,
 * nunca el texto del servidor. Es un `Map` porque el `codigo` viene del servidor (ver `bajas.ts`).
 */
const COPIA_POR_CODIGO: ReadonlyMap<string, string> = new Map([
  ['host_duplicado', 'Ese host ya está cargado en otro dominio.'],
  ['host_invalido', 'El host no es válido: indica solo el nombre de dominio, sin https:// ni rutas.'],
])

/**
 * Texto a mostrar ante el fallo de una lectura, un alta o una verificación. La baja usa
 * `copiaDeFalloDeBaja`. `encabezado` nombra lo que se intentaba ("No se pudo agregar el dominio.").
 *
 * - Un código conocido rinde su copia propia.
 * - Un 404 ya no tiene nada que decir del servidor: el tenant o el dominio dejó de existir.
 * - Un 5xx, o un error que no es de la API, no dice si se llegó a escribir: manda a verificar el
 *   listado, salvo que el servidor haya marcado el resultado como incierto con su propio texto.
 * - Cualquier otro rechazo rinde el mensaje de la API.
 */
export function copiaDeFalloDeDominio(error: unknown, encabezado: string): string {
  if (!(error instanceof ErrorApi)) return `${encabezado} ${COPIA_RESULTADO_INCIERTO}`

  const propia = COPIA_POR_CODIGO.get(error.codigo)
  if (propia) return propia

  if (error.estado === 404) return `${encabezado} El tenant o el dominio ya no existe. Actualiza la pantalla.`

  if (error.estado >= 500) {
    const delServidor = error.message.trim()
    const esCommitAmbiguo = error.codigo === CODIGO_RESULTADO_INCIERTO && delServidor.length > 0

    return `${encabezado} ${esCommitAmbiguo ? delServidor : COPIA_RESULTADO_INCIERTO}`
  }

  return error.message.trim() || encabezado
}
