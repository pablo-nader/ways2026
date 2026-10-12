import type { FiltroDelCatalogo } from './useCatalogo'

/** Mismo tope que el campo de búsqueda de la barra (`maxLength`). */
const LARGO_MAXIMO_DE_BUSQUEDA = 100

const PATRON_DE_ENTERO_POSITIVO = /^[1-9]\d{0,9}$/

/**
 * Filtro con el que arranca el catálogo, tomado de la URL: `?categoria=<id>` y `?buscar=<texto>`
 * (los enlaces de la landing). Un `categoria` que no es un entero positivo y un `buscar` vacío se
 * ignoran, así que una URL mal armada abre el catálogo completo en vez de uno vacío.
 */
export function filtroInicialDeLaUrl(consulta: string): FiltroDelCatalogo {
  const parametros = new URLSearchParams(consulta)
  const categoria = (parametros.get('categoria') ?? '').trim()
  const buscar = (parametros.get('buscar') ?? '').trim().slice(0, LARGO_MAXIMO_DE_BUSQUEDA).trim()

  return {
    buscar,
    idCategoria: PATRON_DE_ENTERO_POSITIVO.test(categoria) ? Number(categoria) : null,
  }
}
