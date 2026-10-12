import { memo, useEffect, useRef } from 'react'

import type { ArticuloPublico } from '../api/publico'
import { ImagenDelArticulo } from '../publico/ImagenDelArticulo'
import { articuloParaElCarrito, formatearPrecio } from './carrito'
import { useAccionesDelCarrito } from './CarritoContext'
import { ControlDeCantidad } from './ControlDeCantidad'

/**
 * Tarjeta de un artículo. Recibe su cantidad en el carrito por prop (0 = no está) y opera con las
 * acciones, que no cambian: así `memo` evita redibujar las tarjetas a las que un cambio del
 * carrito no les toca.
 *
 * "Agregar" y el selector de cantidad se reemplazan entre sí, y el botón que se pulsó desaparece
 * con el cambio: el foco no puede quedarse ahí (el navegador lo mandaría al `<body>`). Cada
 * gesto de la tarjeta anota a dónde va el foco, y se aplica cuando el nuevo control ya está
 * montado: tras "Agregar", al "+" del selector; tras quitar la línea con "−", al "Agregar" de esta
 * misma tarjeta. Un cambio de cantidad que no sale de la tarjeta (el panel, otra pestaña) no mueve el foco.
 */
export const TarjetaDeArticulo = memo(function TarjetaDeArticulo({
  articulo,
  cantidad,
}: {
  articulo: ArticuloPublico
  cantidad: number
}) {
  const { agregar, quitar } = useAccionesDelCarrito()
  const destinoDelFoco = useRef<'agregar' | 'mas' | null>(null)
  const botonAgregar = useRef<HTMLButtonElement>(null)
  const botonMas = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    const destino = destinoDelFoco.current
    destinoDelFoco.current = null
    if (destino === 'agregar') botonAgregar.current?.focus()
    else if (destino === 'mas') botonMas.current?.focus()
  }, [cantidad])

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
          ref={botonAgregar}
          type="button"
          className="publico-boton publico-boton-acento publico-articulo-agregar"
          aria-label={`Agregar ${articulo.nombre} al carrito`}
          onClick={() => {
            destinoDelFoco.current = 'mas'
            agregar(articuloParaElCarrito(articulo))
          }}
        >
          Agregar
        </button>
      ) : (
        <ControlDeCantidad
          idArticulo={articulo.id}
          nombre={articulo.nombre}
          unidadVenta={articulo.unidadVenta}
          cantidad={cantidad}
          refMas={botonMas}
          alQuitar={() => {
            destinoDelFoco.current = 'agregar'
            quitar(articulo.id)
          }}
        />
      )}
    </li>
  )
})
