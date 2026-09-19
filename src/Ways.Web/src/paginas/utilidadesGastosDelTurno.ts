/**
 * Helpers puros de "Gastos del turno" (stage-gastos-turno-carga-simple) — separados de
 * `GastosDelTurno.tsx` para poder testearlos sin DOM (`web-descriptor-tests`/
 * `mutation-proof-tests`), mismo criterio que `utilidadesVentasDelTurno.ts`.
 */
import { CATEGORIAS_GASTO } from '../api/tipos'
import type { CategoriaGasto, GastoDeTurno, MedioPagoListado, SolicitudDeGasto } from '../api/tipos'
import { formatearImporte } from '../formato/importes'

export function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

export function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

export function etiquetaDeCategoriaGasto(categoria: CategoriaGasto): string {
  return CATEGORIAS_GASTO.find((c) => c.valor === categoria)?.etiqueta ?? categoria
}

/**
 * El medio de pago de un gasto tiene que representar una salida real de dinero de la caja.
 * `CuentaCorriente` es el mecanismo de CRÉDITO DEL CLIENTE en una venta (aumenta lo que el
 * cliente debe) — no hay ninguna caja de la que salga dinero cuando "se paga" un gasto con
 * cuenta corriente, así que se excluye acá (mismo criterio que el Consumidor Final en
 * `Pos.tsx`, que nunca puede pagar con cuenta corriente, aplicado del lado de los gastos).
 * `Efectivo`/`Electronico` sí son salidas reales (caja física o transferencia/tarjeta).
 */
export function mediosValidosParaGasto(medios: MedioPagoListado[]): MedioPagoListado[] {
  return medios.filter((m) => m.comportamiento !== 'CuentaCorriente')
}

/**
 * Decisión del pedido (1): elegir un proveedor cambia la categoría a "Proveedor" (el cajero
 * puede después volver a cambiarla a mano, esta función no se vuelve a llamar en ese caso);
 * limpiar el proveedor mientras la categoría sigue en "Proveedor" la vuelve a "Otros". Cualquier
 * otra combinación (proveedor limpiado con una categoría ya distinta de "Proveedor", elegida a
 * mano por el cajero) queda intacta.
 */
export function categoriaAlElegirProveedor(idProveedorNuevo: number | null, categoriaActual: CategoriaGasto): CategoriaGasto {
  if (idProveedorNuevo !== null) return 'Proveedor'
  return categoriaActual === 'Proveedor' ? 'Otros' : categoriaActual
}

export type FormularioDeGasto = {
  idPuntoVenta: number
  importe: number
  idMedioPago: number
  categoria: CategoriaGasto
  idProveedor: number | null
  observaciones: string
}

/**
 * Cuerpo de `POST /api/gastos` (decisión del pedido 2): `concepto` es la observación recortada
 * cuando no está vacía, o el texto fijo "Gasto del turno" en caso contrario — nunca viaja en
 * blanco (el servidor lo rechaza con 400 `gasto_concepto_requerido`,
 * `ServicioDeGastos.ExigirConceptoValido`). `detalle`/`idArea`/`numeroFactura`/
 * `idComprobanteCompra` no los pide este formulario: viajan `null`.
 */
export function aSolicitudDeGasto(form: FormularioDeGasto): SolicitudDeGasto {
  const observaciones = form.observaciones.trim()
  return {
    idPuntoVenta: form.idPuntoVenta,
    categoria: form.categoria,
    idProveedor: form.idProveedor,
    idArea: null,
    concepto: observaciones !== '' ? observaciones : 'Gasto del turno',
    detalle: null,
    idMedioPago: form.idMedioPago,
    numeroFactura: null,
    importe: form.importe,
    idComprobanteCompra: null,
  }
}

/** Total de gastos del turno (pie de la tabla) — suma simple, no hay anuladas que excluir (a
 * diferencia de `totalesDeVentas`: un gasto registrado no tiene estado de anulación). */
export function totalDeGastos(gastos: GastoDeTurno[]): number {
  return gastos.reduce((acumulado, g) => acumulado + g.importe, 0)
}
