import type { VitrinaPublica } from '../api/tipos'
import { aplicarTema } from '../tema/tema'
import type { Tema } from '../tema/tema'

/** Naranja de Ways: el acento de un comercio que no eligió otro color. */
export const ACENTO_POR_DEFECTO = '#F7991C'

const PATRON_DE_COLOR = /^#[0-9a-fA-F]{6}$/

export function acentoDeVitrina(colorAcento: string | null | undefined): string {
  return colorAcento && PATRON_DE_COLOR.test(colorAcento) ? colorAcento : ACENTO_POR_DEFECTO
}

/** Todo lo que no sea `claro` se muestra oscuro, que es el tema por defecto de la marca. */
export function temaDeVitrina(tema: string | null | undefined): Tema {
  return tema === 'claro' ? 'claro' : 'oscuro'
}

/** Pinta la página con el tema y el acento del comercio: `data-bs-theme` y `--vitrina-acento`. */
export function aplicarTemaDeVitrina(vitrina: Pick<VitrinaPublica, 'tema' | 'colorAcento'>) {
  aplicarTema(temaDeVitrina(vitrina.tema))
  document.documentElement.style.setProperty('--vitrina-acento', acentoDeVitrina(vitrina.colorAcento))
}

function meta(nombre: string): HTMLMetaElement {
  const existente = document.head.querySelector<HTMLMetaElement>(`meta[name="${nombre}"]`)
  if (existente) return existente

  const nuevo = document.createElement('meta')
  nuevo.name = nombre
  document.head.appendChild(nuevo)

  return nuevo
}

/** Título y descripción del documento: el nombre y la frase del comercio, o "Ways" si no hay. */
export function aplicarCabeceraDelDocumento(vitrina: Pick<VitrinaPublica, 'nombre' | 'tagline'> | null) {
  document.title = vitrina?.nombre.trim() || 'Ways'
  meta('description').content = vitrina?.tagline?.trim() ?? ''
}
