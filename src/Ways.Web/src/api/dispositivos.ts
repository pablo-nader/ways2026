/**
 * Cliente del dispositivo vinculado (stage-desktop-pos) — el POS de escritorio (`pos.html`) no
 * tiene login por mail/password como el resto de la app: primero se vincula el dispositivo a un
 * punto de venta (una vez, con sesión de un Admin) y después cada cajero entra con usuario +
 * contraseña contra ESE dispositivo (`POST /auth/login-dispositivo`). `obtenerActual` es anónimo
 * (lee la cookie `ways.dispositivo`, nunca la de sesión) — un 404 `dispositivo_no_vinculado` es
 * el camino esperado la primera vez, o si el dispositivo fue desvinculado del lado del servidor.
 */
import { api } from './cliente'
import type { UsuarioAutenticado } from './tipos'

export type DispositivoActual = {
  id: number
  nombre: string
  idPuntoVenta: number
  puntoVenta: { numero: number; nombre: string }
  empresa: { nombre: string }
}

/** Cuerpo de `POST /api/dispositivos` — requiere sesión de Admin, el servidor setea la cookie de
 * dispositivo en la respuesta. */
export type AltaDispositivo = { idPuntoVenta: number; nombre: string }

export type CredencialesDeDispositivo = { usuario: string; password: string }

export const clienteDeDispositivos = {
  obtenerActual: () => api.get<DispositivoActual>('/dispositivos/actual'),
  vincular: (datos: AltaDispositivo) => api.post<DispositivoActual>('/dispositivos', datos),
  /** `POST /auth/login-dispositivo` — misma forma de respuesta que `POST /auth/login`, pero
   * exige la cookie de dispositivo (nunca funciona en la app web normal). */
  iniciarSesion: (credenciales: CredencialesDeDispositivo) =>
    api.post<UsuarioAutenticado>('/auth/login-dispositivo', credenciales),
}
