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
  /** Suma artículos que ya existen a la familia, alineados con su referencia (su miembro vivo de menor id). Rechaza sin
   * escribir: 400 `articulos_requeridos` con la lista vacía, 400 `demasiados_articulos` (por la lista o por los pares) o
   * `referencia_invalida`, 404 si la familia no existe o está dada de baja, 409 `familia_inactiva`,
   * `familia_sin_articulos` o `articulo_en_otra_familia`, y 422 `familia_precio_inalineable`. Un artículo que ya es
   * miembro de esta familia no es un rechazo: se alinea igual. */
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
