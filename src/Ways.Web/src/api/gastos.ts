/**
 * Cliente de gastos del turno (POS, stage-gastos-turno-carga-simple): alta contra el turno
 * abierto del punto de venta — el servidor resuelve el turno desde `idPuntoVenta`, nunca viaja
 * en el cuerpo (mismo criterio que `clienteDeCaja.abrir`).
 */
import { api } from './cliente'
import type {
  CategoriaGasto,
  GastoDeAdministracionListado,
  GastoRegistrado,
  OrigenFondosGasto,
  PaginaDeGastosDeAdministracion,
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
