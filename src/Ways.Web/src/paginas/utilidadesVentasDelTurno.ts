/**
 * Helpers puros de "Ventas del turno" (stage-desktop-pos) — separados de `VentasDelTurno.tsx`
 * para poder testearlos sin DOM (`web-descriptor-tests`/`mutation-proof-tests`): la regla de
 * negocio "solo las no anuladas cuentan en el total" vive acá, no inline en el componente.
 */
import { ROL } from '../api/tipos'
import type { EstadoComprobante, VentaDeTurnoListado } from '../api/tipos'
import { formatearImporte } from '../formato/importes'

export function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

export function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

export function etiquetaDeEstadoVenta(estado: EstadoComprobante): string {
  return estado === 'Anulado' ? 'Anulada' : 'Emitida'
}

export function claseDeBadgeDeEstadoVenta(estado: EstadoComprobante): string {
  return estado === 'Anulado' ? 'bg-danger' : 'bg-success'
}

/** Pie de la tabla (spec del pedido: "cantidad de ventas y total vendido" EXCLUYENDO anuladas) —
 * mutation-proof-tests: el `filter` de acá es el predicado bajo prueba. */
export function totalesDeVentas(ventas: VentaDeTurnoListado[]): { cantidad: number; total: number } {
  const vigentes = ventas.filter((v) => v.estado !== 'Anulado')
  return {
    cantidad: vigentes.length,
    total: vigentes.reduce((acumulado, v) => acumulado + v.total, 0),
  }
}

/** Espejo cliente de `Politicas.OperacionDePos` (Vendedor/Supervisor/Admin, nunca Root) + la
 * transición válida del servidor (`Emitido → Anulado` únicamente, `ReglaDeComprobantes`) — nunca
 * autoritativo (el servidor vuelve a validar todo, 403/409 se manejan igual si este espejo
 * queda desactualizado). */
export function puedeAnular(fila: VentaDeTurnoListado, rolId: number): boolean {
  return fila.estado === 'Emitido' && rolId !== ROL.Root
}
