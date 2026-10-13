import type { ArticuloPublico, CatalogoPublico, CategoriaPublica, ComboPublico, ComponenteDeComboPublico } from '../api/publico'

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

  return { categorias: [], articulos, total: articulos.length, pagina: 1, tamano: 24, combos: [], ...cambios }
}

export function componenteDeCombo(idArticulo: number, cambios: Partial<ComponenteDeComboPublico> = {}): ComponenteDeComboPublico {
  return { idArticulo, nombre: `Componente ${idArticulo}`, cantidad: 1, imagenVersion: null, ...cambios }
}

export function comboPublico(id: number, cambios: Partial<ComboPublico> = {}): ComboPublico {
  return {
    id,
    nombre: `Combo ${id}`,
    precio: 500,
    componentes: [componenteDeCombo(id * 10, { nombre: `Gaseosa ${id}`, cantidad: 2 })],
    ...cambios,
  }
}

/** Las líneas del carrito tal como quedaron en el navegador (formato 2: `{ version, items }`). */
export function itemsGuardados(crudo: string | null): Record<string, unknown>[] {
  const guardado = JSON.parse(crudo ?? 'null') as { items: Record<string, unknown>[] } | null

  return guardado?.items ?? []
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
