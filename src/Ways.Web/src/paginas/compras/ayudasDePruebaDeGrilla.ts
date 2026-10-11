import { screen, within } from '@testing-library/react'
import type userEvent from '@testing-library/user-event'

/** Ayudas de prueba compartidas por las pruebas de la grilla de items de una compra. */

export type UsuarioDePrueba = ReturnType<typeof userEvent.setup>

export function tablaDeItems(): HTMLElement {
  return screen.getByRole('table', { name: 'Items de la compra' })
}

/** Las filas de datos de la grilla (sin el encabezado ni las filas de lote). */
export function filasDeItems(): HTMLElement[] {
  return Array.from(tablaDeItems().querySelectorAll<HTMLElement>('tr[data-fila]'))
}

export function filaDeItems(indice: number): HTMLElement {
  const fila = filasDeItems()[indice]
  if (!fila) throw new Error(`No hay una fila ${indice} en la grilla (hay ${filasDeItems().length}).`)
  return fila
}

/** Una celda de la fila `indice` por su etiqueta (los nombres accesibles llevan el número de línea). */
export function celdaDeItems(indice: number, etiqueta: string): HTMLElement {
  return within(filaDeItems(indice)).getByLabelText(`${etiqueta}, línea ${indice + 1}`)
}

export type ValoresDeFila = {
  codigo?: string
  detalle?: string
  cantidad?: string
  importe?: string
  descuento?: string
  /** Id de la alícuota (valor del `<option>`). */
  iva?: string
}

/** Carga una fila en el orden de lectura de la grilla; solo toca lo que se pasa. */
export async function cargarFila(usuario: UsuarioDePrueba, indice: number, valores: ValoresDeFila): Promise<void> {
  const celda = (etiqueta: string) => celdaDeItems(indice, etiqueta)
  if (valores.codigo !== undefined) await usuario.type(celda('Código'), valores.codigo)
  if (valores.detalle !== undefined) await usuario.type(celda('Detalle'), valores.detalle)
  if (valores.cantidad !== undefined) await usuario.type(celda('Cantidad'), valores.cantidad)
  if (valores.importe !== undefined) await usuario.type(celda('Importe'), valores.importe)
  if (valores.descuento !== undefined) await usuario.type(celda('Descuento (%)'), valores.descuento)
  if (valores.iva !== undefined) await usuario.selectOptions(celda('IVA'), valores.iva)
}

/** Un concepto de cantidad 1 en la primera fila (reemplaza al antiguo "Cargar por total"). */
export async function cargarConcepto(
  usuario: UsuarioDePrueba,
  importe: string,
  detalle = 'Total del comprobante',
  iva = '3',
): Promise<void> {
  await cargarFila(usuario, 0, { detalle, cantidad: '1', importe, iva })
}
