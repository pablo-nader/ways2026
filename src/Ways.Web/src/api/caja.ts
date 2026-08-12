/**
 * Cliente HTTP + reglas puras de caja (stage-6-turnos-caja, Slice 6): apertura de turno,
 * movimientos físicos fuera de la venta (retiro/refuerzo/apertura de cajón) y resumen parcial.
 * `motivoValido`/`importeValidoParaTipo` espejan `ReglaDeMovimientosDeCaja` (Ways.Domain.Caja)
 * para dar feedback instantáneo en pantalla — nunca son autoritativos, el servidor vuelve a
 * validar todo (mismo criterio que `pagos.ts`, stage-5).
 */
import { api } from './cliente'
import type {
  DetalleDeTurno,
  MovimientoRegistrado,
  ResumenDeTurno,
  SolicitudDeApertura,
  SolicitudDeCierre,
  SolicitudDeMovimiento,
  TipoMovimientoCaja,
  TurnoConArqueos,
  TurnoResumen,
} from './tipos'

export const clienteDeCaja = {
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
  /** `GET /api/caja/turnos/{id}/detalle` (stage-11-exportacion-reportes, Slice 5a/6b, spec
   * historico-de-cajas: G2 Detail Reuses ResumenDeTurno Plus Ticket And Gasto Listings) — el
   * Z-report: mismo `ResumenDeTurno` que `/resumen` más los tickets y gastos del turno. Mismo
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
