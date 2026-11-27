export type DecoracionDePlantilla = 'franja' | 'marco' | 'esquina' | 'liso'

export type PlantillaDeGondola = {
  readonly id: string
  readonly nombre: string
  readonly decoracion: DecoracionDePlantilla
  /** Colores de tinta: nombre, precio, acento (franja, esquina, marca de oferta) y borde. Nunca hay fondo: se imprime sobre papel blanco. */
  readonly colores: {
    readonly texto: string
    readonly precio: string
    readonly acento: string
    readonly borde: string
  }
}

// Paleta de la marca Ways (tema.css): naranja #f7941d y sus variantes #e8830c, #9a5200, #3a2606, gris #c4c4ca, más neutros oscuros.
export const PLANTILLAS_DE_GONDOLA: readonly PlantillaDeGondola[] = [
  {
    id: 'naranja',
    nombre: 'Naranja Ways',
    decoracion: 'franja',
    colores: { texto: '#1c1c1f', precio: '#e8830c', acento: '#f7941d', borde: '#c4c4ca' },
  },
  {
    id: 'marco-naranja',
    nombre: 'Marco naranja',
    decoracion: 'marco',
    colores: { texto: '#1c1c1f', precio: '#e8830c', acento: '#9a5200', borde: '#f7941d' },
  },
  {
    id: 'esquina-naranja',
    nombre: 'Esquina naranja',
    decoracion: 'esquina',
    colores: { texto: '#1c1c1f', precio: '#1c1c1f', acento: '#f7941d', borde: '#c4c4ca' },
  },
  {
    id: 'liso',
    nombre: 'Liso',
    decoracion: 'liso',
    colores: { texto: '#1c1c1f', precio: '#1c1c1f', acento: '#f7941d', borde: '#c4c4ca' },
  },
  {
    id: 'marco-gris',
    nombre: 'Marco gris',
    decoracion: 'marco',
    colores: { texto: '#1c1c1f', precio: '#9a5200', acento: '#f7941d', borde: '#c4c4ca' },
  },
  {
    id: 'negro',
    nombre: 'Negro',
    decoracion: 'franja',
    colores: { texto: '#111113', precio: '#111113', acento: '#111113', borde: '#c4c4ca' },
  },
  {
    id: 'esquina-negra',
    nombre: 'Esquina negra',
    decoracion: 'esquina',
    colores: { texto: '#1c1c1f', precio: '#e8830c', acento: '#111113', borde: '#c4c4ca' },
  },
  {
    id: 'marco-ambar',
    nombre: 'Marco ámbar',
    decoracion: 'marco',
    colores: { texto: '#1c1c1f', precio: '#3a2606', acento: '#9a5200', borde: '#9a5200' },
  },
  {
    id: 'grafito',
    nombre: 'Grafito',
    decoracion: 'liso',
    colores: { texto: '#2c2c31', precio: '#f7941d', acento: '#f7941d', borde: '#c4c4ca' },
  },
  {
    id: 'marco-negro',
    nombre: 'Marco negro',
    decoracion: 'marco',
    colores: { texto: '#000000', precio: '#000000', acento: '#f7941d', borde: '#111113' },
  },
]

export const PLANTILLA_POR_DEFECTO_DE_GONDOLA = PLANTILLAS_DE_GONDOLA[0]

export function plantillaDeGondolaPorId(id: string): PlantillaDeGondola {
  return PLANTILLAS_DE_GONDOLA.find((p) => p.id === id) ?? PLANTILLA_POR_DEFECTO_DE_GONDOLA
}

/** Logo de Ways de trazo oscuro: las etiquetas siempre se imprimen sobre papel blanco. */
export const RUTA_DEL_LOGO_DE_GONDOLA = '/marca/ways-logo-claro.png'
