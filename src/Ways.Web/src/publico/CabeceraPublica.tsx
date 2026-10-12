import type { ReactNode } from 'react'

import { urlDelLogoPublico } from '../api/publico'
import type { VitrinaPublica } from '../api/tipos'

/** Alto máximo del logo en píxeles; coincide con `max-height` de `.publico-logo`. */
const ALTO_DEL_LOGO = 44

/** Barra superior del comercio: su logo (o su nombre) a la izquierda y la acción de la página a la derecha. */
export function CabeceraPublica({ vitrina, children }: { vitrina: VitrinaPublica; children: ReactNode }) {
  return (
    <header className="publico-cabecera">
      <a className="publico-cabecera-marca" href="/">
        {vitrina.tieneLogo ? (
          <img
            className="publico-logo"
            height={ALTO_DEL_LOGO}
            src={urlDelLogoPublico(vitrina.logoVersion)}
            alt={vitrina.nombre}
          />
        ) : (
          <span className="publico-titulo publico-cabecera-nombre">{vitrina.nombre}</span>
        )}
      </a>
      <div className="publico-cabecera-acciones">{children}</div>
    </header>
  )
}
