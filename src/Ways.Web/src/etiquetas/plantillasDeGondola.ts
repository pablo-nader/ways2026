export type TonoDePlantilla = 'claro' | 'oscuro'

export type DecoracionDePlantilla = 'franja' | 'marco' | 'esquina' | 'liso'

export type PlantillaDeGondola = {
  readonly id: string
  readonly nombre: string
  readonly tono: TonoDePlantilla
  readonly decoracion: DecoracionDePlantilla
  /** Colores de la etiqueta: fondo, texto del nombre, precio, acento (franja, marca de oferta) y borde. */
  readonly colores: {
    readonly fondo: string
    readonly texto: string
    readonly precio: string
    readonly acento: string
    readonly textoSobreAcento: string
    readonly borde: string
  }
}

export const PLANTILLAS_DE_GONDOLA: readonly PlantillaDeGondola[] = [
  {
    id: 'almacen',
    nombre: 'Almacén',
    tono: 'claro',
    decoracion: 'franja',
    colores: { fondo: '#fffaf0', texto: '#2b1d0e', precio: '#c62828', acento: '#c62828', textoSobreAcento: '#ffffff', borde: '#e8d9b8' },
  },
  {
    id: 'verduleria',
    nombre: 'Verdulería',
    tono: 'claro',
    decoracion: 'marco',
    colores: { fondo: '#ffffff', texto: '#14351c', precio: '#1b7a34', acento: '#1b7a34', textoSobreAcento: '#ffffff', borde: '#1b7a34' },
  },
  {
    id: 'gondola-azul',
    nombre: 'Góndola azul',
    tono: 'claro',
    decoracion: 'esquina',
    colores: { fondo: '#f4f8ff', texto: '#0f2a52', precio: '#0b4fb3', acento: '#0b4fb3', textoSobreAcento: '#ffffff', borde: '#b9cff2' },
  },
  {
    id: 'mostaza',
    nombre: 'Mostaza',
    tono: 'claro',
    decoracion: 'liso',
    colores: { fondo: '#ffd23f', texto: '#1a1a1a', precio: '#1a1a1a', acento: '#1a1a1a', textoSobreAcento: '#ffd23f', borde: '#1a1a1a' },
  },
  {
    id: 'clasico',
    nombre: 'Clásico',
    tono: 'claro',
    decoracion: 'marco',
    colores: { fondo: '#ffffff', texto: '#111111', precio: '#111111', acento: '#111111', textoSobreAcento: '#ffffff', borde: '#111111' },
  },
  {
    id: 'noche',
    nombre: 'Noche dorada',
    tono: 'oscuro',
    decoracion: 'franja',
    colores: { fondo: '#14120f', texto: '#f6ecd2', precio: '#f2b705', acento: '#f2b705', textoSobreAcento: '#14120f', borde: '#4a3f1a' },
  },
  {
    id: 'carbon-rojo',
    nombre: 'Carbón rojo',
    tono: 'oscuro',
    decoracion: 'esquina',
    colores: { fondo: '#232326', texto: '#f5f5f5', precio: '#ff5a4d', acento: '#e53935', textoSobreAcento: '#ffffff', borde: '#e53935' },
  },
  {
    id: 'oceano',
    nombre: 'Océano',
    tono: 'oscuro',
    decoracion: 'marco',
    colores: { fondo: '#0b2239', texto: '#e6f4ff', precio: '#4fd8ff', acento: '#4fd8ff', textoSobreAcento: '#0b2239', borde: '#4fd8ff' },
  },
  {
    id: 'bosque',
    nombre: 'Bosque',
    tono: 'oscuro',
    decoracion: 'franja',
    colores: { fondo: '#10281a', texto: '#eaf7e4', precio: '#b6f23a', acento: '#b6f23a', textoSobreAcento: '#10281a', borde: '#2f5a3a' },
  },
  {
    id: 'violeta',
    nombre: 'Violeta',
    tono: 'oscuro',
    decoracion: 'liso',
    colores: { fondo: '#2a1745', texto: '#f4ecff', precio: '#ff8ad8', acento: '#ff8ad8', textoSobreAcento: '#2a1745', borde: '#6b3fa8' },
  },
]

export const PLANTILLA_POR_DEFECTO_DE_GONDOLA = PLANTILLAS_DE_GONDOLA[0]

export function plantillaDeGondolaPorId(id: string): PlantillaDeGondola {
  return PLANTILLAS_DE_GONDOLA.find((p) => p.id === id) ?? PLANTILLA_POR_DEFECTO_DE_GONDOLA
}

/** Logo de Ways cuyo trazo contrasta con el fondo de la plantilla (negro sobre claro, blanco sobre oscuro). */
export function rutaDelLogoDeGondola(tono: TonoDePlantilla): string {
  return tono === 'claro' ? '/marca/ways-logo-claro.png' : '/marca/ways-logo-oscuro.png'
}
