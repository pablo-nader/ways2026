import { useCallback, useEffect, useRef, useState } from 'react'

import { clienteDePublico, type ArticuloPublico, type CategoriaPublica } from '../api/publico'

export const TAMANO_DE_PAGINA = 24

export type FiltroDelCatalogo = { buscar: string; idCategoria: number | null }

export type EstadoDelCatalogo = {
  fase: 'cargando' | 'listo' | 'error'
  articulos: readonly ArticuloPublico[]
  categorias: readonly CategoriaPublica[]
  total: number
  /** Última página ya cargada. */
  pagina: number
  /** Falso hasta la primera respuesta buena; separa "todavía no sé" de "el comercio no publicó nada". */
  cargadoAlgunaVez: boolean
  cargandoMas: boolean
  errorDeMas: boolean
}

const INICIAL: EstadoDelCatalogo = {
  fase: 'cargando',
  articulos: [],
  categorias: [],
  total: 0,
  pagina: 0,
  cargadoAlgunaVez: false,
  cargandoMas: false,
  errorDeMas: false,
}

/**
 * Lee el catálogo público para un filtro (búsqueda + categoría) y pagina con "Ver más".
 *
 * Contrato de la generación (`generacion`): cualquier lectura nueva la incrementa, y toda respuesta
 * que llegue con otro número se descarta. Cambiar el filtro, reintentar, pedir otra página o
 * desmontar invalidan lo que sigue en vuelo, así una respuesta tardía nunca pisa la más nueva ni
 * agrega una página de un filtro viejo a la lista del nuevo. Mientras llega el primer resultado de
 * un filtro nuevo se conserva la lista anterior (atenuada por la pantalla) para no parpadear.
 */
export function useCatalogo({ buscar, idCategoria }: FiltroDelCatalogo) {
  const [estado, setEstado] = useState<EstadoDelCatalogo>(INICIAL)
  const generacion = useRef(0)

  const cargarPrimera = useCallback(async () => {
    const mia = ++generacion.current
    setEstado((prev) => ({ ...prev, fase: 'cargando', cargandoMas: false, errorDeMas: false }))
    try {
      const catalogo = await clienteDePublico.obtenerCatalogo({ buscar, idCategoria, pagina: 1, tamano: TAMANO_DE_PAGINA })
      if (generacion.current !== mia) return
      setEstado({
        fase: 'listo',
        articulos: catalogo.articulos,
        categorias: catalogo.categorias,
        total: catalogo.total,
        pagina: catalogo.pagina,
        cargadoAlgunaVez: true,
        cargandoMas: false,
        errorDeMas: false,
      })
    } catch {
      if (generacion.current !== mia) return
      setEstado((prev) => ({ ...prev, fase: 'error' }))
    }
  }, [buscar, idCategoria])

  const invalidar = useCallback(() => {
    generacion.current++
  }, [])

  useEffect(() => {
    void cargarPrimera()

    return invalidar
  }, [cargarPrimera, invalidar])

  const paginaCargada = estado.pagina
  const cargarMas = useCallback(async () => {
    const mia = ++generacion.current
    setEstado((prev) => ({ ...prev, cargandoMas: true, errorDeMas: false }))
    try {
      const catalogo = await clienteDePublico.obtenerCatalogo({
        buscar,
        idCategoria,
        pagina: paginaCargada + 1,
        tamano: TAMANO_DE_PAGINA,
      })
      if (generacion.current !== mia) return
      setEstado((prev) => {
        const ids = new Set(prev.articulos.map((a) => a.id))

        return {
          ...prev,
          articulos: [...prev.articulos, ...catalogo.articulos.filter((a) => !ids.has(a.id))],
          categorias: catalogo.categorias,
          total: catalogo.total,
          pagina: catalogo.pagina,
          cargandoMas: false,
        }
      })
    } catch {
      if (generacion.current !== mia) return
      setEstado((prev) => ({ ...prev, cargandoMas: false, errorDeMas: true }))
    }
  }, [buscar, idCategoria, paginaCargada])

  const hayMas = estado.pagina * TAMANO_DE_PAGINA < estado.total

  return { ...estado, hayMas, cargarMas, reintentar: cargarPrimera }
}
