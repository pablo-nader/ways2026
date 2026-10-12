/**
 * Lógica pura del carrito de la tienda: el reductor, la regla de cantidades, los importes y la
 * persistencia en el navegador. No toca React ni el DOM; `CarritoProvider` la conecta.
 */
import { DECIMALES_DE_PESO, esCantidadValida } from '../api/cantidadPorUnidad'
import type { UnidadVenta } from '../api/tipos'
import { formatearImporte, productoRedondeado } from '../formato/importes'

export const CANTIDAD_MAXIMA = 999

/** Lo que el botón "más" y "menos" suman: un entero para `Unidad`, medio kilo para `Peso`. */
export function pasoDelBoton(unidadVenta: UnidadVenta): number {
  return unidadVenta === 'Unidad' ? 1 : 0.5
}

export type ItemDelCarrito = {
  idArticulo: number
  nombre: string
  precio: number
  unidadVenta: UnidadVenta
  cantidad: number
  imagenVersion: number | null
}

export type ArticuloParaElCarrito = Omit<ItemDelCarrito, 'cantidad'>

export type AccionDelCarrito =
  | { tipo: 'agregar'; articulo: ArticuloParaElCarrito; cantidad: number }
  | { tipo: 'quitar'; idArticulo: number }
  | { tipo: 'cambiarCantidad'; idArticulo: number; cantidad: number }
  | { tipo: 'vaciar' }

const FACTOR_DE_PESO = 10 ** DECIMALES_DE_PESO

/** Suma o resta cantidades sin arrastrar el ruido binario (`0,1 + 0,2`) y sin pasar del máximo. */
export function normalizarCantidad(cantidad: number): number {
  return Math.min(Math.round(cantidad * FACTOR_DE_PESO) / FACTOR_DE_PESO, CANTIDAD_MAXIMA)
}

/** Una cantidad se acepta si respeta la unidad (enteros para `Unidad`, tres decimales para `Peso`) y es positiva. */
export function esCantidadAceptada(unidadVenta: UnidadVenta, cantidad: number): boolean {
  return esCantidadValida(unidadVenta, cantidad) && cantidad <= CANTIDAD_MAXIMA
}

const PATRON_DE_CANTIDAD = /^\d+(?:[.,]\d+)?$/

/** Texto tipeado → cantidad aceptada, o `null`. Acepta coma o punto decimal y rechaza notación científica o signos. */
export function parsearCantidad(unidadVenta: UnidadVenta, texto: string): number | null {
  const limpio = texto.trim()
  if (!PATRON_DE_CANTIDAD.test(limpio)) return null
  const cantidad = Number(limpio.replace(',', '.'))

  return esCantidadAceptada(unidadVenta, cantidad) ? cantidad : null
}

/** Cantidad para mostrar: coma decimal, sin ceros de más. */
export function formatearCantidad(cantidad: number): string {
  return String(cantidad).replace('.', ',')
}

export function totalDeLinea(item: Pick<ItemDelCarrito, 'precio' | 'cantidad'>): number {
  return productoRedondeado([item.precio, item.cantidad])
}

export function totalDelCarrito(items: readonly ItemDelCarrito[]): number {
  const centavos = items.reduce((suma, item) => suma + Math.round(totalDeLinea(item) * 100), 0)

  return centavos / 100
}

export function formatearPrecio(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

/** "$ 1.200,00" o, para un artículo por peso, "$ 1.200,00 por kg". */
export function formatearPrecioUnitario(precio: number, unidadVenta: UnidadVenta): string {
  return unidadVenta === 'Peso' ? `${formatearPrecio(precio)} por kg` : formatearPrecio(precio)
}

export function reducirCarrito(items: readonly ItemDelCarrito[], accion: AccionDelCarrito): readonly ItemDelCarrito[] {
  switch (accion.tipo) {
    case 'agregar': {
      const { articulo, cantidad } = accion
      if (!esCantidadAceptada(articulo.unidadVenta, cantidad)) return items
      const existente = items.find((i) => i.idArticulo === articulo.idArticulo)
      if (existente === undefined) return [...items, { ...articulo, cantidad }]

      // El precio y el nombre de la última lectura del catálogo mandan; la cantidad se suma.
      return items.map((i) =>
        i.idArticulo === articulo.idArticulo
          ? { ...articulo, cantidad: normalizarCantidad(i.cantidad + cantidad) }
          : i,
      )
    }
    case 'quitar':
      return items.filter((i) => i.idArticulo !== accion.idArticulo)
    case 'cambiarCantidad': {
      const actual = items.find((i) => i.idArticulo === accion.idArticulo)
      if (actual === undefined || !esCantidadAceptada(actual.unidadVenta, accion.cantidad)) return items

      return items.map((i) => (i.idArticulo === accion.idArticulo ? { ...i, cantidad: accion.cantidad } : i))
    }
    case 'vaciar':
      return items.length === 0 ? items : []
  }
}

/** Clave de almacenamiento: lleva el host para que dos comercios en el mismo navegador no compartan carrito. */
export function claveDelCarrito(host: string): string {
  return `ways.tienda.carrito:${host}`
}

type AlmacenDeTexto = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>

function esItemValido(valor: unknown): valor is ItemDelCarrito {
  if (typeof valor !== 'object' || valor === null) return false
  const i = valor as Record<string, unknown>
  if (i.unidadVenta !== 'Unidad' && i.unidadVenta !== 'Peso') return false

  return (
    Number.isInteger(i.idArticulo) &&
    typeof i.nombre === 'string' &&
    typeof i.precio === 'number' &&
    Number.isFinite(i.precio) &&
    i.precio >= 0 &&
    typeof i.cantidad === 'number' &&
    esCantidadAceptada(i.unidadVenta, i.cantidad) &&
    (i.imagenVersion === null || Number.isFinite(i.imagenVersion))
  )
}

/** Lee el carrito guardado. Un almacén roto, un JSON corrupto o líneas con forma inválida no rompen la tienda: se descartan. */
export function leerCarrito(almacen: AlmacenDeTexto | null, host: string): readonly ItemDelCarrito[] {
  try {
    const crudo = almacen?.getItem(claveDelCarrito(host))
    if (!crudo) return []
    const lectura: unknown = JSON.parse(crudo)
    if (!Array.isArray(lectura)) return []
    const vistos = new Set<number>()

    return lectura.filter((item): item is ItemDelCarrito => {
      if (!esItemValido(item) || vistos.has(item.idArticulo)) return false
      vistos.add(item.idArticulo)

      return true
    })
  } catch {
    return []
  }
}

export function guardarCarrito(almacen: AlmacenDeTexto | null, host: string, items: readonly ItemDelCarrito[]): void {
  try {
    if (items.length === 0) almacen?.removeItem(claveDelCarrito(host))
    else almacen?.setItem(claveDelCarrito(host), JSON.stringify(items))
  } catch {
    // Sin espacio o almacenamiento bloqueado: el carrito sigue vivo en memoria.
  }
}
