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

// Paleta de la marca Ways (tema.css): naranja #f7941d, sus variantes #e8830c, #fdebd3, #9a5200, #3a2606, #f9ab4a y el gris #c4c4ca, más neutros.
export const PLANTILLAS_DE_GONDOLA: readonly PlantillaDeGondola[] = [
  {
    id: 'naranja',
    nombre: 'Naranja Ways',
    tono: 'claro',
    decoracion: 'franja',
    colores: { fondo: '#ffffff', texto: '#1c1c1f', precio: '#e8830c', acento: '#f7941d', textoSobreAcento: '#1c1c1f', borde: '#c4c4ca' },
  },
  {
    id: 'marco-naranja',
    nombre: 'Marco naranja',
    tono: 'claro',
    decoracion: 'marco',
    colores: { fondo: '#ffffff', texto: '#1c1c1f', precio: '#e8830c', acento: '#9a5200', textoSobreAcento: '#ffffff', borde: '#f7941d' },
  },
  {
    id: 'esquina-naranja',
    nombre: 'Esquina naranja',
    tono: 'claro',
    decoracion: 'esquina',
    colores: { fondo: '#ffffff', texto: '#1c1c1f', precio: '#1c1c1f', acento: '#f7941d', textoSobreAcento: '#1c1c1f', borde: '#c4c4ca' },
  },
  {
    id: 'liso',
    nombre: 'Liso',
    tono: 'claro',
    decoracion: 'liso',
    colores: { fondo: '#ffffff', texto: '#1c1c1f', precio: '#1c1c1f', acento: '#f7941d', textoSobreAcento: '#1c1c1f', borde: '#c4c4ca' },
  },
  {
    id: 'marco-gris',
    nombre: 'Marco gris',
    tono: 'claro',
    decoracion: 'marco',
    colores: { fondo: '#ffffff', texto: '#1c1c1f', precio: '#9a5200', acento: '#f7941d', textoSobreAcento: '#1c1c1f', borde: '#c4c4ca' },
  },
  {
    id: 'noche',
    nombre: 'Noche Ways',
    tono: 'oscuro',
    decoracion: 'franja',
    colores: { fondo: '#111113', texto: '#f4f4f6', precio: '#f7941d', acento: '#f7941d', textoSobreAcento: '#111113', borde: '#3a3a40' },
  },
  {
    id: 'carbon',
    nombre: 'Carbón',
    tono: 'oscuro',
    decoracion: 'esquina',
    colores: { fondo: '#232326', texto: '#f4f4f6', precio: '#f9ab4a', acento: '#f7941d', textoSobreAcento: '#111113', borde: '#c4c4ca' },
  },
  {
    id: 'ambar',
    nombre: 'Ámbar',
    tono: 'oscuro',
    decoracion: 'marco',
    colores: { fondo: '#3a2606', texto: '#fdebd3', precio: '#f9ab4a', acento: '#f7941d', textoSobreAcento: '#3a2606', borde: '#f7941d' },
  },
  {
    id: 'grafito',
    nombre: 'Grafito',
    tono: 'oscuro',
    decoracion: 'liso',
    colores: { fondo: '#2c2c31', texto: '#ffffff', precio: '#ffffff', acento: '#f7941d', textoSobreAcento: '#111113', borde: '#c4c4ca' },
  },
  {
    id: 'negro-naranja',
    nombre: 'Negro y naranja',
    tono: 'oscuro',
    decoracion: 'marco',
    colores: { fondo: '#000000', texto: '#ffffff', precio: '#f7941d', acento: '#f7941d', textoSobreAcento: '#000000', borde: '#f7941d' },
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
