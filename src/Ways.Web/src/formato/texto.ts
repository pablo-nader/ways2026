const REEMPLAZOS_SIN_DESCOMPOSICION: Record<string, string> = { ß: 'ss', æ: 'ae', ø: 'o', œ: 'oe' }

/** Forma comparable de un texto para búsquedas locales: sin acentos ni mayúsculas, igual que la
 * búsqueda del servidor (`sin_acentos` + `lower`). "Ñandú" y "nandu" quedan iguales. Los pocos
 * caracteres que NFD no descompone y `unaccent` sí (ß, æ, ø, œ) se mapean a mano. */
export function normalizarParaBuscar(texto: string): string {
  return texto
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLocaleLowerCase('es-AR')
    .replace(/[ßæøœ]/g, (c) => REEMPLAZOS_SIN_DESCOMPOSICION[c] ?? c)
}

/** `true` si `buscado` está vacío (sin filtro) o aparece dentro de `contenedor`, sin distinguir
 * mayúsculas ni acentos de ningún lado. */
export function contieneSinAcentos(contenedor: string, buscado: string): boolean {
  if (buscado.trim() === '') return true
  return normalizarParaBuscar(contenedor).includes(normalizarParaBuscar(buscado))
}
