/** Hoja A4 apaisada: las etiquetas de góndola siempre se imprimen así. */
export const HOJA_APAISADA_MM = { ancho: 297, alto: 210 } as const
export const MARGEN_DE_HOJA_MM = 8
export const SEPARACION_DE_CELDAS_MM = 3
// Holgura para que el redondeo del navegador no desborde el área imprimible y agregue una página.
export const HOLGURA_DE_HOJA_MM = 1

export const ETIQUETAS_POR_HOJA = [2, 4, 8, 16] as const
export type EtiquetasPorHoja = (typeof ETIQUETAS_POR_HOJA)[number]

export type DisposicionDeHoja = {
  columnas: number
  filas: number
  celdaAnchoMm: number
  celdaAltoMm: number
}

const GRILLAS: Record<EtiquetasPorHoja, { columnas: number; filas: number }> = {
  2: { columnas: 2, filas: 1 },
  4: { columnas: 2, filas: 2 },
  8: { columnas: 4, filas: 2 },
  16: { columnas: 4, filas: 4 },
}

export function esEtiquetasPorHoja(valor: number): valor is EtiquetasPorHoja {
  return (ETIQUETAS_POR_HOJA as readonly number[]).includes(valor)
}

export function disposicionDeHoja(porHoja: EtiquetasPorHoja): DisposicionDeHoja {
  const { columnas, filas } = GRILLAS[porHoja]
  const util = {
    ancho: HOJA_APAISADA_MM.ancho - 2 * MARGEN_DE_HOJA_MM - HOLGURA_DE_HOJA_MM,
    alto: HOJA_APAISADA_MM.alto - 2 * MARGEN_DE_HOJA_MM - HOLGURA_DE_HOJA_MM,
  }
  return {
    columnas,
    filas,
    celdaAnchoMm: (util.ancho - SEPARACION_DE_CELDAS_MM * (columnas - 1)) / columnas,
    celdaAltoMm: (util.alto - SEPARACION_DE_CELDAS_MM * (filas - 1)) / filas,
  }
}

/** Reparte los elementos en hojas de `porHoja`, en orden y sin repetir: cada artículo se imprime una vez. */
export function dividirEnHojas<T>(elementos: readonly T[], porHoja: EtiquetasPorHoja): T[][] {
  const hojas: T[][] = []
  for (let desde = 0; desde < elementos.length; desde += porHoja) {
    hojas.push(elementos.slice(desde, desde + porHoja))
  }
  return hojas
}
