import type { ComboPublico } from '../api/publico'
import { PREFIJO_ONLINE } from '../config/rutasPublicas'
import { ID_DE_LA_SECCION_DE_COMBOS } from '../online/SeccionDeCombos'
import { CuerpoDelCombo } from '../online/TarjetaDeCombo'

/** Cuántos combos muestra la tira de la landing. */
export const CANTIDAD_DE_COMBOS_EN_LA_LANDING = 4

/** Un combo de la tira: enlace a su sección de la tienda. Aquí no hay carrito. */
function TarjetaDeComboDestacado({ combo }: { combo: ComboPublico }) {
  return (
    <li className="publico-articulo publico-destacado publico-combo">
      <a className="publico-destacado-enlace" href={`${PREFIJO_ONLINE}#${ID_DE_LA_SECCION_DE_COMBOS}`}>
        <CuerpoDelCombo combo={combo} />
      </a>
    </li>
  )
}

/** Tira de combos de la tienda. Sin combos la sección no se dibuja. */
export function CombosDeLaLanding({ combos }: { combos: readonly ComboPublico[] }) {
  if (combos.length === 0) return null

  return (
    <section className="publico-seccion" aria-labelledby="combos-landing-titulo">
      <h2 id="combos-landing-titulo" className="publico-titulo publico-seccion-titulo">
        Combos
      </h2>
      <div className="publico-barra publico-barra-fina" aria-hidden="true" />
      <ul className="publico-grilla publico-grilla-compacta">
        {combos.slice(0, CANTIDAD_DE_COMBOS_EN_LA_LANDING).map((combo) => (
          <TarjetaDeComboDestacado key={combo.id} combo={combo} />
        ))}
      </ul>
      <div className="publico-ver-mas">
        <a className="publico-boton publico-boton-contorno" href={`${PREFIJO_ONLINE}#${ID_DE_LA_SECCION_DE_COMBOS}`}>
          Ver todos los combos
        </a>
      </div>
    </section>
  )
}
