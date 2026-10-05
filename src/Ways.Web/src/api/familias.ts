/**
 * Cliente de familias de artículos (doc 10 §3). Todo el grupo `/api/familias` es solo de admin
 * (`Politicas.GestionDeCatalogo`), la misma puerta que el alta y la edición de artículos.
 */
import { api } from './cliente'
import type { FamiliaDetalle, FamiliaListado } from './tipos'

export const clienteDeFamilias = {
  /** Las familias vivas del tenant por nombre, cada una con la cantidad de miembros vivos. */
  listar: () => api.get<FamiliaListado[]>('/familias'),
  /** La familia con sus miembros vivos, los valores compartidos y el estado de precios de su referencia.
   * 404 si no existe, está dada de baja o es de otro tenant. */
  obtener: (id: number) => api.get<FamiliaDetalle>(`/familias/${id}`),
  /** Saca al artículo de su familia: queda sin familia y conserva todos sus valores y sus precios. 409
   * `familia_cambio` si el artículo existe pero ya no es miembro de esa familia. */
  sacarArticulo: (idFamilia: number, idArticulo: number) =>
    api.delete<void>(`/familias/${idFamilia}/articulos/${idArticulo}`),
}
