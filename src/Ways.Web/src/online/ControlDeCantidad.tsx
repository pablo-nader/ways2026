import { useId, useState } from 'react'

import type { UnidadVenta } from '../api/tipos'
import { CANTIDAD_MAXIMA, formatearCantidad, normalizarCantidad, parsearCantidad, pasoDelBoton } from './carrito'
import { useCarrito } from './CarritoContext'

type PropsDeControl = { idArticulo: number; nombre: string; unidadVenta: UnidadVenta; cantidad: number }

function etiquetas(unidadVenta: UnidadVenta, nombre: string) {
  return unidadVenta === 'Unidad'
    ? { menos: `Quitar uno de ${nombre}`, mas: `Agregar uno más de ${nombre}` }
    : { menos: `Quitar medio kilo de ${nombre}`, mas: `Agregar medio kilo más de ${nombre}` }
}

/**
 * Selector de cantidad de una línea del carrito: − / campo / +. Los botones suman el paso de la
 * unidad (uno, o medio kilo); el campo admite lo que la unidad admite. Mientras se escribe algo
 * que no vale, el campo avisa y el carrito conserva la última cantidad buena; al salir del campo
 * vuelve a mostrarla. Bajar del mínimo quita la línea.
 */
export function ControlDeCantidad({ idArticulo, nombre, unidadVenta, cantidad }: PropsDeControl) {
  const { cambiarCantidad, quitar } = useCarrito()
  const [borrador, setBorrador] = useState<string | null>(null)
  const idDeAyuda = useId()
  const paso = pasoDelBoton(unidadVenta)
  const { menos, mas } = etiquetas(unidadVenta, nombre)
  const invalido = borrador !== null && parsearCantidad(unidadVenta, borrador) === null

  const restar = () => {
    const nueva = normalizarCantidad(cantidad - paso)
    if (nueva <= 0) quitar(idArticulo)
    else cambiarCantidad(idArticulo, nueva)
  }

  const escribir = (texto: string) => {
    setBorrador(texto)
    const escrita = parsearCantidad(unidadVenta, texto)
    if (escrita !== null) cambiarCantidad(idArticulo, escrita)
  }

  return (
    <div className="publico-cantidad">
      <button type="button" className="publico-cantidad-boton" aria-label={menos} onClick={restar}>
        <span aria-hidden="true">−</span>
      </button>
      <input
        className="publico-cantidad-campo"
        type="text"
        inputMode={unidadVenta === 'Unidad' ? 'numeric' : 'decimal'}
        autoComplete="off"
        aria-label={`Cantidad de ${nombre}`}
        aria-invalid={invalido}
        aria-describedby={invalido ? idDeAyuda : undefined}
        value={borrador ?? formatearCantidad(cantidad)}
        onChange={(evento) => escribir(evento.target.value)}
        onBlur={() => setBorrador(null)}
      />
      <button
        type="button"
        className="publico-cantidad-boton"
        aria-label={mas}
        disabled={cantidad >= CANTIDAD_MAXIMA}
        onClick={() => cambiarCantidad(idArticulo, normalizarCantidad(cantidad + paso))}
      >
        <span aria-hidden="true">+</span>
      </button>
      {invalido && (
        <p id={idDeAyuda} className="publico-cantidad-ayuda" role="alert">
          {unidadVenta === 'Unidad'
            ? `Ingresá una cantidad entera, hasta ${CANTIDAD_MAXIMA}.`
            : `Ingresá una cantidad mayor que 0, hasta ${CANTIDAD_MAXIMA} y con hasta 3 decimales.`}
        </p>
      )}
    </div>
  )
}
