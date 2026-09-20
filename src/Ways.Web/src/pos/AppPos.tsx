import { useCallback, useEffect, useRef, useState } from 'react'
import { MemoryRouter } from 'react-router'
import { clienteDeDispositivos } from '../api/dispositivos'
import type { DispositivoActual } from '../api/dispositivos'
import { alPerderLaSesion, api, ErrorApi } from '../api/cliente'
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
 * Punto de entrada del POS de escritorio (`pos.html`, servido remotamente por la API — Tauri lo
 * carga desde el origen del servidor, no desde un archivo local) — stage-desktop-pos. Máquina de
 * estados propia (no reusa `AuthProvider`/`RutaProtegida`/`PuertaDePuntoVenta` de la app completa:
 * esas asumen login por mail/password y elección manual de PV, acá el dispositivo fija ambas
 * cosas):
 *
 * 1. `GET /dispositivos/actual` (anónimo) → 404 `dispositivo_no_vinculado` = primera vez o
 *    dispositivo revocado → `PantallaDeVinculacion`.
 * 2. Dispositivo conocido → `resolverSesionDelDispositivo` (`GET /auth/me`): con una cookie de
 *    sesión todavía viva y operable, entra derecho a `con-sesion` sin pedir nada — sin sesión (o
 *    inválida), `LoginDeDispositivo`.
 * 3. Con sesión → `ShellPos` (vender / cerrar caja / Caja Z), con el punto de venta fijo del
 *    dispositivo.
 *
 * Un 401 en CUALQUIER llamada mientras se está en `con-sesion` (sesión revocada del lado del
 * servidor, ej. el dispositivo se desvincula con el cajero todavía adentro) vuelve a correr el
 * paso 1 completo (`alPerderLaSesion`, el mismo observador que usa `AuthContext`) — así se
 * distingue solo "cerró sesión" (paso 2 lo manda a `LoginDeDispositivo`) de "además el dispositivo
 * quedó revocado" (paso 1 lo manda a `PantallaDeVinculacion`) sin duplicar esa lógica.
 *
 * El `MemoryRouter` es una única instancia para las cuatro fases (nunca se remonta al cambiar de
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

      if (error instanceof ErrorApi && error.codigo === 'dispositivo_no_vinculado') {
        // 404 explícito y alcanzable: el servidor confirmó "no vinculado" — se respeta siempre,
        // aunque haya una credencial local (sería el caso de un dispositivo revocado del lado
        // del servidor; la fuente de verdad es la base, nunca el archivo local).
        setEstado({ fase: 'sin-vincular' })
      } else {
        // La llamada no dio una respuesta concluyente (red caída, error inesperado del
        // servidor). judgment-day ronda 1: esta pantalla ya no puede preguntarle a Rust si hay
        // una credencial local guardada — la capacidad remota perdió el permiso de LECTURA del
        // secreto de dispositivo a propósito (ver `lib.rs`, `PERMISOS_REMOTOS`), así que siempre
        // muestra el mensaje genérico. La distinción "sin red pero ya vinculado" vuelve cuando la
        // slice 3 mueva esta pantalla a una página local con permiso de lectura.
        setEstado({ fase: 'error', mensaje: error instanceof ErrorApi ? error.message : MENSAJE_GENERICO })
      }
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
