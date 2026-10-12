/**
 * Cliente de lo público: lo que se puede preguntar sin sesión. La pantalla de ingreso lee el sitio
 * por el que se entró (plataforma o dominio de un comercio) y la landing y la tienda leen la
 * vitrina publicada de ese comercio.
 */
import { api } from './cliente'
import type { SitioPublico, UnidadVenta, VitrinaPublica } from './tipos'

export type CategoriaPublica = { id: number; nombre: string; cantidad: number }

export type ArticuloPublico = {
  id: number
  nombre: string
  descripcion: string | null
  idCategoria: number | null
  precio: number
  unidadVenta: UnidadVenta
  tieneImagen: boolean
  imagenVersion: number | null
}

export type CatalogoPublico = {
  categorias: CategoriaPublica[]
  articulos: ArticuloPublico[]
  total: number
  pagina: number
  tamano: number
}

export type ConsultaDeCatalogo = {
  buscar: string
  idCategoria: number | null
  pagina: number
  tamano: number
}

/** Ruta del catálogo: solo viajan los filtros que tienen valor, así la URL cacheable por el servidor es la mínima. */
export function rutaDelCatalogo(consulta: ConsultaDeCatalogo): string {
  const parametros = new URLSearchParams()
  if (consulta.buscar !== '') parametros.set('buscar', consulta.buscar)
  if (consulta.idCategoria !== null) parametros.set('idCategoria', String(consulta.idCategoria))
  parametros.set('pagina', String(consulta.pagina))
  parametros.set('tamano', String(consulta.tamano))

  return `/publico/catalogo?${parametros.toString()}`
}

export const clienteDePublico = {
  obtenerSitio: () => api.get<SitioPublico>('/publico/sitio'),
  obtenerVitrina: () => api.get<VitrinaPublica>('/publico/vitrina'),
  obtenerCatalogo: (consulta: ConsultaDeCatalogo) => api.get<CatalogoPublico>(rutaDelCatalogo(consulta)),
}

/** Logo del comercio para un `<img src>`; `version` (`logoVersion`) cambia la URL al subir otro. */
export function urlDelLogoPublico(version: number | null): string {
  const base = '/api/publico/vitrina/logo'

  return version === null ? base : `${base}?v=${version}`
}

/** Imagen de un artículo para un `<img src>`; `version` (`imagenVersion`) cambia la URL al subir otra. */
export function urlDeLaImagenDelArticulo(id: number, version: number | null): string {
  const base = `/api/publico/articulos/${id}/imagen`

  return version === null ? base : `${base}?v=${version}`
}
