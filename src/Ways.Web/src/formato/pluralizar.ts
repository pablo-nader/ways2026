const SIN_TILDE: Record<string, string> = { á: 'a', é: 'e', í: 'i', ó: 'o', ú: 'u' }

/** Plural regular del español para un sustantivo en singular: vocal final suma "s", consonante
 * final suma "es", "z" pasa a "ces" y "-ón" a "-ones". Las excepciones (por ejemplo "orden" →
 * "órdenes") se pasan como plural explícito. */
export function pluralDe(singular: string): string {
  const ultima = singular.slice(-1)
  if (/[aeiouáéíóú]/.test(ultima)) return `${singular}s`
  if (ultima === 'z') return `${singular.slice(0, -1)}ces`
  const anteultima = singular.slice(-2, -1)
  if (/[áéíóú]/.test(anteultima) && ultima === 'n') {
    return `${singular.slice(0, -2)}${SIN_TILDE[anteultima]}nes`
  }
  return `${singular}es`
}

/** "1 artículo" / "912 artículos". Cero lleva plural. El número se muestra tal cual, sin
 * separador de miles, como los paginadores que usan este texto. */
export function pluralizar(cantidad: number, singular: string, plural?: string): string {
  return `${cantidad} ${cantidad === 1 ? singular : (plural ?? pluralDe(singular))}`
}
