export type DireccionDeOrden = 'ascendente' | 'descendente'

/** `null` = orden original de las filas. */
export type EstadoDeOrden = { columna: string; direccion: DireccionDeOrden } | null

export type ValorOrdenable = string | number | boolean | Date | null | undefined

export type ColumnaOrdenable<T> = {
  /** Valor por el que se ordena la fila. Un número se compara como número, una fecha (`Date`) cronológicamente y el resto
   * como texto. Tiene que ser lo que la celda muestra: si la celda muestra la etiqueta de una referencia, devolver la
   * etiqueta y no el id; una referencia a un registro dado de baja se trata como vacía. */
  valor: (fila: T) => ValorOrdenable
  /** Comparador de filas para el caso en que el valor no alcanza. Solo se consulta cuando las dos filas tienen valor. */
  comparar?: (a: T, b: T) => number
}

export type ColumnasOrdenables<T> = Record<string, ColumnaOrdenable<T>>

const COLACION = new Intl.Collator('es', { sensitivity: 'base', numeric: true })

function esVacio(valor: ValorOrdenable): boolean {
  if (valor === null || valor === undefined) return true
  if (typeof valor === 'string') return valor.trim() === ''
  if (typeof valor === 'number') return Number.isNaN(valor)
  if (valor instanceof Date) return Number.isNaN(valor.getTime())

  return false
}

/** Compara dos valores que no están vacíos. */
export function compararValores(a: ValorOrdenable, b: ValorOrdenable): number {
  if (typeof a === 'number' && typeof b === 'number') return a - b
  if (a instanceof Date && b instanceof Date) return a.getTime() - b.getTime()
  if (typeof a === 'boolean' && typeof b === 'boolean') return Number(a) - Number(b)

  const textoA = a instanceof Date ? a.toISOString() : String(a)
  const textoB = b instanceof Date ? b.toISOString() : String(b)

  return COLACION.compare(textoA, textoB)
}

/** El clic en el encabezado recorre ascendente → descendente → orden original. */
export function siguienteOrden(actual: EstadoDeOrden, columna: string): EstadoDeOrden {
  if (actual === null || actual.columna !== columna) return { columna, direccion: 'ascendente' }
  if (actual.direccion === 'ascendente') return { columna, direccion: 'descendente' }

  return null
}

/**
 * Ordena una copia de las filas. Los vacíos van siempre al final, en cualquier dirección, y las filas que empatan
 * conservan su orden original. Sin orden (o con una columna que no existe) devuelve las filas tal cual. Sirve también
 * para ordenar dentro de cada grupo de una grilla agrupada: se la llama una vez por grupo.
 */
export function ordenarFilas<T>(
  filas: readonly T[],
  columnas: ColumnasOrdenables<T>,
  orden: EstadoDeOrden,
): readonly T[] {
  if (orden === null) return filas

  const columna = columnas[orden.columna]
  if (!columna) return filas

  const signo = orden.direccion === 'ascendente' ? 1 : -1
  const entradas = filas.map((fila, posicion) => ({ fila, posicion, valor: columna.valor(fila) }))

  entradas.sort((a, b) => {
    const vacioA = esVacio(a.valor)
    const vacioB = esVacio(b.valor)
    if (vacioA || vacioB) {
      if (vacioA && vacioB) return a.posicion - b.posicion

      return vacioA ? 1 : -1
    }

    const comparacion = columna.comparar ? columna.comparar(a.fila, b.fila) : compararValores(a.valor, b.valor)

    return comparacion === 0 ? a.posicion - b.posicion : signo * comparacion
  })

  return entradas.map((entrada) => entrada.fila)
}
