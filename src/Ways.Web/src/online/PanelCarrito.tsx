import { useEffect, useRef, useState } from 'react'

import { formatearPrecio, formatearPrecioUnitario, totalDeLinea } from './carrito'
import { useCarrito } from './CarritoContext'
import { ControlDeCantidad } from './ControlDeCantidad'

export const ID_DEL_PANEL_DEL_CARRITO = 'panel-carrito'

const SELECTOR_DE_FOCUSABLES = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'

/**
 * Panel lateral modal del carrito (`role="dialog"` con `aria-modal`). Mientras está abierto:
 * - lleva el foco al botón de cerrar y el scroll de la página queda bloqueado (se restaura al cerrar);
 * - Tab y Shift+Tab circulan dentro del panel, aunque el foco esté fuera de él;
 * - Escape lo cierra desde cualquier punto del documento, y el clic en el velo también.
 * "Vaciar carrito" pide confirmación en línea: no es una compuerta modal, el resto del panel sigue activo.
 * No marca el resto de la página como inerte: el aislamiento para lectores de pantalla descansa en
 * `aria-modal`. Devolver el foco al botón del carrito es tarea de quien lo abre (`alCerrar`).
 */
export function PanelCarrito({ alCerrar }: { alCerrar: () => void }) {
  const { items, cantidad, total, quitar, vaciar } = useCarrito()
  const [confirmandoVaciado, setConfirmandoVaciado] = useState(false)
  const cerrar = useRef<HTMLButtonElement>(null)
  const panel = useRef<HTMLElement>(null)
  const cancelarVaciado = useRef<HTMLButtonElement>(null)
  const pedirVaciado = useRef<HTMLButtonElement>(null)
  const focoTrasElCambio = useRef<'cancelar' | 'pedir' | null>(null)

  useEffect(() => {
    const destino = focoTrasElCambio.current === 'cancelar' ? cancelarVaciado : pedirVaciado
    if (focoTrasElCambio.current !== null) destino.current?.focus()
    focoTrasElCambio.current = null
  }, [confirmandoVaciado])

  useEffect(() => {
    cerrar.current?.focus()
  }, [])

  useEffect(() => {
    const overflowPrevio = document.body.style.overflow
    document.body.style.overflow = 'hidden'

    return () => {
      document.body.style.overflow = overflowPrevio
    }
  }, [])

  useEffect(() => {
    const alPresionarTecla = (evento: KeyboardEvent) => {
      if (evento.key === 'Escape') {
        alCerrar()
        return
      }
      if (evento.key !== 'Tab' || panel.current === null) return

      const focusables = Array.from(panel.current.querySelectorAll<HTMLElement>(SELECTOR_DE_FOCUSABLES))
      if (focusables.length === 0) return

      const primero = focusables[0]
      const ultimo = focusables[focusables.length - 1]
      const activo = document.activeElement
      const dentro = activo !== null && panel.current.contains(activo)

      if (evento.shiftKey && (!dentro || activo === primero)) {
        evento.preventDefault()
        ultimo.focus()
      } else if (!evento.shiftKey && (!dentro || activo === ultimo)) {
        evento.preventDefault()
        primero.focus()
      }
    }

    document.addEventListener('keydown', alPresionarTecla)

    return () => document.removeEventListener('keydown', alPresionarTecla)
  }, [alCerrar])

  return (
    <div className="publico-carrito-capa">
      <div className="publico-carrito-velo" aria-hidden="true" onClick={alCerrar} />
      <aside
        ref={panel}
        id={ID_DEL_PANEL_DEL_CARRITO}
        className="publico-carrito"
        role="dialog"
        aria-modal="true"
        aria-labelledby="carrito-titulo"
      >
        <div className="publico-carrito-encabezado">
          <h2 id="carrito-titulo" className="publico-titulo publico-carrito-titulo">
            Carrito
          </h2>
          <button ref={cerrar} type="button" className="publico-boton publico-boton-contorno publico-boton-chico" onClick={alCerrar}>
            Cerrar
          </button>
        </div>
        {cantidad === 0 ? (
          <p className="publico-carrito-vacio">Tu carrito está vacío</p>
        ) : (
          <>
            <ul className="publico-carrito-lineas">
              {items.map((item) => (
                <li key={item.idArticulo} className="publico-carrito-linea">
                  <p className="publico-carrito-nombre">{item.nombre}</p>
                  <p className="publico-carrito-unitario">{formatearPrecioUnitario(item.precio, item.unidadVenta)}</p>
                  <ControlDeCantidad
                    idArticulo={item.idArticulo}
                    nombre={item.nombre}
                    unidadVenta={item.unidadVenta}
                    cantidad={item.cantidad}
                  />
                  <p className="publico-carrito-subtotal">{formatearPrecio(totalDeLinea(item))}</p>
                  <button
                    type="button"
                    className="publico-boton publico-boton-contorno publico-boton-chico"
                    aria-label={`Quitar ${item.nombre} del carrito`}
                    onClick={() => quitar(item.idArticulo)}
                  >
                    Quitar
                  </button>
                </li>
              ))}
            </ul>
            <div className="publico-carrito-pie">
              <p className="publico-carrito-total">
                <span>Total</span>
                <span className="publico-precio">{formatearPrecio(total)}</span>
              </p>
              <button type="button" className="publico-boton publico-boton-acento" disabled aria-describedby="carrito-ayuda-pedido">
                Finalizar pedido
              </button>
              <p id="carrito-ayuda-pedido" className="publico-carrito-ayuda">
                Muy pronto vas a poder enviar tu pedido desde acá.
              </p>
              {confirmandoVaciado ? (
                <div className="publico-carrito-confirmacion" role="group" aria-label="Confirmar que se vacía el carrito">
                  <p>¿Vaciar el carrito?</p>
                  <button
                    type="button"
                    className="publico-boton publico-boton-contorno publico-boton-chico"
                    onClick={() => {
                      vaciar()
                      cerrar.current?.focus()
                    }}
                  >
                    Sí, vaciar
                  </button>
                  <button
                    ref={cancelarVaciado}
                    type="button"
                    className="publico-boton publico-boton-contorno publico-boton-chico"
                    onClick={() => {
                      focoTrasElCambio.current = 'pedir'
                      setConfirmandoVaciado(false)
                    }}
                  >
                    Cancelar
                  </button>
                </div>
              ) : (
                <button
                  ref={pedirVaciado}
                  type="button"
                  className="publico-boton publico-boton-contorno publico-boton-chico"
                  onClick={() => {
                    focoTrasElCambio.current = 'cancelar'
                    setConfirmandoVaciado(true)
                  }}
                >
                  Vaciar carrito
                </button>
              )}
            </div>
          </>
        )}
      </aside>
    </div>
  )
}
