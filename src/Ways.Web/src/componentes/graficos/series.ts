/**
 * Forma mínima de un bucket de un reporte de series temporales del backend
 * (p. ej. `BucketDeVentas`). Estructural a propósito: este archivo no importa
 * los tipos de `src/api/` para no acoplar `componentes/graficos/` a un
 * reporte puntual — cualquier bucket con `etiqueta` + un valor numérico
 * (posiblemente `null`, como `ticketPromedio` sin denominador) encaja.
 */
export type BucketDeReporte = {
  etiqueta: string
  valor: number | null
}

/** Punto que consumen los wrappers de gráfico (`GraficoDeLineas`, `GraficoDeBarras`). */
export type PuntoDeGrafico = {
  etiqueta: string
  valor: number
}

/**
 * Convierte los buckets de un reporte a la forma que consumen los wrappers de
 * gráfico. Un `valor` `null` (p. ej. ticket promedio sin ventas en el bucket)
 * se mapea a `0` solo para el eje del gráfico — el dato crudo nunca debe
 * mostrarse como cifra de texto sin pasar antes por su propio manejo de
 * nulabilidad (ver `Rentabilidad`/`ResumenDeVentas`, campos nullable).
 */
export function aSerieDeGrafico(buckets: readonly BucketDeReporte[]): PuntoDeGrafico[] {
  return buckets.map(({ etiqueta, valor }) => ({ etiqueta, valor: valor ?? 0 }))
}
