/**
 * Helpers puros de "Pagos del turno" (stage-gastos-turno-carga-simple) — separados de
 * `PagosDelTurno.tsx` para poder testearlos sin DOM (`web-descriptor-tests`/
 * `mutation-proof-tests`), mismo criterio que `utilidadesVentasDelTurno.ts`.
 */
import { aLineasDeSolicitud, textoDeMediosDelPago, totalDeLineasDePago, type LineaDePagoEditable } from '../api/lineasDePago'
import { CATEGORIAS_PAGO, ORIGENES_DE_FONDOS_PAGO } from '../api/tipos'
import type {
  AreaListado,
  CategoriaPago,
  PagoDeTurno,
  MedioPagoListado,
  OrigenFondosPago,
  ResumenDeTurno,
  SolicitudDePagoConLineas,
} from '../api/tipos'
import { formatearImporte, redondearImporte } from '../formato/importes'
import { fechaHoraATexto } from '../formato/fechas'

export function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

export function formatearFechaHora(iso: string): string {
  return fechaHoraATexto(iso)
}

export function etiquetaDeCategoriaPago(categoria: CategoriaPago): string {
  return CATEGORIAS_PAGO.find((c) => c.valor === categoria)?.etiqueta ?? categoria
}

export function etiquetaDeOrigenFondos(origenFondos: OrigenFondosPago): string {
  return ORIGENES_DE_FONDOS_PAGO.find((o) => o.valor === origenFondos)?.etiqueta ?? origenFondos
}

/**
 * El medio de pago de un pago tiene que representar una salida real de dinero de la caja.
 * `CuentaCorriente` es el mecanismo de CRÉDITO DEL CLIENTE en una venta (aumenta lo que el
 * cliente debe) — no hay ninguna caja de la que salga dinero cuando "se paga" un pago con
 * cuenta corriente, así que se excluye acá (mismo criterio que el Consumidor Final en
 * `Pos.tsx`, que nunca puede pagar con cuenta corriente, aplicado del lado de los pagos).
 * `Efectivo`/`Electronico` sí son salidas reales (caja física o transferencia/tarjeta).
 */
export function mediosValidosParaPago(medios: MedioPagoListado[]): MedioPagoListado[] {
  return medios.filter((m) => m.comportamiento !== 'CuentaCorriente')
}

/**
 * Decisión del pedido (1): elegir un proveedor cambia la categoría a "Proveedor" (el cajero
 * puede después volver a cambiarla a mano, esta función no se vuelve a llamar en ese caso);
 * limpiar el proveedor mientras la categoría sigue en "Proveedor" la vuelve a "Otros". Cualquier
 * otra combinación (proveedor limpiado con una categoría ya distinta de "Proveedor", elegida a
 * mano por el cajero) queda intacta.
 */
export function categoriaAlElegirProveedor(idProveedorNuevo: number | null, categoriaActual: CategoriaPago): CategoriaPago {
  if (idProveedorNuevo !== null) return 'Proveedor'
  return categoriaActual === 'Proveedor' ? 'Otros' : categoriaActual
}

export type FormularioDePago = {
  idPuntoVenta: number
  lineas: readonly LineaDePagoEditable[]
  categoria: CategoriaPago
  idProveedor: number | null
  idArea: number | null
  observaciones: string
  origenFondos: OrigenFondosPago
}

/**
 * Cuerpo de `POST /api/pagos` (decisión del pedido 2): `concepto` es la observación recortada
 * cuando no está vacía, o el texto fijo "Pago del turno" en caso contrario — nunca viaja en
 * blanco (el servidor lo rechaza con 400 `gasto_concepto_requerido`,
 * `ServicioDePagos.ExigirConceptoValido`). `detalle`/`numeroFactura`/
 * `idComprobanteCompra` no los pide este formulario: viajan `null`. `origenFondos` se manda tal
 * cual lo eligió el cajero, nunca inferido. El llamador garantiza `lineasDePagoValidas`.
 */
export function aSolicitudDePago(form: FormularioDePago): SolicitudDePagoConLineas {
  const observaciones = form.observaciones.trim()
  return {
    idPuntoVenta: form.idPuntoVenta,
    categoria: form.categoria,
    idProveedor: form.idProveedor,
    idArea: form.idArea,
    concepto: observaciones !== '' ? observaciones : 'Pago del turno',
    detalle: null,
    numeroFactura: null,
    lineas: aLineasDeSolicitud(form.lineas),
    idComprobanteCompra: null,
    origenFondos: form.origenFondos,
  }
}

/** Total de pagos del turno (pie de la tabla) — suma simple, no hay anuladas que excluir (a
 * diferencia de `totalesDeVentas`: un pago registrado no tiene estado de anulación). */
export function totalDePagos(pagos: PagoDeTurno[]): number {
  return pagos.reduce((acumulado, g) => acumulado + g.importe, 0)
}

/** Efectivo esperado en el cajón: la línea del medio ancla del resumen (ventas en efectivo + fondo
 * + refuerzos − retiros − pagos en efectivo − vueltos). Sin línea para el ancla, 0. */
export function efectivoDisponibleEnCaja(resumen: ResumenDeTurno): number {
  return resumen.medios.find((m) => m.idMedioPago === resumen.idMedioAncla)?.importeEsperado ?? 0
}

/** Un pago saca efectivo del cajón solo si el medio es Efectivo Y se paga desde la caja del
 * turno: el efectivo pagado desde Tesorería no pasa por el cajón. */
export function salidaDeEfectivoDeCaja(medio: MedioPagoListado | undefined, origenFondos: OrigenFondosPago): boolean {
  return medio?.comportamiento === 'Efectivo' && origenFondos === 'CajaTurno'
}

/** Líneas del pago que sacan efectivo del cajón (medio Efectivo desde la caja del turno): las demás no cuentan para el
 * cajón ni para el faltante. */
function lineasQueSacanEfectivo(
  lineas: readonly LineaDePagoEditable[],
  medios: readonly MedioPagoListado[],
  origenFondos: OrigenFondosPago,
): LineaDePagoEditable[] {
  return lineas.filter((l) => salidaDeEfectivoDeCaja(medios.find((m) => m.id === l.idMedioPago), origenFondos))
}

/** `true` si alguna línea saca efectivo del cajón: habilita el aviso de efectivo disponible y el pulso del cajón. */
export function haySalidaDeEfectivoDeCaja(
  lineas: readonly LineaDePagoEditable[],
  medios: readonly MedioPagoListado[],
  origenFondos: OrigenFondosPago,
): boolean {
  return lineasQueSacanEfectivo(lineas, medios, origenFondos).length > 0
}

/** Suma de las líneas que sacan efectivo del cajón; 0 si ninguna lo hace. */
export function importeEnEfectivoDeCaja(
  lineas: readonly LineaDePagoEditable[],
  medios: readonly MedioPagoListado[],
  origenFondos: OrigenFondosPago,
): number {
  return totalDeLineasDePago(lineasQueSacanEfectivo(lineas, medios, origenFondos))
}

/** Texto de la columna "Medio de pago" del pago de un turno: con varias líneas, el reparto; con una sola (o sin
 * líneas, respuestas anteriores) el nombre del medio del catálogo del dispositivo. */
export function textoDeMedioDelPagoDeTurno(pago: PagoDeTurno, medios: readonly MedioPagoListado[] | null): string {
  if ((pago.lineas?.length ?? 0) > 1) return textoDeMediosDelPago(pago)
  return medios?.find((m) => m.id === pago.idMedioPago)?.nombre ?? `Medio #${pago.idMedioPago}`
}

/** Cuánto efectivo falta en el cajón para cubrir el importe; 0 si alcanza o no hay importe. */
export function faltanteDeEfectivo(importe: number | null, disponible: number): number {
  if (importe === null || importe <= 0) return 0
  return Math.max(0, redondearImporte(importe - disponible))
}

/** Área con la que arranca un pago nuevo: la primera ACTIVA (el servidor ordena por nombre, no por
 * estado), igual que el artículo nuevo. `''` si no hay ninguna. */
export function areaPorDefecto(areas: AreaListado[]): number | '' {
  return areas.find((a) => a.activo)?.id ?? ''
}
