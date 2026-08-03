/**
 * Cliente de artículos (stage-3-articulos-y-precios): ABM dedicado, no la máquina genérica de
 * catálogos (design decision 1) — mismo shape que `clientes.ts`/`proveedores.ts`, con dos
 * sub-colecciones propias (códigos de barra, sugerencia de precio).
 */
import { api } from './cliente'
import type {
  AltaArticulo,
  AltaCodigoBarra,
  ArticuloListado,
  CodigoBarraListado,
  EdicionArticulo,
  PaginaDe,
  SugerenciaDePrecio,
} from './tipos'

export const clienteDeArticulos = {
  listar: (busqueda: string, incluirEliminados: boolean) => {
    const parametros = new URLSearchParams()
    if (busqueda) parametros.set('busqueda', busqueda)
    if (incluirEliminados) parametros.set('incluirEliminados', 'true')
    const cadena = parametros.toString()
    return api.get<PaginaDe<ArticuloListado>>(`/articulos${cadena ? `?${cadena}` : ''}`)
  },
  /** El listado paginado no completa `idsEmpresas` (evita el N+1) — antes de editar hay que
   * pedir el detalle puntual para no perder el subconjunto real de empresas. */
  obtener: (id: number) => api.get<ArticuloListado>(`/articulos/${id}`),
  crear: (datos: AltaArticulo) => api.post<ArticuloListado>('/articulos', datos),
  actualizar: (id: number, datos: EdicionArticulo) => api.put<ArticuloListado>(`/articulos/${id}`, datos),
  eliminar: (id: number) => api.delete(`/articulos/${id}`),
  codigosBarra: (id: number) => api.get<CodigoBarraListado[]>(`/articulos/${id}/codigos-barra`),
  agregarCodigoBarra: (id: number, datos: AltaCodigoBarra) =>
    api.post<CodigoBarraListado>(`/articulos/${id}/codigos-barra`, datos),
  eliminarCodigoBarra: (id: number, idCodigoBarra: number) =>
    api.delete(`/articulos/${id}/codigos-barra/${idCodigoBarra}`),
  /** Solo lectura: propone, nunca persiste un precio por sí sola (spec: Margin-Based Price
   * Suggestion, "Suggestion requires explicit apply"). */
  sugerenciaDePrecio: (id: number) => api.get<SugerenciaDePrecio>(`/articulos/${id}/sugerencia-precio`),
}
