import type { IdDeModelo } from './modelosDeGondola'

export type PlantillaDeGondola = {
  readonly id: string
  readonly nombre: string
  /** Diseño de la etiqueta: disposición, tipografía y trazos (ver `modelosDeGondola`). */
  readonly modelo: IdDeModelo
  /** Colores de tinta: nombre, precio, acento (trazos y marca de oferta) y borde. Nunca hay fondo: se imprime sobre papel blanco. */
  readonly colores: {
    readonly texto: string
    readonly precio: string
    readonly acento: string
    readonly borde: string
  }
}

// Paleta de la marca Ways (tema.css): naranja #f7941d, naranja tinta #e8830c, tostado #9a5200, café #3a2606, gris #c4c4ca, más negros.
export const PLANTILLAS_DE_GONDOLA: readonly PlantillaDeGondola[] = [
  { id: 'pizarra', nombre: 'Pizarra', modelo: 'pizarra', colores: { texto: '#1c1c1f', precio: '#1c1c1f', acento: '#f7941d', borde: '#f7941d' } },
  { id: 'ticket', nombre: 'Ticket', modelo: 'ticket', colores: { texto: '#1c1c1f', precio: '#1c1c1f', acento: '#e8830c', borde: '#1c1c1f' } },
  { id: 'columna', nombre: 'Columna', modelo: 'columna', colores: { texto: '#1c1c1f', precio: '#3a2606', acento: '#f7941d', borde: '#c4c4ca' } },
  { id: 'subrayado', nombre: 'Subrayado', modelo: 'subrayado', colores: { texto: '#1c1c1f', precio: '#1c1c1f', acento: '#f7941d', borde: '#c4c4ca' } },
  { id: 'colgante', nombre: 'Colgante', modelo: 'colgante', colores: { texto: '#3a2606', precio: '#e8830c', acento: '#f7941d', borde: '#9a5200' } },
  { id: 'sello', nombre: 'Sello', modelo: 'sello', colores: { texto: '#1c1c1f', precio: '#9a5200', acento: '#f7941d', borde: '#c4c4ca' } },
  { id: 'corchetes', nombre: 'Corchetes', modelo: 'corchetes', colores: { texto: '#1c1c1f', precio: '#1c1c1f', acento: '#f7941d', borde: '#c4c4ca' } },
  { id: 'doble', nombre: 'Doble línea', modelo: 'doble', colores: { texto: '#1c1c1f', precio: '#000000', acento: '#e8830c', borde: '#000000' } },
  { id: 'centavos', nombre: 'Centavos arriba', modelo: 'centavos', colores: { texto: '#1c1c1f', precio: '#e8830c', acento: '#f7941d', borde: '#c4c4ca' } },
  { id: 'banderin', nombre: 'Banderín', modelo: 'banderin', colores: { texto: '#1c1c1f', precio: '#1c1c1f', acento: '#f7941d', borde: '#c4c4ca' } },
]

export const PLANTILLA_POR_DEFECTO_DE_GONDOLA = PLANTILLAS_DE_GONDOLA[0]

export function plantillaDeGondolaPorId(id: string): PlantillaDeGondola {
  return PLANTILLAS_DE_GONDOLA.find((p) => p.id === id) ?? PLANTILLA_POR_DEFECTO_DE_GONDOLA
}

/** Logo de Ways de trazo oscuro: las etiquetas siempre se imprimen sobre papel blanco. */
export const RUTA_DEL_LOGO_DE_GONDOLA = '/marca/ways-logo-claro.png'
