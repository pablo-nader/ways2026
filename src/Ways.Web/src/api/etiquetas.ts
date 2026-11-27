import { api } from './cliente'
import type { ArticuloListado, DatosDeEtiquetas, FiltroDeEtiquetas, PaginaDe } from './tipos'

/** Tope de la selección explícita del servidor (`seleccion_excedida` por encima). */
export const MAXIMO_DE_ARTICULOS_POR_HOJA = 200

export const clienteDeEtiquetas = {
  /** Por selección explícita: no muta nada, compone precio y ofertas vigentes a un único instante. */
  datosDeArticulos: (idPuntoVenta: number, idListaPrecio: number, idsArticulo: number[]) =>
    api.post<DatosDeEtiquetas>('/etiquetas/datos', { idPuntoVenta, idListaPrecio, idsArticulo }),
  /** Por filtro: lo usa el modo Ofertas para listar solo los artículos con oferta vigente. */
  datosPorFiltro: (idPuntoVenta: number, idListaPrecio: number, filtro: FiltroDeEtiquetas) =>
    api.post<DatosDeEtiquetas>('/etiquetas/datos', { idPuntoVenta, idListaPrecio, filtro }),
  /** Candidatos de la grilla de selección: búsqueda por código o nombre, paginada, solo vendibles. */
  buscarArticulos: (busqueda: string, pagina: number, tamanio: number) => {
    const parametros = new URLSearchParams()
    if (busqueda.trim()) parametros.set('busqueda', busqueda.trim())
    parametros.set('soloVendibles', 'true')
    parametros.set('pagina', String(pagina))
    parametros.set('tamanio', String(tamanio))
    return api.get<PaginaDe<ArticuloListado>>(`/articulos?${parametros.toString()}`)
  },
}
