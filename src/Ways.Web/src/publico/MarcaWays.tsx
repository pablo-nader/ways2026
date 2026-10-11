import type { Tema } from '../tema/tema'

const ANCHO_DEL_PNG = 1800
const ALTO_DEL_PNG = 981

/** Wordmark de Ways (no el logo del comercio): `claro` es el trazo negro para fondos claros y
 * `oscuro` el trazo blanco para fondos oscuros. `alto` es el alto en píxeles con el que se muestra: con él
 * se reserva el ancho antes de que cargue la imagen y la página no se corre. */
export function MarcaWays({ tema, alto, className }: { tema: Tema; alto: number; className?: string }) {
  return (
    <img
      className={className}
      width={Math.round((alto * ANCHO_DEL_PNG) / ALTO_DEL_PNG)}
      height={alto}
      src={tema === 'claro' ? '/marca/ways-logo-claro.png' : '/marca/ways-logo-oscuro.png'}
      alt="Ways"
    />
  )
}
