import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { useLocation, useNavigate } from 'react-router'
import { useAuth } from '../auth/useAuth'
import { ErrorApi } from '../api/cliente'
import { Cargando } from '../componentes/Cargando'

type EstadoDeRuta = { desde?: { pathname: string; search: string } }

export function Login() {
  const { usuario, cargando, iniciarSesion } = useAuth()
  const navegar = useNavigate()
  const ubicacion = useLocation()

  const [nombre, setNombre] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [enviando, setEnviando] = useState(false)

  const estado = ubicacion.state as EstadoDeRuta | null
  const destino = estado?.desde
    ? `${estado.desde.pathname}${estado.desde.search ?? ''}`
    : '/'

  // Si ya hay sesión activa, /login no tiene sentido: vamos al destino.
  useEffect(() => {
    if (!cargando && usuario) {
      navegar(destino, { replace: true })
    }
  }, [cargando, usuario, destino, navegar])

  async function enviar(evento: FormEvent) {
    evento.preventDefault()
    setError('')
    setEnviando(true)

    try {
      await iniciarSesion(nombre, password)
      navegar(destino, { replace: true })
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo iniciar sesión.')
      setPassword('')
    } finally {
      setEnviando(false)
    }
  }

  if (cargando) {
    return <Cargando />
  }

  return (
    <div className="login d-flex align-items-center justify-content-center min-vh-100">
      <div className="form-signin rounded-0">
        <h1 className="text-center ways-brand">Ways</h1>
        <hr />

        <form onSubmit={enviar} autoComplete="off" noValidate>
          <p className="text-muted text-center">Ingresá tu usuario y contraseña</p>

          <input
            type="text"
            name="usuario"
            className="form-control mb-3 rounded-0"
            placeholder="Usuario"
            value={nombre}
            onChange={(e) => setNombre(e.target.value)}
            autoFocus
            required
          />

          <input
            type="password"
            name="password"
            className="form-control mb-3 rounded-0"
            placeholder="Contraseña"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />

          {error && <div className="text-danger text-center mb-3">{error}</div>}

          <button
            type="submit"
            className="btn btn-lg btn-success form-control rounded-0"
            disabled={enviando}
          >
            {enviando ? 'Ingresando…' : 'Continuar'}
          </button>
        </form>
      </div>
    </div>
  )
}
