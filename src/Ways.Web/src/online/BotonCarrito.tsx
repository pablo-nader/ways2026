import { forwardRef } from 'react'

import { useCarrito } from './CarritoContext'
import { ID_DEL_PANEL_DEL_CARRITO } from './PanelCarrito'

function descripcionDelCarrito(cantidad: number): string {
  if (cantidad === 0) return 'Carrito, vacío'

  return `Carrito, ${cantidad} ${cantidad === 1 ? 'artículo' : 'artículos'}`
}

export const BotonCarrito = forwardRef<HTMLButtonElement, { abierto: boolean; alAlternar: () => void }>(
  function BotonCarrito({ abierto, alAlternar }, ref) {
    const { cantidad } = useCarrito()

    return (
      <button
        ref={ref}
        type="button"
        className="publico-boton publico-boton-contorno publico-boton-chico"
        aria-label={descripcionDelCarrito(cantidad)}
        aria-expanded={abierto}
        aria-controls={ID_DEL_PANEL_DEL_CARRITO}
        onClick={alAlternar}
      >
        Carrito
        <span className="publico-carrito-cuenta" aria-hidden="true">
          {cantidad}
        </span>
      </button>
    )
  },
)
