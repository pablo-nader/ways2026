import type { FilaDeEtiqueta } from '../../api/tipos'
import { tieneOferta } from '../../etiquetas/contenidoDeGondola'

/** Ids de los artículos elegidos: cada uno se imprime una sola vez. */
export type Seleccion = ReadonlySet<number>

export const SELECCION_VACIA: Seleccion = new Set()

export type ResultadoDeSeleccion = { seleccion: Seleccion; excedido: boolean }

export function alternarArticulo(seleccion: Seleccion, id: number, maximo: number): ResultadoDeSeleccion {
  const siguiente = new Set(seleccion)
  if (siguiente.delete(id)) return { seleccion: siguiente, excedido: false }
  if (siguiente.size >= maximo) return { seleccion, excedido: true }
  siguiente.add(id)
  return { seleccion: siguiente, excedido: false }
}

/** Suma los de la página hasta el tope; `excedido` avisa que alguno quedó afuera. */
export function agregarPagina(seleccion: Seleccion, ids: readonly number[], maximo: number): ResultadoDeSeleccion {
  const siguiente = new Set(seleccion)
  let excedido = false
  for (const id of ids) {
    if (siguiente.has(id)) continue
    if (siguiente.size >= maximo) {
      excedido = true
      break
    }
    siguiente.add(id)
  }
  return { seleccion: siguiente, excedido }
}

export function quitarPagina(seleccion: Seleccion, ids: readonly number[]): Seleccion {
  const siguiente = new Set(seleccion)
  for (const id of ids) siguiente.delete(id)
  return siguiente
}

export type FilasParaImprimir = { filas: FilaDeEtiqueta[]; omitidos: number }

/** Las filas devueltas por el servidor, en el orden de la selección; con `soloConOfertas` salen solo las que tienen oferta.
 * `omitidos` cuenta los elegidos que no se imprimen (sin precio en la lista o, en modo Ofertas, sin oferta). */
export function filasParaImprimir(ids: readonly number[], filas: readonly FilaDeEtiqueta[], soloConOfertas: boolean): FilasParaImprimir {
  const porId = new Map(filas.map((fila) => [fila.idArticulo, fila]))
  const ordenadas = ids
    .map((id) => porId.get(id))
    .filter((fila): fila is FilaDeEtiqueta => fila !== undefined && (!soloConOfertas || tieneOferta(fila)))
  return { filas: ordenadas, omitidos: ids.length - ordenadas.length }
}
