/**
 * Cliente HTTP de `GET /api/reportes/*` (stage-10-agregacion-dashboard, Slice 7 — G1 parity;
 * Slice 8 — desglose por dimensión): ventas/resumen, gastos/resumen y los tres breakdowns de
 * ventas. `desde`/`hasta` son `DateOnly` del lado del servidor, así que viajan como `YYYY-MM-DD`
 * sin ningún offset horario — a diferencia del filtro de `compras.ts`, que filtra contra un
 * `timestamptz` y sí necesita ese offset.
 */
import { api } from './cliente'
import type {
  Comisiones,
  Granularidad,
  PaginaDeHistoricoDeCajas,
  PaginaDeMovimientosTesoreria,
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
  comisiones: (filtros: FiltrosDeBreakdownConPv) =>
    api.get<Comisiones>(`/reportes/comisiones${construirQueryDeBreakdownConPv(filtros)}`),
  historicoDeCajas: (filtros: FiltrosDeHistoricoDeCajas) =>
    api.get<PaginaDeHistoricoDeCajas>(`/reportes/cajas${construirQueryDeHistoricoDeCajas(filtros)}`),
  tesoreria: (filtros: FiltrosDeTesoreria) =>
    api.get<PaginaDeMovimientosTesoreria>(`/reportes/tesoreria${construirQueryDeTesoreria(filtros)}`),
}

// ---- Offset local para desde/hasta de /cajas y /tesoreria (stage-11-exportacion-reportes,
// Slices 6a/7): a diferencia de ventas/resumen (arriba, `DateOnly` del lado del servidor), estas
// dos rutas filtran contra un `timestamptz` (`fecha_cierre`/`fecha`) — mismo criterio de offset
// explícito que `compras.ts`/`cuentaCorriente.ts`. Duplicado a propósito: no hay un módulo
// compartido de utilidades de fecha en esta web todavía. -------------------------------------
function desplazamientoUtcLocal(anio: number, mes: number, dia: number): string {
  const minutos = new Date(anio, mes - 1, dia).getTimezoneOffset()
  const signo = minutos > 0 ? '-' : '+'
  const minutosAbsolutos = Math.abs(minutos)
  const horas = String(Math.floor(minutosAbsolutos / 60)).padStart(2, '0')
  const restoMinutos = String(minutosAbsolutos % 60).padStart(2, '0')
  return `${signo}${horas}:${restoMinutos}`
}

function fechaIsoConOffset(fechaIso: string, horaLimite: string): string {
  const [anio, mes, dia] = fechaIso.split('-').map(Number)
  return `${fechaIso}T${horaLimite}${desplazamientoUtcLocal(anio, mes, dia)}`
}

/** Filtro de `GET /api/reportes/cajas` — `idPuntoVenta` es opcional (a diferencia de
 * `FiltrosDeTesoreria`): el histórico de cierres admite "Todos". */
export type FiltrosDeHistoricoDeCajas = {
  idPuntoVenta: number | null
  desde: string
  hasta: string
  pagina: number
  tamanio: number
}

export function filtrosDeHistoricoDeCajasVacios(): FiltrosDeHistoricoDeCajas {
  const rango = rangoUltimosSieteDias()
  return { idPuntoVenta: null, desde: rango.desde, hasta: rango.hasta, pagina: 1, tamanio: 25 }
}

/** Query compartido de `/cajas` y `/cajas/export` — `pagina`/`tamanio` NO viajan al export
 * (`ListarCierresParaExportacionAsync` no los lee, dto-contract-honesty), así que quedan fuera de
 * este helper y los agrega solo `construirQueryDeHistoricoDeCajas`. */
function construirQueryDeAlcanceDeCajas(filtros: { idPuntoVenta: number | null; desde: string; hasta: string }): string {
  const parametros = new URLSearchParams()
  if (filtros.idPuntoVenta !== null) parametros.set('idPuntoVenta', String(filtros.idPuntoVenta))
  if (filtros.desde) parametros.set('desde', fechaIsoConOffset(filtros.desde, '00:00:00'))
  if (filtros.hasta) parametros.set('hasta', fechaIsoConOffset(filtros.hasta, '23:59:59.999'))
  return `?${parametros.toString()}`
}

export function construirQueryDeHistoricoDeCajas(filtros: FiltrosDeHistoricoDeCajas): string {
  const parametros = new URLSearchParams(construirQueryDeAlcanceDeCajas(filtros).slice(1))
  parametros.set('pagina', String(filtros.pagina))
  parametros.set('tamanio', String(filtros.tamanio))
  return `?${parametros.toString()}`
}

/** Filtro de `GET /api/reportes/tesoreria` — `idPuntoVenta` es OBLIGATORIO (a diferencia de
 * `FiltrosDeHistoricoDeCajas`): mezclar puntos de venta rompería el significado de la cadena
 * inicio/final (design decisión 11), así que acá no existe la opción "Todos". */
export type FiltrosDeTesoreria = {
  idPuntoVenta: number
  desde: string
  hasta: string
  pagina: number
  tamanio: number
}

function construirQueryDeAlcanceDeTesoreria(filtros: { idPuntoVenta: number; desde: string; hasta: string }): string {
  const parametros = new URLSearchParams()
  parametros.set('idPuntoVenta', String(filtros.idPuntoVenta))
  if (filtros.desde) parametros.set('desde', fechaIsoConOffset(filtros.desde, '00:00:00'))
  if (filtros.hasta) parametros.set('hasta', fechaIsoConOffset(filtros.hasta, '23:59:59.999'))
  return `?${parametros.toString()}`
}

export function construirQueryDeTesoreria(filtros: FiltrosDeTesoreria): string {
  const parametros = new URLSearchParams(construirQueryDeAlcanceDeTesoreria(filtros).slice(1))
  parametros.set('pagina', String(filtros.pagina))
  parametros.set('tamanio', String(filtros.tamanio))
  return `?${parametros.toString()}`
}

/** Rutas de descarga (`/export`, stage-11 slice 4) de los tres paneles de `Tablero` que ya tienen
 * su ruta `/export` mergeada: ventas resumen y gastos resumen (card G1) y rentabilidad. Reutilizan
 * el mismo query string que su ruta JSON hermana — `formato=xlsx` es el único parámetro propio de
 * la descarga (spec exportación-de-reportes: `formato` es requerido, único valor legal `xlsx`). */
export const rutasDeExportacion = {
  ventasResumen: (filtros: FiltrosDeReporte) =>
    `/reportes/ventas/resumen/export${construirQueryDeReporte(filtros)}&formato=xlsx`,
  gastosResumen: (filtros: FiltrosDeReporte) =>
    `/reportes/gastos/resumen/export${construirQueryDeReporte(filtros)}&formato=xlsx`,
  rentabilidad: (filtros: FiltrosDeRentabilidad) => {
    const query = construirQueryDeBreakdownConPv(filtros)
    const conEstimados = filtros.incluirEstimados ? `${query}&incluirEstimados=true` : query
    return `/reportes/rentabilidad/export${conEstimados}&formato=xlsx`
  },
  /** `desde`/`hasta` son OBLIGATORIOS en `/cajas/export` (a diferencia de `/cajas`): un nombre de
   * archivo determinístico necesita un rango acotado (mismo criterio que los exports de listado
   * de Slice 3). `pagina`/`tamanio` no viajan — el export no pagina. */
  historicoDeCajas: (filtros: { idPuntoVenta: number | null; desde: string; hasta: string }) =>
    `/reportes/cajas/export${construirQueryDeAlcanceDeCajas(filtros)}&formato=xlsx`,
  /** `desde`/`hasta` OBLIGATORIOS en `/tesoreria/export`, mismo criterio que `historicoDeCajas`. */
  tesoreria: (filtros: { idPuntoVenta: number; desde: string; hasta: string }) =>
    `/reportes/tesoreria/export${construirQueryDeAlcanceDeTesoreria(filtros)}&formato=xlsx`,
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
