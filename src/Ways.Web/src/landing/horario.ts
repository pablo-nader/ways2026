/** Atención "24 hs", "24 horas", "24h", "24/7" o "24x7", con el 24 como número entero (no `124 hs` ni `10:24 hs`). */
const PATRON_DE_VEINTICUATRO_HORAS = /(?:^|[^\d:.,])24\s*(?:hs?\b|horas?\b|\/\s*7|x\s*7)/i

/** Un cierre a las 24 (`8 a 24 hs`, `9-24`, `hasta las 24 hs`): cierra a medianoche, no atiende todo el día. */
const PATRON_DE_RANGO_HASTA_24 = /(?:\d\s*(?:a|al|-|–|—)|hasta(?:\s+las)?)\s*24/i

/** El horario dice que se atiende las 24 horas; un horario que solo llega hasta las 24 no lo dice. */
export function atiendeLasVeinticuatroHoras(horario: string): boolean {
  return PATRON_DE_VEINTICUATRO_HORAS.test(horario) && !PATRON_DE_RANGO_HASTA_24.test(horario)
}

/** Texto del sello de la portada: "Abierto 24 hs" si atiende todo el día; si no, el horario tal como lo cargó el comercio. */
export function textoDelSello(horario: string): string {
  const texto = horario.trim()

  return atiendeLasVeinticuatroHoras(texto) ? 'Abierto 24 hs' : texto
}
