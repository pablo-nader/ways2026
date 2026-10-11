import type { ListaPrecioListado } from '../../api/tipos'

/** Listas manuales (`Fija`) antes que las derivadas, y por nombre dentro de cada grupo. No muta la entrada. */
export function ordenarListasParaTabla(listas: ListaPrecioListado[]): ListaPrecioListado[] {
  const rango = (lista: ListaPrecioListado) => (lista.modo === 'Fija' ? 0 : 1)
  return [...listas].sort(
    (a, b) => rango(a) - rango(b) || a.nombre.localeCompare(b.nombre, 'es', { sensitivity: 'base' }),
  )
}
