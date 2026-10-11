/**
 * Cliente de lo público: lo que se puede preguntar sin sesión. Hoy es una sola lectura, la del
 * sitio por el que se entró (plataforma o dominio de un comercio), que la pantalla de ingreso usa
 * para saber qué identificador pedir.
 */
import { api } from './cliente'
import type { SitioPublico } from './tipos'

export const clienteDePublico = {
  obtenerSitio: () => api.get<SitioPublico>('/publico/sitio'),
}
