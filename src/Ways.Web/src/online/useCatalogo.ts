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
  /** La respuesta que se ve se pidió sin búsqueda y sin categoría, sea cual sea el filtro de ahora. */
  respuestaSinFiltros: boolean
  /**
   * La primera respuesta buena, pedida sin filtros, vino sin artículos: el comercio no publicó nada.
   * Una vez que hubo una primera respuesta no vuelve a cambiar, así que no puede aparecer ni
   * desaparecer por lo que se tipee después.
   */
  vacioAlEntrar: boolean
  cargandoMas: boolean
  errorDeMas: boolean
  /** Cuántas lecturas se pidieron (primera página, otro filtro, reintento, "Ver más"): quien relee algo aparte la usa como disparador. */
  lecturas: number
}

const INICIAL: EstadoDelCatalogo = {
  fase: 'cargando',
  articulos: [],
  categorias: [],
  total: 0,
  pagina: 0,
  cargadoAlgunaVez: false,
  respuestaSinFiltros: false,
  vacioAlEntrar: false,
  cargandoMas: false,
  errorDeMas: false,
  lecturas: 0,
}

/**
 * Lee el catálogo público para un filtro (búsqueda + categoría) y pagina con "Ver más".
 *
 * Contrato de la generación (`generacion`): cualquier lectura nueva la incrementa, y toda respuesta
 * que llegue con otro número se descarta. Cambiar el filtro, reintentar, pedir otra página o
 * desmontar invalidan lo que sigue en vuelo, así una respuesta tardía nunca pisa la más nueva ni
 * agrega una página de un filtro viejo a la lista del nuevo. Mientras llega el primer resultado de
 * un filtro nuevo se conserva la lista anterior (atenuada por la pantalla) para no parpadear.
 *
 * Lo que la pantalla decide sobre "catálogo vacío" sale de la respuesta (`respuestaSinFiltros`,
 * `vacioAlEntrar`), nunca de comparar el filtro de ahora con una respuesta que se pidió con otro:
 * entre que el filtro cambia y llega la respuesta nueva se dibuja una vez más con la respuesta vieja.
 */
export function useCatalogo({ buscar, idCategoria }: FiltroDelCatalogo) {
  const [estado, setEstado] = useState<EstadoDelCatalogo>(INICIAL)
  const generacion = useRef(0)

  const cargarPrimera = useCallback(async () => {
    const mia = ++generacion.current
    setEstado((prev) => ({ ...prev, fase: 'cargando', cargandoMas: false, errorDeMas: false, lecturas: prev.lecturas + 1 }))
    try {
      const catalogo = await clienteDePublico.obtenerCatalogo({ buscar, idCategoria, pagina: 1, tamano: TAMANO_DE_PAGINA })
      if (generacion.current !== mia) return
      const sinFiltros = buscar === '' && idCategoria === null
      setEstado((prev) => ({
        fase: 'listo',
        articulos: catalogo.articulos,
        categorias: catalogo.categorias,
        total: catalogo.total,
        pagina: catalogo.pagina,
        cargadoAlgunaVez: true,
        respuestaSinFiltros: sinFiltros,
        vacioAlEntrar: prev.cargadoAlgunaVez ? prev.vacioAlEntrar : sinFiltros && catalogo.total === 0,
        cargandoMas: false,
        errorDeMas: false,
        lecturas: prev.lecturas,
      }))
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
    setEstado((prev) => ({ ...prev, cargandoMas: true, errorDeMas: false, lecturas: prev.lecturas + 1 }))
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
