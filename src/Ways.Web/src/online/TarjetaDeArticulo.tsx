import { memo, useState } from 'react'

import { urlDeLaImagenDelArticulo, type ArticuloPublico } from '../api/publico'
import { Chevrones } from '../publico/Glifos'
import { articuloParaElCarrito, formatearPrecio } from './carrito'
import { useAccionesDelCarrito } from './CarritoContext'
import { ControlDeCantidad } from './ControlDeCantidad'

/** Lado de la caja de la imagen en píxeles; fija la proporción antes de que cargue y evita saltos de layout. */
const LADO_DE_LA_IMAGEN = 320

function ImagenDelArticulo({ articulo }: { articulo: ArticuloPublico }) {
  const [fallo, setFallo] = useState(false)

  return (
    <div className="publico-articulo-imagen">
      {articulo.tieneImagen && !fallo ? (
        <img
          src={urlDeLaImagenDelArticulo(articulo.id, articulo.imagenVersion)}
          alt=""
          width={LADO_DE_LA_IMAGEN}
          height={LADO_DE_LA_IMAGEN}
          loading="lazy"
          onError={() => setFallo(true)}
        />
      ) : (
        <Chevrones className="publico-articulo-marca" />
      )}
    </div>
  )
}

/**
 * Tarjeta de un artículo. Recibe su cantidad en el carrito por prop (0 = no está) y opera con las
 * acciones, que no cambian: así `memo` evita redibujar las tarjetas a las que un cambio del
 * carrito no les toca.
 */
export const TarjetaDeArticulo = memo(function TarjetaDeArticulo({
  articulo,
  cantidad,
}: {
  articulo: ArticuloPublico
  cantidad: number
}) {
  const { agregar } = useAccionesDelCarrito()

  return (
    <li className="publico-articulo">
      <ImagenDelArticulo articulo={articulo} />
      <h2 className="publico-articulo-nombre">{articulo.nombre}</h2>
      {articulo.descripcion && <p className="publico-articulo-descripcion">{articulo.descripcion}</p>}
      <p className="publico-articulo-precio">
        <span className="publico-precio">{formatearPrecio(articulo.precio)}</span>
        {articulo.unidadVenta === 'Peso' && <span className="publico-articulo-unidad"> por kg</span>}
      </p>
      {cantidad === 0 ? (
        <button
          type="button"
          className="publico-boton publico-boton-acento publico-articulo-agregar"
          aria-label={`Agregar ${articulo.nombre} al carrito`}
          onClick={() => agregar(articuloParaElCarrito(articulo))}
        >
          Agregar
        </button>
      ) : (
        <ControlDeCantidad
          idArticulo={articulo.id}
          nombre={articulo.nombre}
          unidadVenta={articulo.unidadVenta}
          cantidad={cantidad}
        />
      )}
    </li>
  )
})
