/**
 * Credencial del ingreso: el servidor acepta exactamente uno de los dos identificadores
 * (`POST /api/auth/login`), y el `usuario` solo vale bajo el dominio propio de un comercio.
 *
 * Módulo PURO: sin React y sin fetch.
 */
import { ErrorApi } from '../api/cliente'

export type CredencialDeLogin = { mail: string } | { usuario: string }

export const MENSAJE_USUARIO_REQUIERE_DOMINIO =
  'Para ingresar con el usuario hay que entrar por el dominio del comercio. Con el correo se puede desde cualquier dominio.'

/**
 * Arma la credencial a partir de lo que se escribió en el campo único. Bajo la plataforma
 * (`admiteUsuario` falso) todo es un correo. Bajo el dominio de un comercio, un `@` lo vuelve
 * correo y su ausencia lo vuelve usuario. El servidor no prohíbe el `@` en un nombre de usuario:
 * quien tenga uno así no puede ingresar por usuario y entra con su correo. El identificador viaja
 * sin espacios en los extremos.
 */
export function credencialDeLogin(identificador: string, admiteUsuario: boolean): CredencialDeLogin {
  const limpio = identificador.trim()

  if (admiteUsuario && !limpio.includes('@')) return { usuario: limpio }

  return { mail: limpio }
}

/** Texto a mostrar ante un ingreso fallido. El `codigo` elige la copia, nunca el mensaje del
 * servidor; el resto de los rechazos rinde el mensaje que trae la API. */
export function mensajeDeFalloDeLogin(error: unknown): string {
  if (!(error instanceof ErrorApi)) return 'No se pudo iniciar sesión.'

  if (error.codigo === 'usuario_requiere_dominio') return MENSAJE_USUARIO_REQUIERE_DOMINIO

  return error.message
}
