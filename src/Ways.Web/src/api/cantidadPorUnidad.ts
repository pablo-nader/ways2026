/**
 * Qué cantidades admite un artículo según su unidad de venta — espejo web de
 * `Ways.Domain.Articulos.ReglaDeCantidadDeVenta`: `Unidad` solo enteros; `Peso` hasta tres
 * decimales. Es el único lugar que decide el paso, el mínimo y la validez de un campo de cantidad:
 * las pantallas no repiten la regla.
 *
 * Una unidad ausente (`undefined`/`null`: una línea guardada antes de que existiera el dato, una
 * instantánea offline anterior, un artículo que todavía no se resolvió) se trata como `Peso`, o
 * sea el comportamiento de siempre. Nunca bloquea por falta de dato; el servidor es quien manda.
 */
import type { UnidadVenta } from './tipos'

export const DECIMALES_DE_PESO = 3

const PASO_DE_PESO = 1 / 10 ** DECIMALES_DE_PESO

export const MENSAJE_DE_CANTIDAD_ENTERA = 'Este artículo se vende por unidad: la cantidad tiene que ser entera.'

export type RestriccionDeCantidad = { step: number; min: number }

type UnidadOAusente = UnidadVenta | null | undefined

/** `permiteCero` es para los campos donde cero es una cantidad legítima (lo contado en un
 * inventario, las unidades sueltas de una compra con bultos). */
export function restriccionDeCantidad(unidadVenta: UnidadOAusente, opciones?: { permiteCero?: boolean }): RestriccionDeCantidad {
  const paso = unidadVenta === 'Unidad' ? 1 : PASO_DE_PESO
  return { step: paso, min: opciones?.permiteCero ? 0 : paso }
}

/** Granularidad sola, sin mirar el signo ni el cero (mismo alcance que la regla del dominio). */
export function respetaGranularidad(unidadVenta: UnidadOAusente, cantidad: number): boolean {
  if (!Number.isFinite(cantidad)) return false
  if (unidadVenta === 'Unidad') return Number.isInteger(cantidad)
  const escalada = cantidad * 10 ** DECIMALES_DE_PESO
  return Math.abs(escalada - Math.round(escalada)) < 1e-6
}

/** Cantidad de una línea: positiva y con la granularidad de la unidad. */
export function esCantidadValida(unidadVenta: UnidadOAusente, cantidad: number): boolean {
  return Number.isFinite(cantidad) && cantidad > 0 && respetaGranularidad(unidadVenta, cantidad)
}

/** Texto de un `<input type="number">` → `esCantidadValida`. Vacío o no numérico es inválido. */
export function esTextoDeCantidadValido(unidadVenta: UnidadOAusente, texto: string): boolean {
  return texto.trim() !== '' && esCantidadValida(unidadVenta, Number(texto))
}

/** Una cantidad positiva con fracción en un artículo por unidad: el único caso en que la pantalla
 * puede explicar por qué no se acepta. */
export function fraccionaUnaUnidad(unidadVenta: UnidadOAusente, cantidad: number): boolean {
  return unidadVenta === 'Unidad' && Number.isFinite(cantidad) && cantidad > 0 && !Number.isInteger(cantidad)
}

/** Lo mismo que `fraccionaUnaUnidad`, sobre el texto de un `<input type="number">`. */
export function esFraccionDeArticuloPorUnidad(unidadVenta: UnidadOAusente, texto: string): boolean {
  return texto.trim() !== '' && fraccionaUnaUnidad(unidadVenta, Number(texto))
}
