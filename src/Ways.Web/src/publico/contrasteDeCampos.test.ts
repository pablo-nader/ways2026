import { describe, expect, it } from 'vitest'

import cssTexto from './publico.css?raw'

/**
 * Lee `publico.css` como texto: prueba los valores de los colores que el stylesheet declara y qué
 * reglas los usan. No dibuja nada, así que el contraste real depende de que el navegador aplique
 * esas reglas tal como están escritas.
 */

const MINIMO_PARA_CONTROLES = 3

function luminancia(hex: string): number {
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255)
  const lineal = (c: number) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4)

  return 0.2126 * lineal(r) + 0.7152 * lineal(g) + 0.0722 * lineal(b)
}

function contraste(a: string, b: string): number {
  const [claro, oscuro] = [luminancia(a), luminancia(b)].sort((x, y) => y - x)

  return (claro + 0.05) / (oscuro + 0.05)
}

function escapar(texto: string): string {
  return texto.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

function cuerpoDeLaRegla(selector: string): string {
  const coincidencia = new RegExp(`${escapar(selector)}\\s*\\{([^}]*)\\}`).exec(cssTexto)
  if (coincidencia === null) throw new Error(`No hay una regla con el selector ${selector}`)

  return coincidencia[1]
}

function color(cuerpo: string, variable: string): string {
  const coincidencia = new RegExp(`${escapar(variable)}:\\s*(#[0-9a-fA-F]{6})`).exec(cuerpo)
  if (coincidencia === null) throw new Error(`No se declara ${variable}`)

  return coincidencia[1]
}

describe('contraste del borde de los campos de formulario', () => {
  it('la fórmula de contraste da 21:1 entre negro y blanco', () => {
    expect(contraste('#000000', '#ffffff')).toBeCloseTo(21, 5)
  })

  it.each([
    ['claro', "[data-bs-theme='light'] .publico"],
    ['oscuro', "[data-bs-theme='dark'] .publico"],
  ])('en el tema %s el borde de los campos tiene al menos 3:1 contra el fondo de la página y contra el de los paneles', (_tema, selector) => {
    const tema = cuerpoDeLaRegla(selector)
    const borde = color(tema, '--pub-borde-campo')

    expect(contraste(borde, color(tema, '--pub-fondo'))).toBeGreaterThanOrEqual(MINIMO_PARA_CONTROLES)
    expect(contraste(borde, color(tema, '--pub-panel'))).toBeGreaterThanOrEqual(MINIMO_PARA_CONTROLES)
  })

  it.each(['.publico-busqueda-campo', '.publico-cantidad-campo'])('%s usa ese borde', (selector) => {
    expect(cuerpoDeLaRegla(selector)).toMatch(/border:\s*2px solid var\(--pub-borde-campo\)/)
  })
})
