/**
 * Tienda online del back office: publicar un artículo y cargarle su imagen. Llamadas a la API y
 * copia de la sección "Tienda online" del formulario de artículo. Las funciones de cabecera son
 * finas a propósito; lo que se prueba sin montar nada son los helpers puros de abajo.
 */
import { api, ErrorApi } from './cliente'
import { CODIGO_RESULTADO_INCIERTO } from './bajas'
import { urlBaseApi } from './entornoTauri'
import { ACEPTAR_LOGO, TAMANO_MAXIMO_DEL_LOGO, esTipoDeLogoAdmitido } from './vitrinas'

/** El servidor valida la imagen de un artículo y el logo con el mismo validador: mismo tope y mismos tipos. */
export const TAMANO_MAXIMO_DE_LA_IMAGEN = TAMANO_MAXIMO_DEL_LOGO

/** Valor del atributo `accept` del selector de archivo. */
export const ACEPTAR_IMAGEN = ACEPTAR_LOGO

/** `PUT /api/articulos/{id}/web`: lo que quedó guardado. */
export type PublicacionWeb = { idArticulo: number; publicado: boolean }

/** Lo que contesta el servidor después de escribir o quitar la imagen. `imagenVersion` son los segundos Unix de la
 * última carga y solo sirve para romper la caché de la vista previa. */
export type ImagenDeArticuloAdmin = { idArticulo: number; tieneImagen: boolean; imagenVersion: number | null }

/** Escribe la publicación y nada más: no es un campo de la edición del artículo. Idempotente. */
export const publicarEnLaWeb = (idArticulo: number, publicado: boolean) =>
  api.put<PublicacionWeb>(`/articulos/${idArticulo}/web`, { publicado })

/** El cuerpo es la imagen cruda con su tipo; contesta el estado de la imagen ya con la versión nueva. */
export const subirImagenDeArticulo = (idArticulo: number, archivo: File) =>
  api.putBinario<ImagenDeArticuloAdmin>(`/articulos/${idArticulo}/imagen`, archivo)

/** Idempotente: sin imagen también contesta 204. */
export const quitarImagenDeArticulo = (idArticulo: number) => api.delete<void>(`/articulos/${idArticulo}/imagen`)

/** Vista previa para un `<img src>`. `version` solo cambia la URL cuando se sube otra imagen, para que el `<img>`
 * la vuelva a pedir. */
export function urlDeImagenDeArticuloAdmin(idArticulo: number, version: number | null): string {
  const base = `${urlBaseApi()}/api/articulos/${idArticulo}/imagen`

  return version === null ? base : `${base}?v=${version}`
}

export const COPIA_IMAGEN_NO_ADMITIDA = 'La imagen tiene que ser PNG, JPG o WebP.'
export const COPIA_IMAGEN_DEMASIADO_GRANDE = 'La imagen no puede pesar más de 1 MB.'

/**
 * Rechazo de la imagen antes de salir a la red, con el mismo orden que el servidor: primero el tipo, después el
 * tamaño. Devuelve el texto a mostrar, o `null` si el archivo puede subirse. El servidor sigue validando por el
 * contenido real; esto solo ahorra el viaje.
 */
export function validarImagenDeArticulo(archivo: { type: string; size: number }): string | null {
  if (!esTipoDeLogoAdmitido(archivo.type)) return COPIA_IMAGEN_NO_ADMITIDA
  if (archivo.size > TAMANO_MAXIMO_DE_LA_IMAGEN) return COPIA_IMAGEN_DEMASIADO_GRANDE

  return null
}

/** Rechazos que la sección explica por su `codigo`: el contrato es el código, nunca el texto del servidor. Es un
 * `Map` porque el `codigo` viene del servidor (ver `bajas.ts`). */
const COPIA_POR_CODIGO: ReadonlyMap<string, string> = new Map([
  ['imagen_demasiado_grande', COPIA_IMAGEN_DEMASIADO_GRANDE],
  ['imagen_no_admitida', COPIA_IMAGEN_NO_ADMITIDA],
])

/** Un 413 o un 415 que llegan sin el código del servidor (un proxy que corta el cuerpo antes de que la API lo vea)
 * dicen lo mismo que sus códigos. */
const COPIA_POR_ESTADO: ReadonlyMap<number, string> = new Map([
  [413, COPIA_IMAGEN_DEMASIADO_GRANDE],
  [415, COPIA_IMAGEN_NO_ADMITIDA],
])

export const COPIA_RESULTADO_INCIERTO_DE_TIENDA =
  'No se pudo confirmar el resultado: revisá el estado del artículo en la tienda antes de reintentar.'

const COPIA_ARTICULO_INEXISTENTE = 'El artículo ya no existe. Actualizá la pantalla.'

/**
 * Texto a mostrar ante el fallo de una escritura de la tienda. `encabezado` nombra lo que se intentaba ("No se
 * pudo subir la imagen.").
 *
 * - Un código conocido (o un 413/415) rinde su copia propia.
 * - Un 404 ya no tiene nada que decir del servidor: el artículo dejó de existir.
 * - Un 5xx, o un error que no es de la API, no dice si se llegó a escribir: manda a verificar, salvo que el servidor
 *   haya marcado el resultado como incierto con su propio texto.
 * - Cualquier otro rechazo rinde el mensaje de la API.
 */
export function copiaDeFalloDeTienda(error: unknown, encabezado: string): string {
  if (!(error instanceof ErrorApi)) return `${encabezado} ${COPIA_RESULTADO_INCIERTO_DE_TIENDA}`

  const propia = COPIA_POR_CODIGO.get(error.codigo) ?? COPIA_POR_ESTADO.get(error.estado)
  if (propia) return propia

  if (error.estado === 404) return `${encabezado} ${COPIA_ARTICULO_INEXISTENTE}`

  if (error.estado >= 500) {
    const delServidor = error.message.trim()
    const esCommitAmbiguo = error.codigo === CODIGO_RESULTADO_INCIERTO && delServidor.length > 0

    return `${encabezado} ${esCommitAmbiguo ? delServidor : COPIA_RESULTADO_INCIERTO_DE_TIENDA}`
  }

  return error.message.trim() || encabezado
}

/**
 * Si tras este fallo de escritura conviene volver a leer el artículo: un 404 (cambió) o un resultado incierto (5xx o
 * error que no es de la API, pudo haberse escrito). Un rechazo de validación (400, 403, 409, 413, 415) no cambió nada
 * y no relee.
 */
export function debeReleerTrasFalloDeTienda(error: unknown): boolean {
  if (!(error instanceof ErrorApi)) return true

  return error.estado === 404 || error.estado >= 500
}
