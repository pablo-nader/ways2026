/**
 * stage-14-auditoria-trazabilidad (Slice 6): SOLO el constructor de ruta de exportación —
 * `rutasDeExportacion.auditoria(filtros)`. El cliente JSON (`clienteDeAuditoria`) y los espejos
 * de tipos (`FiltrosDeAuditoria`/`FilaDeAuditoria`/`PaginaDeAuditoria`, `dto-contract-honesty`,
 * design decisión 8) quedan para la Slice 7 (la pantalla `Auditoria.tsx`) — acá no hay ningún
 * consumidor todavía.
 *
 * `tipos.ts` no se modifica en esta slice: es un módulo de tipos verdaderamente transversales
 * (`ROL`, `EstadoUsuario`, `PaginaDe<T>`, …), y el precedente de `reportes.ts` es que los tipos de
 * filtro propios de un dominio (`FiltrosDeHistoricoDeCajas`, `FiltrosDeTesoreria`, …) viven en el
 * archivo del dominio, nunca en `tipos.ts` — `FiltrosDeExportacionDeAuditoria` sigue ese mismo
 * criterio acá.
 *
 * Mismo criterio de offset local que `reportes.ts` (`fechaIsoConOffset`/`desplazamientoUtcLocal`,
 * duplicado a propósito: no hay un módulo compartido de utilidades de fecha en esta web todavía) —
 * `auditoria.creado_el` es `timestamptz`, igual que `cajas.fecha_cierre`.
 */
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
 * módulo — `auditoria.ts` no depende de `reportes.ts` ni viceversa. */
export const rutasDeExportacion = {
  auditoria: (filtros: FiltrosDeExportacionDeAuditoria) =>
    `/auditoria/export${construirQueryDeExportacionDeAuditoria(filtros)}&formato=xlsx`,
}
