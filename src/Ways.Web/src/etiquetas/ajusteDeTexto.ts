/** Ancho medio de un carácter en negrita, relativo al tamaño de la fuente (sans-serif). */
const ANCHO_DE_CARACTER = 0.56
const ANCHO_DE_DIGITO = 0.62
const INTERLINEADO = 1.1
const PASO_MM = 0.25
const FUENTE_MINIMA_MM = 2.2
const RELLENO_HORIZONTAL = 0.88
const ALTO_DE_NOMBRE = 0.34

export const MAXIMO_DE_LINEAS_DEL_NOMBRE = 2

export type MedidaDeCelda = { anchoMm: number; altoMm: number }

/** Corte voraz por palabras; una palabra más larga que la línea se parte por caracteres, igual que `overflow-wrap: anywhere`. */
export function partirEnLineas(texto: string, caracteresPorLinea: number): string[] {
  const ancho = Math.max(1, Math.floor(caracteresPorLinea))
  const lineas: string[] = []
  let actual = ''

  for (const palabra of texto.trim().split(/\s+/).filter(Boolean)) {
    let resto = palabra
    while (resto.length > ancho) {
      if (actual) {
        lineas.push(actual)
        actual = ''
      }
      lineas.push(resto.slice(0, ancho))
      resto = resto.slice(ancho)
    }
    if (!actual) {
      actual = resto
    } else if (actual.length + 1 + resto.length <= ancho) {
      actual = `${actual} ${resto}`
    } else {
      lineas.push(actual)
      actual = resto
    }
  }
  if (actual) lineas.push(actual)
  return lineas
}

/** Mayor tamaño de fuente, en milímetros, con el que el nombre entra en dos líneas dentro de la celda. */
export function tamanioDeFuenteDelNombre(nombre: string, celda: MedidaDeCelda): number {
  const anchoUtil = celda.anchoMm * RELLENO_HORIZONTAL
  const altoDisponible = celda.altoMm * ALTO_DE_NOMBRE
  const maximo = celda.altoMm * 0.13

  for (let fuente = maximo; fuente > FUENTE_MINIMA_MM; fuente -= PASO_MM) {
    const lineas = partirEnLineas(nombre, anchoUtil / (fuente * ANCHO_DE_CARACTER))
    if (lineas.length <= MAXIMO_DE_LINEAS_DEL_NOMBRE && lineas.length * fuente * INTERLINEADO <= altoDisponible) {
      return Math.round(fuente * 100) / 100
    }
  }
  return FUENTE_MINIMA_MM
}

/** Tamaño de fuente en milímetros del precio: grande, pero sin pasarse del ancho de la celda. */
export function tamanioDeFuenteDelPrecio(textoDelPrecio: string, celda: MedidaDeCelda): number {
  const porAncho = (celda.anchoMm * RELLENO_HORIZONTAL) / (Math.max(1, textoDelPrecio.length) * ANCHO_DE_DIGITO)
  return Math.round(Math.min(celda.altoMm * 0.3, porAncho) * 100) / 100
}
