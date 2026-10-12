import type { CategoriaPublica } from '../api/publico'
import { PREFIJO_ONLINE } from '../config/rutasPublicas'

/** Enlace a la tienda ya filtrada por la categoría (`/online?categoria=<id>`). */
function hrefDeCategoria(id: number): string {
  return `${PREFIJO_ONLINE}?categoria=${id}`
}

/** Categorías con artículos publicados y cuántos tiene cada una. Sin categorías la sección no se dibuja. */
export function Categorias({ categorias }: { categorias: readonly CategoriaPublica[] }) {
  if (categorias.length === 0) return null

  return (
    <section className="publico-seccion" aria-labelledby="categorias-titulo">
      <h2 id="categorias-titulo" className="publico-titulo publico-seccion-titulo">
        Explorá por categoría
      </h2>
      <div className="publico-barra publico-barra-fina" aria-hidden="true" />
      <ul className="publico-chips-enlaces">
        {categorias.map((categoria) => (
          <li key={categoria.id}>
            <a className="publico-chip" href={hrefDeCategoria(categoria.id)}>
              {categoria.nombre} <span className="publico-chip-cuenta">{categoria.cantidad}</span>
            </a>
          </li>
        ))}
      </ul>
    </section>
  )
}
