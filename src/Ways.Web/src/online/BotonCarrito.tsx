import { forwardRef } from 'react'

import { useCarrito } from './CarritoContext'
import { ID_DEL_PANEL_DEL_CARRITO } from './PanelCarrito'

/**
 * `cantidadQueSuma` no incluye las líneas de combo que ya no están disponibles. Si solo quedan de
 * esas, el carrito no está vacío (el panel las muestra) pero nada suma.
 */
function descripcionDelCarrito(cantidadQueSuma: number, hayLineas: boolean): string {
  if (cantidadQueSuma === 0) return hayLineas ? 'Carrito, sin ítems disponibles' : 'Carrito, vacío'

  return `Carrito, ${cantidadQueSuma} ${cantidadQueSuma === 1 ? 'ítem' : 'ítems'}`
}

export const BotonCarrito = forwardRef<HTMLButtonElement, { abierto: boolean; alAlternar: () => void }>(
  function BotonCarrito({ abierto, alAlternar }, ref) {
    const { cantidad, cantidadQueSuma } = useCarrito()

    return (
      <button
        ref={ref}
        type="button"
        className="publico-boton publico-boton-contorno publico-boton-chico"
        aria-label={descripcionDelCarrito(cantidadQueSuma, cantidad > 0)}
        aria-expanded={abierto}
        aria-controls={abierto ? ID_DEL_PANEL_DEL_CARRITO : undefined}
        onClick={alAlternar}
      >
        Carrito
        <span className="publico-carrito-cuenta" aria-hidden="true">
          {cantidadQueSuma}
        </span>
      </button>
    )
  },
)
