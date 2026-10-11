import type { Tema } from '../tema/tema'

/** Wordmark de Ways (no el logo del comercio): `claro` es el trazo negro para fondos claros y
 * `oscuro` el trazo blanco para fondos oscuros. */
export function MarcaWays({ tema, className }: { tema: Tema; className?: string }) {
  return (
    <img
      className={className}
      src={tema === 'claro' ? '/marca/ways-logo-claro.png' : '/marca/ways-logo-oscuro.png'}
      alt="Ways"
    />
  )
}
