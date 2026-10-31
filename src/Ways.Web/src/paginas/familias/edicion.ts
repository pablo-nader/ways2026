import type { EdicionFamilia } from '../../api/tipos'
import type { FormularioDeFamilia } from './ModalDeFamilia'

/** Texto de ayuda de la marca "Permite desglosar costo", el mismo al crear y al editar. */
export const AYUDA_DE_DESGLOSAR_COSTO =
  'Cada artículo puede tener su propio costo de lista e impuesto interno; el costo nominal es el mismo para todos.'

export const ETIQUETA_DE_DESGLOSAR_COSTO = 'Permite desglosar costo'

/**
 * El cuerpo del PUT de una familia. `permiteDesglosarCosto` viaja solo si el usuario cambió la casilla: mandar `false`
 * toma un lock exclusivo en el servidor aunque la marca ya estuviera apagada, y ausente la conserva.
 */
export function cuerpoDeEdicionDeFamilia(f: FormularioDeFamilia): EdicionFamilia {
  const base = { nombre: f.nombre.trim(), activo: f.activo }
  return f.permiteDesglosarCosto === f.permiteDesglosarCostoInicial ? base : { ...base, permiteDesglosarCosto: f.permiteDesglosarCosto }
}
