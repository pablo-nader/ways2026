/**
 * Cliente de familias de artículos (doc 10 §3). Todo el grupo `/api/familias` es solo de admin
 * (`Politicas.GestionDeCatalogo`), la misma puerta que el alta y la edición de artículos.
 */
import { api } from './cliente'
import type {
  AgregadoDeArticulos,
  AltaDeFamilia,
  EdicionFamilia,
  FamiliaDetalle,
  FamiliaListado,
  PrevisualizacionDeAgrupacion,
  ResultadoDeAgrupacion,
  SolicitudDePrevisualizacion,
} from './tipos'

export const clienteDeFamilias = {
  /** Las familias vivas del tenant por nombre, cada una con la cantidad de miembros vivos. */
  listar: () => api.get<FamiliaListado[]>('/familias'),
  /** La familia con sus miembros vivos, los valores compartidos y el estado de precios de su referencia.
   * 404 si no existe, está dada de baja o es de otro tenant. */
  obtener: (id: number) => api.get<FamiliaDetalle>(`/familias/${id}`),
  /** Qué cambiaría si se agruparan esos artículos con la referencia como modelo. No escribe nada: es una foto sin
   * locks, así que el pedido real puede rechazar lo que acá salió bien. */
  previsualizar: (solicitud: SolicitudDePrevisualizacion) =>
    api.post<PrevisualizacionDeAgrupacion>('/familias/previsualizacion', solicitud),
  /** Crea la familia y la agrupa. Todo o nada: 400 `referencia_invalida` o `demasiados_articulos`, 409
   * `familia_nombre_duplicado` o `articulo_en_otra_familia`, 422 `familia_precio_inalineable`. */
  crear: (alta: AltaDeFamilia) => api.post<ResultadoDeAgrupacion>('/familias', alta),
  /** Suma artículos que ya existen a la familia, alineados con su referencia. Los mismos rechazos que `crear`, más
   * 409 `familia_inactiva` y `familia_sin_articulos`, y 400 `articulos_requeridos` con la lista vacía. */
  agregarArticulos: (id: number, datos: AgregadoDeArticulos) =>
    api.post<ResultadoDeAgrupacion>(`/familias/${id}/articulos`, datos),
  /** Cambia el nombre y el estado; responde la familia como el listado. 409 `familia_nombre_duplicado`. */
  actualizar: (id: number, datos: EdicionFamilia) => api.put<FamiliaListado>(`/familias/${id}`, datos),
  /** Saca al artículo de su familia: queda sin familia y conserva todos sus valores y sus precios. 409
   * `familia_cambio` si el artículo existe pero ya no es miembro de esa familia. */
  sacarArticulo: (idFamilia: number, idArticulo: number) =>
    api.delete<void>(`/familias/${idFamilia}/articulos/${idArticulo}`),
  /** Disuelve la familia: sus miembros vivos quedan sin familia con todos sus valores y la familia se da de baja. */
  disolver: (id: number) => api.delete<void>(`/familias/${id}`),
}
