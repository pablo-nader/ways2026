import type { ReactNode } from 'react'

import { urlDelLogoPublico } from '../api/publico'
import type { VitrinaPublica } from '../api/tipos'

/** Barra superior del comercio: su logo (o su nombre) a la izquierda y la acción de la página a la derecha. */
export function CabeceraPublica({ vitrina, children }: { vitrina: VitrinaPublica; children: ReactNode }) {
  return (
    <header className="publico-cabecera">
      <a className="publico-cabecera-marca" href="/">
        {vitrina.tieneLogo ? (
          <img className="publico-logo" src={urlDelLogoPublico(vitrina.logoVersion)} alt={vitrina.nombre} />
        ) : (
          <span className="publico-titulo publico-cabecera-nombre">{vitrina.nombre}</span>
        )}
      </a>
      <div className="publico-cabecera-acciones">{children}</div>
    </header>
  )
}
