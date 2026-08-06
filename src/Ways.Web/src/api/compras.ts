/**
 * Cliente HTTP + reglas puras de compras (stage-8-compras-transferencias-inventario, Slice 5):
 * CRUD de borrador, confirmar/anular/aplicar precio sugerido, y un mirror **no autoritativo** de
 * `CalculadorDeCompra` (Ways.Domain.Compras) para feedback instantáneo en pantalla — el servidor
 * vuelve a derivar todo (`dto-contract-honesty`): ningún endpoint acepta `cantidad`, un total de
 * línea ni un total de header, solo los inputs (`unidades`/`bultos`/`unidadesPorBulto`/
 * `costoUnitario`/`descuento`/`idAlicuotaIva`).
 */
import { api } from './cliente'
import type {
  CompraDetalle,
  EstadoCompra,
  ItemDeCompra,
  LineaDeCompraSolicitada,
  PaginaDeCompras,
  ResultadoAnulacion,
  ResultadoAplicarPrecio,
  SaldoDeProveedor,
  SolicitudDeAplicarPrecios,
  SolicitudDeCompra,
} from './tipos'

function redondear(valor: number, decimales: number): number {
  const factor = 10 ** decimales
  return Math.round((valor + Number.EPSILON) * factor) / factor
}

// ---- Offset local para el filtro desde/hasta (mismo criterio que cuentaCorriente.ts: el
// servidor corre en UTC, `fecha_recepcion` es `timestamptz` y un `<input type="date">` sin
// offset se interpretaría como UTC, perdiendo actividad nocturna en ART). Duplicado a propósito
// (sibling helper, mismo criterio que los statements crudos de `ServicioDeCompras`): no hay un
// módulo compartido de utilidades de fecha en esta web todavía. -----------------------------
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

export type FiltrosDeCompras = {
  idProveedor: number | null
  estado: EstadoCompra | null
  desde: string
  hasta: string
  pagina: number
  tamanio: number
}

export function filtrosDeComprasVacios(): FiltrosDeCompras {
  return { idProveedor: null, estado: null, desde: '', hasta: '', pagina: 1, tamanio: 25 }
}

/** Arma el query string de `GET /api/compras` — `desde`/`hasta` filtran por `fecha_recepcion`
 * (`ServicioDeCompras.ListarAsync`), nunca por `fecha_comprobante`, así que llevan el mismo
 * offset horario explícito que el resto de la web. */
export function construirQueryDeCompras(filtros: FiltrosDeCompras): string {
  const parametros = new URLSearchParams()
  if (filtros.idProveedor !== null) parametros.set('idProveedor', String(filtros.idProveedor))
  if (filtros.estado !== null) parametros.set('estado', filtros.estado)
  if (filtros.desde) parametros.set('desde', fechaIsoConOffset(filtros.desde, '00:00:00'))
  if (filtros.hasta) parametros.set('hasta', fechaIsoConOffset(filtros.hasta, '23:59:59.999'))
  parametros.set('pagina', String(filtros.pagina))
  parametros.set('tamanio', String(filtros.tamanio))
  return `?${parametros.toString()}`
}

export const clienteDeCompras = {
  listar: (filtros: FiltrosDeCompras) => api.get<PaginaDeCompras>(`/compras${construirQueryDeCompras(filtros)}`),
  obtener: (id: number) => api.get<CompraDetalle>(`/compras/${id}`),
  crear: (solicitud: SolicitudDeCompra) => api.post<CompraDetalle>('/compras', solicitud),
  actualizar: (id: number, solicitud: SolicitudDeCompra) => api.put<CompraDetalle>(`/compras/${id}`, solicitud),
  confirmar: (id: number) => api.post<CompraDetalle>(`/compras/${id}/confirmar`),
  anular: (id: number) => api.post<ResultadoAnulacion>(`/compras/${id}/anular`),
  aplicarPrecios: (id: number, solicitud: SolicitudDeAplicarPrecios) =>
    api.post<ResultadoAplicarPrecio[]>(`/compras/${id}/precios`, solicitud),
  /** `GET /api/proveedores/{id}/saldo` — top-level, no dentro de `/api/proveedores` (design: API
   * Surface). Usado acá solo para la columna de estado de pago cuando el listado está filtrado
   * por un proveedor puntual; el panel completo lo construye `Proveedores.tsx` en la Slice 6. */
  obtenerSaldoDeProveedor: (idProveedor: number) => api.get<SaldoDeProveedor>(`/proveedores/${idProveedor}/saldo`),
}

export function etiquetaDeEstadoCompra(estado: EstadoCompra): string {
  switch (estado) {
    case 'Borrador':
      return 'Borrador'
    case 'Confirmada':
      return 'Confirmada'
    case 'Anulada':
      return 'Anulada'
  }
}

// ---- Formulario del editor: una línea por fila de la grilla ---------------------------------

export type LineaDeCompraFormulario = {
  /** Clave solo de React — el `PUT` es un replace-set completo (design decisión 2), ningún id de
   * línea persistido viaja en el request ni sobrevive entre saves. */
  clave: number
  idArticulo: number | ''
  descripcion: string
  unidades: string
  bultos: string
  unidadesPorBulto: string
  costoUnitario: string
  descuento: string
  idAlicuotaIva: number | ''
  actualizaCosto: boolean
}

export function lineaDeCompraVacia(clave: number): LineaDeCompraFormulario {
  return {
    clave,
    idArticulo: '',
    descripcion: '',
    unidades: '',
    bultos: '',
    unidadesPorBulto: '',
    costoUnitario: '',
    descuento: '',
    idAlicuotaIva: '',
    actualizaCosto: true,
  }
}

/** Un item ya persistido → fila de formulario, para reabrir un borrador existente. */
export function itemAFormulario(clave: number, item: ItemDeCompra): LineaDeCompraFormulario {
  return {
    clave,
    idArticulo: item.idArticulo,
    descripcion: item.descripcion,
    unidades:
      item.bultos !== null && item.unidadesPorBulto !== null
        ? String(redondear(item.cantidad - item.bultos * item.unidadesPorBulto, 3))
        : String(item.cantidad),
    bultos: item.bultos === null ? '' : String(item.bultos),
    unidadesPorBulto: item.unidadesPorBulto === null ? '' : String(item.unidadesPorBulto),
    costoUnitario: String(item.costoUnitario),
    descuento: String(item.descuento),
    idAlicuotaIva: item.idAlicuotaIva,
    actualizaCosto: item.actualizaCosto,
  }
}

function numero(valor: string): number {
  const n = Number(valor)
  return valor.trim() === '' || !Number.isFinite(n) ? 0 : n
}

function numeroONulo(valor: string): number | null {
  return valor.trim() === '' ? null : Number(valor)
}

export function lineaCompletaParaEnvio(l: LineaDeCompraFormulario): boolean {
  return l.idArticulo !== '' && l.idAlicuotaIva !== '' && l.unidades.trim() !== '' && l.costoUnitario.trim() !== ''
}

/** Fila de formulario → `LineaDeCompraSolicitada` — solo se envían las filas completas
 * (`lineaCompletaParaEnvio`), una fila a medio llenar nunca viaja al servidor. */
export function aLineaSolicitada(l: LineaDeCompraFormulario): LineaDeCompraSolicitada {
  return {
    idArticulo: Number(l.idArticulo),
    descripcion: l.descripcion.trim(),
    unidades: numero(l.unidades),
    bultos: numeroONulo(l.bultos),
    unidadesPorBulto: numeroONulo(l.unidadesPorBulto),
    costoUnitario: numero(l.costoUnitario),
    descuento: l.descuento.trim() === '' ? 0 : numero(l.descuento),
    idAlicuotaIva: Number(l.idAlicuotaIva),
    actualizaCosto: l.actualizaCosto,
  }
}

export type EncabezadoDeCompraFormulario = {
  idProveedor: number | ''
  idTipoComprobante: number | ''
  idPuntoVenta: number | ''
  numeroExterno: string
  fechaComprobante: string
  observaciones: string
}

/** `fechaComprobante` es `date` (`DateOnly`), no `timestamptz` — viaja tal cual el
 * `<input type="date">` la entrega (`YYYY-MM-DD`), sin ningún offset horario (a diferencia de
 * `desde`/`hasta` del listado, que sí filtran contra un `timestamptz`). */
export function aSolicitudDeCompra(
  encabezado: EncabezadoDeCompraFormulario,
  lineas: LineaDeCompraFormulario[],
): SolicitudDeCompra {
  return {
    idProveedor: encabezado.idProveedor === '' ? 0 : encabezado.idProveedor,
    idTipoComprobante: encabezado.idTipoComprobante === '' ? 0 : encabezado.idTipoComprobante,
    idPuntoVenta: encabezado.idPuntoVenta === '' ? 0 : encabezado.idPuntoVenta,
    numeroExterno: encabezado.numeroExterno.trim() === '' ? null : encabezado.numeroExterno.trim(),
    fechaComprobante: encabezado.fechaComprobante === '' ? null : encabezado.fechaComprobante,
    observaciones: encabezado.observaciones.trim() === '' ? null : encabezado.observaciones.trim(),
    items: lineas.filter(lineaCompletaParaEnvio).map(aLineaSolicitada),
  }
}

// ---- Mirror no autoritativo de CalculadorDeCompra (design: "Compra Arithmetic") --------------

export type LineaDeCalculo = {
  unidades: number
  bultos: number
  unidadesPorBulto: number
  costoUnitario: number
  descuento: number
  porcentajeIva: number
}

export type ItemCalculado = { cantidad: number; bruto: number; total: number; costoEfectivo: number | null }

export type TotalesDeCompra = {
  items: ItemCalculado[]
  subtotal: number
  descuentoTotal: number
  ivaTotal: number | null
  total: number
}

/** Fila de formulario → `LineaDeCalculo`, resolviendo `porcentajeIva` desde el catálogo de
 * alícuotas (el formulario solo guarda el id, nunca el porcentaje). */
export function lineaFormularioACalculo(
  l: LineaDeCompraFormulario,
  porcentajePorAlicuota: Record<number, number>,
): LineaDeCalculo {
  return {
    unidades: numero(l.unidades),
    bultos: numero(l.bultos),
    unidadesPorBulto: numero(l.unidadesPorBulto),
    costoUnitario: numero(l.costoUnitario),
    descuento: numero(l.descuento),
    porcentajeIva: l.idAlicuotaIva === '' ? 0 : (porcentajePorAlicuota[l.idAlicuotaIva] ?? 0),
  }
}

function calcularItem(l: LineaDeCalculo, discriminaIva: boolean): ItemCalculado {
  const cantidad = redondear(l.unidades + l.bultos * l.unidadesPorBulto, 3)
  const bruto = redondear(cantidad * l.costoUnitario, 2)
  const total = redondear(bruto - l.descuento, 2)
  const costoEfectivo =
    cantidad <= 0
      ? null
      : discriminaIva
        ? redondear((total * (1 + l.porcentajeIva / 100)) / cantidad, 2)
        : redondear(total / cantidad, 2)
  return { cantidad, bruto, total, costoEfectivo }
}

/** Espejo de `CalculadorDeCompra.Calcular` (design: "Compra Arithmetic") — puramente informativo,
 * el servidor recalcula todo desde cero al guardar (`dto-contract-honesty`). */
export function calcularTotalesDeCompra(lineas: LineaDeCalculo[], discriminaIva: boolean): TotalesDeCompra {
  const items = lineas.map((l) => calcularItem(l, discriminaIva))
  const subtotal = redondear(
    items.reduce((acumulado, i) => acumulado + i.bruto, 0),
    2,
  )
  const descuentoTotal = redondear(
    lineas.reduce((acumulado, l) => acumulado + l.descuento, 0),
    2,
  )

  if (!discriminaIva) {
    return { items, subtotal, descuentoTotal, ivaTotal: null, total: redondear(subtotal - descuentoTotal, 2) }
  }

  const ivaTotal = redondear(
    items.reduce((acumulado, item, indice) => acumulado + redondear((item.total * lineas[indice].porcentajeIva) / 100, 2), 0),
    2,
  )
  return { items, subtotal, descuentoTotal, ivaTotal, total: redondear(subtotal - descuentoTotal + ivaTotal, 2) }
}

/** Espejo de la regla `descuento(i) > bruto(i) ⇒ 400 descuento_de_item_invalido` — feedback
 * instantáneo, nunca autoritativo (el servidor la vuelve a validar). */
export function lineaConDescuentoInvalido(l: LineaDeCalculo): boolean {
  const cantidad = redondear(l.unidades + l.bultos * l.unidadesPorBulto, 3)
  const bruto = redondear(cantidad * l.costoUnitario, 2)
  return l.descuento > bruto
}
