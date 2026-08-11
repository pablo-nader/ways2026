/**
 * Cliente HTTP de `GET /api/reportes/*` (stage-10-agregacion-dashboard, Slice 7 — G1 parity):
 * ventas/resumen y gastos/resumen. `desde`/`hasta` son `DateOnly` del lado del servidor, así que
 * viajan como `YYYY-MM-DD` sin ningún offset horario — a diferencia del filtro de `compras.ts`,
 * que filtra contra un `timestamptz` y sí necesita ese offset.
 */
import { api } from './cliente'
import type { Granularidad, ResumenDeGastos, ResumenDeVentas } from './tipos'

export type FiltrosDeReporte = {
  idEmpresa: number
  idPuntoVenta: number | null
  desde: string
  hasta: string
  granularidad: Granularidad
}

/** Arma el query string compartido por todo `GET /api/reportes/*` que acepta este shape de
 * filtro (`dto-contract-honesty`: cada parámetro solo se agrega si el backend lo lee). */
export function construirQueryDeReporte(filtros: FiltrosDeReporte): string {
  const parametros = new URLSearchParams()
  parametros.set('idEmpresa', String(filtros.idEmpresa))
  if (filtros.idPuntoVenta !== null) parametros.set('idPuntoVenta', String(filtros.idPuntoVenta))
  parametros.set('desde', filtros.desde)
  parametros.set('hasta', filtros.hasta)
  parametros.set('granularidad', filtros.granularidad)
  return `?${parametros.toString()}`
}

export const clienteDeReportes = {
  ventasResumen: (filtros: FiltrosDeReporte) =>
    api.get<ResumenDeVentas>(`/reportes/ventas/resumen${construirQueryDeReporte(filtros)}`),
  gastosResumen: (filtros: FiltrosDeReporte) =>
    api.get<ResumenDeGastos>(`/reportes/gastos/resumen${construirQueryDeReporte(filtros)}`),
}

function aFechaIso(fecha: Date): string {
  const anio = fecha.getFullYear()
  const mes = String(fecha.getMonth() + 1).padStart(2, '0')
  const dia = String(fecha.getDate()).padStart(2, '0')
  return `${anio}-${mes}-${dia}`
}

/** Rango por defecto de `Tablero` (spec tablero: "Default load shows the last 7 days") —
 * `[hoy - 6 días, hoy]`, 7 días inclusive. Recibe `ahora` como parámetro (default `new Date()`)
 * para quedar testeable sin mockear el reloj del sistema. */
export function rangoUltimosSieteDias(ahora: Date = new Date()): { desde: string; hasta: string } {
  const hace6Dias = new Date(ahora)
  hace6Dias.setDate(hace6Dias.getDate() - 6)
  return { desde: aFechaIso(hace6Dias), hasta: aFechaIso(ahora) }
}
