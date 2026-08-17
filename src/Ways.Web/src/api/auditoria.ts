/**
 * Cliente de `GET /api/auditoria` + `/export` (Slice 5/6) y ahora también el cliente JSON
 * (`clienteDeAuditoria`, Slice 7) consumido por `Auditoria.tsx`. `FiltrosDeExportacionDeAuditoria`
 * (export, Slice 6) y `FiltrosDeConsultaDeAuditoria` (JSON, Slice 7) son shapes de pantalla
 * distintos, mismo criterio que `reportes.ts` (`FiltrosDeHistoricoDeCajas` vs. el query del
 * export): los tipos de filtro propios de un dominio viven en el archivo del dominio, nunca en
 * `tipos.ts` — ahí solo viven los DTOs espejo del backend (`FiltrosDeAuditoria`/
 * `FilaDeAuditoria`/`PaginaDeAuditoria`, `dto-contract-honesty`, design decisión 8/Orchestrator
 * Decision 8).
 *
 * Mismo criterio de offset local que `reportes.ts` (`fechaIsoConOffset`/`desplazamientoUtcLocal`,
 * duplicado a propósito: no hay un módulo compartido de utilidades de fecha en esta web todavía) —
 * `auditoria.creado_el` es `timestamptz`, igual que `cajas.fecha_cierre`. `rangoUltimosSieteDias`
 * en cambio SÍ se reutiliza de `reportes.ts` (mismo criterio que `Tesoreria.tsx`/`Tablero.tsx`,
 * que ya lo importan cross-dominio) — es un helper de fecha genérico, no un concern propio de
 * reportes.
 */
import { api } from './cliente'
import { rangoUltimosSieteDias } from './reportes'
import type { PaginaDeAuditoria } from './tipos'

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

/** Filtros del export de auditoría — `desde`/`hasta` son OBLIGATORIOS acá (a diferencia del
 * futuro `GET /api/auditoria` JSON, Slice 7): regla de la casa del export + nombre de archivo
 * determinístico (mismo criterio que `historicoDeCajas`/`tesoreria` en `reportes.ts`). El resto
 * replica 1:1 los 5 filtros opcionales de `FiltrosDeAuditoria` (backend, Slice 5). */
export type FiltrosDeExportacionDeAuditoria = {
  desde: string
  hasta: string
  accion: string | null
  idActor: number | null
  entidad: string | null
  idEntidad: number | null
  idPuntoVenta: number | null
}

function construirQueryDeExportacionDeAuditoria(filtros: FiltrosDeExportacionDeAuditoria): string {
  const parametros = new URLSearchParams()
  parametros.set('desde', fechaIsoConOffset(filtros.desde, '00:00:00'))
  parametros.set('hasta', fechaIsoConOffset(filtros.hasta, '23:59:59.999'))
  if (filtros.accion !== null) parametros.set('accion', filtros.accion)
  if (filtros.idActor !== null) parametros.set('idActor', String(filtros.idActor))
  if (filtros.entidad !== null) parametros.set('entidad', filtros.entidad)
  if (filtros.idEntidad !== null) parametros.set('idEntidad', String(filtros.idEntidad))
  if (filtros.idPuntoVenta !== null) parametros.set('idPuntoVenta', String(filtros.idPuntoVenta))
  return `?${parametros.toString()}`
}

/** Misma convención de `reportes.ts` (`rutasDeExportacion.<dominio>(filtros)`), en su propio
 * módulo. */
export const rutasDeExportacion = {
  auditoria: (filtros: FiltrosDeExportacionDeAuditoria) =>
    `/auditoria/export${construirQueryDeExportacionDeAuditoria(filtros)}&formato=xlsx`,
}

// ---- Cliente JSON de `GET /api/auditoria` (Slice 7, `Auditoria.tsx`) --------------------------

/** Filtro de pantalla de `Auditoria.tsx` — mismo shape que `FiltrosDeHistoricoDeCajas`
 * (`reportes.ts`): `desde`/`hasta` en formato `input[type=date]` (`YYYY-MM-DD`), a diferencia de
 * `FiltrosDeAuditoria` (`tipos.ts`, el DTO crudo del backend, `DateTimeOffset?` ISO completo) —
 * `construirQueryDeConsultaDeAuditoria` hace la conversión recién al armar el query string, mismo
 * criterio que `construirQueryDeExportacionDeAuditoria` arriba. `desde`/`hasta` vacíos SÍ están
 * permitidos acá (a diferencia del export, que los exige): el listado JSON no necesita un rango
 * acotado para nombrar un archivo. */
export type FiltrosDeConsultaDeAuditoria = {
  desde: string
  hasta: string
  accion: string | null
  idActor: number | null
  entidad: string | null
  idEntidad: number | null
  idPuntoVenta: number | null
  pagina: number
  tamanio: number
}

/** Default de `Auditoria.tsx` al montar — últimos 7 días (mismo criterio "vacíos" de
 * `filtrosDeHistoricoDeCajasVacios`: el nombre dice "vacíos" pero el rango de fechas viene
 * prellenado), sin ningún otro filtro y página 1. */
export function filtrosDeAuditoriaVacios(): FiltrosDeConsultaDeAuditoria {
  const rango = rangoUltimosSieteDias()
  return {
    desde: rango.desde,
    hasta: rango.hasta,
    accion: null,
    idActor: null,
    entidad: null,
    idEntidad: null,
    idPuntoVenta: null,
    pagina: 1,
    tamanio: 25,
  }
}

/** Query compartido de `GET /api/auditoria` — `dto-contract-honesty`: cada parámetro solo se
 * agrega si `AuditoriaEndpoints.cs` lo lee (`desde, hasta, accion, idActor, entidad, idEntidad,
 * idPuntoVenta, pagina, tamanio`, todos opcionales salvo pagina/tamanio con default del propio
 * endpoint — acá se mandan siempre explícitos, mismo criterio que `construirQueryDeHistoricoDeCajas`). */
function construirQueryDeConsultaDeAuditoria(filtros: FiltrosDeConsultaDeAuditoria): string {
  const parametros = new URLSearchParams()
  if (filtros.desde) parametros.set('desde', fechaIsoConOffset(filtros.desde, '00:00:00'))
  if (filtros.hasta) parametros.set('hasta', fechaIsoConOffset(filtros.hasta, '23:59:59.999'))
  if (filtros.accion !== null) parametros.set('accion', filtros.accion)
  if (filtros.idActor !== null) parametros.set('idActor', String(filtros.idActor))
  if (filtros.entidad !== null) parametros.set('entidad', filtros.entidad)
  if (filtros.idEntidad !== null) parametros.set('idEntidad', String(filtros.idEntidad))
  if (filtros.idPuntoVenta !== null) parametros.set('idPuntoVenta', String(filtros.idPuntoVenta))
  parametros.set('pagina', String(filtros.pagina))
  parametros.set('tamanio', String(filtros.tamanio))
  return `?${parametros.toString()}`
}

/** Único consumidor JSON de `/api/auditoria` — el export (Slice 6) ya vive en
 * `rutasDeExportacion.auditoria` arriba, sin pasar por `api.get` (es una descarga de archivo). */
export const clienteDeAuditoria = {
  consultar: (filtros: FiltrosDeConsultaDeAuditoria) =>
    api.get<PaginaDeAuditoria>(`/auditoria${construirQueryDeConsultaDeAuditoria(filtros)}`),
}
