/**
 * Cliente HTTP de `parametros` (ADR-13, `GET /api/parametros/{clave}`): resolución punto de
 * venta > empresa > default declarado — espejo de `ServicioDeParametros.ResolverAsync`. Usado por
 * pantallas operativas (no solo `Parametros.tsx`, admin-only) que necesitan el valor EFECTIVO de
 * un parámetro para una empresa/punto de venta puntual — `GET /api/parametros/{clave}` solo exige
 * `Politicas.OperacionDePos`, no `GestionDeCatalogo` (ver `ParametrosEndpoints`).
 */
import { api } from './cliente'
import type { ParametroResuelto } from './tipos'

export const clienteDeParametros = {
  resolver: (clave: string, idEmpresa: number, idPuntoVenta: number | null) =>
    api.get<ParametroResuelto>(
      `/parametros/${clave}?idEmpresa=${idEmpresa}${idPuntoVenta !== null ? `&idPuntoVenta=${idPuntoVenta}` : ''}`,
    ),
}
