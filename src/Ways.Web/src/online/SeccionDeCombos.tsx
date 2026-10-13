import type { ComboPublico } from '../api/publico'
import { TarjetaDeCombo } from './TarjetaDeCombo'

export const ID_DE_LA_SECCION_DE_COMBOS = 'combos'

/** Los combos de la tienda sobre la grilla de artículos. `cantidades` da la de cada combo que está en el carrito. */
export function SeccionDeCombos({
  combos,
  cantidades,
}: {
  combos: readonly ComboPublico[]
  cantidades: ReadonlyMap<number, number>
}) {
  return (
    <section id={ID_DE_LA_SECCION_DE_COMBOS} className="publico-combos" aria-labelledby="combos-titulo">
      <h2 id="combos-titulo" className="publico-titulo publico-combos-titulo">
        Combos
      </h2>
      <div className="publico-barra publico-barra-fina" aria-hidden="true" />
      <ul className="publico-grilla">
        {combos.map((combo) => (
          <TarjetaDeCombo key={combo.id} combo={combo} cantidad={cantidades.get(combo.id) ?? 0} />
        ))}
      </ul>
    </section>
  )
}
