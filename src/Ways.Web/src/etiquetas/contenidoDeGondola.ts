import type { FilaDeEtiqueta, PromocionDeEtiqueta } from '../api/tipos'
import { formatearImporte } from '../formato/importes'

export type ContenidoDeGondola = {
  precio: number
  /** Precio de lista tachado; solo cuando una oferta lo baja. */
  precioTachado: number | null
  /** "-15%" cuando el descuento se puede derivar y redondea a un entero mayor que cero. */
  porcentaje: string | null
  /** "3x $ 2.700": la oferta por cantidad elegida. */
  promocion: string | null
}

/** Pesos enteros sin decimales ("$ 2.700"); con centavos, dos decimales. */
export function formatearPrecioDeGondola(valor: number): string {
  const entero = Number.isInteger(valor)
  return formatearImporte(valor, { simbolo: true, decimales: entero ? 0 : 2 })
}

export type PrecioPartido = {
  /** Todo lo que va antes del número, con el signo pesos: "$" o "3x $". */
  prefijo: string
  entero: string
  /** Los centavos sin la coma; `null` cuando el precio no tiene decimales. */
  centavos: string | null
}

/** Parte un precio ya formateado ("$ 12.345,67", "3x $ 2.700") para la tipografía de supermercado, sin volver a formatear números. */
export function partirPrecio(texto: string): PrecioPartido {
  const signo = texto.lastIndexOf('$')
  const prefijo = signo >= 0 ? texto.slice(0, signo + 1).trim() : ''
  const numero = (signo >= 0 ? texto.slice(signo + 1) : texto).trim()
  const coma = numero.lastIndexOf(',')
  return coma >= 0 ? { prefijo, entero: numero.slice(0, coma), centavos: numero.slice(coma + 1) } : { prefijo, entero: numero, centavos: null }
}

const FORMATO_DE_CANTIDAD = new Intl.NumberFormat('es-AR', { maximumFractionDigits: 3 })

/** Cantidad con coma decimal ("1,5"), sin ceros de más. */
export function formatearCantidadDePromocion(cantidad: number): string {
  return FORMATO_DE_CANTIDAD.format(cantidad)
}

/** Total del lote: cantidad por precio unitario, redondeado a centavos para no arrastrar ruido de coma flotante. */
export function textoDePromocion(promocion: PromocionDeEtiqueta): string {
  const total = Math.round(promocion.cantidad * promocion.precioUnitarioFinal * 100) / 100
  return `${formatearCantidadDePromocion(promocion.cantidad)}x ${formatearPrecioDeGondola(total)}`
}

/** La promoción que se muestra: la de menor cantidad (la más alcanzable); a igual cantidad, la de menor precio unitario. */
export function elegirPromocion(promociones: readonly PromocionDeEtiqueta[]): PromocionDeEtiqueta | null {
  let elegida: PromocionDeEtiqueta | null = null
  for (const promocion of promociones) {
    if (
      elegida === null ||
      promocion.cantidad < elegida.cantidad ||
      (promocion.cantidad === elegida.cantidad && promocion.precioUnitarioFinal < elegida.precioUnitarioFinal)
    ) {
      elegida = promocion
    }
  }
  return elegida
}

export function porcentajeDeDescuento(original: number, final: number): string | null {
  if (original <= 0 || final >= original) return null
  const porcentaje = Math.round((1 - final / original) * 100)
  return porcentaje >= 1 ? `-${porcentaje}%` : null
}

export function tieneOferta(fila: Pick<FilaDeEtiqueta, 'ofertas' | 'promociones'>): boolean {
  return fila.ofertas.length > 0 || fila.promociones.length > 0
}

/** Qué se imprime en la etiqueta. Sin `conOfertas` las ofertas se ignoran y va el precio de la lista. */
export function contenidoDeGondola(fila: FilaDeEtiqueta, conOfertas: boolean): ContenidoDeGondola {
  if (!conOfertas) {
    return { precio: fila.precioOriginal, precioTachado: null, porcentaje: null, promocion: null }
  }

  const promocion = elegirPromocion(fila.promociones)
  const bajaElPrecio = fila.ofertas.length > 0 && fila.precioFinal < fila.precioOriginal

  return {
    precio: bajaElPrecio ? fila.precioFinal : fila.precioOriginal,
    precioTachado: bajaElPrecio ? fila.precioOriginal : null,
    porcentaje: bajaElPrecio ? porcentajeDeDescuento(fila.precioOriginal, fila.precioFinal) : null,
    promocion: promocion ? textoDePromocion(promocion) : null,
  }
}
