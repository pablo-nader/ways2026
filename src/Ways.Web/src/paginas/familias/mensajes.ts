/**
 * Copia de lo que la API rechaza al gestionar familias (doc 10 §3). Módulo PURO: sin React y sin fetch, para que
 * cada rama se pueda probar sin montar una pantalla.
 *
 * Dos reglas, las mismas de `bajas.ts`: la ayuda se elige por `codigo`, nunca por el texto del mensaje (el código
 * es contrato; el mensaje es texto libre), y el mensaje del servidor NO se tira: es lo único que nombra el artículo
 * o la lista que bloquea, así que va primero y la ayuda va detrás, con lo que el mensaje no trae: qué hacer.
 */
import { CODIGO_RESULTADO_INCIERTO, COPIA_RESULTADO_INCIERTO } from '../../api/bajas'
import { ErrorApi } from '../../api/cliente'

/**
 * Qué hacer con cada rechazo conocido de agrupar, crear, renombrar y sumar artículos. Es un `Map` y no un objeto
 * literal porque el `codigo` viene del SERVIDOR: sobre un objeto, `AYUDA['constructor']` resolvería contra el
 * prototipo y un código exótico dejaría de caer por el fallback.
 */
const AYUDA_POR_CODIGO: ReadonlyMap<string, string> = new Map([
  [
    'familia_precio_inalineable',
    'Un precio nunca se quita: dejá a ese artículo fuera de la selección o corregí sus precios antes de agrupar.',
  ],
  ['articulo_en_otra_familia', 'Agrupar no mueve a nadie de su familia: sacalo de la que tiene o dejalo fuera de la selección.'],
  [
    'referencia_invalida',
    'Revisá que todos los artículos existan y no estén dados de baja, y que el área, la categoría, el grupo, el proveedor habitual y la alícuota de IVA del artículo de referencia tampoco lo estén.',
  ],
  ['demasiados_articulos', 'Elegí menos artículos y agrupá en más de un paso.'],
  ['familia_nombre_duplicado', 'Elegí otro nombre.'],
  ['familia_inactiva', 'Activala desde el listado de familias para poder sumarle artículos.'],
  ['familia_sin_articulos', 'Una familia sin artículos vivos no tiene referencia: disolvela desde el listado o creá una nueva.'],
])

/** `{mensaje del servidor} {ayuda elegida por el código}`. Sin ayuda conocida, el mensaje solo; sin mensaje, la
 * ayuda sola, para que nunca quede un aviso vacío. */
export function mensajeDeProblema(codigo: string, mensaje: string): string {
  const detalle = mensaje.trim()
  const ayuda = AYUDA_POR_CODIGO.get(codigo)

  if (ayuda === undefined) return detalle
  return detalle === '' ? ayuda : `${detalle} ${ayuda}`
}

/** El texto de un fallo al llamar a la API: el rechazo del servidor con su ayuda, o un mensaje de `accion` para lo
 * que no es una respuesta del servidor (la red se cayó). `accion` va en infinitivo: "crear la familia". */
export function mensajeDeError(error: unknown, accion: string): string {
  if (error instanceof ErrorApi) return mensajeDeProblema(error.codigo, error.message) || `No se pudo ${accion}.`

  return `No se pudo ${accion}.`
}

/**
 * El texto de un fallo al ESCRIBIR (crear, agregar, guardar, sacar). Un rechazo del servidor rinde lo mismo que
 * `mensajeDeError`. Lo que no es un rechazo —la red se cayó, un 5xx— no dice si el servidor llegó a commitear, así que
 * comparte la copia del resultado incierto de las bajas (`bajas.ts`): `No se pudo {accion}. No se pudo confirmar el
 * resultado: verificá el listado antes de reintentar.` Un 5xx con `resultado_incierto` rinde el mensaje del servidor,
 * que ya dice qué verificar; cualquier otro 5xx no trae detalle útil y no se anexa.
 */
export function mensajeDeFalloDeEscritura(error: unknown, accion: string): string {
  const encabezado = `No se pudo ${accion}.`

  if (!(error instanceof ErrorApi)) return `${encabezado} ${COPIA_RESULTADO_INCIERTO}`
  if (error.estado < 500) return mensajeDeError(error, accion)

  const delServidor = error.message.trim()
  const esCommitAmbiguo = error.codigo === CODIGO_RESULTADO_INCIERTO && delServidor.length > 0

  return `${encabezado} ${esCommitAmbiguo ? delServidor : COPIA_RESULTADO_INCIERTO}`
}
