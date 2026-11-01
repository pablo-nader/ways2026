/**
 * Cliente HTTP + reglas puras de caja (stage-6-turnos-caja, Slice 6): apertura de turno,
 * movimientos físicos fuera de la venta (retiro/refuerzo/apertura de cajón) y resumen parcial.
 * `motivoValido`/`importeValidoParaTipo` espejan `ReglaDeMovimientosDeCaja` (Ways.Domain.Caja)
 * para dar feedback instantáneo en pantalla — nunca son autoritativos, el servidor vuelve a
 * validar todo (mismo criterio que `pagos.ts`, stage-5).
 */
import { api } from './cliente'
import { fechaIsoConOffset } from './fechas'
import { agregarOrdenAlQuery, type OrdenDeServidor } from './reportes'
import type {
  DetalleDeTurno,
  FilaDeRetiro,
  MovimientoRegistrado,
  PaginaDeRetiros,
  PaginaDeTurnos,
  ResumenDeCierrePorRetiro,
  ResumenDeTurno,
  SolicitudDeApertura,
  SolicitudDeCierre,
  SolicitudDeCierrePorRetiro,
  SolicitudDeEdicionDeRetiro,
  SolicitudDeMovimiento,
  TipoMovimientoCaja,
  TurnoConArqueos,
  TurnoResumen,
} from './tipos'

/**
 * ¿Trae el resumen de cierre los tres importes del desglose y todos son números? Un payload anterior
 * al desglose, o parcial, no los trae: el ticket y el detalle omiten el bloque en lugar de mostrar
 * `undefined` o `NaN`.
 */
export function tieneDesgloseDeCierre(resumen: ResumenDeCierrePorRetiro): boolean {
  return [resumen.totalDigitales, resumen.totalEfectivo, resumen.ventasACuentaCorriente].every(
    (valor) => typeof valor === 'number' && Number.isFinite(valor),
  )
}

/** Claves de `orden` que acepta `GET /api/caja/turnos`: una por cada columna de datos de "Cierres de caja". */
export const COLUMNAS_ORDENABLES_DE_CIERRES = ['turno', 'apertura', 'cierre'] as const

export type ColumnaOrdenableDeCierres = (typeof COLUMNAS_ORDENABLES_DE_CIERRES)[number]

export type OrdenDeCierres = OrdenDeServidor<ColumnaOrdenableDeCierres>

export function esColumnaOrdenableDeCierres(columna: string): columna is ColumnaOrdenableDeCierres {
  return (COLUMNAS_ORDENABLES_DE_CIERRES as readonly string[]).includes(columna)
}

/** Query de los cierres de un punto de venta: sin `orden` no viajan ni `orden` ni `direccion` y el servidor
 * responde con el más reciente primero. */
export function construirQueryDeCierres(
  idPuntoVenta: number,
  pagina: number,
  tamanio: number,
  orden: OrdenDeCierres = null,
): string {
  const parametros = new URLSearchParams()
  parametros.set('idPuntoVenta', String(idPuntoVenta))
  parametros.set('estado', 'Cerrado')
  agregarOrdenAlQuery(parametros, orden)
  parametros.set('pagina', String(pagina))
  parametros.set('tamanio', String(tamanio))
  return `?${parametros.toString()}`
}

/** Claves de `orden` de `GET /api/caja/retiros`: una por cada columna de datos de la grilla. */
export const COLUMNAS_ORDENABLES_DE_RETIROS = [
  'fecha',
  'puntoVenta',
  'turno',
  'empleado',
  'importe',
  'motivo',
  'estadoTurno',
  'conciliado',
  'esRetiroDeCierre',
] as const

export type ColumnaOrdenableDeRetiros = (typeof COLUMNAS_ORDENABLES_DE_RETIROS)[number]

export type OrdenDeRetiros = OrdenDeServidor<ColumnaOrdenableDeRetiros>

export function esColumnaOrdenableDeRetiros(columna: string): columna is ColumnaOrdenableDeRetiros {
  return (COLUMNAS_ORDENABLES_DE_RETIROS as readonly string[]).includes(columna)
}

/** Lo que muestran los controles de filtro; cada clave es un parámetro de `GET /api/caja/retiros`. `desde`/`hasta` son
 * `YYYY-MM-DD` o `''`; `idTurnoCaja` se tipea como texto y solo viaja si es un entero. */
export type FiltrosDeRetiros = {
  desde: string
  hasta: string
  idPuntoVenta: number | null
  idTurnoCaja: string
  idEmpleado: number | null
  importeDesde: number | null
  importeHasta: number | null
  motivo: string
  turnoCerrado: boolean | null
  conciliado: boolean | null
  esRetiroDeCierre: boolean | null
  pagina: number
  tamanio: number
}

export function filtrosDeRetirosVacios(): FiltrosDeRetiros {
  return {
    desde: '',
    hasta: '',
    idPuntoVenta: null,
    idTurnoCaja: '',
    idEmpleado: null,
    importeDesde: null,
    importeHasta: null,
    motivo: '',
    turnoCerrado: null,
    conciliado: null,
    esRetiroDeCierre: null,
    pagina: 1,
    tamanio: 25,
  }
}

/** Query de la grilla de retiros: un parámetro por filtro activo, `orden` + `direccion` solo con orden. */
export function construirQueryDeRetiros(filtros: FiltrosDeRetiros, orden: OrdenDeRetiros = null): string {
  const parametros = new URLSearchParams()
  if (filtros.desde) parametros.set('desde', fechaIsoConOffset(filtros.desde, '00:00:00'))
  if (filtros.hasta) parametros.set('hasta', fechaIsoConOffset(filtros.hasta, '23:59:59.999'))
  if (filtros.idPuntoVenta !== null) parametros.set('idPuntoVenta', String(filtros.idPuntoVenta))
  if (/^\d+$/.test(filtros.idTurnoCaja.trim())) parametros.set('idTurnoCaja', filtros.idTurnoCaja.trim())
  if (filtros.idEmpleado !== null) parametros.set('idEmpleado', String(filtros.idEmpleado))
  if (filtros.importeDesde !== null) parametros.set('importeDesde', String(filtros.importeDesde))
  if (filtros.importeHasta !== null) parametros.set('importeHasta', String(filtros.importeHasta))
  if (filtros.motivo.trim()) parametros.set('motivo', filtros.motivo.trim())
  if (filtros.turnoCerrado !== null) parametros.set('turnoCerrado', String(filtros.turnoCerrado))
  if (filtros.conciliado !== null) parametros.set('conciliado', String(filtros.conciliado))
  if (filtros.esRetiroDeCierre !== null) parametros.set('esRetiroDeCierre', String(filtros.esRetiroDeCierre))
  agregarOrdenAlQuery(parametros, orden)
  parametros.set('pagina', String(filtros.pagina))
  parametros.set('tamanio', String(filtros.tamanio))
  return `?${parametros.toString()}`
}

export const clienteDeCaja = {
  /** `GET /api/caja/retiros` (Supervisor y Admin): filtra, ordena y pagina en el servidor. */
  listarRetiros: (filtros: FiltrosDeRetiros, orden: OrdenDeRetiros = null) =>
    api.get<PaginaDeRetiros>(`/caja/retiros${construirQueryDeRetiros(filtros, orden)}`),
  /** `PUT /api/caja/retiros/{id}` (Admin): corrige importe y motivo; en un turno cerrado el servidor recalcula el arqueo. */
  editarRetiro: (id: number, solicitud: SolicitudDeEdicionDeRetiro) => api.put<FilaDeRetiro>(`/caja/retiros/${id}`, solicitud),
  /** `DELETE /api/caja/retiros/{id}` (Admin): baja lógica del retiro. */
  eliminarRetiro: (id: number) => api.delete(`/caja/retiros/${id}`),
  /** `POST /api/caja/turnos` (design: API Surface): 201 + turno, o `409 turno_ya_abierto` si el
   * punto de venta ya tiene uno abierto. */
  abrir: (solicitud: SolicitudDeApertura) => api.post<TurnoResumen>('/caja/turnos', solicitud),
  /** `GET /api/caja/turnos/abierto?idPuntoVenta=` — fuente de verdad del estado del turno: 200
   * con el turno abierto o 200 con `null`, nunca un error. */
  obtenerAbierto: (idPuntoVenta: number) =>
    api.get<TurnoResumen | null>(`/caja/turnos/abierto?idPuntoVenta=${idPuntoVenta}`),
  /** `POST /api/caja/turnos/{id}/movimientos` — retiro / refuerzo / apertura de cajón contra el
   * turno de la ruta (nunca contra un `idTurnoCaja` del cuerpo). */
  registrarMovimiento: (idTurnoCaja: number, solicitud: SolicitudDeMovimiento) =>
    api.post<MovimientoRegistrado>(`/caja/turnos/${idTurnoCaja}/movimientos`, solicitud),
  /** `GET /api/caja/turnos/{id}/resumen` — resumen parcial, misma derivación que el cierre
   * (spec: Resumen Parcial Uses The Same Derivation As Cierre), de solo lectura. */
  obtenerResumen: (idTurnoCaja: number) => api.get<ResumenDeTurno>(`/caja/turnos/${idTurnoCaja}/resumen`),
  /** `POST /api/caja/turnos/{id}/cierre` (Slice 7, design: The Cierre Transaction) —
   * irreversible: deriva el arqueo, lo persiste y encadena la tesorería en una única transacción
   * atómica. El cuerpo SOLO trae los conteos declarados (spec: Cierre Payload Carries Only
   * Declared Counts). */
  cerrar: (idTurnoCaja: number, solicitud: SolicitudDeCierre) =>
    api.post<TurnoConArqueos>(`/caja/turnos/${idTurnoCaja}/cierre`, solicitud),
  /** `POST /api/caja/turnos/{id}/cierre-por-retiro` (etapa 5, práctica del dueño) —
   * irreversible: el cajero retira el efectivo contado y deja el fondo inicial en el cajón, nada
   * se cuenta. El cuerpo SOLO trae el retiro de cierre (spec: Cierre Por Retiro Payload Carries
   * Only The Withdrawal Amount) — todo lo demás lo deriva el servidor. */
  cerrarPorRetiro: (idTurnoCaja: number, solicitud: SolicitudDeCierrePorRetiro) =>
    api.post<ResumenDeCierrePorRetiro>(`/caja/turnos/${idTurnoCaja}/cierre-por-retiro`, solicitud),
  /** `GET /api/caja/turnos/{id}/resumen-de-cierre` — reimpresión / recuperación tras una falla de
   * red ambigua sobre un cierre ya persistido (cualquiera de los dos modos, spec: Resumen De
   * Cierre Is Available For Any Closed Turno, By Either Mode); `409 turno_no_cerrado` si el turno
   * sigue abierto. */
  obtenerResumenDeCierre: (idTurnoCaja: number) =>
    api.get<ResumenDeCierrePorRetiro>(`/caja/turnos/${idTurnoCaja}/resumen-de-cierre`),
  /** `GET /api/caja/turnos/{id}` — el turno con su estado y sus arqueos persistidos; el detalle del Z no los trae. */
  obtenerTurno: (idTurnoCaja: number) => api.get<TurnoConArqueos>(`/caja/turnos/${idTurnoCaja}`),
  /** `GET /api/caja/turnos?idPuntoVenta=&estado=Cerrado` — turnos cerrados de UN punto de venta,
   * el más reciente primero salvo que se pida un `orden`; ambos filtros y el orden los aplica el servidor. */
  listarCerrados: (idPuntoVenta: number, pagina: number, tamanio: number, orden: OrdenDeCierres = null) =>
    api.get<PaginaDeTurnos>(`/caja/turnos${construirQueryDeCierres(idPuntoVenta, pagina, tamanio, orden)}`),
  /** `GET /api/caja/turnos/{id}/detalle` (stage-11-exportacion-reportes, Slice 5a/6b, spec
   * historico-de-cajas: G2 Detail Reuses ResumenDeTurno Plus Ticket And Pago Listings) — el
   * Z-report: mismo `ResumenDeTurno` que `/resumen` más los tickets y pagos del turno. Mismo
   * gate `OperacionDePos` que `/resumen`: el cajero puede leer su propio cierre. */
  obtenerDetalle: (idTurnoCaja: number) => api.get<DetalleDeTurno>(`/caja/turnos/${idTurnoCaja}/detalle`),
}

/** Rutas de descarga (`/export`, stage-11 slice 6b) del Z-report — sibling de `/detalle` bajo el
 * mismo gate `OperacionDePos` heredado por co-locación (design: "The load-bearing refinement of
 * the proposal is where the caja detail lives"). */
export const rutasDeExportacionDeCaja = {
  detalleDeTurno: (idTurnoCaja: number) => `/caja/turnos/${idTurnoCaja}/detalle/export?formato=xlsx`,
}

/** Longitud mínima del motivo, uniforme para los 3 tipos de movimiento (design decisión 8;
 * spec: movimientos-de-caja / Motivo Required For Retiro And Refuerzo, Apertura De Cajón Follows
 * Legacy F12 Parity). */
const MOTIVO_LONGITUD_MINIMA = 5

/** Espejo de `ReglaDeMovimientosDeCaja.ExigirMotivoValido` — `motivo` es obligatorio y con al
 * menos 5 caracteres (tras recortar espacios) para los 3 tipos de movimiento. */
export function motivoValido(motivo: string): boolean {
  return motivo.trim().length >= MOTIVO_LONGITUD_MINIMA
}

/** Espejo de `ReglaDeMovimientosDeCaja.ExigirImporteValido`: `apertura_cajon` es SIEMPRE `0`
 * (paridad legacy F12), los demás tipos exigen un importe positivo. */
export function importeValidoParaTipo(tipo: TipoMovimientoCaja, importe: number): boolean {
  if (tipo === 'AperturaCajon') return importe === 0
  return Number.isFinite(importe) && importe > 0
}

/** Arma el cuerpo de `POST …/movimientos`: fuerza `importe = 0` para `apertura_cajon` (el
 * cajero nunca tipea uno, el campo queda deshabilitado en pantalla) y recorta el motivo. */
export function aSolicitudDeMovimiento(tipo: TipoMovimientoCaja, importe: string, motivo: string): SolicitudDeMovimiento {
  return {
    tipo,
    importe: tipo === 'AperturaCajon' ? 0 : Number(importe),
    motivo: motivo.trim(),
  }
}
