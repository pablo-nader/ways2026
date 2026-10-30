/**
 * Consulta de un padrón que filtra, ordena y pagina en el servidor (clientes, proveedores, personal, usuarios): los
 * filtros por columna, el orden y la página viajan como parámetros de la misma consulta. Ordenar o filtrar solo las filas
 * ya cargadas mentiría, porque el listado trae una página.
 */

export type DireccionDePadron = 'ascendente' | 'descendente'

/** `null` = el orden de siempre del servidor. Las columnas son las claves de `orden` de cada listado. */
export type OrdenDePadron<C extends string> = { columna: C; direccion: DireccionDePadron } | null

/** Un filtro vacío (`''` o `null`) no filtra y no viaja. Los rangos de importe son números; el resto, texto. */
export type ValorDeFiltro = string | number | null

export type FiltrosDePadron = Record<string, ValorDeFiltro>

export type ConsultaDePadron<F extends FiltrosDePadron, C extends string> = {
  filtros: F
  orden: OrdenDePadron<C>
  pagina: number
  tamanio: number
}

export const TAMANIO_DE_PAGINA_POR_DEFECTO = 25

export const TAMANIOS_DE_PAGINA_DE_PADRON = [25, 50, 100] as const

/**
 * Query de un listado de padrón. Omite todo lo que el servidor ya asume: un filtro vacío, el orden de siempre, la página 1
 * y los 25 por página. Los textos viajan sin espacios en los bordes. `orden` y `direccion` viajan siempre juntos.
 */
export function construirQueryDePadron<F extends FiltrosDePadron, C extends string>(
  consulta: ConsultaDePadron<F, C>,
): string {
  const parametros = new URLSearchParams()

  for (const [clave, valor] of Object.entries(consulta.filtros)) {
    if (valor === null) continue

    const texto = typeof valor === 'string' ? valor.trim() : String(valor)
    if (texto !== '') parametros.set(clave, texto)
  }

  if (consulta.orden !== null) {
    parametros.set('orden', consulta.orden.columna)
    parametros.set('direccion', consulta.orden.direccion)
  }

  if (consulta.pagina !== 1) parametros.set('pagina', String(consulta.pagina))
  if (consulta.tamanio !== TAMANIO_DE_PAGINA_POR_DEFECTO) parametros.set('tamanio', String(consulta.tamanio))

  const cadena = parametros.toString()

  return cadena === '' ? '' : `?${cadena}`
}

/** Hay algún filtro puesto (el orden y el tamaño de página son preferencias de vista, no filtros). */
export function hayFiltrosEnPadron(filtros: FiltrosDePadron): boolean {
  return Object.values(filtros).some((valor) => valor !== null && String(valor).trim() !== '')
}

export function ultimaPaginaDePadron(total: number, tamanio: number): number {
  return Math.max(1, Math.ceil(total / tamanio))
}
