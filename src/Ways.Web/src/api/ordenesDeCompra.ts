/**
 * Cliente HTTP + mappers puros de órdenes de compra (stage-16-ordenes-de-compra, Slice 6):
 * CRUD de borrador, `enviar`/`cerrar`/`anular`, y el listado paginado — espejo del contrato de
 * `Ways.Application.Compras.ContratosDeOrdenDeCompra`/`OrdenesDeCompraEndpoints`
 * (`design.md`: "client + pure mappers; `tipos.ts` mirrors the read/write DTOs").
 */
import { api } from './cliente'
import type {
  EstadoOrdenCompra,
  ItemDeOrden,
  LineaDeOrdenSolicitada,
  OrdenDeCompraBorrador,
  OrdenDeCompraDetalle,
  PaginaDeOrdenesDeCompra,
  SolicitudDeOrdenDeCompra,
} from './tipos'

// ---- Offset local para desde/hasta — mismo criterio que compras.ts/cuentaCorriente.ts: el
// servidor corre en UTC, `fecha_emision` es `timestamptz` y un `<input type="date">` sin offset
// se interpretaría como UTC (mutation-proof-tests regla 10). Duplicado a propósito: no hay un
// módulo compartido de utilidades de fecha en esta web todavía. -----------------------------------
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

export type FiltrosDeOrdenesDeCompra = {
  idProveedor: number | null
  idPuntoVenta: number | null
  estado: EstadoOrdenCompra | null
  desde: string
  hasta: string
  pagina: number
  tamanio: number
}

export function filtrosDeOrdenesDeCompraVacios(): FiltrosDeOrdenesDeCompra {
  return { idProveedor: null, idPuntoVenta: null, estado: null, desde: '', hasta: '', pagina: 1, tamanio: 25 }
}

/** Arma el query string de `GET /api/ordenes-compra` — `desde`/`hasta` filtran por
 * `fecha_emision` (`ServicioDeOrdenesDeCompra.ListarAsync`), con el mismo offset horario
 * explícito que el resto de la web (mutation-proof-tests regla 10). */
export function construirQueryDeOrdenesDeCompra(filtros: FiltrosDeOrdenesDeCompra): string {
  const parametros = new URLSearchParams()
  if (filtros.idProveedor !== null) parametros.set('idProveedor', String(filtros.idProveedor))
  if (filtros.idPuntoVenta !== null) parametros.set('idPuntoVenta', String(filtros.idPuntoVenta))
  if (filtros.estado !== null) parametros.set('estado', filtros.estado)
  if (filtros.desde) parametros.set('desde', fechaIsoConOffset(filtros.desde, '00:00:00'))
  if (filtros.hasta) parametros.set('hasta', fechaIsoConOffset(filtros.hasta, '23:59:59.999'))
  parametros.set('pagina', String(filtros.pagina))
  parametros.set('tamanio', String(filtros.tamanio))
  return `?${parametros.toString()}`
}

export const clienteDeOrdenesDeCompra = {
  listar: (filtros: FiltrosDeOrdenesDeCompra) => api.get<PaginaDeOrdenesDeCompra>(`/ordenes-compra${construirQueryDeOrdenesDeCompra(filtros)}`),
  obtener: (id: number) => api.get<OrdenDeCompraDetalle>(`/ordenes-compra/${id}`),
  crear: (solicitud: SolicitudDeOrdenDeCompra) => api.post<OrdenDeCompraBorrador>('/ordenes-compra', solicitud),
  actualizar: (id: number, solicitud: SolicitudDeOrdenDeCompra) => api.put<OrdenDeCompraBorrador>(`/ordenes-compra/${id}`, solicitud),
  enviar: (id: number) => api.post<OrdenDeCompraBorrador>(`/ordenes-compra/${id}/enviar`),
  cerrar: (id: number) => api.post<OrdenDeCompraBorrador>(`/ordenes-compra/${id}/cerrar`),
  anular: (id: number) => api.post<OrdenDeCompraBorrador>(`/ordenes-compra/${id}/anular`),
}

export function etiquetaDeEstadoOrdenCompra(estado: EstadoOrdenCompra): string {
  switch (estado) {
    case 'Borrador':
      return 'Borrador'
    case 'Enviada':
      return 'Enviada'
    case 'RecibidaParcial':
      return 'Recibida parcial'
    case 'Cerrada':
      return 'Cerrada'
    case 'Anulada':
      return 'Anulada'
  }
}

export function claseDeBadgeDeEstadoOrdenCompra(estado: EstadoOrdenCompra): string {
  switch (estado) {
    case 'Borrador':
      return 'text-bg-secondary'
    case 'Enviada':
      return 'text-bg-primary'
    case 'RecibidaParcial':
      return 'text-bg-warning'
    case 'Cerrada':
      return 'text-bg-success'
    case 'Anulada':
      return 'text-bg-danger'
  }
}

/** `Pendiente`/`Desvio`/`CostoEstimado`/`CostoReal` renderizan `—`, NUNCA `0` — la celda que un
 * operador leería como "nada pendiente"/"sin desvío" cuando el dato en realidad no existe (spec
 * ordenes-de-compra: "no comparable, never zero"; design decisión 13: `Pendiente` sí puede ser
 * un `0` genuino — ESE caso pasa por acá con normalidad, el guard es solo contra `null`). */
export function formatearCantidadNullable(valor: number | null): string {
  return valor === null ? '—' : valor.toLocaleString('es-AR', { minimumFractionDigits: 0, maximumFractionDigits: 3 })
}

/** `Desvio`/`DesvioTotal` ya vienen como PORCENTAJE redondeado (`design.md` decisión 14) — renderiza
 * con signo explícito (`+12%`/`-5%`) y `—` cuando no es comparable, nunca `0%`. */
export function formatearDesvio(valorPorcentaje: number | null): string {
  if (valorPorcentaje === null) return '—'
  const signo = valorPorcentaje > 0 ? '+' : ''
  return `${signo}${valorPorcentaje}%`
}

export function formatearMonedaNullable(valor: number | null): string {
  if (valor === null) return '—'
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

// ---- Formulario del editor de borrador: una línea por fila de la grilla (mismo shape que
// `LineaDeCompraFormulario`/`EncabezadoDeCompraFormulario` de `compras.ts`, sin bultos/lote/IVA —
// una OC no tiene esos conceptos, `orden` es server-asignado igual que en compras) -------------

export type LineaDeOrdenFormulario = {
  /** Clave solo de React — el `PUT` es un replace-set completo (mismo criterio que compras),
   * ningún `orden` persistido viaja en el request. */
  clave: number
  idArticulo: number | ''
  descripcion: string
  cantidadPedida: string
  costoUnitarioEstimado: string
}

export function lineaDeOrdenVacia(clave: number): LineaDeOrdenFormulario {
  return { clave, idArticulo: '', descripcion: '', cantidadPedida: '', costoUnitarioEstimado: '' }
}

/** Un item ya persistido → fila de formulario, para reabrir un borrador existente. */
export function itemDeOrdenAFormulario(clave: number, item: ItemDeOrden): LineaDeOrdenFormulario {
  return {
    clave,
    idArticulo: item.idArticulo,
    descripcion: item.descripcion,
    cantidadPedida: String(item.cantidadPedida),
    costoUnitarioEstimado: item.costoUnitarioEstimado === null ? '' : String(item.costoUnitarioEstimado),
  }
}

/** Una línea sin artículo o sin cantidad > 0 nunca viaja al servidor — mismo criterio que
 * `lineaCompletaParaEnvio` de compras. */
export function lineaDeOrdenCompletaParaEnvio(l: LineaDeOrdenFormulario): boolean {
  const cantidad = Number(l.cantidadPedida)
  return l.idArticulo !== '' && l.cantidadPedida.trim() !== '' && Number.isFinite(cantidad) && cantidad > 0
}

function numeroDeOrden(valor: string): number {
  const n = Number(valor)
  return valor.trim() === '' || !Number.isFinite(n) ? 0 : n
}

function numeroDeOrdenONulo(valor: string): number | null {
  return valor.trim() === '' ? null : Number(valor)
}

/** Fila de formulario → `LineaDeOrdenSolicitada` — solo las líneas completas
 * (`lineaDeOrdenCompletaParaEnvio`) viajan; una fila a medio llenar nunca llega al servidor. */
export function aLineaDeOrdenSolicitada(l: LineaDeOrdenFormulario): LineaDeOrdenSolicitada {
  return {
    idArticulo: Number(l.idArticulo),
    descripcion: l.descripcion.trim(),
    cantidadPedida: numeroDeOrden(l.cantidadPedida),
    costoUnitarioEstimado: numeroDeOrdenONulo(l.costoUnitarioEstimado),
  }
}

export type EncabezadoDeOrdenFormulario = {
  idProveedor: number | ''
  idPuntoVenta: number | ''
  fechaEsperada: string
  observaciones: string
}

export function encabezadoDeOrdenVacio(): EncabezadoDeOrdenFormulario {
  return { idProveedor: '', idPuntoVenta: '', fechaEsperada: '', observaciones: '' }
}

/** `fechaEsperada` es `date` (`DateOnly`), viaja tal cual el `<input type="date">` la entrega —
 * mismo criterio que `fechaComprobante` en `compras.ts`, ningún offset horario. */
export function aSolicitudDeOrdenDeCompra(
  encabezado: EncabezadoDeOrdenFormulario,
  lineas: LineaDeOrdenFormulario[],
): SolicitudDeOrdenDeCompra {
  return {
    idProveedor: encabezado.idProveedor === '' ? 0 : encabezado.idProveedor,
    idPuntoVenta: encabezado.idPuntoVenta === '' ? 0 : encabezado.idPuntoVenta,
    fechaEsperada: encabezado.fechaEsperada === '' ? null : encabezado.fechaEsperada,
    observaciones: encabezado.observaciones.trim() === '' ? null : encabezado.observaciones.trim(),
    items: lineas.filter(lineaDeOrdenCompletaParaEnvio).map(aLineaDeOrdenSolicitada),
  }
}
