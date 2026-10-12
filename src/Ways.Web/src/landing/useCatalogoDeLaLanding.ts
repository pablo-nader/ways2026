import { useEffect, useState } from 'react'

import { clienteDePublico, type CatalogoPublico } from '../api/publico'

/** Cuántos artículos muestra la sección de destacados. */
export const CANTIDAD_DE_DESTACADOS = 8

/**
 * Primera página del catálogo público para las secciones de destacados y categorías. Es una lectura
 * de adorno: si falla, el comercio no publicó nada o la API no responde, devuelve `null` y esas
 * secciones no se dibujan; la landing nunca muestra un error por esto.
 *
 * Cada lectura vive en su propia pasada del efecto: el desmontaje (y la segunda pasada del modo
 * estricto) la marca como vencida, y una respuesta tardía no pisa a la más nueva.
 */
export function useCatalogoDeLaLanding(): CatalogoPublico | null {
  const [catalogo, setCatalogo] = useState<CatalogoPublico | null>(null)

  useEffect(() => {
    let vigente = true
    void (async () => {
      try {
        const respuesta = await clienteDePublico.obtenerCatalogo({
          buscar: '',
          idCategoria: null,
          pagina: 1,
          tamano: CANTIDAD_DE_DESTACADOS,
        })
        if (vigente) setCatalogo(respuesta)
      } catch {
        // Sin catálogo no se dibujan las secciones que dependen de él: no hay nada que mostrar ni que reintentar.
      }
    })()

    return () => {
      vigente = false
    }
  }, [])

  return catalogo
}
