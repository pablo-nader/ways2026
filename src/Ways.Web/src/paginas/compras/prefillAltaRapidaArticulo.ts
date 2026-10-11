import { redondearImporte } from '../../formato/importes'

/**
 * El costo de la línea admite 4 decimales pero `articulos.costo_lista` es `numeric(14,2)`: se
 * redondea al precargar el alta rápida para que lo que se ve en el campo sea lo que se guarda.
 */
export function costoDeListaDeAltaRapida(costoDeLaLinea: number | null): number | null {
  return costoDeLaLinea === null ? null : redondearImporte(costoDeLaLinea, 2)
}
