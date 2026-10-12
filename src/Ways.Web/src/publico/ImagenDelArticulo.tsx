import { useState } from 'react'

import { urlDeLaImagenDelArticulo, type ArticuloPublico } from '../api/publico'
import { Chevrones } from './Glifos'

/** Lado de la caja de la imagen en píxeles; fija la proporción antes de que cargue y evita saltos de layout. */
const LADO_DE_LA_IMAGEN = 320

/** Imagen del artículo en una caja cuadrada de proporción fija; sin imagen (o si falla) se ve la marca de Ways. */
export function ImagenDelArticulo({ articulo }: { articulo: ArticuloPublico }) {
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
