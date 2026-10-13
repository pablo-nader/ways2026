/**
 * Lógica pura del carrito de la tienda: el reductor, la regla de cantidades, los importes y la
 * persistencia en el navegador. No toca React ni el DOM; `CarritoProvider` la conecta.
 *
 * El carrito tiene dos tipos de línea: `articulo` (cantidad según la unidad de venta) y `combo`
 * (cantidad entera, precio fijo del conjunto). Los importes siempre salen de `precio` por
 * `cantidad`; `precioOriginal` y `oferta` de una línea de artículo son solo para mostrar.
 */
import { DECIMALES_DE_PESO, esCantidadValida } from '../api/cantidadPorUnidad'
import type { ArticuloPublico, ComboPublico, ComponenteDeComboPublico } from '../api/publico'
import type { UnidadVenta } from '../api/tipos'
import { formatearImporte, productoRedondeado } from '../formato/importes'

export const CANTIDAD_MAXIMA = 999

/** Un combo es un conjunto fijo que se compra entero: no se pide más de esto de una vez. */
export const CANTIDAD_MAXIMA_DE_COMBO = 99

/** Lo que el botón "más" y "menos" suman: medio kilo para `Peso`, un entero para el resto. */
export function pasoDelBoton(unidadVenta: UnidadVenta): number {
  return unidadVenta === 'Peso' ? 0.5 : 1
}

export type ItemArticulo = {
  tipo: 'articulo'
  idArticulo: number
  nombre: string
  precio: number
  unidadVenta: UnidadVenta
  cantidad: number
  imagenVersion: number | null
  /** Solo para mostrar: el precio de lista que rebaja la oferta. El importe sale de `precio`. */
  precioOriginal?: number
  oferta?: string
}

export type ItemCombo = {
  tipo: 'combo'
  idCombo: number
  nombre: string
  precio: number
  /** Entera, de 1 a `CANTIDAD_MAXIMA_DE_COMBO`. */
  cantidad: number
  componentes: readonly ComponenteDeComboPublico[]
  /** El combo ya no figura entre los de la tienda: la línea se queda a la vista pero no suma al total. */
  noDisponible?: boolean
}

export type ItemDelCarrito = ItemArticulo | ItemCombo

export type ArticuloParaElCarrito = Omit<ItemArticulo, 'cantidad'>
export type ComboParaElCarrito = Omit<ItemCombo, 'cantidad' | 'noDisponible'>

export type AccionDelCarrito =
  | { tipo: 'agregar'; articulo: ArticuloParaElCarrito; cantidad: number }
  | { tipo: 'quitar'; idArticulo: number }
  | { tipo: 'cambiarCantidad'; idArticulo: number; cantidad: number }
  | { tipo: 'sincronizar'; articulos: readonly ArticuloParaElCarrito[] }
  | { tipo: 'agregarCombo'; combo: ComboParaElCarrito; cantidad: number }
  | { tipo: 'quitarCombo'; idCombo: number }
  | { tipo: 'cambiarCantidadCombo'; idCombo: number; cantidad: number }
  | { tipo: 'sincronizarCombos'; combos: readonly ComboParaElCarrito[] }
  | { tipo: 'reemplazar'; items: readonly ItemDelCarrito[] }
  | { tipo: 'vaciar' }

/** De un artículo del catálogo público a lo que el carrito guarda de él. */
export function articuloParaElCarrito(articulo: ArticuloPublico): ArticuloParaElCarrito {
  return {
    tipo: 'articulo',
    idArticulo: articulo.id,
    nombre: articulo.nombre,
    precio: articulo.precio,
    unidadVenta: articulo.unidadVenta,
    imagenVersion: articulo.imagenVersion,
    ...(articulo.precioOriginal === undefined ? {} : { precioOriginal: articulo.precioOriginal }),
    ...(articulo.oferta === undefined ? {} : { oferta: articulo.oferta }),
  }
}

/** De un combo de la tienda a lo que el carrito guarda de él. */
export function comboParaElCarrito(combo: ComboPublico): ComboParaElCarrito {
  return {
    tipo: 'combo',
    idCombo: combo.id,
    nombre: combo.nombre,
    precio: combo.precio,
    componentes: combo.componentes,
  }
}

/** Identifica una línea dentro del carrito: un artículo y un combo pueden compartir número. */
export function claveDeLinea(item: ItemDelCarrito): string {
  return item.tipo === 'combo' ? `c:${item.idCombo}` : `a:${item.idArticulo}`
}

const FACTOR_DE_PESO = 10 ** DECIMALES_DE_PESO

/** Suma o resta cantidades sin arrastrar el ruido binario (`0,1 + 0,2`) y sin pasar del máximo. */
export function normalizarCantidad(cantidad: number, maximo = CANTIDAD_MAXIMA): number {
  return Math.min(Math.round(cantidad * FACTOR_DE_PESO) / FACTOR_DE_PESO, maximo)
}

/** Una cantidad se acepta si respeta la unidad (enteros para `Unidad`, tres decimales para `Peso`) y es positiva. */
export function esCantidadAceptada(unidadVenta: UnidadVenta, cantidad: number, maximo = CANTIDAD_MAXIMA): boolean {
  return esCantidadValida(unidadVenta, cantidad) && cantidad <= maximo
}

/** La cantidad de un combo es un entero de 1 a `CANTIDAD_MAXIMA_DE_COMBO`. */
export function esCantidadDeComboAceptada(cantidad: number): boolean {
  return esCantidadAceptada('Unidad', cantidad, CANTIDAD_MAXIMA_DE_COMBO)
}

const PATRON_DE_CANTIDAD = /^\d+(?:[.,]\d+)?$/

/** Texto tipeado → cantidad aceptada, o `null`. Acepta coma o punto decimal y rechaza notación científica o signos. */
export function parsearCantidad(unidadVenta: UnidadVenta, texto: string, maximo = CANTIDAD_MAXIMA): number | null {
  const limpio = texto.trim()
  if (!PATRON_DE_CANTIDAD.test(limpio)) return null
  const cantidad = Number(limpio.replace(',', '.'))

  return esCantidadAceptada(unidadVenta, cantidad, maximo) ? cantidad : null
}

/** Cantidad para mostrar: coma decimal, sin ceros de más. */
export function formatearCantidad(cantidad: number): string {
  return String(cantidad).replace('.', ',')
}

/** "2 × Coca Cola 1,5 L": un componente de un combo, como lo ve el cliente. */
export function descripcionDelComponente(componente: Pick<ComponenteDeComboPublico, 'cantidad' | 'nombre'>): string {
  return `${formatearCantidad(componente.cantidad)} × ${componente.nombre}`
}

export function totalDeLinea(item: Pick<ItemDelCarrito, 'precio' | 'cantidad'>): number {
  return productoRedondeado([item.precio, item.cantidad])
}

/** Suma las líneas; un combo que ya no está disponible no suma. */
export function totalDelCarrito(items: readonly ItemDelCarrito[]): number {
  const centavos = items.reduce(
    (suma, item) => (item.tipo === 'combo' && item.noDisponible === true ? suma : suma + Math.round(totalDeLinea(item) * 100)),
    0,
  )

  return centavos / 100
}

export function formatearPrecio(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

/** "$ 1.200,00" o, para un artículo por peso, "$ 1.200,00 por kg". */
export function formatearPrecioUnitario(precio: number, unidadVenta: UnidadVenta): string {
  return unidadVenta === 'Peso' ? `${formatearPrecio(precio)} por kg` : formatearPrecio(precio)
}

/**
 * La cantidad guardada pasada a la unidad actual del artículo. Los enteros valen en las dos
 * unidades; una cantidad con decimales (de un artículo que pasó de `Peso` a `Unidad`) se redondea al
 * entero más cercano, y no baja de 1.
 */
function cantidadParaLaUnidad(unidadVenta: UnidadVenta, cantidad: number): number {
  if (unidadVenta === 'Peso' || Number.isInteger(cantidad)) return cantidad

  return Math.max(1, Math.round(cantidad))
}

/** Pone al día nombre, precio, oferta, unidad e imagen de las líneas de artículo que figuran en `articulos`; las demás no se tocan. */
function sincronizarLineas(items: readonly ItemDelCarrito[], articulos: readonly ArticuloParaElCarrito[]): readonly ItemDelCarrito[] {
  const porId = new Map(articulos.map((a) => [a.idArticulo, a]))
  let huboCambios = false
  const resultado = items.map((item) => {
    if (item.tipo !== 'articulo') return item
    const actual = porId.get(item.idArticulo)
    if (actual === undefined) return item
    const cantidad = cantidadParaLaUnidad(actual.unidadVenta, item.cantidad)
    if (
      actual.nombre === item.nombre &&
      actual.precio === item.precio &&
      actual.precioOriginal === item.precioOriginal &&
      actual.oferta === item.oferta &&
      actual.unidadVenta === item.unidadVenta &&
      actual.imagenVersion === item.imagenVersion &&
      cantidad === item.cantidad
    ) {
      return item
    }
    huboCambios = true

    return { ...actual, cantidad }
  })

  return huboCambios ? resultado : items
}

function mismosComponentes(a: readonly ComponenteDeComboPublico[], b: readonly ComponenteDeComboPublico[]): boolean {
  return (
    a.length === b.length &&
    a.every(
      (c, i) =>
        c.idArticulo === b[i].idArticulo &&
        c.nombre === b[i].nombre &&
        c.cantidad === b[i].cantidad &&
        c.imagenVersion === b[i].imagenVersion,
    )
  )
}

/**
 * Pone al día nombre, precio y componentes de las líneas de combo que figuran en `combos`. Una
 * línea cuyo combo ya no figura queda marcada `noDisponible` (se conserva, pero no suma al total); si
 * vuelve a figurar, se desmarca.
 */
function sincronizarLineasDeCombo(items: readonly ItemDelCarrito[], combos: readonly ComboParaElCarrito[]): readonly ItemDelCarrito[] {
  const porId = new Map(combos.map((c) => [c.idCombo, c]))
  let huboCambios = false
  const resultado = items.map((item) => {
    if (item.tipo !== 'combo') return item
    const actual = porId.get(item.idCombo)
    if (actual === undefined) {
      if (item.noDisponible === true) return item
      huboCambios = true

      return { ...item, noDisponible: true }
    }
    if (
      item.noDisponible !== true &&
      actual.nombre === item.nombre &&
      actual.precio === item.precio &&
      mismosComponentes(actual.componentes, item.componentes)
    ) {
      return item
    }
    huboCambios = true

    return { ...actual, cantidad: item.cantidad }
  })

  return huboCambios ? resultado : items
}

export function reducirCarrito(items: readonly ItemDelCarrito[], accion: AccionDelCarrito): readonly ItemDelCarrito[] {
  switch (accion.tipo) {
    case 'agregar': {
      const { articulo, cantidad } = accion
      if (!esCantidadAceptada(articulo.unidadVenta, cantidad)) return items
      const existe = items.some((i) => i.tipo === 'articulo' && i.idArticulo === articulo.idArticulo)
      if (!existe) return [...items, { ...articulo, cantidad }]

      // En la interfaz se llega por dos "Agregar" del mismo artículo en el mismo instante (si la
      // línea ya existe, la tarjeta muestra el selector de cantidad). Manda la lectura más nueva y la cantidad se suma.
      return items.map((i) =>
        i.tipo === 'articulo' && i.idArticulo === articulo.idArticulo
          ? { ...articulo, cantidad: normalizarCantidad(i.cantidad + cantidad) }
          : i,
      )
    }
    case 'quitar':
      return items.filter((i) => i.tipo !== 'articulo' || i.idArticulo !== accion.idArticulo)
    case 'cambiarCantidad': {
      const actual = items.find((i) => i.tipo === 'articulo' && i.idArticulo === accion.idArticulo)
      if (actual?.tipo !== 'articulo' || !esCantidadAceptada(actual.unidadVenta, accion.cantidad)) return items

      return items.map((i) => (i === actual ? { ...actual, cantidad: accion.cantidad } : i))
    }
    case 'sincronizar':
      return sincronizarLineas(items, accion.articulos)
    case 'agregarCombo': {
      const { combo, cantidad } = accion
      if (!esCantidadDeComboAceptada(cantidad)) return items
      const existe = items.some((i) => i.tipo === 'combo' && i.idCombo === combo.idCombo)
      if (!existe) return [...items, { ...combo, cantidad }]

      return items.map((i) =>
        i.tipo === 'combo' && i.idCombo === combo.idCombo
          ? { ...combo, cantidad: Math.min(i.cantidad + cantidad, CANTIDAD_MAXIMA_DE_COMBO) }
          : i,
      )
    }
    case 'quitarCombo':
      return items.filter((i) => i.tipo !== 'combo' || i.idCombo !== accion.idCombo)
    case 'cambiarCantidadCombo': {
      const actual = items.find((i) => i.tipo === 'combo' && i.idCombo === accion.idCombo)
      if (actual?.tipo !== 'combo' || !esCantidadDeComboAceptada(accion.cantidad)) return items

      return items.map((i) => (i === actual ? { ...actual, cantidad: accion.cantidad } : i))
    }
    case 'sincronizarCombos':
      return sincronizarLineasDeCombo(items, accion.combos)
    case 'reemplazar':
      return accion.items
    case 'vaciar':
      return items.length === 0 ? items : []
  }
}

/** Clave de almacenamiento: lleva el host para que dos comercios en el mismo navegador no compartan carrito. */
export function claveDelCarrito(host: string): string {
  return `ways.tienda.carrito:${host}`
}

/**
 * Versión del formato guardado. La 1 era la lista de líneas de artículo sin `tipo`; la 2 es
 * `{ version, items }` con `tipo` en cada línea. Un carrito de la versión 1 se lee como artículos.
 */
export const VERSION_DEL_ALMACEN = 2

type AlmacenDeTexto = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>

function esObjeto(valor: unknown): valor is Record<string, unknown> {
  return typeof valor === 'object' && valor !== null
}

function esPrecioValido(valor: unknown): valor is number {
  return typeof valor === 'number' && Number.isFinite(valor) && valor >= 0
}

function esImagenVersionValida(valor: unknown): boolean {
  return valor === null || (typeof valor === 'number' && Number.isFinite(valor))
}

function esItemDeArticuloValido(i: Record<string, unknown>): i is ItemArticulo {
  // Un artículo por importe no se vende en la tienda: una línea guardada con esa unidad se descarta.
  if (i.unidadVenta !== 'Unidad' && i.unidadVenta !== 'Peso') return false

  return (
    Number.isInteger(i.idArticulo) &&
    typeof i.nombre === 'string' &&
    esPrecioValido(i.precio) &&
    typeof i.cantidad === 'number' &&
    esCantidadAceptada(i.unidadVenta, i.cantidad) &&
    esImagenVersionValida(i.imagenVersion) &&
    (i.precioOriginal === undefined || esPrecioValido(i.precioOriginal)) &&
    (i.oferta === undefined || typeof i.oferta === 'string')
  )
}

function esComponenteValido(valor: unknown): valor is ComponenteDeComboPublico {
  if (!esObjeto(valor)) return false

  return (
    Number.isInteger(valor.idArticulo) &&
    typeof valor.nombre === 'string' &&
    typeof valor.cantidad === 'number' &&
    Number.isFinite(valor.cantidad) &&
    valor.cantidad > 0 &&
    esImagenVersionValida(valor.imagenVersion)
  )
}

function esItemDeComboValido(i: Record<string, unknown>): i is ItemCombo {
  return (
    Number.isInteger(i.idCombo) &&
    typeof i.nombre === 'string' &&
    esPrecioValido(i.precio) &&
    typeof i.cantidad === 'number' &&
    esCantidadDeComboAceptada(i.cantidad) &&
    Array.isArray(i.componentes) &&
    i.componentes.length > 0 &&
    i.componentes.every(esComponenteValido) &&
    (i.noDisponible === undefined || typeof i.noDisponible === 'boolean')
  )
}

/** La línea guardada ya validada, o `null`. En el formato 1 una línea sin `tipo` es un artículo. */
function leerLinea(valor: unknown, delFormatoAnterior: boolean): ItemDelCarrito | null {
  if (!esObjeto(valor)) return null
  const tipo = valor.tipo === undefined && delFormatoAnterior ? 'articulo' : valor.tipo
  if (tipo === 'articulo') {
    const linea = { ...valor, tipo }

    return esItemDeArticuloValido(linea) ? linea : null
  }
  if (tipo === 'combo') return esItemDeComboValido(valor) ? valor : null

  return null
}

function lineasGuardadas(lectura: unknown): { lineas: readonly unknown[]; delFormatoAnterior: boolean } | null {
  if (Array.isArray(lectura)) return { lineas: lectura, delFormatoAnterior: true }
  if (esObjeto(lectura) && lectura.version === VERSION_DEL_ALMACEN && Array.isArray(lectura.items)) {
    return { lineas: lectura.items, delFormatoAnterior: false }
  }

  return null
}

/** Lee el carrito guardado. Un almacén roto, un JSON corrupto o líneas con forma inválida no rompen la tienda: se descartan. */
export function leerCarrito(almacen: AlmacenDeTexto | null, host: string): readonly ItemDelCarrito[] {
  try {
    const crudo = almacen?.getItem(claveDelCarrito(host))
    if (!crudo) return []
    const guardado = lineasGuardadas(JSON.parse(crudo))
    if (guardado === null) return []
    const vistos = new Set<string>()
    const items: ItemDelCarrito[] = []
    for (const valor of guardado.lineas) {
      const linea = leerLinea(valor, guardado.delFormatoAnterior)
      if (linea === null || vistos.has(claveDeLinea(linea))) continue
      vistos.add(claveDeLinea(linea))
      items.push(linea)
    }

    return items
  } catch {
    return []
  }
}

/**
 * Guarda el carrito solo si lo almacenado difiere. Es lo que corta el eco entre pestañas: la que
 * acaba de adoptar el carrito de otra lo encuentra igual y no lo vuelve a escribir.
 */
export function guardarCarrito(almacen: AlmacenDeTexto | null, host: string, items: readonly ItemDelCarrito[]): void {
  try {
    const clave = claveDelCarrito(host)
    if (items.length === 0) {
      if (almacen?.getItem(clave) !== null) almacen?.removeItem(clave)

      return
    }
    const serializado = JSON.stringify({ version: VERSION_DEL_ALMACEN, items })
    if (almacen?.getItem(clave) !== serializado) almacen?.setItem(clave, serializado)
  } catch {
    // Sin espacio o almacenamiento bloqueado: el carrito sigue vivo en memoria.
  }
}
