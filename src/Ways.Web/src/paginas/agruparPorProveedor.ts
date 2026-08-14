import type { FilaDeReposicion } from '../api/tipos'

/** Un bucket de `Reposicion.tsx`: todas las filas consecutivas que comparten `idProveedor`. */
export type GrupoDeReposicion = {
  idProveedor: number | null
  proveedor: string | null
  filas: FilaDeReposicion[]
}

/**
 * Agrupa las filas de `GET /api/reportes/stock/reposicion` por proveedor habitual — un FOLD sobre
 * la lista YA ORDENADA que devuelve el servidor (design decisión 4), NUNCA un sort del lado del
 * cliente. El servidor ya garantiza un único bucket "Sin proveedor" al final (proveedor
 * soft-deleted proyecta `idProveedor: null` y ordena por presencia primero — orchestrator
 * decision 12, tasks.md stage-13), así que agrupar filas CONSECUTIVAS que comparten `idProveedor`
 * alcanza: nunca puede haber dos runs separados del mismo proveedor en la lista que llega acá.
 */
export function agruparPorProveedor(filas: FilaDeReposicion[]): GrupoDeReposicion[] {
  const grupos: GrupoDeReposicion[] = []

  for (const fila of filas) {
    const grupoActual = grupos[grupos.length - 1]
    if (grupoActual && grupoActual.idProveedor === fila.idProveedor) {
      grupoActual.filas.push(fila)
      continue
    }
    grupos.push({ idProveedor: fila.idProveedor, proveedor: fila.proveedor, filas: [fila] })
  }

  return grupos
}
