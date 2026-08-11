/**
 * Cliente HTTP de `GET /api/reportes/*` (stage-10-agregacion-dashboard, Slice 7 — G1 parity;
 * Slice 8 — desglose por dimensión): ventas/resumen, gastos/resumen y los tres breakdowns de
 * ventas. `desde`/`hasta` son `DateOnly` del lado del servidor, así que viajan como `YYYY-MM-DD`
 * sin ningún offset horario — a diferencia del filtro de `compras.ts`, que filtra contra un
 * `timestamptz` y sí necesita ese offset.
 */
import { api } from './cliente'
import type {
  Granularidad,
  Rentabilidad,
  ResumenDeGastos,
  ResumenDeVentas,
  TopArticulos,
  VentasPorMedioPago,
  VentasPorPuntoVenta,
  VentasPorVendedor,
} from './tipos'

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

/** Filtro de los breakdowns por dimensión: sin `granularidad` — ninguno de los tres bucketea por
 * tiempo, cada fila ya es un subtotal propio del período completo (dto-contract-honesty: el
 * backend no lee ese parámetro en estas tres rutas). */
export type FiltrosDeBreakdown = { idEmpresa: number; desde: string; hasta: string }

/** `por-punto-venta` además NO acepta `idPuntoVenta` — sería una contradicción filtrar por el
 * mismo campo que se está agrupando (design: Endpoints). Los otros dos breakdowns sí lo aceptan. */
export type FiltrosDeBreakdownConPv = FiltrosDeBreakdown & { idPuntoVenta: number | null }

export function construirQueryDeBreakdown(filtros: FiltrosDeBreakdown): string {
  const parametros = new URLSearchParams()
  parametros.set('idEmpresa', String(filtros.idEmpresa))
  parametros.set('desde', filtros.desde)
  parametros.set('hasta', filtros.hasta)
  return `?${parametros.toString()}`
}

export function construirQueryDeBreakdownConPv(filtros: FiltrosDeBreakdownConPv): string {
  const parametros = new URLSearchParams()
  parametros.set('idEmpresa', String(filtros.idEmpresa))
  if (filtros.idPuntoVenta !== null) parametros.set('idPuntoVenta', String(filtros.idPuntoVenta))
  parametros.set('desde', filtros.desde)
  parametros.set('hasta', filtros.hasta)
  return `?${parametros.toString()}`
}

/** `articulos/top` reutiliza el shape de `FiltrosDeBreakdownConPv` (acepta `idPuntoVenta`, a
 * diferencia de `por-punto-venta`) y suma `limite` — único parámetro propio de esta ruta
 * (`ReportesEndpoints.cs`: `int? limite`). */
export type FiltrosDeTopArticulos = FiltrosDeBreakdownConPv & { limite: number | null }

/** `rentabilidad` reutiliza el shape de `FiltrosDeBreakdownConPv` y suma `incluirEstimados` —
 * único parámetro propio de esta ruta (`ReportesEndpoints.cs`: `bool? incluirEstimados`, ausente
 * en la query string ⇒ excluido por default, spec rentabilidad-y-comisiones: Margin Excludes
 * Estimated Cost Lines By Default). */
export type FiltrosDeRentabilidad = FiltrosDeBreakdownConPv & { incluirEstimados: boolean }

export const clienteDeReportes = {
  ventasResumen: (filtros: FiltrosDeReporte) =>
    api.get<ResumenDeVentas>(`/reportes/ventas/resumen${construirQueryDeReporte(filtros)}`),
  gastosResumen: (filtros: FiltrosDeReporte) =>
    api.get<ResumenDeGastos>(`/reportes/gastos/resumen${construirQueryDeReporte(filtros)}`),
  ventasPorPuntoVenta: (filtros: FiltrosDeBreakdown) =>
    api.get<VentasPorPuntoVenta>(`/reportes/ventas/por-punto-venta${construirQueryDeBreakdown(filtros)}`),
  ventasPorVendedor: (filtros: FiltrosDeBreakdownConPv) =>
    api.get<VentasPorVendedor>(`/reportes/ventas/por-vendedor${construirQueryDeBreakdownConPv(filtros)}`),
  ventasPorMedioPago: (filtros: FiltrosDeBreakdownConPv) =>
    api.get<VentasPorMedioPago>(`/reportes/ventas/por-medio-pago${construirQueryDeBreakdownConPv(filtros)}`),
  articulosTop: (filtros: FiltrosDeTopArticulos) => {
    const query = construirQueryDeBreakdownConPv(filtros)
    const conLimite = filtros.limite === null ? query : `${query}&limite=${filtros.limite}`
    return api.get<TopArticulos>(`/reportes/articulos/top${conLimite}`)
  },
  rentabilidad: (filtros: FiltrosDeRentabilidad) => {
    const query = construirQueryDeBreakdownConPv(filtros)
    const conEstimados = filtros.incluirEstimados ? `${query}&incluirEstimados=true` : query
    return api.get<Rentabilidad>(`/reportes/rentabilidad${conEstimados}`)
  },
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
