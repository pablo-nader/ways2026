/**
 * Cliente de artículos (stage-3-articulos-y-precios): ABM dedicado, no la máquina genérica de
 * catálogos (design decision 1) — mismo shape que `clientes.ts`/`proveedores.ts`, con dos
 * sub-colecciones propias (códigos de barra, sugerencia de precio).
 */
import { api } from './cliente'
import type {
  AltaArticulo,
  AltaCodigoBarra,
  ArticuloEscaneado,
  ArticuloListado,
  CodigoBarraListado,
  EdicionArticulo,
  FiltrosDeGrillaDeArticulos,
  PaginaDe,
  PaginaDeGrillaDeArticulos,
  SugerenciaDePrecio,
} from './tipos'

export function filtrosDeGrillaDeArticulosVacios(): FiltrosDeGrillaDeArticulos {
  return {
    codigo: '',
    nombre: '',
    precioDesde: null,
    precioHasta: null,
    idProveedor: null,
    sinProveedor: false,
    activo: null,
    pagina: 1,
    tamanio: 25,
  }
}

/** Query de `GET /api/articulos/grilla` (feat: articulos-grilla-web): omite cada filtro vacío/no
 * aplicado — nunca manda `codigo`/`nombre` en blanco ni un precio `null`. `idProveedor` y
 * `sinProveedor` son mutuamente excluyentes en el servidor (400 `filtro_proveedor_ambiguo` si
 * viajan los dos): acá `sinProveedor` gana cuando el llamador seteó ambos, para que esta capa
 * nunca sea la que dispare esa ambigüedad. */
export function construirQueryDeGrillaDeArticulos(filtros: FiltrosDeGrillaDeArticulos): string {
  const parametros = new URLSearchParams()
  if (filtros.codigo.trim()) parametros.set('codigo', filtros.codigo.trim())
  if (filtros.nombre.trim()) parametros.set('nombre', filtros.nombre.trim())
  if (filtros.precioDesde !== null) parametros.set('precioDesde', String(filtros.precioDesde))
  if (filtros.precioHasta !== null) parametros.set('precioHasta', String(filtros.precioHasta))
  if (filtros.sinProveedor) {
    parametros.set('sinProveedor', 'true')
  } else if (filtros.idProveedor !== null) {
    parametros.set('idProveedor', String(filtros.idProveedor))
  }
  if (filtros.activo !== null) parametros.set('activo', String(filtros.activo))
  parametros.set('pagina', String(filtros.pagina))
  parametros.set('tamanio', String(filtros.tamanio))
  return `?${parametros.toString()}`
}

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
  /** Grilla con filtro multi-columna y paginación real (feat: articulos-grilla-web) — reemplaza
   * `listar` como fuente de `GrillaDeArticulos.tsx`; `listar` sigue viva para otras pantallas
   * (buscador del POS, alta rápida). */
  grilla: (filtros: FiltrosDeGrillaDeArticulos) =>
    api.get<PaginaDeGrillaDeArticulos>(`/articulos/grilla${construirQueryDeGrillaDeArticulos(filtros)}`),
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
  /** Resolución de escaneo del POS (stage-5-pos-ventas, Slice 2/6): identidad únicamente, nunca
   * precio (design decisión 7). `entrada` viaja tal cual la tipeó/leyó el lector — el parseo de
   * `N*codigo` es responsabilidad del servidor (`ParserDeEscaneo`). */
  escanear: (entrada: string) => api.get<ArticuloEscaneado>(`/articulos/escaneo?entrada=${encodeURIComponent(entrada)}`),
}
