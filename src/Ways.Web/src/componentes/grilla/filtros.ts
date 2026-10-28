export type OpcionDeFiltro = { valor: string; etiqueta: string }

export type DefinicionDeFiltro = {
  /** Nombre de la columna: arma el nombre accesible del control ("Filtrar por nombre"). */
  etiqueta: string
  /** Con opciones el filtro es un select y la fila tiene que tener exactamente ese valor; sin ellas es un campo de
   * texto y alcanza con que lo contenga. */
  opciones?: readonly OpcionDeFiltro[]
}

export type ColumnaFiltrable<T> = DefinicionDeFiltro & {
  /** Lo que la celda muestra (la etiqueta de una referencia, no su id). Vacío si no hay valor o si la referencia apunta a
   * un registro dado de baja. */
  valor: (fila: T) => string | number | null | undefined
}

export type ColumnasFiltrables<T> = Record<string, ColumnaFiltrable<T>>

/** Texto escrito o elegido por columna; una columna ausente o vacía no filtra. */
export type ValoresDeFiltro = Record<string, string>

/** Minúsculas y sin acentos, para comparar "Cafe", "café" y "CAFÉ" como lo mismo. */
export function normalizarTexto(texto: string): string {
  return texto
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase()
    .trim()
}

function coincideConUnFiltro(mostrado: string, filtro: string, conOpciones: boolean): boolean {
  return conOpciones ? mostrado === filtro : normalizarTexto(mostrado).includes(normalizarTexto(filtro))
}

/** Las condiciones de todas las columnas se combinan con Y. */
export function filtrarFilas<T>(
  filas: readonly T[],
  columnas: ColumnasFiltrables<T>,
  filtros: ValoresDeFiltro,
): readonly T[] {
  const activos = Object.entries(filtros).filter(([clave, valor]) => valor.trim() !== '' && columnas[clave])
  if (activos.length === 0) return filas

  return filas.filter((fila) =>
    activos.every(([clave, filtro]) => {
      const columna = columnas[clave]

      return coincideConUnFiltro(String(columna.valor(fila) ?? ''), filtro, columna.opciones !== undefined)
    }),
  )
}

export function hayFiltrosActivos(filtros: ValoresDeFiltro): boolean {
  return Object.values(filtros).some((valor) => valor.trim() !== '')
}
