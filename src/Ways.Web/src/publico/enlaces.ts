import type { SucursalPublica } from '../api/tipos'

/** Largo de un número internacional según E.164: de 10 a 15 dígitos con el código de país. */
const MINIMO_INTERNACIONAL = 10
const MAXIMO_INTERNACIONAL = 15
/** Largo de un número argentino sin el 0 de larga distancia: código de área más abonado. */
const DIGITOS_ARGENTINOS = 10
/** Prefijo de país y de celular con el que `wa.me` reconoce un móvil argentino. */
const PREFIJO_MOVIL_ARGENTINO = '549'

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

/**
 * `https://wa.me/<número internacional>`. `wa.me` exige el número completo con código de país, y el
 * dato cargado suele venir en formato local, así que el producto asume Argentina:
 * - con `+` o `00` adelante es internacional: se usa tal cual si tiene de 10 a 15 dígitos;
 * - si no, se descarta un único `0` inicial y, si quedan exactamente 10 dígitos (área más número),
 *   se les antepone `549` (celular argentino);
 * - cualquier otra forma no genera enlace antes que uno equivocado: 8 dígitos sin área, 11 dígitos
 *   ambiguos (`223 15 555 1234`), dos números en el mismo campo o un número con el 54 pero sin `+`.
 */
export function enlaceWhatsapp(valor: string | null | undefined): string | null {
  const texto = (valor ?? '').trim()
  let digitos = texto.replace(/\D/g, '')

  if (texto.startsWith('+') || digitos.startsWith('00')) {
    if (!texto.startsWith('+')) digitos = digitos.slice(2)
    const esValido = digitos.length >= MINIMO_INTERNACIONAL && digitos.length <= MAXIMO_INTERNACIONAL

    return esValido ? `https://wa.me/${digitos}` : null
  }

  const local = digitos.startsWith('0') ? digitos.slice(1) : digitos

  return local.length === DIGITOS_ARGENTINOS ? `https://wa.me/${PREFIJO_MOVIL_ARGENTINO}${local}` : null
}

/**
 * Acepta el usuario (con o sin `@`) o la URL del perfil. Un valor sin `@` que parece un dominio
 * (`instagram.com`, `sur.com.ar`) se trata como URL y no como usuario: un usuario con puntos que
 * parezca dominio se escribe con `@`.
 */
export function enlaceInstagram(valor: string | null | undefined): string | null {
  const texto = (valor ?? '').trim()
  if (texto === '') return null

  const usuario = texto.replace(/^@/, '')
  const pareceDominio = !texto.startsWith('@') && PATRON_DE_DOMINIO.test(texto)
  if (!pareceDominio && PATRON_DE_USUARIO_DE_INSTAGRAM.test(usuario)) return `https://www.instagram.com/${usuario}`

  const url = aUrlHttp(texto)

  return url !== null && esDeDominio(url, 'instagram.com') ? url : null
}

/**
 * Acepta la URL de la página o su nombre de usuario (`FerreteriaSur`). Un valor con forma de
 * dominio (`www.facebook.com`, `sur.com.ar`) se trata como URL, de modo que un nombre de página con
 * puntos solo se acepta dentro de una URL.
 */
export function enlaceFacebook(valor: string | null | undefined): string | null {
  const texto = (valor ?? '').trim()
  if (texto === '') return null

  if (!PATRON_DE_DOMINIO.test(texto) && PATRON_DE_PAGINA_DE_FACEBOOK.test(texto)) return `https://www.facebook.com/${texto}`

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
