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

const TINTA_OSCURA = '#121212'
const TINTA_CLARA = '#ffffff'

function canalLineal(canal: number): number {
  const valor = canal / 255

  return valor <= 0.03928 ? valor / 12.92 : ((valor + 0.055) / 1.055) ** 2.4
}

/** Luminancia relativa de WCAG de un `#rrggbb`. */
function luminancia(color: string): number {
  const rojo = canalLineal(parseInt(color.slice(1, 3), 16))
  const verde = canalLineal(parseInt(color.slice(3, 5), 16))
  const azul = canalLineal(parseInt(color.slice(5, 7), 16))

  return 0.2126 * rojo + 0.7152 * verde + 0.0722 * azul
}

function contraste(luminanciaA: number, luminanciaB: number): number {
  const [claro, oscuro] = luminanciaA > luminanciaB ? [luminanciaA, luminanciaB] : [luminanciaB, luminanciaA]

  return (claro + 0.05) / (oscuro + 0.05)
}

/** Casi negro o blanco: el que más contraste da sobre el acento, para el texto de botones y chips. */
export function tintaSobreAcento(acento: string): string {
  const luminanciaDelAcento = luminancia(acentoDeVitrina(acento))
  const sobreOscura = contraste(luminanciaDelAcento, luminancia(TINTA_OSCURA))
  const sobreClara = contraste(luminanciaDelAcento, luminancia(TINTA_CLARA))

  return sobreOscura >= sobreClara ? TINTA_OSCURA : TINTA_CLARA
}

/** Pinta la página con el tema y el acento del comercio: `data-bs-theme`, `--vitrina-acento` y el color del texto que va sobre él. */
export function aplicarTemaDeVitrina(vitrina: Pick<VitrinaPublica, 'tema' | 'colorAcento'>) {
  const acento = acentoDeVitrina(vitrina.colorAcento)
  aplicarTema(temaDeVitrina(vitrina.tema))
  document.documentElement.style.setProperty('--vitrina-acento', acento)
  document.documentElement.style.setProperty('--pub-tinta-sobre-acento', tintaSobreAcento(acento))
}

function meta(nombre: string): HTMLMetaElement {
  const existente = document.head.querySelector<HTMLMetaElement>(`meta[name="${nombre}"]`)
  if (existente) return existente

  const nuevo = document.createElement('meta')
  nuevo.name = nombre
  document.head.appendChild(nuevo)

  return nuevo
}

const LARGO_MAXIMO_DE_DESCRIPCION = 160

function descripcionDelDocumento(vitrina: Pick<VitrinaPublica, 'tagline' | 'descripcion'>): string {
  const frase = vitrina.tagline?.trim()
  if (frase) return frase

  return (vitrina.descripcion ?? '').replace(/\s+/g, ' ').trim().slice(0, LARGO_MAXIMO_DE_DESCRIPCION)
}

/**
 * Título y descripción del documento a partir de la vitrina. Sin vitrina no toca nada: queda el
 * título del HTML. La descripción es la frase del comercio o, si no tiene, los primeros 160
 * caracteres de su texto; si tampoco hay texto no se crea la meta. `prefijoDeTitulo` antepone una
 * sección (`Tienda online · Sur`).
 */
export function aplicarCabeceraDelDocumento(
  vitrina: Pick<VitrinaPublica, 'nombre' | 'tagline' | 'descripcion'> | null,
  prefijoDeTitulo?: string,
) {
  if (vitrina === null) return

  const nombre = vitrina.nombre.trim()
  if (nombre !== '') document.title = prefijoDeTitulo ? `${prefijoDeTitulo} · ${nombre}` : nombre

  const descripcion = descripcionDelDocumento(vitrina)
  if (descripcion !== '') meta('description').content = descripcion
}
