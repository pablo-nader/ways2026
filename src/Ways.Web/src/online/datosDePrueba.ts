import type { ArticuloPublico, CatalogoPublico, CategoriaPublica } from '../api/publico'

export function articuloPublico(id: number, cambios: Partial<ArticuloPublico> = {}): ArticuloPublico {
  return {
    id,
    nombre: `Artículo ${id}`,
    descripcion: null,
    idCategoria: null,
    precio: 100,
    unidadVenta: 'Unidad',
    tieneImagen: false,
    imagenVersion: null,
    ...cambios,
  }
}

export function categoriaPublica(id: number, nombre: string, cantidad: number): CategoriaPublica {
  return { id, nombre, cantidad }
}

export function catalogoPublico(cambios: Partial<CatalogoPublico> = {}): CatalogoPublico {
  const articulos = cambios.articulos ?? []

  return { categorias: [], articulos, total: articulos.length, pagina: 1, tamano: 24, ...cambios }
}

export function diferida<T>() {
  let resolver: (valor: T) => void = () => {}
  let rechazar: (error: unknown) => void = () => {}
  const promesa = new Promise<T>((resolve, reject) => {
    resolver = resolve
    rechazar = reject
  })

  return { promesa, resolver, rechazar }
}
