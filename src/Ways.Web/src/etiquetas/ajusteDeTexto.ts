/** Ancho medio de un carácter en negrita, relativo al tamaño de la fuente (sans-serif). */
const ANCHO_DE_CARACTER = 0.68
const ANCHO_DE_DIGITO = 0.62
const INTERLINEADO = 1.1
const PASO_MM = 0.25
const FUENTE_MINIMA_MM = 2.2
const RELLENO_HORIZONTAL = 0.88
const ALTO_DE_NOMBRE = 0.34
const ALTO_DE_NOMBRE_CON_OFERTA = 0.24
const ALTO_DE_PRECIO = 0.3
const ALTO_DE_PRECIO_CON_OFERTA = 0.2
const ALTO_DEL_LOGO = 0.11
const ALTO_DEL_TACHADO = 0.07
const ALTO_DE_LA_PROMOCION = 0.09
const INTERLINEADO_DE_LA_PROMOCION = 1.2
const RELLENO_DE_LA_PROMOCION_MM = 1.2
/** Relleno vertical de la etiqueta (4% arriba y abajo) y reserva para el borde y la franja decorativa más gruesos. */
const RELLENO_VERTICAL = 0.08
const RESERVA_DE_BORDES_MM = 3.4

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
export function tamanioDeFuenteDelNombre(nombre: string, celda: MedidaDeCelda, conOferta = false): number {
  const anchoUtil = celda.anchoMm * RELLENO_HORIZONTAL
  const altoDisponible = celda.altoMm * (conOferta ? ALTO_DE_NOMBRE_CON_OFERTA : ALTO_DE_NOMBRE)
  const maximo = celda.altoMm * 0.13

  for (let fuente = maximo; fuente > FUENTE_MINIMA_MM; fuente -= PASO_MM) {
    const lineas = partirEnLineas(nombre, anchoUtil / (fuente * ANCHO_DE_CARACTER))
    if (lineas.length <= MAXIMO_DE_LINEAS_DEL_NOMBRE && lineas.length * fuente * INTERLINEADO <= altoDisponible) {
      return Math.round(fuente * 100) / 100
    }
  }
  return FUENTE_MINIMA_MM
}

/** Tamaño de fuente en milímetros del precio: grande, pero sin pasarse del ancho ni, con oferta, del alto que dejan el tachado y la promoción. */
export function tamanioDeFuenteDelPrecio(textoDelPrecio: string, celda: MedidaDeCelda, conOferta = false): number {
  const porAncho = (celda.anchoMm * RELLENO_HORIZONTAL) / (Math.max(1, textoDelPrecio.length) * ANCHO_DE_DIGITO)
  const porAlto = celda.altoMm * (conOferta ? ALTO_DE_PRECIO_CON_OFERTA : ALTO_DE_PRECIO)
  return Math.round(Math.min(porAlto, porAncho) * 100) / 100
}

export type PresupuestoVertical = {
  logoMm: number
  nombreMm: number
  precioMm: number
  tachadoMm: number
  promocionMm: number
  /** Suma de las alturas de los bloques, más relleno y bordes: debe caber en la celda. */
  totalMm: number
}

/** Alturas, en milímetros, de cada bloque de la etiqueta; con `conOferta` incluyen el tachado y la píldora de promoción. */
export function presupuestoVertical(nombre: string, textoDelPrecio: string, celda: MedidaDeCelda, conOferta: boolean): PresupuestoVertical {
  const nombreMm = tamanioDeFuenteDelNombre(nombre, celda, conOferta)
  const lineasDelNombre = Math.min(MAXIMO_DE_LINEAS_DEL_NOMBRE, Math.max(1, partirEnLineas(nombre, (celda.anchoMm * RELLENO_HORIZONTAL) / (nombreMm * ANCHO_DE_CARACTER)).length))
  const precioMm = tamanioDeFuenteDelPrecio(textoDelPrecio, celda, conOferta)
  const logoMm = celda.altoMm * ALTO_DEL_LOGO
  const tachadoMm = conOferta ? celda.altoMm * ALTO_DEL_TACHADO : 0
  const promocionMm = conOferta ? celda.altoMm * ALTO_DE_LA_PROMOCION : 0
  const alturaDePromocion = conOferta ? promocionMm * INTERLINEADO_DE_LA_PROMOCION + RELLENO_DE_LA_PROMOCION_MM : 0
  const totalMm =
    logoMm + lineasDelNombre * nombreMm * INTERLINEADO + tachadoMm + precioMm + alturaDePromocion + celda.altoMm * RELLENO_VERTICAL + RESERVA_DE_BORDES_MM
  return { logoMm, nombreMm, precioMm, tachadoMm, promocionMm, totalMm }
}
