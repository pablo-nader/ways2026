import type { CuentaDelLibro, DesgloseDeMedioDelLibro, FilaDelLibro, LibroDeTesoreria, SaldosDelLibro } from '../../api/tipos'
import { formatearImporte, redondearImporte } from '../../formato/importes'

export const CLASE_DE_IMPORTE_NEGATIVO = 'libro-importe--negativo'

/** Clase del importe: solo el negativo se pinta (rojo del tema); positivo y cero conservan el color del texto. */
export function claseDeImporte(valor: number | null | undefined): string {
  if (valor === null || valor === undefined) return ''
  return redondearImporte(valor) < 0 ? CLASE_DE_IMPORTE_NEGATIVO : ''
}

/** Texto de una celda de importe; vacío cuando la fila no toca esa columna. */
export function textoDeImporte(valor: number | null | undefined): string {
  if (valor === null || valor === undefined) return ''
  return formatearImporte(valor, { simbolo: true })
}

/** `$ -1.500` / `$ +1.300`: el signo siempre se muestra y los centavos solo cuando existen. */
export function textoDeDiferencia(valor: number): string {
  const sinCentavos = Number.isInteger(redondearImporte(valor))
  const numero = formatearImporte(valor, { decimales: sinCentavos ? 0 : 2 })
  return `$ ${valor > 0 ? '+' : ''}${numero}`
}

export function etiquetaDeCaja(numero: number, diferenciaArqueo: number | null): string {
  const base = `Caja #${numero}`
  return diferenciaArqueo === null ? base : `${base} (${textoDeDiferencia(diferenciaArqueo)})`
}

export function detalleDeAjuste(concepto: string): string {
  return `Ajuste: ${concepto}`
}

export type ColumnaDeCuenta = { id: number; titulo: string }

export function columnasDeCuentas(cuentas: readonly CuentaDelLibro[]): ColumnaDeCuenta[] {
  return cuentas.map((c) => ({ id: c.idCuentaTesoreria, titulo: c.activo ? c.nombre : `${c.nombre} (inactiva)` }))
}

export function importeDeCuenta(fila: FilaDelLibro, idCuenta: number): number | null {
  return fila.cuentas.find((c) => c.idCuentaTesoreria === idCuenta)?.importe ?? null
}

export function saldoDeCuenta(saldos: SaldosDelLibro, idCuenta: number): number {
  return saldos.porCuenta.find((c) => c.idCuentaTesoreria === idCuenta)?.importe ?? 0
}

export function hayFilasSinCuenta(libro: Pick<LibroDeTesoreria, 'filas'>): boolean {
  return libro.filas.some((f) => f.sinCuenta !== null)
}

/** La columna de retenciones del desglose solo aparece cuando algún medio las tiene. */
export function hayRetenciones(medios: readonly DesgloseDeMedioDelLibro[]): boolean {
  return medios.some((m) => redondearImporte(m.retenciones) !== 0)
}
