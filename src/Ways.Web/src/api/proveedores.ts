/**
 * Cliente de proveedores (stage-2-clientes-proveedores): ABM dedicado, no la máquina genérica
 * de catálogos (design decision 1) — mismo shape que `clientes.ts`.
 */
import { api } from './cliente'
import type { AltaProveedor, EdicionProveedor, PaginaDe, ProveedorListado } from './tipos'

export const clienteDeProveedores = {
  listar: (busqueda: string, incluirEliminados: boolean) => {
    const parametros = new URLSearchParams()
    if (busqueda) parametros.set('busqueda', busqueda)
    if (incluirEliminados) parametros.set('incluirEliminados', 'true')
    const cadena = parametros.toString()
    return api.get<PaginaDe<ProveedorListado>>(`/proveedores${cadena ? `?${cadena}` : ''}`)
  },
  /** `GET /api/proveedores/{id}` — resuelve la identidad de un proveedor sin pasar por el
   * listado (stage-15-cc-proveedores-ledger, Slice 6): único camino de `CuentaCorrienteDeProveedor`
   * cuando se llega por URL directa, sin `location.state` (mismo criterio que
   * `clienteDeClientes.obtener`). */
  obtener: (id: number) => api.get<ProveedorListado>(`/proveedores/${id}`),
  crear: (datos: AltaProveedor) => api.post<ProveedorListado>('/proveedores', datos),
  actualizar: (id: number, datos: EdicionProveedor) => api.put<ProveedorListado>(`/proveedores/${id}`, datos),
  eliminar: (id: number) => api.delete(`/proveedores/${id}`),
}
