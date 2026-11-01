import {
  CATEGORIAS_PAGO,
  type CategoriaPago,
  type CuentaDelLibro,
  type DesgloseDeMedioDelLibro,
  type FilaDelLibro,
  type LibroDeTesoreria,
  type SaldosDelLibro,
} from '../../api/tipos'
import { formatearImporte, redondearImporte } from '../../formato/importes'

export const CLASE_DE_IMPORTE_NEGATIVO = 'libro-importe--negativo'
export const CLASE_DE_IMPORTE_POSITIVO = 'libro-importe--positivo'

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

/** Tono del chip: sufijo de la clase `text-bg-*` del badge del sistema de diseño. */
export type CategoriaDeFila = { etiqueta: string; tono: 'primary' | 'success' | 'warning' | 'danger' | 'info' | 'secondary' }

const TONO_DE_CATEGORIA_DE_PAGO: Record<CategoriaPago, CategoriaDeFila['tono']> = {
  Proveedor: 'primary',
  Sueldos: 'success',
  Viaticos: 'warning',
  Impuestos: 'danger',
  Servicios: 'info',
  Otros: 'secondary',
}

/** La categoría que rotula la fila: la del pago, "Caja" para el cierre de un turno y "Ajuste" para los ajustes manuales. */
export function categoriaDeFila(fila: FilaDelLibro): CategoriaDeFila {
  if (fila.tipo === 'Caja') return { etiqueta: 'Caja', tono: 'secondary' }
  if (fila.tipo === 'Pago' && fila.pago) {
    const { categoria } = fila.pago
    return {
      etiqueta: CATEGORIAS_PAGO.find((c) => c.valor === categoria)?.etiqueta ?? categoria,
      tono: TONO_DE_CATEGORIA_DE_PAGO[categoria] ?? 'secondary',
    }
  }
  return { etiqueta: 'Ajuste', tono: 'secondary' }
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

/** Una fila queda congelada cuando su instante es igual o anterior a la marca de conciliación (borde inclusivo). */
export function estaConciliada(fecha: string, conciliadoHasta: string | null): boolean {
  if (conciliadoHasta === null) return false
  const instante = Date.parse(fecha)
  const marca = Date.parse(conciliadoHasta)
  return !Number.isNaN(instante) && !Number.isNaN(marca) && instante <= marca
}

/** Cada fila con su posición original (orden del servidor, de la más vieja a la más nueva), que desempata el orden por fecha. */
export function entradasDelLibro(filas: readonly FilaDelLibro[]): { fila: FilaDelLibro; indice: number }[] {
  return filas.map((fila, indice) => ({ fila, indice }))
}

/** Clase de la diferencia del arqueo: el faltante usa el rojo del tema y el sobrante el verde. */
export function claseDeDiferencia(valor: number): string {
  const redondeado = redondearImporte(valor)
  if (redondeado < 0) return CLASE_DE_IMPORTE_NEGATIVO

  return redondeado > 0 ? CLASE_DE_IMPORTE_POSITIVO : ''
}

/** Destino del ícono de detalle; `null` cuando la fila no tiene a dónde ir (los pagos solo los abre el Admin). */
export function destinoDelDetalle(fila: FilaDelLibro, puedeAbrirPagos: boolean): string | null {
  if (fila.tipo === 'Caja' && fila.caja) return `/caja/turnos/${fila.caja.idTurnoCaja}/z`
  if (fila.tipo === 'Pago' && fila.pago && puedeAbrirPagos) return `/pagos?id=${fila.pago.idPago}`
  return null
}
