/**
 * Cliente de gastos del turno (POS, stage-gastos-turno-carga-simple): alta contra el turno
 * abierto del punto de venta — el servidor resuelve el turno desde `idPuntoVenta`, nunca viaja
 * en el cuerpo (mismo criterio que `clienteDeCaja.abrir`).
 */
import { api, ErrorApi } from './cliente'
import type {
  CategoriaGasto,
  GastoDeAdministracionListado,
  GastoRegistrado,
  OrigenFondosGasto,
  PaginaDeGastosDeAdministracion,
  SolicitudDeEdicionDeGasto,
  SolicitudDeGasto,
  SolicitudDeGastoDeAdministracion,
} from './tipos'

export const clienteDeGastos = {
  /** `POST /api/gastos` — 201 + gasto registrado, o `409 turno_no_abierto` si el punto de venta
   * no tiene un turno abierto. */
  registrar: (solicitud: SolicitudDeGasto) => api.post<GastoRegistrado>('/gastos', solicitud),
  /** stage-gasto-a-compra (PR4): `POST /api/gastos/{id}/vincular-compra` — liga (o convierte a
   * proveedor) un gasto YA existente a una compra confirmada. `GestionDeCatalogo` del lado del
   * servidor (mismo gate que toda escritura de `/api/compras`). */
  vincularCompra: (id: number, idComprobanteCompra: number) =>
    api.post<GastoRegistrado>(`/gastos/${id}/vincular-compra`, { idComprobanteCompra }),
  /** `PUT /api/gastos/{id}` — edición desde el POS, solo con el turno del gasto abierto: si está
   * cerrado (o el gasto no tiene turno) el servidor responde `409 gasto_turno_cerrado`. */
  actualizar: (id: number, solicitud: SolicitudDeEdicionDeGasto) => api.put<GastoRegistrado>(`/gastos/${id}`, solicitud),
}

// --- Gastos de administración (stage-gastos-admin-retroactivos, PR3): alta SIN turno, pagada de
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

export type FiltrosDeGastosDeAdministracion = {
  idEmpresa: number | null
  idPuntoVenta: number | null
  categoria: CategoriaGasto | null
  origenFondos: OrigenFondosGasto | null
  idProveedor: number | null
  desde: string
  hasta: string
  pagina: number
  tamanio: number
}

export function filtrosDeGastosDeAdministracionVacios(): FiltrosDeGastosDeAdministracion {
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

export function construirQueryDeGastosDeAdministracion(filtros: FiltrosDeGastosDeAdministracion): string {
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

export const clienteDeGastosDeAdministracion = {
  listar: (filtros: FiltrosDeGastosDeAdministracion) =>
    api.get<PaginaDeGastosDeAdministracion>(`/gastos/administracion${construirQueryDeGastosDeAdministracion(filtros)}`),
  registrar: (solicitud: SolicitudDeGastoDeAdministracion) =>
    api.post<GastoRegistrado>('/gastos/administracion', solicitud),
  /** stage-gasto-a-compra (PR4), judgment follow-up: `GET /api/gastos/administracion/{id}` — un
   * solo gasto, mismo shape que el listado. Usado por `CompraEditor.tsx` para prefillear desde
   * `?desdeGasto=`. */
  obtener: (id: number) => api.get<GastoDeAdministracionListado>(`/gastos/administracion/${id}`),
  /** `PUT /api/gastos/administracion/{id}` — edita cualquier gasto; si es de caja de un turno
   * cerrado el servidor recalcula su arqueo. */
  actualizar: (id: number, solicitud: SolicitudDeEdicionDeGasto) =>
    api.put<GastoDeAdministracionListado>(`/gastos/administracion/${id}`, solicitud),
  /** `DELETE /api/gastos/administracion/{id}` — 204 sin cuerpo. */
  eliminar: (id: number) => api.delete<void>(`/gastos/administracion/${id}`),
}

export type FormularioDeGastoDeAdministracion = {
  fecha: string
  idEmpresa: number | ''
  idPuntoVenta: number | ''
  categoria: CategoriaGasto
  idProveedor: number | ''
  idArea: number | ''
  concepto: string
  detalle: string
  idMedioPago: number | ''
  numeroFactura: string
  importe: number | null
}

export function formularioDeGastoDeAdministracionVacio(
  fechaDeHoy: string,
  idEmpresa: number | '' = '',
): FormularioDeGastoDeAdministracion {
  return {
    fecha: fechaDeHoy,
    idEmpresa,
    idPuntoVenta: '',
    categoria: 'Otros',
    idProveedor: '',
    idArea: '',
    concepto: '',
    detalle: '',
    idMedioPago: '',
    numeroFactura: '',
    importe: null,
  }
}

/** Espejo de `ServicioDeGastos.categoriaAlElegirProveedor` (mismo criterio que
 * `GastosDelTurno.tsx`/`utilidadesGastosDelTurno.ts`): elegir un proveedor cambia la categoría a
 * "Proveedor"; limpiarlo mientras la categoría sigue en "Proveedor" la vuelve a "Otros". */
export function categoriaAlElegirProveedorAdministracion(
  nuevoIdProveedor: number | null,
  categoriaPrevia: CategoriaGasto,
): CategoriaGasto {
  if (nuevoIdProveedor !== null) return 'Proveedor'
  return categoriaPrevia === 'Proveedor' ? 'Otros' : categoriaPrevia
}

/** `fecha` viaja tal cual el `<input type="date">` la entrega (`YYYY-MM-DD`, `DateOnly` del
 * lado del servidor) — sin ningún offset horario, a diferencia de `desde`/`hasta` del listado
 * (que sí filtran contra un `timestamptz`). Mismo criterio que
 * `compras.ts.aSolicitudDeCompra` con `fechaComprobante`. */
export function aSolicitudDeGastoDeAdministracion(f: FormularioDeGastoDeAdministracion): SolicitudDeGastoDeAdministracion {
  return {
    fecha: f.fecha,
    idEmpresa: f.idEmpresa === '' ? 0 : f.idEmpresa,
    idPuntoVenta: f.idPuntoVenta === '' ? null : f.idPuntoVenta,
    categoria: f.categoria,
    idProveedor: f.idProveedor === '' ? null : f.idProveedor,
    idArea: f.idArea === '' ? null : f.idArea,
    concepto: f.concepto.trim(),
    detalle: f.detalle.trim() === '' ? null : f.detalle.trim(),
    idMedioPago: f.idMedioPago === '' ? 0 : f.idMedioPago,
    numeroFactura: f.numeroFactura.trim() === '' ? null : f.numeroFactura.trim(),
    importe: f.importe ?? 0,
    idComprobanteCompra: null,
  }
}

/** Feedback instantáneo client-side (el servidor vuelve a validar todo, `dto-contract-honesty`):
 * fecha elegida, empresa, medio de pago, concepto no vacío e importe positivo. */
export function formularioDeGastoDeAdministracionCompleto(f: FormularioDeGastoDeAdministracion): boolean {
  return (
    f.fecha.trim() !== '' &&
    f.idEmpresa !== '' &&
    f.idMedioPago !== '' &&
    f.concepto.trim() !== '' &&
    f.importe !== null &&
    f.importe > 0
  )
}

// --- Edición de gastos (POS y administración comparten formulario y mapeo) ---------------------

/** Lo mínimo que `GastoDeTurno` y `GastoDeAdministracionListado` tienen en común para precargar
 * la edición. */
export type GastoEditable = {
  categoria: CategoriaGasto
  idProveedor: number | null
  idArea: number | null
  concepto: string
  detalle: string | null
  idMedioPago: number
  numeroFactura: string | null
  importe: number
}

export type FormularioDeEdicionDeGasto = {
  importe: number | null
  idMedioPago: number | ''
  categoria: CategoriaGasto
  idProveedor: number | ''
  idArea: number | ''
  concepto: string
  detalle: string
  numeroFactura: string
}

export function aFormularioDeEdicion(gasto: GastoEditable): FormularioDeEdicionDeGasto {
  return {
    importe: gasto.importe,
    idMedioPago: gasto.idMedioPago,
    categoria: gasto.categoria,
    idProveedor: gasto.idProveedor ?? '',
    idArea: gasto.idArea ?? '',
    concepto: gasto.concepto,
    detalle: gasto.detalle ?? '',
    numeroFactura: gasto.numeroFactura ?? '',
  }
}

/** Cuerpo del `PUT`: el servidor valida de nuevo todo, esto solo normaliza (recorta y manda
 * `null` en los textos opcionales vacíos). El llamador garantiza `formularioDeEdicionCompleto`. */
export function aSolicitudDeEdicionDeGasto(f: FormularioDeEdicionDeGasto): SolicitudDeEdicionDeGasto {
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

export function formularioDeEdicionCompleto(f: FormularioDeEdicionDeGasto): boolean {
  return f.idMedioPago !== '' && f.concepto.trim() !== '' && f.importe !== null && f.importe > 0
}

export const COPIA_GASTO_TURNO_CERRADO =
  'El turno de este gasto ya está cerrado: no se puede editar desde la caja. Pedile a un administrador que lo corrija.'

/** Copia del fallo de un `PUT`/`DELETE` de gasto: el `409 gasto_turno_cerrado` tiene texto propio
 * (es una precondición vigente, no un error genérico); el resto muestra el mensaje del servidor. */
export function copiaDeFalloDeEdicion(error: unknown, accion = 'guardar'): string {
  if (error instanceof ErrorApi) {
    if (error.estado === 409 && error.codigo === 'gasto_turno_cerrado') return COPIA_GASTO_TURNO_CERRADO
    return error.message.trim() || `No se pudo ${accion} el gasto.`
  }
  return `No se pudo ${accion} el gasto.`
}

export const ADVERTENCIA_TURNO_CERRADO =
  'Este gasto pertenece a un turno cerrado: se recalculará el arqueo del turno y quedará marcado como recalculado.'

type GastoConEfectos = Pick<GastoDeAdministracionListado, 'turnoAbierto' | 'idTurnoCaja' | 'origenFondos' | 'categoria' | 'idProveedor'>

/** Avisos que el admin tiene que ver antes de editar o eliminar un gasto: reflejan los efectos
 * reales del servidor (arqueo recalculado en gasto de caja de un turno cerrado; ajuste
 * compensatorio en el saldo del proveedor y/o en la caja general). */
export function advertenciasDeGastoDeAdministracion(gasto: GastoConEfectos): string[] {
  const avisos: string[] = []

  if (gasto.idTurnoCaja !== null && !gasto.turnoAbierto && gasto.origenFondos === 'CajaTurno') {
    avisos.push(ADVERTENCIA_TURNO_CERRADO)
  }

  const afectados: string[] = []
  if (gasto.categoria === 'Proveedor' && gasto.idProveedor !== null) afectados.push('el saldo del proveedor')
  if (gasto.origenFondos === 'Tesoreria') afectados.push('la caja general')
  if (afectados.length > 0) {
    avisos.push(`Se registrará un ajuste compensatorio sobre ${afectados.join(' y ')}.`)
  }

  return avisos
}
