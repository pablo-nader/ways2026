/**
 * Cliente HTTP de `parametros` (ADR-13, `GET /api/parametros/{clave}`): resolución punto de
 * venta > empresa > default declarado para las claves de caja, y sucursal > empresa > default para las de sucursal (zona
 * horaria, lotes, días de alerta) — espejo de `ServicioDeParametros.ResolverEnAlcanceAsync`. Con un punto de venta, una
 * clave de sucursal se resuelve por la sucursal de esa caja. Usado por
 * pantallas operativas (no solo `Parametros.tsx`, admin-only) que necesitan el valor EFECTIVO de
 * un parámetro para una empresa/punto de venta puntual — `GET /api/parametros/{clave}` solo exige
 * `Politicas.OperacionDePos`, no `GestionDeCatalogo` (ver `ParametrosEndpoints`).
 */
import { api } from './cliente'
import type { ParametroResuelto } from './tipos'

export const clienteDeParametros = {
  resolver: (clave: string, idEmpresa: number, idPuntoVenta: number | null, idSucursal: number | null = null) =>
    api.get<ParametroResuelto>(
      `/parametros/${clave}?idEmpresa=${idEmpresa}${idPuntoVenta !== null ? `&idPuntoVenta=${idPuntoVenta}` : ''}${
        idSucursal !== null ? `&idSucursal=${idSucursal}` : ''
      }`,
    ),
}
