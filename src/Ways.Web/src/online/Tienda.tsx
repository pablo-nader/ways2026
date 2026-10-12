import { useRef, useState } from 'react'

import { CabeceraPublica } from '../publico/CabeceraPublica'
import { PorEstadoDeVitrina } from '../publico/PorEstadoDeVitrina'
import { useVitrinaPublica } from '../publico/useVitrinaPublica'
import { BotonCarrito } from './BotonCarrito'
import { CarritoProvider } from './CarritoProvider'
import { CatalogoDeLaTienda } from './CatalogoDeLaTienda'
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
            <CatalogoDeLaTienda />
            {carritoAbierto && <PanelCarrito alCerrar={cerrarCarrito} />}
          </div>
        )}
      </PorEstadoDeVitrina>
    </CarritoProvider>
  )
}
