import type { AlicuotaDeLibroIva, FilaDeLibroIva, LibroIva } from '../api/tipos'

export type PestanaDeLibroIva = 'compras' | 'ventas'

/** Lo único que cambia entre las dos pestañas: los rótulos de la contraparte y si hay columnas de
 * percepciones (en ventas el backend las manda siempre en cero y no se muestran). */
export const CONFIGURACION_DE_PESTANA: Record<
  PestanaDeLibroIva,
  { titulo: string; contraparte: string; documento: string; conPercepciones: boolean }
> = {
  compras: { titulo: 'Compras', contraparte: 'Proveedor', documento: 'CUIT', conPercepciones: true },
  ventas: { titulo: 'Ventas', contraparte: 'Cliente', documento: 'Documento', conPercepciones: false },
}

/** Porcentajes con columna propia: los presentes en los totales y en las filas, de mayor a menor.
 * Un porcentaje sin ningún importe no genera columna. */
export function porcentajesDelLibro(libro: LibroIva): number[] {
  const porcentajes = new Set<number>()
  for (const alicuota of libro.totales.porAlicuota) porcentajes.add(alicuota.porcentaje)
  for (const fila of libro.filas) for (const alicuota of fila.alicuotas) porcentajes.add(alicuota.porcentaje)
  return [...porcentajes].sort((a, b) => b - a)
}

export function alicuotaDeFila(fila: FilaDeLibroIva, porcentaje: number): AlicuotaDeLibroIva | null {
  return fila.alicuotas.find((a) => a.porcentaje === porcentaje) ?? null
}

/** "21%", "10,5%", "0%": sin ceros de más y con coma decimal, como el encabezado del export. */
export function etiquetaDePorcentaje(porcentaje: number): string {
  return `${String(porcentaje).replace('.', ',')}%`
}

/** El período se puede consultar solo con las dos fechas cargadas y `desde` no posterior a `hasta`
 * (el backend rechaza lo contrario con 400). Las fechas son `YYYY-MM-DD`, comparables como texto. */
export function periodoValido(desde: string, hasta: string): boolean {
  return desde !== '' && hasta !== '' && desde <= hasta
}

/** `2026-05-10` → `10/05/2026`, sin pasar por `Date` (no hay zona horaria que corra el día). */
export function formatearFechaDeLibro(fechaIso: string): string {
  const [anio, mes, dia] = fechaIso.split('-')
  return `${dia}/${mes}/${anio}`
}

/** Comprobantes cuyos componentes no cierran contra su total: se muestran, nunca se ocultan. */
export function filasConDiferencia(libro: LibroIva): FilaDeLibroIva[] {
  return libro.filas.filter((fila) => fila.diferencia !== 0)
}
