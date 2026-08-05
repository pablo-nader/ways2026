/**
 * Cliente de clientes (stage-2-clientes-proveedores): ABM dedicado, no la máquina genérica de
 * catálogos (design decision 1). `listasDePrecioAsignables` es una referencia de solo lectura
 * para el selector del formulario — no un ABM de `listas_precio` (sin ABM propio esta etapa).
 */
import { api } from './cliente'
import type { AltaCliente, ClienteListado, EdicionCliente, ListaPrecioAsignable, PaginaDe } from './tipos'

export const clienteDeClientes = {
  listar: (busqueda: string, incluirEliminados: boolean) => {
    const parametros = new URLSearchParams()
    if (busqueda) parametros.set('busqueda', busqueda)
    if (incluirEliminados) parametros.set('incluirEliminados', 'true')
    const cadena = parametros.toString()
    return api.get<PaginaDe<ClienteListado>>(`/clientes${cadena ? `?${cadena}` : ''}`)
  },
  obtener: (id: number) => api.get<ClienteListado>(`/clientes/${id}`),
  crear: (datos: AltaCliente) => api.post<ClienteListado>('/clientes', datos),
  actualizar: (id: number, datos: EdicionCliente) => api.put<ClienteListado>(`/clientes/${id}`, datos),
  eliminar: (id: number) => api.delete(`/clientes/${id}`),
  listasDePrecioAsignables: () => api.get<ListaPrecioAsignable[]>('/listas-precio'),
}
