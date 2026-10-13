import { memo, useEffect, useRef, useState } from 'react'

import { urlDeLaImagenDelArticulo, type ComboPublico, type ComponenteDeComboPublico } from '../api/publico'
import { Chevrones } from '../publico/Glifos'
import { CANTIDAD_MAXIMA_DE_COMBO, comboParaElCarrito, descripcionDelComponente, formatearPrecio } from './carrito'
import { useAccionesDelCarrito } from './CarritoContext'
import { ControlDeCantidad } from './ControlDeCantidad'

/** Cuántas imágenes de componentes muestra la tira de una tarjeta de combo. */
export const MINIATURAS_POR_COMBO = 3

const LADO_DE_LA_MINIATURA = 96

type ComponenteConImagen = ComponenteDeComboPublico & { imagenVersion: number }

function Miniatura({ componente }: { componente: ComponenteConImagen }) {
  const [fallo, setFallo] = useState(false)

  return (
    <span className="publico-combo-miniatura">
      {fallo ? (
        <Chevrones className="publico-articulo-marca" />
      ) : (
        <img
          src={urlDeLaImagenDelArticulo(componente.idArticulo, componente.imagenVersion)}
          alt=""
          width={LADO_DE_LA_MINIATURA}
          height={LADO_DE_LA_MINIATURA}
          loading="lazy"
          onError={() => setFallo(true)}
        />
      )}
    </span>
  )
}

function conImagen(componente: ComponenteDeComboPublico): componente is ComponenteConImagen {
  return componente.imagenVersion !== null
}

/** Tira con las imágenes de los primeros componentes que tienen una; sin ninguna, la marca de Ways. */
function MiniaturasDelCombo({ componentes }: { componentes: readonly ComponenteDeComboPublico[] }) {
  const conImagenes = componentes.filter(conImagen).slice(0, MINIATURAS_POR_COMBO)

  return (
    <div className="publico-combo-miniaturas">
      {conImagenes.length === 0 ? (
        <span className="publico-combo-miniatura">
          <Chevrones className="publico-articulo-marca" />
        </span>
      ) : (
        conImagenes.map((componente) => <Miniatura key={componente.idArticulo} componente={componente} />)
      )}
    </div>
  )
}

/** Lo que muestra una tarjeta de combo, sin carrito: imágenes, nombre, componentes y precio fijo. */
export function CuerpoDelCombo({ combo }: { combo: ComboPublico }) {
  return (
    <>
      <MiniaturasDelCombo componentes={combo.componentes} />
      <h3 className="publico-articulo-nombre">{combo.nombre}</h3>
      <ul className="publico-combo-componentes">
        {combo.componentes.map((componente) => (
          <li key={componente.idArticulo}>{descripcionDelComponente(componente)}</li>
        ))}
      </ul>
      <p className="publico-articulo-precio">
        <span className="publico-precio">{formatearPrecio(combo.precio)}</span>
      </p>
    </>
  )
}

/**
 * Tarjeta de un combo en la tienda. Igual que la de un artículo: recibe su cantidad en el carrito
 * (0 = no está), "Agregar" y el selector se reemplazan entre sí y el foco se lleva al control nuevo.
 * La cantidad de un combo es entera y llega hasta `CANTIDAD_MAXIMA_DE_COMBO`.
 */
export const TarjetaDeCombo = memo(function TarjetaDeCombo({ combo, cantidad }: { combo: ComboPublico; cantidad: number }) {
  const { agregarCombo, quitarCombo, cambiarCantidadCombo } = useAccionesDelCarrito()
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
    <li className="publico-articulo publico-combo">
      <CuerpoDelCombo combo={combo} />
      {cantidad === 0 ? (
        <button
          ref={botonAgregar}
          type="button"
          className="publico-boton publico-boton-acento publico-articulo-agregar"
          aria-label={`Agregar ${combo.nombre} al carrito`}
          onClick={() => {
            destinoDelFoco.current = 'mas'
            agregarCombo(comboParaElCarrito(combo))
          }}
        >
          Agregar
        </button>
      ) : (
        <ControlDeCantidad
          nombre={combo.nombre}
          unidadVenta="Unidad"
          cantidad={cantidad}
          maximo={CANTIDAD_MAXIMA_DE_COMBO}
          refMas={botonMas}
          alCambiar={(nueva) => cambiarCantidadCombo(combo.id, nueva)}
          alQuitar={() => {
            destinoDelFoco.current = 'agregar'
            quitarCombo(combo.id)
          }}
        />
      )}
    </li>
  )
})
