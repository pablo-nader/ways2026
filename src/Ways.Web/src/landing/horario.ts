/**
 * Textos que dicen, enteros y sin nada más, que se atiende las 24 horas: `24 hs`, `abierto las 24 horas`,
 * `24/7`, `todos los días 24 hs`. Se compara el texto completo (no se busca un 24 suelto) porque cualquier
 * otro dato lo contradice o lo acota: `9 a 24 hs`, `Lun a Sáb 24 hs, Dom cerrado`, `Guardia 24 hs`.
 */
const PATRON_DE_HORARIO_DE_VEINTICUATRO_HORAS =
  /^(?:todos los d[ií]as[,:]?\s+)?(?:abierto\s+)?(?:(?:las\s+)?24\s*(?:hs|h|horas)|24\s*(?:\/|x)\s*7)(?:[,\s]+todos los d[ií]as)?$/

/** Un horario que es, entero, una de las formas conocidas de "24 horas"; cualquier otro texto no se interpreta. */
export function atiendeLasVeinticuatroHoras(horario: string): boolean {
  const normalizado = horario
    .trim()
    .toLowerCase()
    .replace(/\s+/g, ' ')
    .replace(/[\s.,;:!]+$/, '')

  return PATRON_DE_HORARIO_DE_VEINTICUATRO_HORAS.test(normalizado)
}

/** Texto del sello de la portada: "Abierto 24 hs" si el horario es solo eso; si no, el horario tal como lo cargó el comercio. */
export function textoDelSello(horario: string): string {
  const texto = horario.trim()

  return atiendeLasVeinticuatroHoras(texto) ? 'Abierto 24 hs' : texto
}
