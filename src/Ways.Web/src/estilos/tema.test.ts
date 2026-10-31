import { describe, expect, it } from 'vitest'

import cssTexto from './tema.css?raw'

/**
 * Lee `tema.css` como texto: prueba los valores que el stylesheet declara para los tokens `--ws-*`
 * de cada tema. No dibuja nada, así que el contraste real depende de que el navegador aplique las
 * variables tal como están escritas.
 */

const MINIMO_PARA_TEXTO = 4.5
const MINIMO_PARA_CONTROLES = 3

const TOKENS_DE_COLOR = [
  'bg',
  'surface',
  'surface-sunken',
  'surface-raised',
  'line',
  'line-control',
  'ink',
  'ink-muted',
  'brand',
  'brand-hover',
  'on-brand',
  'brand-soft',
  'brand-ink',
  'brand-gray',
  'focus',
  'ok',
  'ok-soft',
  'warn',
  'warn-soft',
  'danger',
  'danger-soft',
  'info',
  'info-soft',
  'overlay',
]

const TEMAS = ['light', 'dark'] as const

const SUPERFICIES = ['bg', 'surface', 'surface-sunken', 'surface-raised']

function luminancia(hex: string): number {
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255)
  const lineal = (c: number) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4)

  return 0.2126 * lineal(r) + 0.7152 * lineal(g) + 0.0722 * lineal(b)
}

function contraste(a: string, b: string): number {
  const [claro, oscuro] = [luminancia(a), luminancia(b)].sort((x, y) => y - x)

  return (claro + 0.05) / (oscuro + 0.05)
}

function bloqueDelTema(tema: string): string {
  const coincidencia = new RegExp(`^\\[data-bs-theme='${tema}'\\]\\s*\\{([^}]*)\\}`, 'm').exec(cssTexto)
  if (coincidencia === null) throw new Error(`No hay un bloque [data-bs-theme='${tema}']`)

  return coincidencia[1]
}

function valorDelToken(tema: string, token: string): string {
  const coincidencia = new RegExp(`--ws-${token}:\\s*([^;]+);`).exec(bloqueDelTema(tema))
  if (coincidencia === null) throw new Error(`El tema ${tema} no declara --ws-${token}`)

  return coincidencia[1].trim()
}

function hex(tema: string, token: string): string {
  const valor = valorDelToken(tema, token)
  if (!/^#[0-9a-fA-F]{6}$/.test(valor)) throw new Error(`--ws-${token} del tema ${tema} no es un hex de 6 dígitos: ${valor}`)

  return valor
}

describe('tokens de color del tema', () => {
  it('la fórmula de contraste da 21:1 entre negro y blanco', () => {
    expect(contraste('#000000', '#ffffff')).toBeCloseTo(21, 5)
  })

  it.each(TEMAS)('el tema %s declara cada token de color del sistema', (tema) => {
    const faltantes = TOKENS_DE_COLOR.filter((token) => !new RegExp(`--ws-${token}:\\s*[^;]+;`).test(bloqueDelTema(tema)))

    expect(faltantes).toEqual([])
  })

  it.each(TEMAS)('el tema %s declara como hex de 6 dígitos cada token salvo overlay', (tema) => {
    const conPar = TOKENS_DE_COLOR.filter((token) => token !== 'overlay')

    expect(() => conPar.forEach((token) => hex(tema, token))).not.toThrow()
  })
})

describe('contraste de los pares de texto declarados en tema.css', () => {
  const pares: [string, string][] = [
    ...SUPERFICIES.map((s): [string, string] => ['ink', s]),
    ...SUPERFICIES.map((s): [string, string] => ['ink-muted', s]),
    ['on-brand', 'brand'],
    ['on-brand', 'brand-hover'],
    ['brand-ink', 'surface'],
    ['brand-ink', 'bg'],
    ['brand-ink', 'brand-soft'],
    ...['ok', 'warn', 'danger', 'info'].flatMap((estado): [string, string][] => [
      [estado, 'surface'],
      [estado, `${estado}-soft`],
    ]),
  ]

  it.each(TEMAS.flatMap((tema) => pares.map(([texto, fondo]) => [tema, texto, fondo] as const)))(
    'en el tema %s, %s sobre %s tiene al menos 4.5:1',
    (tema, texto, fondo) => {
      expect(contraste(hex(tema, texto), hex(tema, fondo))).toBeGreaterThanOrEqual(MINIMO_PARA_TEXTO)
    },
  )
})

describe('contraste de los bordes de control y del foco declarados en tema.css', () => {
  const combinaciones = ['line-control', 'focus'].flatMap((token) =>
    ['bg', 'surface', 'surface-sunken'].map((fondo) => [token, fondo] as const),
  )

  it.each(TEMAS.flatMap((tema) => combinaciones.map(([token, fondo]) => [tema, token, fondo] as const)))(
    'en el tema %s, %s sobre %s tiene al menos 3:1',
    (tema, token, fondo) => {
      expect(contraste(hex(tema, token), hex(tema, fondo))).toBeGreaterThanOrEqual(MINIMO_PARA_CONTROLES)
    },
  )
})
