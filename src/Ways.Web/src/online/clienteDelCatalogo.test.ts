import { describe, expect, it } from 'vitest'

import { rutaDelCatalogo, urlDeLaImagenDelArticulo } from '../api/publico'

describe('rutaDelCatalogo', () => {
  it('sin filtros pide solo la página y el tamaño', () => {
    expect(rutaDelCatalogo({ buscar: '', idCategoria: null, pagina: 1, tamano: 24 })).toBe(
      '/publico/catalogo?pagina=1&tamano=24',
    )
  })

  it('manda la búsqueda y la categoría codificadas cuando las hay', () => {
    expect(rutaDelCatalogo({ buscar: 'tuerca & perno', idCategoria: 7, pagina: 3, tamano: 24 })).toBe(
      '/publico/catalogo?buscar=tuerca+%26+perno&idCategoria=7&pagina=3&tamano=24',
    )
  })

  it('la categoría 0 es un filtro, no la ausencia de filtro', () => {
    expect(rutaDelCatalogo({ buscar: '', idCategoria: 0, pagina: 1, tamano: 24 })).toContain('idCategoria=0')
  })
})

describe('urlDeLaImagenDelArticulo', () => {
  it('agrega la versión para romper la caché cuando existe', () => {
    expect(urlDeLaImagenDelArticulo(12, 1700000000)).toBe('/api/publico/articulos/12/imagen?v=1700000000')
    expect(urlDeLaImagenDelArticulo(12, null)).toBe('/api/publico/articulos/12/imagen')
  })
})
