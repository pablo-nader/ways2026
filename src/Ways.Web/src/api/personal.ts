/**
 * Cliente del personal (back office): ABM dedicado, mismo shape que `proveedores.ts`. Los dos
 * listados de referencia (`usuariosParaVincular`, `clientesParaVincular`) alimentan los selectores
 * de vínculo del formulario y son lecturas de solo lectura de los catálogos de usuarios y clientes.
 */
import { api } from './cliente'
import type { ClienteListado, DatosDePersonal, PaginaDe, PersonalListado, UsuarioListado } from './tipos'

/** Tope de filas por página que acepta el servidor (`Math.Clamp(tamanio, 1, 200)`). */
export const TAMANIO_MAXIMO_DE_PAGINA = 200

export const clienteDePersonal = {
  listar: (busqueda: string) => {
    const parametros = new URLSearchParams()
    if (busqueda) parametros.set('busqueda', busqueda)
    parametros.set('tamanio', String(TAMANIO_MAXIMO_DE_PAGINA))
    return api.get<PaginaDe<PersonalListado>>(`/personal?${parametros.toString()}`)
  },
  crear: (datos: DatosDePersonal) => api.post<PersonalListado>('/personal', datos),
  actualizar: (id: number, datos: DatosDePersonal) => api.put<PersonalListado>(`/personal/${id}`, datos),
  eliminar: (id: number) => api.delete(`/personal/${id}`),
  /** `GET /api/usuarios` (solo Admin y Root): los usuarios que se pueden vincular a un personal. */
  usuariosParaVincular: (busqueda = '') =>
    api.get<PaginaDe<UsuarioListado>>(`/usuarios?${parametrosDeReferencia(busqueda)}`),
  /** `GET /api/clientes`: los clientes que se pueden vincular a un personal. */
  clientesParaVincular: (busqueda = '') =>
    api.get<PaginaDe<ClienteListado>>(`/clientes?${parametrosDeReferencia(busqueda)}`),
}

function parametrosDeReferencia(busqueda: string): string {
  const parametros = new URLSearchParams()
  if (busqueda) parametros.set('busqueda', busqueda)
  parametros.set('tamanio', String(TAMANIO_MAXIMO_DE_PAGINA))
  return parametros.toString()
}
