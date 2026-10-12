import type { FiltroDelCatalogo } from './useCatalogo'

/** Mismo tope que el campo de búsqueda de la barra (`maxLength`). */
const LARGO_MAXIMO_DE_BUSQUEDA = 100

const PATRON_DE_ENTERO_POSITIVO = /^[1-9]\d{0,9}$/

/** Mayor id que acepta el API (`int` de 32 bits); un valor más grande no puede existir y rompería el pedido. */
const ID_MAXIMO = 2_147_483_647

function idDeCategoria(texto: string): number | null {
  if (!PATRON_DE_ENTERO_POSITIVO.test(texto)) return null
  const id = Number(texto)

  return id <= ID_MAXIMO ? id : null
}

/**
 * Filtro con el que arranca el catálogo, tomado de la URL: `?categoria=<id>` y `?buscar=<texto>`
 * (los enlaces de la landing). Un `categoria` que no es un entero positivo de 32 bits y un `buscar` vacío se
 * ignoran, así que una URL mal armada abre el catálogo completo en vez de uno vacío.
 */
export function filtroInicialDeLaUrl(consulta: string): FiltroDelCatalogo {
  const parametros = new URLSearchParams(consulta)
  const categoria = (parametros.get('categoria') ?? '').trim()
  const buscar = (parametros.get('buscar') ?? '').trim().slice(0, LARGO_MAXIMO_DE_BUSQUEDA).trim()

  return {
    buscar,
    idCategoria: idDeCategoria(categoria),
  }
}
