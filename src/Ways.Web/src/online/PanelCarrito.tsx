import { useEffect, useRef } from 'react'

import { useCarrito } from './CarritoContext'

export const ID_DEL_PANEL_DEL_CARRITO = 'panel-carrito'

/** Panel lateral del carrito. Al abrirse lleva el foco al botón de cerrar; Escape lo cierra. */
export function PanelCarrito({ alCerrar }: { alCerrar: () => void }) {
  const { cantidad } = useCarrito()
  const cerrar = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    cerrar.current?.focus()
  }, [])

  return (
    <div className="publico-carrito-capa">
      <div className="publico-carrito-velo" aria-hidden="true" onClick={alCerrar} />
      <aside
        id={ID_DEL_PANEL_DEL_CARRITO}
        className="publico-carrito"
        role="dialog"
        aria-labelledby="carrito-titulo"
        onKeyDown={(evento) => {
          if (evento.key === 'Escape') alCerrar()
        }}
      >
        <div className="publico-carrito-encabezado">
          <h2 id="carrito-titulo" className="publico-titulo publico-carrito-titulo">
            Carrito
          </h2>
          <button ref={cerrar} type="button" className="publico-boton publico-boton-contorno publico-boton-chico" onClick={alCerrar}>
            Cerrar
          </button>
        </div>
        {cantidad === 0 && <p className="publico-carrito-vacio">Tu carrito está vacío</p>}
      </aside>
    </div>
  )
}
