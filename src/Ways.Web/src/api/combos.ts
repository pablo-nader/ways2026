/**
 * Combos en el carrito del POS: la fórmula de línea con el ahorro de un combo y el rótulo de los combos
 * aplicados. Puro y sin DOM. La fórmula es la única que comparten la vista previa del carrito (online y
 * offline) y el comprobante sintético de una venta offline, para que lo mostrado, lo encolado y lo
 * impreso no puedan diverger; el servidor (`CalculadorDeTotales`) recalcula en decimal y es quien manda.
 */
import { calcularTotalesDeLinea, type TotalesDeLinea } from './ajusteManual'
import { api } from './cliente'
import type { ComboAplicadoEnPrevia, ComboCargableDeApi, ComboDeLinea, ComboListado } from './tipos'

/**
 * El combo de una línea solo vale si no consumió más que su `cantidad` (unidades, o kilos si el artículo se vende
 * por peso; el servidor valida lo mismo
 * en una línea offline, `combo_offline_invalido`). Con la cantidad ya reducida y la
 * resolución todavía sin refrescar (o un borrador restaurado), el combo viejo consume más de lo que hay:
 * se ignora entero hasta que la vista previa se resuelva de nuevo, en vez de acotar las unidades y
 * seguir descontando un ahorro calculado para otra cantidad. `CalculadorDeTotales` nunca recibe esa
 * entrada, así que ignorar el combo no se aparta de la fórmula del servidor.
 */
export function comboVigenteDeLinea<T extends Pick<ComboDeLinea, 'unidadesConsumidas'>>(cantidad: number, combo: T | null | undefined): T | null {
  return combo && combo.unidadesConsumidas <= cantidad ? combo : null
}

/**
 * Totales de una línea con el combo que la consumió, si lo hay. No tiene fórmula propia: descarta el combo
 * que ya no vale (`comboVigenteDeLinea`) y delega en `calcularTotalesDeLinea`, que es la única fórmula de
 * línea (neto en dos términos al peso entero, ajuste manual sobre ese neto).
 */
export function calcularTotalesDeLineaConCombo(params: {
  cantidad: number
  precioOriginal: number
  descuentoUnitario: number
  porcentaje: number | null | undefined
  combo?: Pick<ComboDeLinea, 'unidadesConsumidas' | 'ahorro'> | null
  /** Descuento de oferta de LÍNEA de una oferta "solo múltiplos" (`ResultadoDeResolucion.descuentoDeLinea`). */
  descuentoDeLinea?: number
}): TotalesDeLinea {
  const { combo: comboCrudo, ...resto } = params
  const combo = comboVigenteDeLinea(params.cantidad, comboCrudo)
  return calcularTotalesDeLinea({ ...resto, unidadesConsumidasPorCombo: combo?.unidadesConsumidas, ahorroDeCombo: combo?.ahorro })
}

/** Rótulo de un combo aplicado: `Pancho + Coca × 2`; sin el sufijo cuando se aplicó una sola vez. */
export function rotuloDeComboAplicado(combo: Pick<ComboAplicadoEnPrevia, 'nombre' | 'veces'>): string {
  return combo.veces > 1 ? `${combo.nombre} × ${combo.veces}` : combo.nombre
}

/** Lecturas de combos que usa el POS: ambas son de `OperacionDePos` (un cajero las puede llamar). El ABM es de otra pantalla. */
export const clienteDeCombos = {
  /** `GET /api/combos`: los combos vivos del tenant, también los inactivos y los de otras empresas; quien llama filtra. */
  listar: () => api.get<ComboListado[]>('/combos'),
  /** `GET /api/combos/{id}/cargable`: el combo con los artículos que completan cada componente. 404 si no existe, está
   * inactivo, dado de baja o es de una empresa distinta de `idEmpresa` (`null` ⇒ solo vale uno de todo el tenant). */
  cargable: (id: number, idEmpresa: number | null) =>
    api.get<ComboCargableDeApi>(`/combos/${id}/cargable${idEmpresa === null ? '' : `?idEmpresa=${idEmpresa}`}`),
}
