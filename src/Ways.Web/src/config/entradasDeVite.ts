import { PREFIJO_ADMIN, PREFIJO_ONLINE } from './rutasPublicas.ts'

const ENTRADAS = [
  { prefijo: PREFIJO_ADMIN, html: '/admin.html' },
  { prefijo: PREFIJO_ONLINE, html: '/online.html' },
]

/**
 * Qué HTML sirve el servidor de desarrollo para una URL: `/admin` y `/admin/...` salen de
 * `admin.html`, `/online` y `/online/...` de `online.html`; cualquier otra URL queda como está
 * (la raíz y lo desconocido caen en `index.html`, que es la landing). Lo usa `vite.config.ts`.
 */
export function reescribirUrlDeDesarrollo(url: string): string {
  const corte = url.search(/[?#]/)
  const ruta = corte === -1 ? url : url.slice(0, corte)
  const resto = corte === -1 ? '' : url.slice(corte)

  for (const { prefijo, html } of ENTRADAS) {
    if (ruta === prefijo || ruta.startsWith(`${prefijo}/`)) {
      return `${html}${resto}`
    }
  }

  return url
}
