import { describe, expect, it } from 'vitest'

import componentesCss from './componentes.css?raw'
import temaCss from './tema.css?raw'

/**
 * Lee `componentes.css` y `tema.css` como texto. Prueba que la piel de los componentes se apoye solo
 * en variables y que cada token `--ws-*` que usa exista en tema.css; no dibuja nada.
 */

const TEMAS = ['light', 'dark'] as const

function sinComentarios(css: string): string {
  return css.replace(/\/\*[\s\S]*?\*\//g, '')
}

function bloqueDelTema(tema: string): string {
  const coincidencia = new RegExp(`^\\[data-bs-theme='${tema}'\\]\\s*\\{([^}]*)\\}`, 'm').exec(temaCss)
  if (coincidencia === null) throw new Error(`No hay un bloque [data-bs-theme='${tema}']`)

  return coincidencia[1]
}

function raiz(): string {
  const coincidencia = /^:root\s*\{([^}]*)\}/m.exec(temaCss)
  if (coincidencia === null) throw new Error('No hay un bloque :root')

  return coincidencia[1]
}

const tokensUsados = [...new Set([...sinComentarios(componentesCss).matchAll(/var\(--ws-([a-z0-9-]+)/g)].map((m) => m[1]))]

describe('componentes.css', () => {
  it('no declara colores literales en hex, rgb, rgba, hsl ni hsla', () => {
    const css = sinComentarios(componentesCss)
    const literales = [...css.matchAll(/#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(/g)].map((m) => m[0])

    expect(literales).toEqual([])
  })

  it('no usa los nombres de color white, black, red, green, blue, orange, gray ni grey como valor', () => {
    const css = sinComentarios(componentesCss)
    const nombres = [...css.matchAll(/:\s*[^;{}]*\b(white|black|red|green|blue|orange|gray|grey)\b[^;{}]*;/g)].map((m) => m[0])

    expect(nombres).toEqual([])
  })

  it('referencia al menos un token --ws-*', () => {
    expect(tokensUsados.length).toBeGreaterThan(0)
  })

  it.each(TEMAS)('cada token --ws-* que usa existe en :root o en el tema %s de tema.css', (tema) => {
    const declarados = `${raiz()}\n${bloqueDelTema(tema)}`
    const faltantes = tokensUsados.filter((token) => !new RegExp(`--ws-${token}:\\s*[^;]+;`).test(declarados))

    expect(faltantes).toEqual([])
  })

  it('no toca las reglas de impresión', () => {
    expect(sinComentarios(componentesCss)).not.toMatch(/@media\s+print/)
  })
})
