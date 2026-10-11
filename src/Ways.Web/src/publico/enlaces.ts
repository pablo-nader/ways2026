import type { SucursalPublica } from '../api/tipos'

/** Largo mínimo de un número que `wa.me` acepta; por debajo es un dato mal cargado, no un teléfono. */
const MINIMO_DE_DIGITOS = 8

const PATRON_DE_USUARIO_DE_INSTAGRAM = /^[A-Za-z0-9._]{1,30}$/
const PATRON_DE_PAGINA_DE_FACEBOOK = /^[A-Za-z0-9.-]{5,50}$/
const PATRON_DE_DOMINIO = /^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+(?::\d+)?([/?#]\S*)?$/

/** Devuelve la URL si es http(s) válida y, si el texto es un dominio pelado (`www.sitio.com`), le
 * antepone `https://`. Cualquier otro esquema (`javascript:`, `data:`) se descarta. */
function aUrlHttp(valor: string): string | null {
  const texto = valor.trim()
  if (texto === '') return null

  const candidata = /^https?:\/\//i.test(texto) ? texto : PATRON_DE_DOMINIO.test(texto) ? `https://${texto}` : null
  if (candidata === null) return null

  try {
    const url = new URL(candidata)

    return url.protocol === 'http:' || url.protocol === 'https:' ? url.href : null
  } catch {
    return null
  }
}

function esDeDominio(url: string, ...dominios: string[]): boolean {
  const host = new URL(url).hostname.toLowerCase()

  return dominios.some((dominio) => host === dominio || host.endsWith(`.${dominio}`))
}

/** `https://wa.me/<dígitos>`: el número puede traer espacios, guiones, paréntesis o `+`. */
export function enlaceWhatsapp(valor: string | null | undefined): string | null {
  const digitos = (valor ?? '').replace(/\D/g, '')

  return digitos.length >= MINIMO_DE_DIGITOS ? `https://wa.me/${digitos}` : null
}

/** Acepta el usuario (con o sin `@`) o la URL del perfil. */
export function enlaceInstagram(valor: string | null | undefined): string | null {
  const texto = (valor ?? '').trim()
  if (texto === '') return null

  const usuario = texto.replace(/^@/, '')
  if (PATRON_DE_USUARIO_DE_INSTAGRAM.test(usuario)) return `https://www.instagram.com/${usuario}`

  const url = aUrlHttp(texto)

  return url !== null && esDeDominio(url, 'instagram.com') ? url : null
}

/** Acepta la URL de la página o su nombre de usuario (`FerreteriaSur`). */
export function enlaceFacebook(valor: string | null | undefined): string | null {
  const texto = (valor ?? '').trim()
  if (texto === '') return null

  if (PATRON_DE_PAGINA_DE_FACEBOOK.test(texto)) return `https://www.facebook.com/${texto}`

  const url = aUrlHttp(texto)

  return url !== null && esDeDominio(url, 'facebook.com', 'fb.com', 'fb.me') ? url : null
}

/** Sitio propio del comercio: URL completa o dominio pelado. */
export function enlaceWeb(valor: string | null | undefined): string | null {
  return aUrlHttp(valor ?? '')
}

/** Enlace del botón de WhatsApp de la portada: el primer punto de venta con un número usable. */
export function enlaceWhatsappDelComercio(sucursales: readonly SucursalPublica[]): string | null {
  for (const sucursal of sucursales) {
    const enlace = enlaceWhatsapp(sucursal.whatsapp)
    if (enlace !== null) return enlace
  }

  return null
}
