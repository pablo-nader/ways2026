import type { ArticuloListado, DatosDeEtiquetas, FilaDeEtiqueta } from '../../api/tipos'
import { tieneOferta } from '../../etiquetas/contenidoDeGondola'

/** Una fila de la grilla de selección con su precio en la lista elegida. */
export type Candidato = {
  idArticulo: number
  codigoInterno: string
  nombre: string
  precio: number | null
  conOferta: boolean
  /** Motivo por el que el servidor no pudo componer su etiqueta (por ejemplo, sin precio en la lista): no se puede elegir. */
  motivoDeExclusion: string | null
}

export type ArticuloElegido = { idArticulo: number; codigoInterno: string; nombre: string }
export type Seleccion = ReadonlyMap<number, ArticuloElegido>

export const SELECCION_VACIA: Seleccion = new Map()

/** Candidatos de una página de búsqueda: la identidad viene del listado y el precio de `datos`. Sin `datos`, sin precio. */
export function candidatosDeBusqueda(
  articulos: readonly Pick<ArticuloListado, 'id' | 'codigoInterno' | 'nombre'>[],
  datos: DatosDeEtiquetas | null,
): Candidato[] {
  const filas = new Map((datos?.filas ?? []).map((fila) => [fila.idArticulo, fila]))
  const excluidos = new Map((datos?.excluidos ?? []).map((excluido) => [excluido.idArticulo, excluido.motivo]))

  return articulos.map((articulo) => {
    const fila = filas.get(articulo.id)
    return {
      idArticulo: articulo.id,
      codigoInterno: articulo.codigoInterno,
      nombre: articulo.nombre,
      precio: fila?.precioOriginal ?? null,
      conOferta: fila ? tieneOferta(fila) : false,
      motivoDeExclusion: fila ? null : (excluidos.get(articulo.id) ?? 'Sin precio en esta lista'),
    }
  })
}

/** Candidatos del modo Ofertas: solo las filas que traen una oferta o una promoción vigente. */
export function candidatosDeOfertas(datos: DatosDeEtiquetas): Candidato[] {
  return datos.filas.filter(tieneOferta).map((fila) => ({
    idArticulo: fila.idArticulo,
    codigoInterno: fila.codigoInterno,
    nombre: fila.nombre,
    precio: fila.precioOriginal,
    conOferta: true,
    motivoDeExclusion: null,
  }))
}

export function esSeleccionable(candidato: Candidato): boolean {
  return candidato.motivoDeExclusion === null
}

function elegido(candidato: Candidato): ArticuloElegido {
  return { idArticulo: candidato.idArticulo, codigoInterno: candidato.codigoInterno, nombre: candidato.nombre }
}

export type ResultadoDeSeleccion = { seleccion: Seleccion; excedido: boolean }

export function alternarArticulo(seleccion: Seleccion, candidato: Candidato, maximo: number): ResultadoDeSeleccion {
  const siguiente = new Map(seleccion)
  if (siguiente.delete(candidato.idArticulo)) return { seleccion: siguiente, excedido: false }
  if (!esSeleccionable(candidato)) return { seleccion, excedido: false }
  if (siguiente.size >= maximo) return { seleccion, excedido: true }
  siguiente.set(candidato.idArticulo, elegido(candidato))
  return { seleccion: siguiente, excedido: false }
}

/** Suma los seleccionables de la página hasta el tope; `excedido` avisa que alguno quedó afuera. */
export function agregarPagina(seleccion: Seleccion, candidatos: readonly Candidato[], maximo: number): ResultadoDeSeleccion {
  const siguiente = new Map(seleccion)
  let excedido = false
  for (const candidato of candidatos) {
    if (!esSeleccionable(candidato) || siguiente.has(candidato.idArticulo)) continue
    if (siguiente.size >= maximo) {
      excedido = true
      break
    }
    siguiente.set(candidato.idArticulo, elegido(candidato))
  }
  return { seleccion: siguiente, excedido }
}

export function quitarPagina(seleccion: Seleccion, candidatos: readonly Candidato[]): Seleccion {
  const siguiente = new Map(seleccion)
  for (const candidato of candidatos) siguiente.delete(candidato.idArticulo)
  return siguiente
}

/** `true` si la página tiene algo seleccionable y todo lo seleccionable ya está elegido. */
export function paginaCompleta(seleccion: Seleccion, candidatos: readonly Candidato[]): boolean {
  const seleccionables = candidatos.filter(esSeleccionable)
  return seleccionables.length > 0 && seleccionables.every((c) => seleccion.has(c.idArticulo))
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
