export const MAXIMO_DE_LINEAS_DEL_NOMBRE = 2
export const FUENTE_MINIMA_MM = 2.2
export const INTERLINEADO_DEL_NOMBRE = 1.12
export const INTERLINEADO_DE_LA_LINEA_CHICA = 1.25
const PASO_MM = 0.1

export type MedidaDeCelda = { anchoMm: number; altoMm: number }
export type MedidaDeCaja = { anchoMm: number; altoMm: number }

/** Ancho de la fuente: condensado (wdth 75), normal (wdth 100) o ancho (mayúsculas con espaciado). */
export type AnchoDeFuente = 'condensado' | 'normal' | 'ancho'

/**
 * Ancho medio de un carácter del nombre (Instrument Sans) relativo al tamaño de la fuente. Medido en el
 * navegador con nombres en mayúsculas, el caso más ancho, y redondeado hacia arriba.
 */
export const ANCHO_DE_CARACTER_DEL_NOMBRE = { condensado: 0.43, normal: 0.6, ancho: 0.68 } as const satisfies Record<AnchoDeFuente, number>

/**
 * Anchos en em de Bricolage Grotesque en negrita (wdth 100, opsz 96 fijo en el CSS), medidos en el navegador
 * y redondeados hacia arriba.
 */
const ANCHO_DE_GLIFO_DEL_PRECIO: Readonly<Record<string, number>> = { ' ': 0.26, '.': 0.26, ',': 0.21, '-': 0.33, '%': 0.96, x: 0.56 }
/** Dígitos tabulares y "$"; también cubre cualquier otro carácter. */
const ANCHO_DE_DIGITO_DEL_PRECIO = 0.6
/** Con wdth 75 los glifos del precio miden un 72% de los normales; se deja margen. */
export const ESCALA_DE_ANCHO_DEL_PRECIO = { condensado: 0.75, normal: 1, ancho: 1 } as const satisfies Record<AnchoDeFuente, number>

/** Ancho en em de un texto de precio con la fuente normal. */
export function anchoDelPrecioEnEm(texto: string): number {
  let ancho = 0
  for (const caracter of texto) ancho += ANCHO_DE_GLIFO_DEL_PRECIO[caracter] ?? ANCHO_DE_DIGITO_DEL_PRECIO
  return ancho
}

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

function redondearHaciaAbajo(mm: number): number {
  return Math.floor(mm * 100) / 100
}

export type NombreAjustado = { fuenteMm: number; lineas: string[] }

/**
 * Mayor fuente (hasta `maximoMm`) con la que el nombre entra en dos líneas dentro de la caja.
 * Si ni con la fuente mínima entra, devuelve la mínima y el CSS recorta a dos líneas.
 */
export function ajustarNombre(nombre: string, caja: MedidaDeCaja, anchoDeCaracter: number, maximoMm: number): NombreAjustado {
  const tope = Math.min(maximoMm, caja.altoMm / INTERLINEADO_DEL_NOMBRE)
  for (let fuente = tope; fuente >= FUENTE_MINIMA_MM; fuente -= PASO_MM) {
    const lineas = partirEnLineas(nombre, caja.anchoMm / (fuente * anchoDeCaracter))
    if (lineas.length <= MAXIMO_DE_LINEAS_DEL_NOMBRE && lineas.length * fuente * INTERLINEADO_DEL_NOMBRE <= caja.altoMm) {
      return { fuenteMm: redondearHaciaAbajo(fuente), lineas }
    }
  }
  return { fuenteMm: FUENTE_MINIMA_MM, lineas: partirEnLineas(nombre, caja.anchoMm / (FUENTE_MINIMA_MM * anchoDeCaracter)) }
}

/** Mayor fuente con la que una línea que mide `anchoEnEm` entra en la caja, sin cortar. */
export function fuenteParaUnaLinea(anchoEnEm: number, caja: MedidaDeCaja, interlineado = 1): number {
  const porAncho = caja.anchoMm / Math.max(0.5, anchoEnEm)
  const porAlto = caja.altoMm / interlineado
  return Math.max(0, redondearHaciaAbajo(Math.min(porAncho, porAlto)))
}
