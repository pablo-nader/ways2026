/**
 * Lo puro de agrupar artículos en una familia (doc 10 §3): el tope por pedido y los avisos de lo que la agrupación
 * hizo. Sin React ni fetch, para probar cada rama sin montar una pantalla (web-descriptor-tests).
 */
import type { CambiosDeUnArticulo, ResultadoDeAgrupacion } from '../../api/tipos'
import { cantidadDeArticulos } from '../articulos/familia'

/** El tope de artículos por pedido que acepta la API (`400 demasiados_articulos`): más no se previsualiza ni se manda. */
export const LIMITE_DE_ARTICULOS = 100

/** `true` si alinear al artículo con la referencia cambia algún campo compartido o algún precio. */
export function cambiaAlgo(cambios: CambiosDeUnArticulo): boolean {
  return cambios.campos.length > 0 || cambios.precios.length > 0
}

/** Lo que se avisa al sumar artículos a una familia: cuántos entraron y cuántos tuvieron que cambiar para igualar a
 * la referencia. `resultado.articulos` trae uno por artículo pedido distinto de la referencia. */
export function avisoDeAgregado(resultado: ResultadoDeAgrupacion): string {
  const cantidad = resultado.articulos.length
  const cambiaron = resultado.articulos.filter(cambiaAlgo).length
  const agregados = `Se ${cantidad === 1 ? 'agregó' : 'agregaron'} ${cantidadDeArticulos(cantidad)} a la familia "${resultado.nombre}".`
  if (cambiaron === 0) return agregados

  return `${agregados} ${cambiaron === 1 ? '1 cambió' : `${cambiaron} cambiaron`} sus valores o sus precios para igualar a la referencia.`
}

/** Lo que se avisa al crear una familia. La referencia es siempre miembro y no figura en `resultado.articulos`. */
export function avisoDeCreacion(resultado: ResultadoDeAgrupacion): string {
  return `Se creó la familia "${resultado.nombre}" con ${cantidadDeArticulos(resultado.articulos.length + 1)}.`
}
