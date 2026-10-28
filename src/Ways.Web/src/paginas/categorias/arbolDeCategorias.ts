import type { CategoriaListado } from '../../api/tipos'
import { normalizarTexto } from '../../componentes/grilla/filtros'
import { ordenarFilas, type EstadoDeOrden } from '../../componentes/grilla/ordenamiento'

export type Nodo = CategoriaListado & { hijos: Nodo[]; nivel: number }

/** Arma el árbol client-side a partir del listado plano (`idCategoriaPadre`). El backend ya
 * valida profundidad/ciclos (ADR-12); acá solo se arma la vista — una categoría cuyo padre no
 * está en el listado visible (dado de baja, filtrado) queda como raíz huérfana en vez de
 * desaparecer, para no esconder datos reales. */
export function armarArbol(items: CategoriaListado[]): Nodo[] {
  const porId = new Map(items.map((c) => [c.id, c]))
  const hijosDe = new Map<number | null, CategoriaListado[]>()

  for (const item of items) {
    const padre = item.idCategoriaPadre !== null && porId.has(item.idCategoriaPadre) ? item.idCategoriaPadre : null
    const lista = hijosDe.get(padre) ?? []
    lista.push(item)
    hijosDe.set(padre, lista)
  }

  function construir(padre: number | null, nivel: number): Nodo[] {
    return (hijosDe.get(padre) ?? [])
      .sort((a, b) => a.orden - b.orden || a.nombre.localeCompare(b.nombre))
      .map((item) => ({ ...item, nivel, hijos: construir(item.id, nivel + 1) }))
  }

  return construir(null, 1)
}

/** IDs de los descendientes (hijos, nietos, etc.) de un nodo dentro del árbol, para excluirlos
 * de las opciones de "categoría padre" — reasignar una categoría a su propio subárbol formaría
 * un ciclo, y el backend lo rechaza igual, pero no tiene sentido ofrecerlo en el select. */
export function idsDeSubarbol(arbol: Nodo[], id: number): Set<number> {
  const ids = new Set<number>()

  function recorrer(nodo: Nodo) {
    for (const hijo of nodo.hijos) {
      ids.add(hijo.id)
      recorrer(hijo)
    }
  }

  function buscar(nodos: Nodo[]): Nodo | undefined {
    for (const nodo of nodos) {
      if (nodo.id === id) return nodo
      const encontrado = buscar(nodo.hijos)
      if (encontrado) return encontrado
    }
    return undefined
  }

  const objetivo = buscar(arbol)
  if (objetivo) recorrer(objetivo)

  return ids
}

export function aplanar(arbol: Nodo[]): Nodo[] {
  return arbol.flatMap((nodo) => [nodo, ...aplanar(nodo.hijos)])
}

/**
 * Deja las categorías cuyo nombre contiene el texto (sin distinguir mayúsculas ni acentos) y los ancestros de cada una,
 * para que el camino hasta la coincidencia siga a la vista. Un nodo que coincide no arrastra a sus hijos: solo se
 * conservan los que coinciden o llevan a una coincidencia. El `nivel` de cada nodo es el del árbol completo.
 */
export function filtrarArbol(arbol: Nodo[], texto: string): Nodo[] {
  const buscado = normalizarTexto(texto)
  if (buscado === '') return arbol

  return arbol.flatMap((nodo) => {
    const hijos = filtrarArbol(nodo.hijos, texto)
    const coincide = normalizarTexto(nodo.nombre).includes(buscado)

    return coincide || hijos.length > 0 ? [{ ...nodo, hijos }] : []
  })
}

const COLUMNA_NOMBRE = { nombre: { valor: (nodo: Nodo) => nodo.nombre } }

/** Ordena por nombre las categorías hermanas, en cada nivel del árbol. Sin orden devuelve el árbol tal cual. */
export function ordenarArbol(arbol: Nodo[], orden: EstadoDeOrden): Nodo[] {
  if (orden === null) return arbol

  return ordenarFilas(arbol, COLUMNA_NOMBRE, orden).map((nodo) => ({
    ...nodo,
    hijos: ordenarArbol(nodo.hijos, orden),
  }))
}
