import type { FilaDeReposicion, LineaDeOrdenSolicitada } from '../api/tipos'

/**
 * Mapea las filas de un grupo de `Reposicion.tsx` a las líneas de una nueva orden de compra
 * (stage-16-ordenes-de-compra, Slice 6; design.md: "posting `filas.filter(f => f.sugerido !==
 * null)` mapped `{IdArticulo, Sugerido} → {IdArticulo, CantidadPedida}`", decisión 16;
 * ordenes-de-compra/spec.md: "Pre-Load From The Reposición List Is Read-Only And Unidirectional").
 *
 * Filas con `sugerido = null` quedan EXCLUIDAS, nunca defaulteadas a `cantidadPedida = 0`
 * (mutation target #34c, parte 3) — el mapeo entero es client-side y unidireccional: la reposición
 * nunca aprende del resultado de la OC. Sin `costoUnitarioEstimado`: la reposición no trae precio,
 * la OC lo deja sin cotizar (`null`, jamás `0` — `dto-contract-honesty`).
 */
export function itemsDeOrdenDesdeFilasDeReposicion(filas: FilaDeReposicion[]): LineaDeOrdenSolicitada[] {
  return filas
    .filter((fila) => fila.sugerido !== null)
    .map((fila) => ({
      idArticulo: fila.idArticulo,
      descripcion: fila.articulo,
      cantidadPedida: fila.sugerido as number,
      costoUnitarioEstimado: null,
    }))
}
