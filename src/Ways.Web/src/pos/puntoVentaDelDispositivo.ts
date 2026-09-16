import { clienteDeOrganizacion } from '../api/organizacion'
import type { DispositivoActual } from '../api/dispositivos'
import type { PuntoVentaListado } from '../api/tipos'

/**
 * Resuelve el `PuntoVentaListado` completo (con `idEmpresa`, del que depende la resolución de
 * precios de `Pos.tsx`) del punto de venta fijo del dispositivo — el contrato de
 * `DispositivoActual` no lo trae completo (solo `numero`/`nombre` para el encabezado), así que se
 * busca en el mismo listado que `PuertaDePuntoVenta` ya usa. Compartido por `LoginDeDispositivo`
 * (login del cajero) y `AppPos` (restart con sesión todavía vigente) — una sola fuente de verdad
 * para "no existe más" en vez de dos copias que se puedan desincronizar.
 *
 * `null` si el punto de venta del dispositivo ya no existe (PV dado de baja, dispositivo obsoleto).
 */
export async function resolverPuntoVentaDelDispositivo(dispositivo: DispositivoActual): Promise<PuntoVentaListado | null> {
  const puntosVenta = await clienteDeOrganizacion.listarPuntosVenta()
  return puntosVenta.find((pv) => pv.id === dispositivo.idPuntoVenta) ?? null
}
