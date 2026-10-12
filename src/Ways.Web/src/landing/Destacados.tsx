import type { ArticuloPublico } from '../api/publico'
import { PREFIJO_ONLINE } from '../config/rutasPublicas'
import { formatearPrecio } from '../online/carrito'
import { ImagenDelArticulo } from '../publico/ImagenDelArticulo'

/** Un artículo de la muestra: enlace a la tienda con el nombre como búsqueda (`?buscar=`), para encontrarlo ahí. Aquí no hay carrito. */
function TarjetaDestacada({ articulo }: { articulo: ArticuloPublico }) {
  return (
    <li className="publico-articulo publico-destacado">
      <a className="publico-destacado-enlace" href={`${PREFIJO_ONLINE}?buscar=${encodeURIComponent(articulo.nombre)}`}>
        <ImagenDelArticulo articulo={articulo} />
        <span className="publico-articulo-nombre">{articulo.nombre}</span>
        <span className="publico-articulo-precio">
          <span className="publico-precio">{formatearPrecio(articulo.precio)}</span>
          {articulo.unidadVenta === 'Peso' && <span className="publico-articulo-unidad"> por kg</span>}
        </span>
      </a>
    </li>
  )
}

/** Muestra de los artículos publicados. Sin artículos la sección no se dibuja. */
export function Destacados({ articulos }: { articulos: readonly ArticuloPublico[] }) {
  if (articulos.length === 0) return null

  return (
    <section className="publico-seccion" aria-labelledby="destacados-titulo">
      <h2 id="destacados-titulo" className="publico-titulo publico-seccion-titulo">
        Lo que vas a encontrar
      </h2>
      <div className="publico-barra publico-barra-fina" aria-hidden="true" />
      <ul className="publico-grilla publico-grilla-compacta">
        {articulos.map((articulo) => (
          <TarjetaDestacada key={articulo.id} articulo={articulo} />
        ))}
      </ul>
      <div className="publico-ver-mas">
        <a className="publico-boton publico-boton-contorno" href={PREFIJO_ONLINE}>
          Ver toda la tienda
        </a>
      </div>
    </section>
  )
}
