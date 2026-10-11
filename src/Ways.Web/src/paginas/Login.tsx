import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useLocation, useNavigate } from 'react-router'
import { useAuth } from '../auth/useAuth'
import { credencialDeLogin, mensajeDeFalloDeLogin } from '../auth/credencialDeLogin'
import { clienteDePublico } from '../api/publico'
import { puedeOperarPos } from '../api/tipos'
import type { SitioPublico } from '../api/tipos'
import { Cargando } from '../componentes/Cargando'
import { BotonDeTema } from '../tema/BotonDeTema'

type EstadoDeRuta = { desde?: { pathname: string; search: string } }

/** Lo que se sabe del sitio por el que se entró. Mientras `cargando` el formulario se rinde
 * inerte; si la lectura falla (`fallido`) se ofrece el ingreso por correo, que vale bajo cualquier
 * dominio. */
type EstadoDelSitio = { fase: 'cargando' } | { fase: 'listo'; sitio: SitioPublico } | { fase: 'fallido' }

export function Login() {
  const { usuario, cargando, iniciarSesion } = useAuth()
  const navegar = useNavigate()
  const ubicacion = useLocation()

  const [identificador, setIdentificador] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [enviando, setEnviando] = useState(false)
  const [estadoDelSitio, setEstadoDelSitio] = useState<EstadoDelSitio>({ fase: 'cargando' })
  const identificadorRef = useRef<HTMLInputElement>(null)
  /** Generación de la lectura del sitio: la única superseder es una lectura nueva (el montaje se
   * repite bajo StrictMode), y gana la última INICIADA, no la última resuelta. */
  const generacionDelSitio = useRef(0)
  /** Espejo SÍNCRONO de `enviando`: dos envíos del mismo tick leen el mismo render. */
  const enviandoRef = useRef(false)

  const sitio = estadoDelSitio.fase === 'listo' ? estadoDelSitio.sitio : null
  const admiteUsuario = sitio?.admiteUsuario ?? false
  const formularioListo = estadoDelSitio.fase !== 'cargando'

  const estado = ubicacion.state as EstadoDeRuta | null
  // Paridad con el legacy (A1): quien opera el POS entra directo a vender.
  const destino = estado?.desde
    ? `${estado.desde.pathname}${estado.desde.search ?? ''}`
    : usuario && puedeOperarPos(usuario.rolId)
      ? '/pos'
      : '/'

  // Con sesión activa (previa o recién iniciada) /login no tiene sentido: esta es la única
  // navegación de salida, y recién acá el rol del usuario ya se conoce.
  useEffect(() => {
    if (!cargando && usuario) {
      navegar(destino, { replace: true })
    }
  }, [cargando, usuario, destino, navegar])

  useEffect(() => {
    const token = ++generacionDelSitio.current

    clienteDePublico
      .obtenerSitio()
      .then((leido) => {
        if (generacionDelSitio.current === token) setEstadoDelSitio({ fase: 'listo', sitio: leido })
      })
      .catch(() => {
        if (generacionDelSitio.current === token) setEstadoDelSitio({ fase: 'fallido' })
      })
  }, [])

  // El `autoFocus` no alcanza: el campo nace inerte y recién se habilita cuando llega el sitio.
  useEffect(() => {
    if (!cargando && formularioListo) identificadorRef.current?.focus()
  }, [cargando, formularioListo])

  async function enviar(evento: FormEvent) {
    evento.preventDefault()
    if (enviandoRef.current) return

    enviandoRef.current = true
    setError('')
    setEnviando(true)

    try {
      await iniciarSesion(credencialDeLogin(identificador, admiteUsuario), password)
    } catch (e) {
      setError(mensajeDeFalloDeLogin(e))
      setPassword('')
    } finally {
      enviandoRef.current = false
      setEnviando(false)
    }
  }

  if (cargando) {
    return <Cargando />
  }

  return (
    <div className="login position-relative d-flex align-items-center justify-content-center min-vh-100">
      <div className="position-absolute top-0 end-0 m-3">
        <BotonDeTema />
      </div>
      <div className="form-signin">
        <h1 className="text-center ways-brand">Ways</h1>
        {sitio?.nombreComercio && <h2 className="h5 text-center">{sitio.nombreComercio}</h2>}
        <hr />

        <form onSubmit={enviar} autoComplete="off" noValidate>
          <p className="text-muted text-center">
            {admiteUsuario ? 'Ingresá tu usuario o correo y tu contraseña' : 'Ingresá tu correo y contraseña'}
          </p>
          {estadoDelSitio.fase === 'fallido' && (
            <p className="text-muted small text-center">
              No se pudo identificar el sitio: por ahora se ingresa solo con el correo.
            </p>
          )}

          <input
            ref={identificadorRef}
            type={admiteUsuario ? 'text' : 'email'}
            name={admiteUsuario ? 'usuario' : 'mail'}
            className="form-control mb-3"
            placeholder={admiteUsuario ? 'Usuario o mail' : 'Correo electrónico'}
            value={identificador}
            onChange={(e) => setIdentificador(e.target.value)}
            disabled={!formularioListo}
            autoFocus
            required
          />

          <input
            type="password"
            name="password"
            className="form-control mb-3"
            placeholder="Contraseña"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            disabled={!formularioListo}
            required
          />

          {error && <div className="text-danger text-center mb-3">{error}</div>}

          <button
            type="submit"
            className="btn btn-lg btn-success form-control"
            disabled={!formularioListo || enviando}
          >
            {enviando ? 'Ingresando…' : 'Continuar'}
          </button>
        </form>

        <p className="text-center mt-3 mb-0">
          <Link to="/descargar" className="text-muted small">
            Descargar la app de escritorio
          </Link>
        </p>
      </div>
    </div>
  )
}
