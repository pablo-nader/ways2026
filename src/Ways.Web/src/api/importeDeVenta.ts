/**
 * Importe que el cajero tipea para un artículo que se vende por importe (`unidadVenta: 'Importe'`).
 * Espejo de `Ways.Domain.Ventas.ReglaDeImporteDeVenta` (`TryParsear`): mayor a cero, dentro del rango de `numeric(14,2)` y solo
 * dígitos con separador decimal opcional y a lo sumo dos dígitos tras él, ceros incluidos: `1.500` es mil quinientos para quien
 * lo tipea y nunca se lee como 1,5. Un cambio en la regla del servidor se replica acá a mano.
 *
 * La única diferencia es de entrada: el cajero puede tipear coma decimal, que se lee como punto. El servidor nunca
 * recibe la coma (la tomaría como separador de miles).
 */

import { TECLAS_ATAJO, type TeclaAtajo } from './tipos'

export const IMPORTE_DE_VENTA_MAXIMO = 999_999_999_999.99

export const DECIMALES_DE_IMPORTE_DE_VENTA = 2

const FORMA_DE_IMPORTE = /^(\d+\.?\d{0,2}|\.\d{1,2})$/

/** Un importe ya numérico cumple la regla del servidor. */
export function esImporteDeVentaValido(importe: number): boolean {
  if (!Number.isFinite(importe) || importe <= 0 || importe > IMPORTE_DE_VENTA_MAXIMO) return false
  return Number(importe.toFixed(DECIMALES_DE_IMPORTE_DE_VENTA)) === importe
}

/** Texto que el cajero escribe antes del `*` → importe, o `null` si no es un importe válido. */
export function leerImporteTipeado(texto: string | null | undefined): number | null {
  if (texto == null) return null
  const normalizado = texto.trim().replace(',', '.')
  if (!FORMA_DE_IMPORTE.test(normalizado)) return null

  const importe = Number(normalizado)
  return esImporteDeVentaValido(importe) ? importe : null
}

/** `KeyboardEvent.key` de una tecla que puede tener un artículo por importe (F5 a F8). */
export function esTeclaDeAtajo(tecla: string): tecla is TeclaAtajo {
  return (TECLAS_ATAJO as readonly string[]).includes(tecla)
}

/** La entrada de escaneo `importe*código` con el importe tipeado con coma (`150,50*500`) escrita con punto, que es lo único que
 * acepta el servidor; `null` si el prefijo no trae coma o no es un importe válido (no hay nada que corregir). Para un artículo
 * común el servidor lee la coma como separador de miles: por eso se reintenta con esta forma solo cuando rechazó el importe. */
export function entradaConPuntoDecimal(entrada: string): string | null {
  const indice = entrada.indexOf('*')
  if (indice < 0) return null

  const prefijo = entrada.slice(0, indice)
  if (!prefijo.includes(',')) return null

  const importe = leerImporteTipeado(prefijo)
  return importe === null ? null : `${importe}*${entrada.slice(indice + 1).trim()}`
}

/** Avisos del POS ante un artículo por importe; los tres caminos de resolución (instantánea, servidor y atajos) usan estos textos. */
export const MENSAJE_DE_IMPORTE_INVALIDO =
  'El importe tiene que ser mayor a cero y admite hasta 2 decimales (por ejemplo 150 o 150,50). Para mil quinientos pesos tipeá 1500, sin punto ni coma.'

export const MENSAJE_DE_ATAJO_SIN_DATOS =
  'Los atajos de importe necesitan los datos del punto de venta y todavía no están disponibles. Tipeá el importe y el código (por ejemplo 150*500).'

export function mensajeDeImporteRequerido(codigo: string): string {
  return `Este artículo se vende por importe: tipeá el importe antes del código (por ejemplo 150*${codigo}).`
}

export function mensajeDeAtajoSinImporte(tecla: string): string {
  return `Tipeá el importe en el campo de código y presioná ${tecla} de nuevo.`
}

export function mensajeDeAtajoSinArticulo(tecla: string): string {
  return `No hay un artículo por importe asignado a ${tecla}.`
}

export function mensajeDeLineaPorImporteObsoleta(nombre: string): string {
  return `${nombre} ya no se vende por importe: quitá la línea y volvé a cargarla antes de cobrar.`
}

export const MENSAJE_DE_CANTIDAD_DE_IMPORTE =
  'La cantidad de un artículo por importe es siempre 1: para corregir el importe, quitá la línea y volvé a cargarla.'
