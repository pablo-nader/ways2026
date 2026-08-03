import type { CatalogoListado } from '../api/tipos'

/** Resuelve la etiqueta de un valor seleccionado que ya no está entre las opciones vigentes
 * del select (p. ej. la lista base de una `Derivada` fue desactivada después, o quedó fuera
 * de `items` porque "Incluir inactivos" está apagado). Evita que el `<select>` controlado
 * quede con un valor sin `<option>` que lo respalde. */
export function etiquetaParaValorFaltante(valorActual: string, items: unknown[]): string {
  const item = (items as CatalogoListado[]).find((i) => String(i.id) === valorActual)
  if (!item) return `Opción no disponible (${valorActual})`
  return item.activo ? item.nombre : `${item.nombre} (inactiva)`
}
