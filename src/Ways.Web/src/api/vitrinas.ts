/**
 * Vitrina de una empresa (la landing de su dominio): llamadas a la API y copia de la pantalla
 * `Vitrina.tsx`. Las funciones de cabecera son finas a propósito; lo que se prueba sin montar nada
 * son los helpers puros de abajo (borrador del formulario, color, validación del logo, copia de los
 * rechazos, opciones del selector de empresa).
 */
import { api, ErrorApi } from './cliente'
import { CODIGO_RESULTADO_INCIERTO } from './bajas'
import { urlBaseApi } from './entornoTauri'
import type { EmpresaListado, ListaPrecioListado, TemaDeVitrina, VitrinaAdmin, VitrinaEdicion } from './tipos'

export const LARGO_MAXIMO_DEL_TAGLINE = 120
export const LARGO_MAXIMO_DE_LA_DESCRIPCION = 2000
export const TAMANO_MAXIMO_DEL_LOGO = 1024 * 1024

/** El naranja de Ways, que la landing usa cuando la vitrina no trae color de acento. */
export const COLOR_DE_MARCA = '#f7991c'

/** Los únicos tipos que el servidor acepta; SVG queda afuera a propósito (puede llevar scripts). */
export const tiposDeLogoAdmitidos: readonly string[] = ['image/png', 'image/jpeg', 'image/webp']

/** Valor del atributo `accept` del selector de archivo. */
export const ACEPTAR_LOGO = tiposDeLogoAdmitidos.join(',')

export const obtenerVitrina = (idEmpresa: number) => api.get<VitrinaAdmin>(`/empresas/${idEmpresa}/vitrina`)

export const guardarVitrina = (idEmpresa: number, datos: VitrinaEdicion) =>
  api.put<VitrinaAdmin>(`/empresas/${idEmpresa}/vitrina`, datos)

/** Baja lógica de la vitrina (la despublica). Contesta 204, también si no había nada. */
export const eliminarVitrina = (idEmpresa: number) => api.delete<void>(`/empresas/${idEmpresa}/vitrina`)

/** El cuerpo es la imagen cruda con su tipo; contesta la vitrina ya con el logo nuevo. */
export const subirLogo = (idEmpresa: number, archivo: File) =>
  api.putBinario<VitrinaAdmin>(`/empresas/${idEmpresa}/vitrina/logo`, archivo)

export const quitarLogo = (idEmpresa: number) => api.delete<void>(`/empresas/${idEmpresa}/vitrina/logo`)

/** Vista previa del logo para un `<img src>`. `version` (`logoVersion`) solo cambia la URL cuando se
 * sube otro logo, para que el `<img>` lo vuelva a pedir. */
export function urlDelLogoAdmin(idEmpresa: number, version: number | null): string {
  const base = `${urlBaseApi()}/api/empresas/${idEmpresa}/vitrina/logo`

  return version === null ? base : `${base}?v=${version}`
}

const PATRON_DE_COLOR = /^#[0-9a-fA-F]{6}$/

/** `#rrggbb` en cualquier caja, que es lo que el servidor acepta (y guarda en minúscula). */
export function esColorHex(valor: string): boolean {
  return PATRON_DE_COLOR.test(valor)
}

/** Lo que muestra el selector de color: el color escrito si es válido; si no, el de la marca. Un
 * `<input type="color">` solo admite `#rrggbb` en minúscula. */
export function colorParaElSelector(texto: string): string {
  const limpio = texto.trim()

  return esColorHex(limpio) ? limpio.toLowerCase() : COLOR_DE_MARCA
}

export function esTipoDeLogoAdmitido(tipo: string): boolean {
  return tiposDeLogoAdmitidos.includes(tipo)
}

export const COPIA_LOGO_NO_ADMITIDO = 'El logo tiene que ser una imagen PNG, JPG o WebP.'
export const COPIA_LOGO_DEMASIADO_GRANDE = 'El logo no puede pesar más de 1 MB.'

/**
 * Rechazo del logo antes de salir a la red, con el mismo orden que el servidor: primero el tipo,
 * después el tamaño. Devuelve el texto a mostrar, o `null` si el archivo puede subirse. El
 * servidor sigue validando por el contenido real; esto solo ahorra el viaje.
 */
export function validarLogo(archivo: { type: string; size: number }): string | null {
  if (!esTipoDeLogoAdmitido(archivo.type)) return COPIA_LOGO_NO_ADMITIDO
  if (archivo.size > TAMANO_MAXIMO_DEL_LOGO) return COPIA_LOGO_DEMASIADO_GRANDE

  return null
}

/** Lo que el formulario edita: texto en vez de `null` para que los campos sean controlados. */
export type BorradorDeVitrina = {
  publicada: boolean
  tagline: string
  descripcion: string
  colorAcento: string
  tema: TemaDeVitrina
  /** Id de la lista de precios de la tienda como texto (así lo entrega el `<select>`); `''` es la lista por defecto. */
  idListaPrecio: string
}

export function aBorrador(vitrina: VitrinaAdmin): BorradorDeVitrina {
  return {
    publicada: vitrina.publicada,
    tagline: vitrina.tagline ?? '',
    descripcion: vitrina.descripcion ?? '',
    colorAcento: vitrina.colorAcento ?? '',
    tema: vitrina.tema === 'claro' ? 'claro' : 'oscuro',
    idListaPrecio: vitrina.idListaPrecio === null ? '' : String(vitrina.idListaPrecio),
  }
}

/** Cuerpo del `PUT`: texto recortado y vacío como `null` (el servidor hace lo mismo; así el
 * borrador y lo guardado dicen lo mismo). */
export function aEdicion(borrador: BorradorDeVitrina): VitrinaEdicion {
  const aNulo = (texto: string) => {
    const limpio = texto.trim()

    return limpio === '' ? null : limpio
  }

  return {
    publicada: borrador.publicada,
    tagline: aNulo(borrador.tagline),
    descripcion: aNulo(borrador.descripcion),
    colorAcento: aNulo(borrador.colorAcento),
    tema: borrador.tema,
    idListaPrecio: borrador.idListaPrecio === '' ? null : Number(borrador.idListaPrecio),
  }
}

/** Compara el borrador con lo guardado tras la misma normalización que `aEdicion`: un espacio al
 * final o un texto de solo espacios no cuentan como cambio. */
export function borradorTieneCambios(borrador: BorradorDeVitrina, guardada: VitrinaAdmin): boolean {
  const escrito = aEdicion(borrador)
  const original = aEdicion(aBorrador(guardada))

  return (
    escrito.publicada !== original.publicada ||
    escrito.tagline !== original.tagline ||
    escrito.descripcion !== original.descripcion ||
    escrito.colorAcento !== original.colorAcento ||
    escrito.tema !== original.tema ||
    escrito.idListaPrecio !== original.idListaPrecio
  )
}

/** Una opción del selector de lista de precios de la tienda. */
export type OpcionDeLista = { valor: string; etiqueta: string }

/**
 * Listas que el servidor acepta para la tienda de una empresa: activas y compartidas o de esa empresa (el mismo
 * criterio con el que responde `lista_invalida`). Ordenadas por nombre; la lista por defecto del comercio se rotula
 * porque es la que la tienda usa cuando no se elige ninguna.
 */
export function opcionesDeListaDeLaTienda(listas: readonly ListaPrecioListado[], idEmpresa: number): OpcionDeLista[] {
  return listas
    .filter((l) => l.activo && (l.idEmpresa === null || l.idEmpresa === idEmpresa))
    .map((l) => ({ valor: String(l.id), etiqueta: l.esDefault ? `${l.nombre} (por defecto)` : l.nombre }))
    .sort((a, b) => a.etiqueta.localeCompare(b.etiqueta, 'es'))
}

/** Una opción del selector de empresa. */
export type OpcionDeEmpresa = { valor: string; etiqueta: string }

/**
 * Opciones del selector: el nombre de fantasía o, si falta, la razón social. Cuando la lista cruza
 * más de un tenant (la plataforma ve todos) se agrega el tenant, y dos empresas con la misma
 * etiqueta se desempatan con su id para que nadie elija a ciegas.
 */
export function opcionesDeEmpresaParaVitrina(empresas: readonly EmpresaListado[]): OpcionDeEmpresa[] {
  const variosTenants = new Set(empresas.map((e) => e.idTenant)).size > 1

  const base = empresas.map((e) => {
    const nombre = e.nombreFantasia?.trim() || e.razonSocial
    const etiqueta = variosTenants ? `${nombre} (${e.nombreTenant ?? 'sin tenant'})` : nombre

    return { valor: String(e.id), etiqueta }
  })

  const repeticiones = new Map<string, number>()
  for (const o of base) repeticiones.set(o.etiqueta, (repeticiones.get(o.etiqueta) ?? 0) + 1)

  return base
    .map((o) => ((repeticiones.get(o.etiqueta) ?? 0) > 1 ? { ...o, etiqueta: `${o.etiqueta} (empresa ${o.valor})` } : o))
    .sort((a, b) => a.etiqueta.localeCompare(b.etiqueta, 'es'))
}

/**
 * Rechazos que la pantalla explica por su `codigo`: el contrato es el código, nunca el texto del
 * servidor. Es un `Map` porque el `codigo` viene del servidor (ver `bajas.ts`).
 */
const COPIA_POR_CODIGO: ReadonlyMap<string, string> = new Map([
  ['color_invalido', 'El color de acento tiene que tener la forma #rrggbb.'],
  ['tema_invalido', 'Elegí el tema claro o el oscuro.'],
  [
    'texto_demasiado_largo',
    `El slogan admite hasta ${LARGO_MAXIMO_DEL_TAGLINE} caracteres y la descripción hasta ${LARGO_MAXIMO_DE_LA_DESCRIPCION}.`,
  ],
  ['publicada_requerida', 'Indicá si la vitrina está publicada o no.'],
  [
    'vitrina_ya_publicada',
    'Otra empresa de este comercio ya tiene su vitrina publicada. Despublicala antes de publicar esta.',
  ],
  [
    'lista_invalida',
    'La lista guardada ya no está disponible: elegí otra o dejá la lista por defecto.',
  ],
  ['logo_demasiado_grande', COPIA_LOGO_DEMASIADO_GRANDE],
  ['logo_no_admitido', COPIA_LOGO_NO_ADMITIDO],
])

/** Un 413 o un 415 que llegan sin el código del servidor (un proxy que corta el cuerpo antes de
 * que la API lo vea) dicen lo mismo que sus códigos. */
const COPIA_POR_ESTADO: ReadonlyMap<number, string> = new Map([
  [413, COPIA_LOGO_DEMASIADO_GRANDE],
  [415, COPIA_LOGO_NO_ADMITIDO],
])

/** Esta pantalla no tiene listado: lo que se revisa es la propia vitrina. */
export const COPIA_RESULTADO_INCIERTO_DE_VITRINA =
  'No se pudo confirmar el resultado: revisá la vitrina antes de reintentar.'

const COPIA_EMPRESA_INEXISTENTE = 'La empresa ya no existe. Actualizá la pantalla.'

/**
 * Texto a mostrar ante el fallo de una escritura de la vitrina. `encabezado` nombra lo que se
 * intentaba ("No se pudo guardar la vitrina.").
 *
 * - Un código conocido (o un 413/415) rinde su copia propia.
 * - Un 404 ya no tiene nada que decir del servidor: la empresa dejó de existir.
 * - Un 5xx, o un error que no es de la API, no dice si se llegó a escribir: manda a verificar,
 *   salvo que el servidor haya marcado el resultado como incierto con su propio texto.
 * - Cualquier otro rechazo rinde el mensaje de la API.
 */
export function copiaDeFalloDeVitrina(error: unknown, encabezado: string): string {
  if (!(error instanceof ErrorApi)) return `${encabezado} ${COPIA_RESULTADO_INCIERTO_DE_VITRINA}`

  const propia = COPIA_POR_CODIGO.get(error.codigo) ?? COPIA_POR_ESTADO.get(error.estado)
  if (propia) return propia

  if (error.estado === 404) return `${encabezado} ${COPIA_EMPRESA_INEXISTENTE}`

  if (error.estado >= 500) {
    const delServidor = error.message.trim()
    const esCommitAmbiguo = error.codigo === CODIGO_RESULTADO_INCIERTO && delServidor.length > 0

    return `${encabezado} ${esCommitAmbiguo ? delServidor : COPIA_RESULTADO_INCIERTO_DE_VITRINA}`
  }

  return error.message.trim() || encabezado
}

/**
 * Texto ante el fallo de leer la vitrina. Una lectura no escribe nada, así que no hay resultado que
 * verificar: solo se distingue la empresa que dejó de existir (404) de cualquier otro fallo.
 */
export function copiaDeFalloDeLecturaDeVitrina(error: unknown): string {
  const encabezado = 'No se pudo cargar la vitrina.'

  if (error instanceof ErrorApi && error.estado === 404) return `${encabezado} ${COPIA_EMPRESA_INEXISTENTE}`

  return `${encabezado} Probá de nuevo.`
}

/**
 * Si tras este fallo de escritura conviene volver a leer la vitrina: un 404 (la empresa cambió) o
 * un resultado incierto (5xx o error que no es de la API, pudo haberse escrito). Un rechazo de
 * validación (400, 409, 413, 415) no cambió nada y no relee.
 */
export function debeReleerTrasFallo(error: unknown): boolean {
  if (!(error instanceof ErrorApi)) return true

  return error.estado === 404 || error.estado >= 500
}
