import { PREFIJO_ADMIN, PREFIJO_ONLINE } from './rutasPublicas.ts'

const ENTRADAS = [
  { prefijo: PREFIJO_ADMIN, html: '/admin.html' },
  { prefijo: PREFIJO_ONLINE, html: '/online.html' },
]

/**
 * Qué HTML sirve el servidor de desarrollo para una URL: `/admin` y `/admin/...` salen de
 * `admin.html`, `/online` y `/online/...` de `online.html`; cualquier otra URL queda como está
 * (la raíz y lo desconocido caen en `index.html`, que es la landing). El primer segmento se compara
 * sin distinguir mayúsculas, como hace el servidor (`/Admin/x` sale de `admin.html`); el resto de la
 * ruta no se toca. Lo usa `vite.config.ts`.
 */
export function reescribirUrlDeDesarrollo(url: string): string {
  const corte = url.search(/[?#]/)
  const ruta = corte === -1 ? url : url.slice(0, corte)
  const resto = corte === -1 ? '' : url.slice(corte)
  const rutaEnMinuscula = ruta.toLowerCase()

  for (const { prefijo, html } of ENTRADAS) {
    if (rutaEnMinuscula === prefijo || rutaEnMinuscula.startsWith(`${prefijo}/`)) {
      return `${html}${resto}`
    }
  }

  return url
}
