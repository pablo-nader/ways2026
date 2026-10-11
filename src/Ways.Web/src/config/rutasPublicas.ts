/** Prefijo bajo el que vive el back office y el POS web. `App` lo usa como `basename` del router. */
export const PREFIJO_ADMIN = '/admin'

/** Prefijo de la tienda online del comercio. */
export const PREFIJO_ONLINE = '/online'

/**
 * Href para un ancla cruda (`<a href>`, `window.location`) hacia una pantalla del back office:
 * el router antepone el `basename` solo a lo que navega él, no a lo que escribe el HTML.
 */
export function hrefDeAdmin(ruta: string): string {
  return `${PREFIJO_ADMIN}${ruta.startsWith('/') ? ruta : `/${ruta}`}`
}
