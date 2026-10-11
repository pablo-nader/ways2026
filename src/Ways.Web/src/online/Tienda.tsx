import { useRef, useState } from 'react'

import { CabeceraPublica } from '../publico/CabeceraPublica'
import { Chevrones } from '../publico/Glifos'
import { PiePublico } from '../publico/PiePublico'
import { PorEstadoDeVitrina } from '../publico/PorEstadoDeVitrina'
import { temaDeVitrina } from '../publico/tema'
import { useVitrinaPublica } from '../publico/useVitrinaPublica'
import { BotonCarrito } from './BotonCarrito'
import { CarritoProvider } from './CarritoProvider'
import { PanelCarrito } from './PanelCarrito'

export function Tienda() {
  const { resultado, recargar } = useVitrinaPublica('Tienda online')
  const [carritoAbierto, setCarritoAbierto] = useState(false)
  const botonDelCarrito = useRef<HTMLButtonElement>(null)

  const cerrarCarrito = () => {
    setCarritoAbierto(false)
    botonDelCarrito.current?.focus()
  }

  return (
    <CarritoProvider>
      <PorEstadoDeVitrina resultado={resultado} recargar={recargar}>
        {(vitrina) => (
          <div className="publico-pagina">
            <CabeceraPublica vitrina={vitrina}>
              <BotonCarrito
                ref={botonDelCarrito}
                abierto={carritoAbierto}
                alAlternar={() => (carritoAbierto ? cerrarCarrito() : setCarritoAbierto(true))}
              />
            </CabeceraPublica>
            <main className="publico-portada">
              <Chevrones className="publico-portada-chevrones" />
              <h1 className="publico-titulo publico-portada-titular">El catálogo se está preparando.</h1>
              <div className="publico-barra" aria-hidden="true" />
              <div className="publico-portada-acciones">
                <a className="publico-boton publico-boton-contorno" href="/">
                  Volver al inicio
                </a>
              </div>
            </main>
            <PiePublico tema={temaDeVitrina(vitrina.tema)} />
            {carritoAbierto && <PanelCarrito alCerrar={cerrarCarrito} />}
          </div>
        )}
      </PorEstadoDeVitrina>
    </CarritoProvider>
  )
}
