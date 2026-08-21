// Etapa 18, Slice 1 (design.md:109-160, decisión 3): la ÚNICA fuente de milímetros de la etapa.
// Registro plano, sin funciones ni derivados almacenados: una fila de un futuro
// `formatos_etiqueta` (C1, OD3) mapea 1:1 sobre estos campos y el renderer no cambia.
// Ningún componente importa un descriptor por id literal — todos reciben
// `descriptores: readonly DescriptorDeFormato[]` como dato (la puerta a C1 se mantiene abierta).

export type CampoDeCelda = 'nombre' | 'codigo' | 'precioFinal' | 'precioOriginal' | 'unidadVenta' | 'nombreDeOferta'

export type DescriptorDeFormato = {
  readonly id: string
  readonly nombre: string
  readonly familia: 'etiqueta' | 'cartel'
  readonly paginaMm: { readonly ancho: number; readonly alto: number }
  readonly margenSuperiorMm: number
  readonly margenIzquierdoMm: number
  readonly columnas: number
  readonly filas: number
  readonly celdaMm: { readonly ancho: number; readonly alto: number }
  readonly medianilHorizontalMm: number
  readonly medianilVerticalMm: number
  /** Padding interior de la celda: la defensa contra el margen no imprimible del hardware,
   * nunca mueve la grilla (design.md:130-131). */
  readonly padExternoMm: number
  readonly campos: readonly CampoDeCelda[]
  /** Tamaño relativo del precio final dentro de la celda. */
  readonly escalaDePrecio: number
  /** La hoja real de la que salió la geometría. */
  readonly referencia: string
}

const CAMPOS_COMPLETOS: readonly CampoDeCelda[] = ['nombre', 'precioFinal', 'precioOriginal', 'codigo', 'unidadVenta', 'nombreDeOferta']

// Reconciliación T2 (tasks.md): la geometría sharpeada de `design.md:143-148` es la verdad de
// implementación — la etiqueta redondeada del proposal/spec queda como label de UI, nunca como
// una segunda tupla.
export const A4_3X8: DescriptorDeFormato = {
  id: 'A4-3x8',
  nombre: 'A4 · 3×8 (24 etiquetas, 70×37 mm)',
  familia: 'etiqueta',
  paginaMm: { ancho: 210, alto: 297 },
  margenSuperiorMm: 0.5,
  margenIzquierdoMm: 0,
  columnas: 3,
  filas: 8,
  celdaMm: { ancho: 70.0, alto: 37.0 },
  medianilHorizontalMm: 0,
  medianilVerticalMm: 0,
  padExternoMm: 5,
  campos: CAMPOS_COMPLETOS,
  escalaDePrecio: 1.4,
  referencia:
    'Hoja A4 autoadhesiva 24 et. 70×37 (Avery 3422 y equivalentes del mercado local). ' +
    'Tesela borde a borde: 3×70 = 210, 8×37 = 296 (+0.5 arriba y abajo).',
}

export const A4_2X7: DescriptorDeFormato = {
  id: 'A4-2x7',
  nombre: 'A4 · 2×7 (14 etiquetas, 99.1×38.1 mm)',
  familia: 'etiqueta',
  paginaMm: { ancho: 210, alto: 297 },
  margenSuperiorMm: 15.15,
  margenIzquierdoMm: 4.65,
  columnas: 2,
  filas: 7,
  celdaMm: { ancho: 99.1, alto: 38.1 },
  medianilHorizontalMm: 2.5,
  medianilVerticalMm: 0,
  padExternoMm: 3,
  campos: CAMPOS_COMPLETOS,
  escalaDePrecio: 1.6,
  referencia:
    'Hoja A4 autoadhesiva 14 et. 99.1×38.1 (Avery L7163 y equivalentes). ' +
    'Cierra exacto: 2×99.1 + 2×4.65 + 2.5 = 210, 7×38.1 + 2×15.15 = 297.',
}

export const CARTEL_A4: DescriptorDeFormato = {
  id: 'CARTEL-A4',
  nombre: 'Cartel A4 (hoja completa)',
  familia: 'cartel',
  paginaMm: { ancho: 210, alto: 297 },
  margenSuperiorMm: 10.0,
  margenIzquierdoMm: 10.0,
  columnas: 1,
  filas: 1,
  celdaMm: { ancho: 190.0, alto: 277.0 },
  medianilHorizontalMm: 0,
  medianilVerticalMm: 0,
  // Sin valor publicado por el design para carteles; el margen de 10 mm ya cubre el margen no
  // imprimible típico del hardware, así que no se suma un pad adicional (asunción documentada,
  // ver reporte de apply del slice 1).
  padExternoMm: 5,
  campos: CAMPOS_COMPLETOS,
  escalaDePrecio: 3.5,
  referencia: 'Hoja entera, margen 10 mm.',
}

export const CARTEL_A5: DescriptorDeFormato = {
  id: 'CARTEL-A5',
  nombre: 'Cartel A5 (media hoja, 2 por A4)',
  familia: 'cartel',
  paginaMm: { ancho: 210, alto: 297 },
  margenSuperiorMm: 10.0,
  margenIzquierdoMm: 10.0,
  columnas: 1,
  filas: 2,
  celdaMm: { ancho: 190.0, alto: 133.5 },
  medianilHorizontalMm: 0,
  medianilVerticalMm: 10.0,
  padExternoMm: 5,
  campos: CAMPOS_COMPLETOS,
  escalaDePrecio: 3.0,
  referencia: 'Media hoja: 2×133.5 + 10 + 2×10 = 297.',
}

/** Los cuatro descriptores fijos de la etapa (OD3: C3, plantillas fijas en código). Todo
 * consumidor recibe este arreglo como dato — nunca importa un descriptor por id literal. */
export const FORMATOS: readonly DescriptorDeFormato[] = [A4_3X8, A4_2X7, CARTEL_A4, CARTEL_A5]

// Derivados: SIEMPRE calculados, JAMÁS almacenados (decisión 3, mutation targets 4/5).
export const celdasPorHoja = (d: DescriptorDeFormato): number => d.columnas * d.filas
export const contarHojas = (celdas: number, d: DescriptorDeFormato): number => Math.ceil(celdas / celdasPorHoja(d))
