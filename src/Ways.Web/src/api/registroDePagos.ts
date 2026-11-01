/**
 * Cliente de pagos del turno (POS, stage-gastos-turno-carga-simple): alta contra el turno
 * abierto del punto de venta — el servidor resuelve el turno desde `idPuntoVenta`, nunca viaja
 * en el cuerpo (mismo criterio que `clienteDeCaja.abrir`).
 */
import { api, ErrorApi } from './cliente'
import {
  aLineasDeSolicitud,
  errorDeLineasDePago,
  lineaDePagoVacia,
  lineasEditablesDesdeListado,
  type LineaDePagoEditable,
} from './lineasDePago'
import type {
  CategoriaPago,
  LineaDePagoListada,
  PagoConLineas,
  PagoDeAdministracionListado,
  OrigenFondosPago,
  PaginaDePagosDeAdministracion,
  SolicitudDeEdicionDePago,
  SolicitudDeEdicionDePagoConLineas,
  SolicitudDePagoConLineas,
  SolicitudDePagoDeAdministracion,
} from './tipos'

export const clienteDePagos = {
  /** `POST /api/pagos` — 201 + pago con sus líneas, o `409 turno_no_abierto` si el punto de venta no tiene un
   * turno abierto. */
  registrar: (solicitud: SolicitudDePagoConLineas) => api.post<PagoConLineas>('/pagos', solicitud),
  /** stage-gasto-a-compra (PR4): `POST /api/pagos/{id}/vincular-compra` — liga (o convierte a
   * proveedor) un pago YA existente a una compra confirmada; responde el pago con sus líneas.
   * `GestionDeCatalogo` del lado del servidor (mismo gate que toda escritura de `/api/compras`). */
  vincularCompra: (id: number, idComprobanteCompra: number) =>
    api.post<PagoConLineas>(`/pagos/${id}/vincular-compra`, { idComprobanteCompra }),
  /** `PUT /api/pagos/{id}` — edición desde el POS reemplazando las líneas, solo con el turno del pago abierto: si
   * está cerrado (o el pago no tiene turno) el servidor responde `409 gasto_turno_cerrado`. */
  actualizar: (id: number, solicitud: SolicitudDeEdicionDePagoConLineas) => api.put<PagoConLineas>(`/pagos/${id}`, solicitud),
}

// --- Pagos de administración (stage-gastos-admin-retroactivos, PR3): alta SIN turno, pagada de
// la tesorería de la empresa — pantalla admin-only (`GestionDeCatalogo` del lado del servidor).

// Offset local para desde/hasta (mismo criterio EXACTO que `compras.ts`/`cuentaCorriente.ts`: el
// servidor filtra `Fecha` como `timestamptz`, un `<input type="date">` sin offset se interpretaría
// como UTC — duplicado a propósito, sibling helper, sin módulo compartido de fechas todavía).
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

export type FiltrosDePagosDeAdministracion = {
  idEmpresa: number | null
  idPuntoVenta: number | null
  categoria: CategoriaPago | null
  origenFondos: OrigenFondosPago | null
  idProveedor: number | null
  desde: string
  hasta: string
  pagina: number
  tamanio: number
}

export function filtrosDePagosDeAdministracionVacios(): FiltrosDePagosDeAdministracion {
  return {
    idEmpresa: null,
    idPuntoVenta: null,
    categoria: null,
    origenFondos: null,
    idProveedor: null,
    desde: '',
    hasta: '',
    pagina: 1,
    tamanio: 25,
  }
}

export function construirQueryDePagosDeAdministracion(filtros: FiltrosDePagosDeAdministracion): string {
  const parametros = new URLSearchParams()
  if (filtros.idEmpresa !== null) parametros.set('idEmpresa', String(filtros.idEmpresa))
  if (filtros.idPuntoVenta !== null) parametros.set('idPuntoVenta', String(filtros.idPuntoVenta))
  if (filtros.categoria !== null) parametros.set('categoria', filtros.categoria)
  if (filtros.origenFondos !== null) parametros.set('origenFondos', filtros.origenFondos)
  if (filtros.idProveedor !== null) parametros.set('idProveedor', String(filtros.idProveedor))
  if (filtros.desde) parametros.set('desde', fechaIsoConOffset(filtros.desde, '00:00:00'))
  if (filtros.hasta) parametros.set('hasta', fechaIsoConOffset(filtros.hasta, '23:59:59.999'))
  parametros.set('pagina', String(filtros.pagina))
  parametros.set('tamanio', String(filtros.tamanio))
  return `?${parametros.toString()}`
}

/** Contrato con líneas por medio (`/api/pagos/administracion`): cada pago lleva su reparto en `lineas`. */
export const clienteDePagosDeAdministracion = {
  listar: (filtros: FiltrosDePagosDeAdministracion) =>
    api.get<PaginaDePagosDeAdministracion>(`/pagos/administracion${construirQueryDePagosDeAdministracion(filtros)}`),
  registrar: (solicitud: SolicitudDePagoDeAdministracion) => api.post<PagoConLineas>('/pagos/administracion', solicitud),
  /** stage-gasto-a-compra (PR4), judgment follow-up: `GET /api/pagos/administracion/{id}` — un
   * solo pago, mismo shape que el listado. Usado por `CompraEditor.tsx` para prefillear desde
   * `?desdePago=`. */
  obtener: (id: number) => api.get<PagoDeAdministracionListado>(`/pagos/administracion/${id}`),
  /** `PUT /api/pagos/administracion/{id}` — edita cualquier pago reemplazando sus líneas; si es de caja
   * de un turno cerrado el servidor recalcula su arqueo. */
  actualizar: (id: number, solicitud: SolicitudDeEdicionDePagoConLineas) =>
    api.put<PagoDeAdministracionListado>(`/pagos/administracion/${id}`, solicitud),
  /** `DELETE /api/pagos/administracion/{id}` — 204 sin cuerpo. */
  eliminar: (id: number) => api.delete<void>(`/pagos/administracion/${id}`),
  /** `POST /api/pagos/administracion/{id}/reasignar-personal` (solo Admin): mueve un pago de sueldo a la cuenta de
   * otro empleado; el concepto pasa a nombrar al nuevo. */
  reasignarPersonal: (id: number, idPersonal: number) =>
    api.post<RespuestaDeReasignacionDePago>(`/pagos/administracion/${id}/reasignar-personal`, { idPersonal }),
}

export type RespuestaDeReasignacionDePago = {
  idPago: number
  idPersonalAnterior: number
  idPersonal: number
  concepto: string
}

export type FormularioDePagoDeAdministracion = {
  fecha: string
  idEmpresa: number | ''
  idPuntoVenta: number | ''
  categoria: CategoriaPago
  idProveedor: number | ''
  idArea: number | ''
  concepto: string
  detalle: string
  numeroFactura: string
  lineas: LineaDePagoEditable[]
}

export function formularioDePagoDeAdministracionVacio(
  fechaDeHoy: string,
  idEmpresa: number | '' = '',
): FormularioDePagoDeAdministracion {
  return {
    fecha: fechaDeHoy,
    idEmpresa,
    idPuntoVenta: '',
    categoria: 'Otros',
    idProveedor: '',
    idArea: '',
    concepto: '',
    detalle: '',
    numeroFactura: '',
    lineas: [lineaDePagoVacia()],
  }
}

/** Espejo de `ServicioDePagos.categoriaAlElegirProveedor` (mismo criterio que
 * `PagosDelTurno.tsx`/`utilidadesPagosDelTurno.ts`): elegir un proveedor cambia la categoría a
 * "Proveedor"; limpiarlo mientras la categoría sigue en "Proveedor" la vuelve a "Otros". */
export function categoriaAlElegirProveedorAdministracion(
  nuevoIdProveedor: number | null,
  categoriaPrevia: CategoriaPago,
): CategoriaPago {
  if (nuevoIdProveedor !== null) return 'Proveedor'
  return categoriaPrevia === 'Proveedor' ? 'Otros' : categoriaPrevia
}

/** `fecha` viaja tal cual el `<input type="date">` la entrega (`YYYY-MM-DD`, `DateOnly` del
 * lado del servidor) — sin ningún offset horario, a diferencia de `desde`/`hasta` del listado
 * (que sí filtran contra un `timestamptz`). Mismo criterio que
 * `compras.ts.aSolicitudDeCompra` con `fechaComprobante`. */
export function aSolicitudDePagoDeAdministracion(f: FormularioDePagoDeAdministracion): SolicitudDePagoDeAdministracion {
  return {
    fecha: f.fecha,
    idEmpresa: f.idEmpresa === '' ? 0 : f.idEmpresa,
    idPuntoVenta: f.idPuntoVenta === '' ? null : f.idPuntoVenta,
    categoria: f.categoria,
    idProveedor: f.idProveedor === '' ? null : f.idProveedor,
    idArea: f.idArea === '' ? null : f.idArea,
    concepto: f.concepto.trim(),
    detalle: f.detalle.trim() === '' ? null : f.detalle.trim(),
    numeroFactura: f.numeroFactura.trim() === '' ? null : f.numeroFactura.trim(),
    lineas: aLineasDeSolicitud(f.lineas),
    idComprobanteCompra: null,
  }
}

/** Feedback instantáneo client-side (el servidor vuelve a validar todo, `dto-contract-honesty`):
 * fecha elegida, empresa, concepto no vacío y todas las líneas válidas (`errorDeLineasDePago`). */
export function formularioDePagoDeAdministracionCompleto(f: FormularioDePagoDeAdministracion): boolean {
  return f.fecha.trim() !== '' && f.idEmpresa !== '' && f.concepto.trim() !== '' && errorDeLineasDePago(f.lineas) === null
}

// --- Edición de pagos (POS y administración comparten formulario y mapeo) ---------------------

/** Lo mínimo que `PagoDeTurno` y `PagoDeAdministracionListado` tienen en común para precargar
 * la edición. */
export type PagoEditable = {
  categoria: CategoriaPago
  idProveedor: number | null
  idArea: number | null
  concepto: string
  detalle: string | null
  idMedioPago: number
  numeroFactura: string | null
  importe: number
}

export type FormularioDeEdicionDePago = {
  importe: number | null
  idMedioPago: number | ''
  categoria: CategoriaPago
  idProveedor: number | ''
  idArea: number | ''
  concepto: string
  detalle: string
  numeroFactura: string
}

export function aFormularioDeEdicion(pago: PagoEditable): FormularioDeEdicionDePago {
  return {
    importe: pago.importe,
    idMedioPago: pago.idMedioPago,
    categoria: pago.categoria,
    idProveedor: pago.idProveedor ?? '',
    idArea: pago.idArea ?? '',
    concepto: pago.concepto,
    detalle: pago.detalle ?? '',
    numeroFactura: pago.numeroFactura ?? '',
  }
}

/** Cuerpo del `PUT`: el servidor valida de nuevo todo, esto solo normaliza (recorta y manda
 * `null` en los textos opcionales vacíos). El llamador garantiza `formularioDeEdicionCompleto`. */
export function aSolicitudDeEdicionDePago(f: FormularioDeEdicionDePago): SolicitudDeEdicionDePago {
  return {
    categoria: f.categoria,
    idProveedor: f.idProveedor === '' ? null : f.idProveedor,
    idArea: f.idArea === '' ? null : f.idArea,
    concepto: f.concepto.trim(),
    detalle: f.detalle.trim() === '' ? null : f.detalle.trim(),
    idMedioPago: f.idMedioPago === '' ? 0 : f.idMedioPago,
    numeroFactura: f.numeroFactura.trim() === '' ? null : f.numeroFactura.trim(),
    importe: f.importe ?? 0,
  }
}

export function formularioDeEdicionCompleto(f: FormularioDeEdicionDePago): boolean {
  return f.idMedioPago !== '' && f.concepto.trim() !== '' && f.importe !== null && f.importe > 0
}

/** Reparto con el que abre la edición de administración: el que devolvió el servidor o, si la respuesta no
 * trae líneas, una sola con el medio y el importe del pago. */
export function lineasIniciales(pago: Pick<PagoEditable, 'idMedioPago' | 'importe'> & { lineas?: readonly LineaDePagoListada[] }): LineaDePagoEditable[] {
  if (pago.lineas !== undefined && pago.lineas.length > 0) return lineasEditablesDesdeListado(pago.lineas)
  return [{ ...lineaDePagoVacia(), idMedioPago: pago.idMedioPago, importe: pago.importe }]
}

/** Cuerpo del `PUT /api/pagos/administracion/{id}`: los campos del formulario (sin medio ni importe, que viven en
 * las líneas) más el reparto. El llamador garantiza `formularioDeEdicionConLineasCompleto`. */
export function aSolicitudDeEdicionConLineas(
  f: FormularioDeEdicionDePago,
  lineas: readonly LineaDePagoEditable[],
): SolicitudDeEdicionDePagoConLineas {
  return {
    categoria: f.categoria,
    idProveedor: f.idProveedor === '' ? null : f.idProveedor,
    idArea: f.idArea === '' ? null : f.idArea,
    concepto: f.concepto.trim(),
    detalle: f.detalle.trim() === '' ? null : f.detalle.trim(),
    numeroFactura: f.numeroFactura.trim() === '' ? null : f.numeroFactura.trim(),
    lineas: aLineasDeSolicitud(lineas),
  }
}

export function formularioDeEdicionConLineasCompleto(f: FormularioDeEdicionDePago, lineas: readonly LineaDePagoEditable[]): boolean {
  return f.concepto.trim() !== '' && errorDeLineasDePago(lineas) === null
}

export const COPIA_PAGO_TURNO_CERRADO =
  'El turno de este pago ya está cerrado: no se puede editar desde la caja. Pedile a un administrador que lo corrija.'

/** Copia del fallo de un `PUT`/`DELETE` de pago: el `409 gasto_turno_cerrado` tiene texto propio
 * (es una precondición vigente, no un error genérico); el resto muestra el mensaje del servidor. */
export function copiaDeFalloDeEdicion(error: unknown, accion = 'guardar'): string {
  if (error instanceof ErrorApi) {
    if (error.estado === 409 && error.codigo === 'gasto_turno_cerrado') return COPIA_PAGO_TURNO_CERRADO
    return error.message.trim() || `No se pudo ${accion} el pago.`
  }
  return `No se pudo ${accion} el pago.`
}

export const ADVERTENCIA_TURNO_CERRADO =
  'Este pago pertenece a un turno cerrado: se recalculará el arqueo del turno y quedará marcado como recalculado.'

/** Un pago que nació de un movimiento de personal no se edita, no se da de baja ni se liga a una compra por
 * separado: el servidor lo rechaza con 409 `gasto_de_pago_de_personal`, así que las pantallas ofrecen esas
 * acciones deshabilitadas. */
export function esPagoOriginadoEnPersonal(pago: Pick<PagoDeAdministracionListado, 'idMovimientoPersonal'>): boolean {
  return typeof pago.idMovimientoPersonal === 'number'
}

export const MOTIVO_PAGO_ORIGINADO_EN_PERSONAL = 'Nació de un movimiento de personal y no se modifica por separado.'

type PagoConEfectos = Pick<PagoDeAdministracionListado, 'turnoAbierto' | 'idTurnoCaja' | 'origenFondos' | 'categoria' | 'idProveedor'>

/** Avisos que el admin tiene que ver antes de editar o eliminar un pago: reflejan los efectos
 * reales del servidor (arqueo recalculado en pago de caja de un turno cerrado; ajuste
 * compensatorio en el saldo del proveedor y/o en la caja general). */
export function advertenciasDePagoDeAdministracion(pago: PagoConEfectos): string[] {
  const avisos: string[] = []

  if (pago.idTurnoCaja !== null && !pago.turnoAbierto && pago.origenFondos === 'CajaTurno') {
    avisos.push(ADVERTENCIA_TURNO_CERRADO)
  }

  const afectados: string[] = []
  if (pago.categoria === 'Proveedor' && pago.idProveedor !== null) afectados.push('el saldo del proveedor')
  if (pago.origenFondos === 'Tesoreria') afectados.push('la caja general')
  if (afectados.length > 0) {
    avisos.push(`Se registrará un ajuste compensatorio sobre ${afectados.join(' y ')}.`)
  }

  return avisos
}
