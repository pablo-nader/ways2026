/**
 * Cliente de `GET /api/auditoria` + `/export` (Slice 5/6) y ahora también el cliente JSON
 * (`clienteDeAuditoria`, Slice 7) consumido por `Auditoria.tsx`. `FiltrosDeExportacionDeAuditoria`
 * (export, Slice 6) y `FiltrosDeConsultaDeAuditoria` (JSON, Slice 7) comparten los mismos 7
 * filtros de alcance vía `FiltrosDeAlcanceDeAuditoria`/`construirQueryDeAlcanceDeAuditoria`
 * (judgment-day ronda 1, finding 1) — `FiltrosDeConsultaDeAuditoria` solo suma `pagina`/`tamanio`,
 * propios de la ruta JSON. Mismo criterio que `reportes.ts` (`FiltrosDeHistoricoDeCajas` vs. el
 * query del export): los tipos de filtro propios de un dominio viven en el archivo del dominio,
 * nunca en `tipos.ts` — ahí solo viven los DTOs espejo del backend que sí comparten forma exacta
 * con el mirror (`FilaDeAuditoria`/`PaginaDeAuditoria`, `dto-contract-honesty`, design decisión 8/
 * Orchestrator Decision 8). El DTO crudo del backend (`FiltrosDeAuditoria`, `Contratos.cs`) NO
 * tiene mirror en `tipos.ts` (judgment-day, ronda 2, juez A) — su forma difiere genuinamente de
 * los filtros de pantalla definidos acá, así que un mirror sin consumidor de tipo se hubiera
 * quedado inerte.
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

/** Filtros compartidos por AMBAS superficies de `/api/auditoria` (JSON y export) — mismo shape,
 * mismo builder (`construirQueryDeAlcanceDeAuditoria` abajo), mismo criterio de guardas que
 * `construirQueryDeAlcanceDeCajas` (`reportes.ts:177`): un único lugar donde vive la guarda de
 * `desde`/`hasta` vacíos, para que el caso vacío quede resuelto igual en los dos lados por
 * construcción — dos builders divergentes (judgment-day, ronda 1, finding 1) dejaban el caso
 * vacío guardado en la consulta JSON pero NO en el export, que mandaba
 * `desde=...T00:00:00+NaN:NaN` (`fechaIsoConOffset` sobre un string vacío) — un `DateTimeOffset`
 * malformado que el servidor rechaza con 400. */
export type FiltrosDeAlcanceDeAuditoria = {
  desde: string
  hasta: string
  accion: string | null
  idActor: number | null
  entidad: string | null
  idEntidad: number | null
  idPuntoVenta: number | null
}

function construirQueryDeAlcanceDeAuditoria(filtros: FiltrosDeAlcanceDeAuditoria): string {
  const parametros = new URLSearchParams()
  if (filtros.desde) parametros.set('desde', fechaIsoConOffset(filtros.desde, '00:00:00'))
  if (filtros.hasta) parametros.set('hasta', fechaIsoConOffset(filtros.hasta, '23:59:59.999'))
  if (filtros.accion !== null) parametros.set('accion', filtros.accion)
  if (filtros.idActor !== null) parametros.set('idActor', String(filtros.idActor))
  if (filtros.entidad !== null) parametros.set('entidad', filtros.entidad)
  if (filtros.idEntidad !== null) parametros.set('idEntidad', String(filtros.idEntidad))
  if (filtros.idPuntoVenta !== null) parametros.set('idPuntoVenta', String(filtros.idPuntoVenta))
  return `?${parametros.toString()}`
}

/** Filtros del export de auditoría — mismo shape que `FiltrosDeAlcanceDeAuditoria`.
 * `AuditoriaEndpoints.cs:44` (`/export`) declara `DateTimeOffset desde, DateTimeOffset hasta` SIN
 * `?` — a diferencia de `/` (Slice 7, ambos opcionales) — así que el servidor rechaza esta ruta
 * con `desde`/`hasta` vacíos. Decisión de producto (judgment-day, ronda 1, finding 1): con
 * filtros de fecha vacíos, `Auditoria.tsx` deshabilita el botón de descarga
 * (`puedeExportarAuditoria` abajo) en vez de emitir una URL que el servidor va a rechazar. El
 * guard de `construirQueryDeAlcanceDeAuditoria` sigue aplicando igual acá como defensa en
 * profundidad — con el botón deshabilitado, esta rama no debería ejecutarse en producción. */
export type FiltrosDeExportacionDeAuditoria = FiltrosDeAlcanceDeAuditoria

/** Habilita el botón de descarga de `Auditoria.tsx` solo cuando el rango está completo —
 * `/export` exige `desde`/`hasta` no nulos (`AuditoriaEndpoints.cs:44`), a diferencia de la
 * consulta JSON (`/`, ambos opcionales). */
export function puedeExportarAuditoria(filtros: { desde: string; hasta: string }): boolean {
  return filtros.desde !== '' && filtros.hasta !== ''
}

/** Misma convención de `reportes.ts` (`rutasDeExportacion.<dominio>(filtros)`), en su propio
 * módulo. */
export const rutasDeExportacion = {
  auditoria: (filtros: FiltrosDeExportacionDeAuditoria) =>
    `/auditoria/export${construirQueryDeAlcanceDeAuditoria(filtros)}&formato=xlsx`,
}

// ---- Cliente JSON de `GET /api/auditoria` (Slice 7, `Auditoria.tsx`) --------------------------

/** Filtro de pantalla de `Auditoria.tsx` — mismo shape que `FiltrosDeHistoricoDeCajas`
 * (`reportes.ts`): `desde`/`hasta` en formato `input[type=date]` (`YYYY-MM-DD`), a diferencia de
 * `FiltrosDeAuditoria` (`Ways.Application.Auditoria.Contratos`, el DTO crudo del backend,
 * `DateTimeOffset?` ISO completo, SIN mirror en `tipos.ts` — ver nota de `dto-contract-honesty`
 * arriba) — `construirQueryDeConsultaDeAuditoria` hace la conversión recién al armar el query
 * string, mismo criterio que `construirQueryDeAlcanceDeAuditoria` arriba. `desde`/`hasta` vacíos
 * SÍ están permitidos acá (a diferencia del export, que los exige): el listado JSON no necesita
 * un rango acotado para nombrar un archivo — el botón de descarga se deshabilita en ese caso
 * (`puedeExportarAuditoria`). */
export type FiltrosDeConsultaDeAuditoria = FiltrosDeAlcanceDeAuditoria & {
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
 * endpoint). Los 7 filtros de alcance vienen de `construirQueryDeAlcanceDeAuditoria` (mismo
 * builder que usa el export, arriba) — `pagina`/`tamanio` son propios de esta ruta y no viajan al
 * export, mismo criterio que `construirQueryDeHistoricoDeCajas`/`construirQueryDeAlcanceDeCajas`
 * (`reportes.ts:177-189`). */
export function construirQueryDeConsultaDeAuditoria(filtros: FiltrosDeConsultaDeAuditoria): string {
  const parametros = new URLSearchParams(construirQueryDeAlcanceDeAuditoria(filtros).slice(1))
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
