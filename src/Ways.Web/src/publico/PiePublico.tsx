import { hrefDeAdmin } from '../config/rutasPublicas'
import type { Tema } from '../tema/tema'
import { Chevrones } from './Glifos'
import { MarcaWays } from './MarcaWays'

export function PiePublico({ tema }: { tema: Tema }) {
  return (
    <footer className="publico-pie">
      <p className="publico-pie-marca">
        <span>Hecho con</span>
        <MarcaWays tema={tema} className="publico-pie-wordmark" />
        <Chevrones className="publico-pie-chevrones" />
      </p>
      <a className="publico-pie-acceso" href={hrefDeAdmin('/login')}>
        Acceso para el comercio
      </a>
    </footer>
  )
}
