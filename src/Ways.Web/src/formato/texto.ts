/** Forma comparable de un texto para búsquedas locales: sin acentos ni mayúsculas, igual que la
 * búsqueda del servidor (`sin_acentos` + `lower`). "Ñandú" y "nandu" quedan iguales. */
export function normalizarParaBuscar(texto: string): string {
  return texto
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLocaleLowerCase('es-AR')
}

/** `true` si `buscado` está vacío (sin filtro) o aparece dentro de `contenedor`, sin distinguir
 * mayúsculas ni acentos de ningún lado. */
export function contieneSinAcentos(contenedor: string, buscado: string): boolean {
  if (buscado.trim() === '') return true
  return normalizarParaBuscar(contenedor).includes(normalizarParaBuscar(buscado))
}
