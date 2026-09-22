/**
 * Cliente del dispositivo vinculado (stage-desktop-pos) — el POS de escritorio (`pos.html`) no
 * tiene login por mail/password como el resto de la app: primero se vincula el dispositivo a un
 * punto de venta (una vez, con sesión de un Admin) y después cada cajero entra con usuario +
 * contraseña contra ESE dispositivo (`POST /auth/login-dispositivo`). `obtenerActual` es anónimo
 * (lee la cookie `ways.dispositivo`, nunca la de sesión) — un 404 `dispositivo_no_vinculado` es
 * el camino esperado la primera vez, o si el dispositivo fue desvinculado del lado del servidor.
 */
import { api } from './cliente'
import { corriendoEnTauri, establecerTokenDeSesionBearer, guardarSesionDeCajeroPersistida } from './entornoTauri'
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

/** Espejo de `Ways.Application.Dispositivos.DispositivoVinculado` — el secreto viaja en el
 * cuerpo UNA sola vez, además de la cookie, para que el shell de escritorio lo persista del lado
 * de Rust (`entornoTauri.ts`). */
export type DispositivoVinculado = { datos: DispositivoActual; secreto: string }

export type CredencialesDeDispositivo = { usuario: string; password: string }

/** Espejo de `AuthEndpoints.SesionDeDispositivoConBearer` — solo llega cuando la solicitud pidió
 * `solicitarBearer: true` (ver `iniciarSesion`); el resto de los casos siguen devolviendo
 * `UsuarioAutenticado` a secas. */
type RespuestaLoginDeDispositivo = UsuarioAutenticado | { usuario: UsuarioAutenticado; token: string; expiraEl: string }

function esRespuestaConBearer(
  respuesta: RespuestaLoginDeDispositivo,
): respuesta is { usuario: UsuarioAutenticado; token: string; expiraEl: string } {
  return 'token' in respuesta
}

export const clienteDeDispositivos = {
  obtenerActual: () => api.get<DispositivoActual>('/dispositivos/actual'),
  /** Devuelve el sobre completo (`DispositivoVinculado`, datos + secreto) — a propósito, no solo
   * `DispositivoActual`: es responsabilidad del LLAMADOR (`PantallaDeVinculacion.tsx`) decidir
   * qué hacer con el secreto (persistirlo del lado de Rust bajo Tauri), no de este cliente. */
  vincular: (datos: AltaDispositivo) => api.post<DispositivoVinculado>('/dispositivos', datos),
  /** `POST /auth/login-dispositivo` — misma forma de respuesta que `POST /auth/login` para el
   * navegador normal (exige la cookie de dispositivo, nunca funciona en la app web sin ella).
   * Bajo Tauri pide ADEMÁS el token bearer (`solicitarBearer: true`) y lo guarda en memoria
   * (`entornoTauri.ts`) para que `cliente.ts` lo adjunte en las requests siguientes — preparación
   * para la slice 3, donde la cookie va a dejar de viajar por ser cross-site.
   *
   * stage-pos-sesion-offline: además lo persiste del lado de Rust (`guardarSesionDeCajeroPersistida`,
   * con el `expiraEl` que ya trae esta misma respuesta) para que sobreviva un restart — es un
   * `await` extra en el camino feliz, nunca lanza (ver el doc-comment de esa función), así que
   * nunca puede convertir un login exitoso en uno fallido. */
  iniciarSesion: async (credenciales: CredencialesDeDispositivo): Promise<UsuarioAutenticado> => {
    const solicitarBearer = corriendoEnTauri()
    const respuesta = await api.post<RespuestaLoginDeDispositivo>('/auth/login-dispositivo', {
      ...credenciales,
      solicitarBearer,
    })

    if (esRespuestaConBearer(respuesta)) {
      establecerTokenDeSesionBearer(respuesta.token)
      await guardarSesionDeCajeroPersistida(respuesta.token, respuesta.expiraEl)
      return respuesta.usuario
    }

    return respuesta
  },
}
