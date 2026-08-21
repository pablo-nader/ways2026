/**
 * Cliente HTTP + mappers puros de remitos (stage-17-presupuestos-y-remitos, Slice 8):
 * CRUD de borrador, `emitir`/`anular`/listado, y la consolidación (`facturacion`) — espejo del
 * contrato de `Ways.Application.Ventas.ContratosDeRemito`/`RemitosEndpoints`
 * (design.md: "client + pure mappers; `tipos.ts` mirrors the read/write DTOs").
 */
import { api } from './cliente'
import type {
  ComprobanteEmitido,
  EstadoRemito,
  ItemDeRemito,
  LineaDeRemito,
  PaginaDeRemitos,
  PagoDeVenta,
  RemitoDetalle,
  SolicitudDeFacturacionDeRemitos,
  SolicitudDeRemito,
} from './tipos'

// ---- Offset local para desde/hasta — mismo criterio que presupuestos.ts/compras.ts/
// cuentaCorriente.ts: el servidor corre en UTC, `fecha_emision` es `timestamptz` y un
// `<input type="date">` sin offset se interpretaría como UTC (mutation-proof-tests regla 10).
// Duplicado a propósito: no hay un módulo compartido de utilidades de fecha en esta web todavía. --
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

export type FiltrosDeRemitos = {
  idPuntoVenta: number | null
  idCliente: number | null
  estado: EstadoRemito | null
  desde: string
  hasta: string
  pagina: number
  tamanio: number
}

export function filtrosDeRemitosVacios(): FiltrosDeRemitos {
  return { idPuntoVenta: null, idCliente: null, estado: null, desde: '', hasta: '', pagina: 1, tamanio: 25 }
}

/** Arma el query string de `GET /api/remitos` — `desde`/`hasta` con el mismo offset horario
 * explícito que el resto de la web (mutation-proof-tests regla 10). */
export function construirQueryDeRemitos(filtros: FiltrosDeRemitos): string {
  const parametros = new URLSearchParams()
  if (filtros.idPuntoVenta !== null) parametros.set('idPuntoVenta', String(filtros.idPuntoVenta))
  if (filtros.idCliente !== null) parametros.set('idCliente', String(filtros.idCliente))
  if (filtros.estado !== null) parametros.set('estado', filtros.estado)
  if (filtros.desde) parametros.set('desde', fechaIsoConOffset(filtros.desde, '00:00:00'))
  if (filtros.hasta) parametros.set('hasta', fechaIsoConOffset(filtros.hasta, '23:59:59.999'))
  parametros.set('pagina', String(filtros.pagina))
  parametros.set('tamanio', String(filtros.tamanio))
  return `?${parametros.toString()}`
}

export const clienteDeRemitos = {
  listar: (filtros: FiltrosDeRemitos) => api.get<PaginaDeRemitos>(`/remitos${construirQueryDeRemitos(filtros)}`),
  obtener: (id: number) => api.get<RemitoDetalle>(`/remitos/${id}`),
  crear: (solicitud: SolicitudDeRemito) => api.post<RemitoDetalle>('/remitos', solicitud),
  actualizar: (id: number, solicitud: SolicitudDeRemito) => api.put<RemitoDetalle>(`/remitos/${id}`, solicitud),
  emitir: (id: number) => api.post<RemitoDetalle>(`/remitos/${id}/emitir`),
  anular: (id: number) => api.post<RemitoDetalle>(`/remitos/${id}/anular`),
  facturar: (solicitud: SolicitudDeFacturacionDeRemitos) => api.post<ComprobanteEmitido>('/remitos/facturacion', solicitud),
}

export function etiquetaDeEstadoRemito(estado: EstadoRemito): string {
  switch (estado) {
    case 'Borrador':
      return 'Borrador'
    case 'Emitido':
      return 'Emitido'
    case 'Facturado':
      return 'Facturado'
    case 'Anulado':
      return 'Anulado'
  }
}

export function claseDeBadgeDeEstadoRemito(estado: EstadoRemito): string {
  switch (estado) {
    case 'Borrador':
      return 'text-bg-secondary'
    case 'Emitido':
      return 'text-bg-primary'
    case 'Facturado':
      return 'text-bg-success'
    case 'Anulado':
      return 'text-bg-danger'
  }
}

// ---- Formulario del editor de borrador: una línea por fila de la grilla (mismo shape que
// `LineaDePresupuestoFormulario`, con `idLote` opcional — el pick explícito que `emitir` honra,
// SelectorDeLote.tsx/design.md: "lot picker reusing SelectorDeLote") ------------------------------

export type LineaDeRemitoFormulario = {
  /** Clave solo de React — el `PUT` es un replace-set completo, ningún `orden` persistido viaja
   * en el request. */
  clave: number
  idArticulo: number | ''
  descripcion: string
  cantidad: string
  idLote: number | null
}

export function lineaDeRemitoVacia(clave: number): LineaDeRemitoFormulario {
  return { clave, idArticulo: '', descripcion: '', cantidad: '', idLote: null }
}

/** Un item ya persistido → fila de formulario, para reabrir un borrador existente. */
export function itemDeRemitoAFormulario(clave: number, item: ItemDeRemito): LineaDeRemitoFormulario {
  return { clave, idArticulo: item.idArticulo, descripcion: item.descripcion, cantidad: String(item.cantidad), idLote: item.idLote }
}

/** Una línea sin artículo o sin cantidad > 0 nunca viaja al servidor — mismo criterio que
 * `lineaDePresupuestoCompletaParaEnvio`. */
export function lineaDeRemitoCompletaParaEnvio(l: LineaDeRemitoFormulario): boolean {
  const cantidad = Number(l.cantidad)
  return l.idArticulo !== '' && l.cantidad.trim() !== '' && Number.isFinite(cantidad) && cantidad > 0
}

function numeroDeCantidad(valor: string): number {
  const n = Number(valor)
  return valor.trim() === '' || !Number.isFinite(n) ? 0 : n
}

/** Fila de formulario → `LineaDeRemito` — solo las líneas completas
 * (`lineaDeRemitoCompletaParaEnvio`) viajan; una fila a medio llenar nunca llega al servidor. */
export function aLineaDeRemitoSolicitada(l: LineaDeRemitoFormulario): LineaDeRemito {
  return { idArticulo: Number(l.idArticulo), cantidad: numeroDeCantidad(l.cantidad), idLote: l.idLote }
}

export type EncabezadoDeRemitoFormulario = {
  idPuntoVenta: number | ''
  idCliente: number | ''
  direccionEntrega: string
  observaciones: string
}

export function encabezadoDeRemitoVacio(): EncabezadoDeRemitoFormulario {
  return { idPuntoVenta: '', idCliente: '', direccionEntrega: '', observaciones: '' }
}

/** `idCliente` vacío viaja como `null` — Consumidor Final por defecto, mismo criterio que
 * `SolicitudDePresupuesto`. */
export function aSolicitudDeRemito(
  encabezado: EncabezadoDeRemitoFormulario,
  lineas: LineaDeRemitoFormulario[],
): SolicitudDeRemito {
  return {
    idPuntoVenta: encabezado.idPuntoVenta === '' ? 0 : encabezado.idPuntoVenta,
    idCliente: encabezado.idCliente === '' ? null : encabezado.idCliente,
    direccionEntrega: encabezado.direccionEntrega.trim() === '' ? null : encabezado.direccionEntrega.trim(),
    observaciones: encabezado.observaciones.trim() === '' ? null : encabezado.observaciones.trim(),
    lineas: lineas.filter(lineaDeRemitoCompletaParaEnvio).map(aLineaDeRemitoSolicitada),
  }
}

/**
 * `RemitoListado[]` elegidos + pagos → `SolicitudDeFacturacionDeRemitos` de la consolidación
 * (design.md: "pick a cliente + punto de venta, list its emitido unlinked remitos, multi-select,
 * show the summed total, take payments with the POS payment rows, post the consolidation"). Sin
 * `idCliente` (`dto-contract-honesty` regla 1: el servidor lo deriva de los remitos mismos).
 */
export function aSolicitudDeFacturacionDeRemitos(
  idPuntoVenta: number,
  idsRemito: number[],
  pagos: PagoDeVenta[],
  observaciones: string,
): SolicitudDeFacturacionDeRemitos {
  return {
    idPuntoVenta,
    idsRemito,
    pagos,
    observaciones: observaciones.trim() === '' ? null : observaciones.trim(),
  }
}

/** `Σ total` de los remitos elegidos para facturar — reducer puro usado tanto por el cálculo del
 * panel de pagos como por el total mostrado en pantalla (web-descriptor-tests: cubierto con
 * fixtures de valores pairwise-distintos). */
export function totalDeRemitosElegidos(remitos: { total: number }[]): number {
  return remitos.reduce((acumulado, r) => acumulado + r.total, 0)
}

// ---- Multi-select de `FacturarRemitos.tsx` (task 8.8: reducer puro con su propio test) -----------

export type AccionDeSeleccionDeRemitos =
  | { tipo: 'alternar'; id: number }
  | { tipo: 'elegirTodos'; ids: number[] }
  | { tipo: 'limpiar' }

/** Reducer puro del multi-select — `ids` viaja completo en `elegirTodos` (nunca se calcula acá
 * cuáles son "todos", eso lo decide el caller con la lista filtrada vigente). `alternar` no
 * duplica un id ya presente ni dos veces (idempotente sobre el mismo id repetido). */
export function reducirSeleccionDeRemitos(seleccionados: number[], accion: AccionDeSeleccionDeRemitos): number[] {
  switch (accion.tipo) {
    case 'alternar':
      return seleccionados.includes(accion.id) ? seleccionados.filter((id) => id !== accion.id) : [...seleccionados, accion.id]
    case 'elegirTodos':
      return accion.ids
    case 'limpiar':
      return []
  }
}
