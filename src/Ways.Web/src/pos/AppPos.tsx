import { useCallback, useEffect, useRef, useState } from 'react'
import { MemoryRouter } from 'react-router'
import { clienteDeDispositivos } from '../api/dispositivos'
import type { DispositivoActual } from '../api/dispositivos'
import { alPerderLaSesion, api, ErrorApi } from '../api/cliente'
import { leerCredencialDeDispositivo } from '../api/entornoTauri'
import { puedeOperarPos } from '../api/tipos'
import type { PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'
import { Cargando } from '../componentes/Cargando'
import { LoginDeDispositivo } from './LoginDeDispositivo'
import { PantallaDeVinculacion } from './PantallaDeVinculacion'
import { resolverPuntoVentaDelDispositivo } from './puntoVentaDelDispositivo'
import { ShellPos } from './ShellPos'

type Estado =
  | { fase: 'cargando' }
  | { fase: 'sin-vincular' }
  | { fase: 'error'; mensaje: string }
  | { fase: 'sin-red-pero-vinculado' }
  | { fase: 'vinculado-sin-sesion'; dispositivo: DispositivoActual }
  | { fase: 'con-sesion'; dispositivo: DispositivoActual; usuario: UsuarioAutenticado; puntoVenta: PuntoVentaListado }

const MENSAJE_GENERICO = 'No se pudo determinar el dispositivo.'

/**
 * Producto: la sesión del cajero NO vence hasta que se cierra a mano (`POST /auth/login-dispositivo`
 * no expira sola) — un restart del shell de escritorio, o un F5 de la página, con la cookie de
 * sesión todavía viva tiene que caer derecho en `con-sesion`, nunca repreguntar login. `GET
 * /auth/me` (el mismo endpoint que ya usa `AuthContext`) es la única fuente de verdad de si esa
 * cookie sigue siendo válida — `GET /dispositivos/actual` no lo sabe, es anónimo.
 *
 * stage-pos-sesion-offline: bajo Tauri no hay cookie (`cliente.ts` manda `credentials: 'omit'`
 * ahí) — el mismo `GET /auth/me` de arriba autentica con el token bearer que `entornoTauri.ts`
 * tenga en memoria en ese momento. `pos/main.tsx` restaura ese bearer desde disco
 * (`restaurarSesionDeCajeroPersistida`) ANTES de montar este componente, así que esta función NO
 * necesita saber nada de Tauri ni de dónde vino el token: si `/auth/me` acepta la credencial que
 * `headerBearerSiCorresponde` adjuntó (cookie o bearer, restaurado o recién logueado), cae en
 * `con-sesion` igual; si la rechaza (vencida, revocada, o no había ninguna), cae en
 * `vinculado-sin-sesion` igual. Ningún estado nuevo hace falta en esta máquina de estados para
 * eso — ver el reporte de la tarea para el trazado completo de por qué.
 *
 * Nunca lanza: cualquier falla se traduce a un `Estado` (nunca deja "a medio camino" una sesión
 * que no se puede operar). Devuelve `vinculado-sin-sesion` tanto para un 401 legítimo como para un
 * usuario que no puede operar el POS o un PV que ya no existe — en estos dos últimos casos cierra
 * esa sesión del lado del servidor antes, porque no hay ningún estado de este shell que sepa qué
 * hacer con ella (ni pairing, que es un dispositivo distinto, ni shell, que exige un PV).
 */
async function resolverSesionDelDispositivo(dispositivo: DispositivoActual): Promise<Estado> {
  try {
    const usuario = await api.get<UsuarioAutenticado>('/auth/me')

    if (!puedeOperarPos(usuario.rolId)) {
      await api.post('/auth/logout').catch(() => undefined)
      return { fase: 'vinculado-sin-sesion', dispositivo }
    }

    const puntoVenta = await resolverPuntoVentaDelDispositivo(dispositivo)
    if (!puntoVenta) {
      await api.post('/auth/logout').catch(() => undefined)
      return { fase: 'vinculado-sin-sesion', dispositivo }
    }

    return { fase: 'con-sesion', dispositivo, usuario, puntoVenta }
  } catch (error) {
    if (error instanceof ErrorApi && error.esNoAutenticado) {
      return { fase: 'vinculado-sin-sesion', dispositivo }
    }
    return { fase: 'error', mensaje: error instanceof ErrorApi ? error.message : MENSAJE_GENERICO }
  }
}

/**
 * Punto de entrada del POS de escritorio (`pos.html`) — stage-desktop-pos. Desde la slice 3 es
 * una página LOCAL bundleada con la app (`Ways.Desktop/ui/pos.html`), no una servida por la API;
 * habla con el servidor por red (`cliente.ts`, `entornoTauri.ts`). Máquina de estados propia (no
 * reusa `AuthProvider`/`RutaProtegida`/`PuertaDePuntoVenta` de la app completa: esas asumen login
 * por mail/password y elección manual de PV, acá el dispositivo fija ambas cosas):
 *
 * 1. `GET /dispositivos/actual` (anónimo) → 404 `dispositivo_no_vinculado` = primera vez o
 *    dispositivo revocado → `PantallaDeVinculacion`. Este resultado es SIEMPRE una respuesta real
 *    del servidor (nunca una inferencia local) y siempre gana, aunque haya una credencial local.
 * 2. Cualquier otra falla en la llamada — el servidor ni siquiera pudo responder (red caída, DNS,
 *    CORS) — consulta la credencial local guardada (`leerCredencialDeDispositivo`,
 *    `entornoTauri.ts`, respaldada por `leer_credencial_de_dispositivo` del lado de Rust, que esta
 *    página SÍ tiene permitido invocar — ver `capabilities/pos.json`). Si hay una, la base sigue
 *    sin poder confirmar nada, pero hay evidencia local de que este equipo ya se vinculó alguna
 *    vez → `sin-red-pero-vinculado` (mensaje específico, con reintentar). Si no hay credencial
 *    local tampoco, → `error` genérico: no hay ninguna evidencia de nada.
 * 3. Dispositivo conocido → `resolverSesionDelDispositivo` (`GET /auth/me`): con una cookie/bearer
 *    de sesión todavía vivo y operable, entra derecho a `con-sesion` sin pedir nada — sin sesión
 *    (o inválida), `LoginDeDispositivo`.
 * 4. Con sesión → `ShellPos` (vender / cerrar caja / Caja Z), con el punto de venta fijo del
 *    dispositivo.
 *
 * Un 401 en CUALQUIER llamada mientras se está en `con-sesion` (sesión revocada del lado del
 * servidor, ej. el dispositivo se desvincula con el cajero todavía adentro) vuelve a correr el
 * paso 1 completo (`alPerderLaSesion`, el mismo observador que usa `AuthContext`) — así se
 * distingue solo "cerró sesión" (paso 3 lo manda a `LoginDeDispositivo`) de "además el dispositivo
 * quedó revocado" (paso 1 lo manda a `PantallaDeVinculacion`) sin duplicar esa lógica.
 *
 * El `MemoryRouter` es una única instancia para todas las fases (nunca se remonta al cambiar de
 * fase) — `Pos.tsx`/`CierreDeCaja.tsx`/`CajaZ.tsx` necesitan un Router para sus hooks de
 * navegación aunque las pantallas de vinculación/login no lo usen.
 */
export function AppPos() {
  const [estado, setEstado] = useState<Estado>({ fase: 'cargando' })
  const generacionRef = useRef(0)
  const estadoRef = useRef(estado)
  estadoRef.current = estado

  const cargarDispositivo = useCallback(async () => {
    const generacion = ++generacionRef.current
    setEstado({ fase: 'cargando' })

    try {
      const dispositivo = await clienteDeDispositivos.obtenerActual()
      if (generacionRef.current !== generacion) return

      const siguiente = await resolverSesionDelDispositivo(dispositivo)
      if (generacionRef.current !== generacion) return
      setEstado(siguiente)
    } catch (error) {
      if (generacionRef.current !== generacion) return

      if (error instanceof ErrorApi) {
        // El servidor SÍ respondió — es una respuesta real, no una inferencia local.
        if (error.codigo === 'dispositivo_no_vinculado') {
          // 404 explícito y alcanzable: el servidor confirmó "no vinculado" — se respeta
          // siempre, aunque haya una credencial local (sería el caso de un dispositivo
          // revocado del lado del servidor; la fuente de verdad es la base, nunca el archivo
          // local).
          setEstado({ fase: 'sin-vincular' })
        } else {
          setEstado({ fase: 'error', mensaje: error.message })
        }
        return
      }

      // El fetch ni siquiera obtuvo una respuesta del servidor (red caída, DNS, CORS) — al
      // servidor no se le pudo ni preguntar. Solo en ESTE caso (nunca cuando hubo una respuesta,
      // aunque sea un error) se consulta el archivo local: la base sigue siendo la única
      // autoridad, el archivo es un respaldo para cuando no se la pudo ni consultar.
      const credencialLocal = await leerCredencialDeDispositivo()
      if (generacionRef.current !== generacion) return
      setEstado(credencialLocal ? { fase: 'sin-red-pero-vinculado' } : { fase: 'error', mensaje: MENSAJE_GENERICO })
    }
  }, [])

  useEffect(() => {
    void cargarDispositivo()
  }, [cargarDispositivo])

  // Sesión revocada del lado del servidor mientras se estaba en `con-sesion`: se vuelve a
  // resolver todo desde `GET /dispositivos/actual`, nunca se asume "solo se cerró la sesión" (el
  // dispositivo también pudo quedar revocado en el medio).
  useEffect(() => {
    return alPerderLaSesion(() => {
      if (estadoRef.current.fase === 'con-sesion') void cargarDispositivo()
    })
  }, [cargarDispositivo])

  return (
    <MemoryRouter initialEntries={['/vender']}>
      {estado.fase === 'cargando' && <Cargando texto="Iniciando…" />}

      {estado.fase === 'error' && (
        <div className="d-flex align-items-center justify-content-center min-vh-100 p-3">
          <div role="alert" className="alert alert-danger rounded-0 text-center w-100" style={{ maxWidth: 480 }}>
            <p>{estado.mensaje}</p>
            <button type="button" className="btn btn-outline-dark rounded-0" onClick={() => void cargarDispositivo()}>
              Reintentar
            </button>
          </div>
        </div>
      )}

      {estado.fase === 'sin-red-pero-vinculado' && (
        <div className="d-flex align-items-center justify-content-center min-vh-100 p-3">
          <div role="alert" className="alert alert-warning rounded-0 text-center w-100" style={{ maxWidth: 480 }}>
            <p>No se pudo conectar con el servidor, pero este equipo ya está vinculado. Revisá la conexión y reintentá.</p>
            <button type="button" className="btn btn-outline-dark rounded-0" onClick={() => void cargarDispositivo()}>
              Reintentar
            </button>
          </div>
        </div>
      )}

      {estado.fase === 'sin-vincular' && (
        <PantallaDeVinculacion alVinculado={(dispositivo) => setEstado({ fase: 'vinculado-sin-sesion', dispositivo })} />
      )}

      {estado.fase === 'vinculado-sin-sesion' && (
        <LoginDeDispositivo
          dispositivo={estado.dispositivo}
          onSesion={(usuario, puntoVenta) => {
            if (estado.fase !== 'vinculado-sin-sesion') return
            setEstado({ fase: 'con-sesion', dispositivo: estado.dispositivo, usuario, puntoVenta })
          }}
          onDispositivoInvalido={() => setEstado({ fase: 'sin-vincular' })}
        />
      )}

      {estado.fase === 'con-sesion' && (
        <ShellPos
          dispositivo={estado.dispositivo}
          usuario={estado.usuario}
          puntoVenta={estado.puntoVenta}
          alCerrarSesion={() => {
            if (estado.fase !== 'con-sesion') return
            setEstado({ fase: 'vinculado-sin-sesion', dispositivo: estado.dispositivo })
          }}
        />
      )}
    </MemoryRouter>
  )
}
