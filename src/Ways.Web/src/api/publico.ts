/**
 * Cliente de lo público: lo que se puede preguntar sin sesión. La pantalla de ingreso lee el sitio
 * por el que se entró (plataforma o dominio de un comercio) y la landing y la tienda leen la
 * vitrina publicada de ese comercio.
 */
import { api } from './cliente'
import type { SitioPublico, VitrinaPublica } from './tipos'

export const clienteDePublico = {
  obtenerSitio: () => api.get<SitioPublico>('/publico/sitio'),
  obtenerVitrina: () => api.get<VitrinaPublica>('/publico/vitrina'),
}

/** Logo del comercio para un `<img src>`; `version` (`logoVersion`) cambia la URL al subir otro. */
export function urlDelLogoPublico(version: number | null): string {
  const base = '/api/publico/vitrina/logo'

  return version === null ? base : `${base}?v=${version}`
}
