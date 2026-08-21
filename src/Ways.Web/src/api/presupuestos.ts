/**
 * Cliente HTTP + mappers puros de presupuestos (stage-17-presupuestos-y-remitos, Slice 7):
 * CRUD de borrador, `enviar`/`anular`/`para-venta`, y el listado paginado — espejo del contrato
 * de `Ways.Application.Ventas.ContratosDePresupuesto`/`PresupuestosEndpoints`
 * (design.md: "client + pure mappers; `tipos.ts` mirrors the read/write DTOs").
 */
import { api } from './cliente'
import type {
  EstadoPresupuesto,
  ItemDePresupuesto,
  LineaDePresupuesto,
  PagoDeVenta,
  PaginaDePresupuestos,
  PresupuestoDetalle,
  PresupuestoParaVenta,
  SolicitudDeEnvio,
  SolicitudDePresupuesto,
  SolicitudDeVenta,
} from './tipos'

// ---- Offset local para desde/hasta — mismo criterio que ordenesDeCompra.ts/compras.ts: el
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

export type FiltrosDePresupuestos = {
  idPuntoVenta: number | null
  idCliente: number | null
  estado: EstadoPresupuesto | null
  vencido: boolean | null
  desde: string
  hasta: string
  pagina: number
  tamanio: number
}

export function filtrosDePresupuestosVacios(): FiltrosDePresupuestos {
  return { idPuntoVenta: null, idCliente: null, estado: null, vencido: null, desde: '', hasta: '', pagina: 1, tamanio: 25 }
}

/** Arma el query string de `GET /api/presupuestos` — `vencido` requiere `idPuntoVenta` (design
 * decisión 16, `400 punto_venta_requerido` del lado del servidor si se manda sin uno): esta
 * función nunca lo manda si no hay punto de venta elegido, la pantalla además deshabilita el
 * toggle (tarea 7.11) como defensa en profundidad. `desde`/`hasta` con el mismo offset horario
 * explícito que el resto de la web (mutation-proof-tests regla 10). */
export function construirQueryDePresupuestos(filtros: FiltrosDePresupuestos): string {
  const parametros = new URLSearchParams()
  if (filtros.idPuntoVenta !== null) parametros.set('idPuntoVenta', String(filtros.idPuntoVenta))
  if (filtros.idCliente !== null) parametros.set('idCliente', String(filtros.idCliente))
  if (filtros.estado !== null) parametros.set('estado', filtros.estado)
  if (filtros.vencido !== null && filtros.idPuntoVenta !== null) parametros.set('vencido', String(filtros.vencido))
  if (filtros.desde) parametros.set('desde', fechaIsoConOffset(filtros.desde, '00:00:00'))
  if (filtros.hasta) parametros.set('hasta', fechaIsoConOffset(filtros.hasta, '23:59:59.999'))
  parametros.set('pagina', String(filtros.pagina))
  parametros.set('tamanio', String(filtros.tamanio))
  return `?${parametros.toString()}`
}

export const clienteDePresupuestos = {
  listar: (filtros: FiltrosDePresupuestos) => api.get<PaginaDePresupuestos>(`/presupuestos${construirQueryDePresupuestos(filtros)}`),
  obtener: (id: number) => api.get<PresupuestoDetalle>(`/presupuestos/${id}`),
  crear: (solicitud: SolicitudDePresupuesto) => api.post<PresupuestoDetalle>('/presupuestos', solicitud),
  actualizar: (id: number, solicitud: SolicitudDePresupuesto) => api.put<PresupuestoDetalle>(`/presupuestos/${id}`, solicitud),
  enviar: (id: number, solicitud: SolicitudDeEnvio) => api.post<PresupuestoDetalle>(`/presupuestos/${id}/enviar`, solicitud),
  anular: (id: number) => api.post<PresupuestoDetalle>(`/presupuestos/${id}/anular`),
  paraVenta: (id: number) => api.get<PresupuestoParaVenta>(`/presupuestos/${id}/para-venta`),
}

export function etiquetaDeEstadoPresupuesto(estado: EstadoPresupuesto): string {
  switch (estado) {
    case 'Borrador':
      return 'Borrador'
    case 'Enviado':
      return 'Enviado'
    case 'Convertido':
      return 'Convertido'
    case 'Anulado':
      return 'Anulado'
  }
}

export function claseDeBadgeDeEstadoPresupuesto(estado: EstadoPresupuesto): string {
  switch (estado) {
    case 'Borrador':
      return 'text-bg-secondary'
    case 'Enviado':
      return 'text-bg-primary'
    case 'Convertido':
      return 'text-bg-success'
    case 'Anulado':
      return 'text-bg-danger'
  }
}

/** Formatter del badge de vencimiento — `null` (todavía borrador, sin `vencimiento`) renderiza
 * `—`, nunca una fecha inventada; el prefijo distingue "Vence"/"Venció" del mismo dato crudo
 * (`vencido` es SIEMPRE derivado server-side, `ReglaDePresupuestos` — acá solo se traduce a
 * texto, nunca se recalcula). */
export function etiquetaDeVencimiento(vencimiento: string | null, vencido: boolean): string {
  if (vencimiento === null) return '—'
  const fecha = new Date(`${vencimiento}T00:00:00`).toLocaleDateString('es-AR')
  return vencido ? `Venció ${fecha}` : `Vence ${fecha}`
}

export function claseDeBadgeDeVencimiento(vencimiento: string | null, vencido: boolean): string {
  if (vencimiento === null) return 'text-bg-secondary'
  return vencido ? 'text-bg-danger' : 'text-bg-success'
}

// ---- Formulario del editor de borrador: una línea por fila de la grilla (mismo shape que
// `LineaDeOrdenFormulario` de `ordenesDeCompra.ts`, sin ningún campo de dinero — el precio lo
// resuelve el motor al guardar, nunca lo tipea el operador, design decisión 2) -------------------

export type LineaDePresupuestoFormulario = {
  /** Clave solo de React — el `PUT` es un replace-set completo, ningún `orden` persistido viaja
   * en el request. */
  clave: number
  idArticulo: number | ''
  descripcion: string
  cantidad: string
}

export function lineaDePresupuestoVacia(clave: number): LineaDePresupuestoFormulario {
  return { clave, idArticulo: '', descripcion: '', cantidad: '' }
}

/** Un item ya persistido → fila de formulario, para reabrir un borrador existente. */
export function itemDePresupuestoAFormulario(clave: number, item: ItemDePresupuesto): LineaDePresupuestoFormulario {
  return { clave, idArticulo: item.idArticulo, descripcion: item.descripcion, cantidad: String(item.cantidad) }
}

/** Una línea sin artículo o sin cantidad > 0 nunca viaja al servidor — mismo criterio que
 * `lineaDeOrdenCompletaParaEnvio` de ordenesDeCompra.ts. */
export function lineaDePresupuestoCompletaParaEnvio(l: LineaDePresupuestoFormulario): boolean {
  const cantidad = Number(l.cantidad)
  return l.idArticulo !== '' && l.cantidad.trim() !== '' && Number.isFinite(cantidad) && cantidad > 0
}

function numeroDeCantidad(valor: string): number {
  const n = Number(valor)
  return valor.trim() === '' || !Number.isFinite(n) ? 0 : n
}

/** Fila de formulario → `LineaDePresupuesto` — solo las líneas completas
 * (`lineaDePresupuestoCompletaParaEnvio`) viajan; una fila a medio llenar nunca llega al
 * servidor. */
export function aLineaDePresupuestoSolicitada(l: LineaDePresupuestoFormulario): LineaDePresupuesto {
  return { idArticulo: Number(l.idArticulo), cantidad: numeroDeCantidad(l.cantidad) }
}

export type EncabezadoDePresupuestoFormulario = {
  idPuntoVenta: number | ''
  idCliente: number | ''
  observaciones: string
}

export function encabezadoDePresupuestoVacio(): EncabezadoDePresupuestoFormulario {
  return { idPuntoVenta: '', idCliente: '', observaciones: '' }
}

/** `idCliente` vacío viaja como `null` — Consumidor Final por defecto, mismo criterio que
 * `SolicitudDeVenta`. */
export function aSolicitudDePresupuesto(
  encabezado: EncabezadoDePresupuestoFormulario,
  lineas: LineaDePresupuestoFormulario[],
): SolicitudDePresupuesto {
  return {
    idPuntoVenta: encabezado.idPuntoVenta === '' ? 0 : encabezado.idPuntoVenta,
    idCliente: encabezado.idCliente === '' ? null : encabezado.idCliente,
    observaciones: encabezado.observaciones.trim() === '' ? null : encabezado.observaciones.trim(),
    lineas: lineas.filter(lineaDePresupuestoCompletaParaEnvio).map(aLineaDePresupuestoSolicitada),
  }
}

/** `vencimiento` viaja tal cual el `<input type="date">` la entrega (`DateOnly`, sin offset
 * horario) — mismo criterio que `fechaEsperada` en ordenesDeCompra.ts. */
export function aSolicitudDeEnvio(vencimiento: string): SolicitudDeEnvio {
  return { vencimiento }
}

/** Sugerencia de `vencimiento` para el input de `enviar` — `hoy + 30` en el reloj LOCAL del
 * navegador (design.md: "a date input defaulted to `hoy + 30` in the PV zone"): es solo una
 * sugerencia editable, el servidor valida `vencimiento >= hoy(zona del PV)` con autoridad — un
 * desvío de zona horaria acá nunca puede rechazar en silencio nada, el operador siempre puede
 * cambiar la fecha antes de confirmar. */
export function vencimientoSugerido(ahora: Date): string {
  const sugerido = new Date(ahora.getTime())
  sugerido.setDate(sugerido.getDate() + 30)
  const anio = sugerido.getFullYear()
  const mes = String(sugerido.getMonth() + 1).padStart(2, '0')
  const dia = String(sugerido.getDate()).padStart(2, '0')
  return `${anio}-${mes}-${dia}`
}

/**
 * `PresupuestoParaVenta` confirmado → `SolicitudDeVenta` de conversión (design: Web composition
 * — "post `{ idPuntoVenta, codigoTipoComprobante: 'TX', idPresupuestoOrigen, lineas: undefined,
 * pagos }`"), invocado por `Pos.tsx` al cobrar bajo `?idPresupuesto=`. Sin `idCliente` a
 * propósito (`dto-contract-honesty` regla 1 — el servidor lo deriva del presupuesto; mandar uno
 * acá sería redundante en el mejor caso y un 409 evitable en el peor si alguna vez desincroniza
 * del cliente hidratado en pantalla) y sin `lineas` (el precio congelado sale de
 * `items_presupuesto`, jamás de lo que el carrito pudiera mostrar — proposal decisión 4). */
export function aSolicitudDeVentaDesdePresupuesto(
  idPuntoVenta: number,
  idPresupuestoOrigen: number,
  pagos: PagoDeVenta[],
): SolicitudDeVenta {
  return {
    idPuntoVenta,
    codigoTipoComprobante: 'TX',
    idComprobanteAsociado: null,
    pagos,
    direccionEntrega: null,
    observaciones: null,
    idPresupuestoOrigen,
  }
}
