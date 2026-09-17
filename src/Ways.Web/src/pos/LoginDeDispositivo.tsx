import { useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { clienteDeDispositivos } from '../api/dispositivos'
import type { DispositivoActual } from '../api/dispositivos'
import { ErrorApi } from '../api/cliente'
import type { PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'
import { resolverPuntoVentaDelDispositivo } from './puntoVentaDelDispositivo'

type Props = {
  dispositivo: DispositivoActual
  onSesion: (usuario: UsuarioAutenticado, puntoVenta: PuntoVentaListado) => void
  /** El dispositivo se desvinculó/revocó entre que se cargó esta pantalla y este intento
   * (`dispositivo_no_vinculado`) — vuelve a la pantalla de vinculación en vez de mostrar un
   * error de login que no tiene arreglo tipeando de nuevo la contraseña. */
  onDispositivoInvalido: () => void
}

/**
 * Login del cajero contra el dispositivo ya vinculado (stage-desktop-pos) — `POST
 * /auth/login-dispositivo`. Login + resolución del punto de venta fijo son UNA sola operación
 * async (react-async-state regla 2/3/9): si la resolución del PV falla después de un login
 * exitoso, no hay una sesión "a medias" que mostrar, se reporta como el mismo error.
 */
export function LoginDeDispositivo({ dispositivo, onSesion, onDispositivoInvalido }: Props) {
  const [usuario, setUsuario] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [enviando, setEnviando] = useState(false)
  const enviandoRef = useRef(false)

  async function enviar(evento: FormEvent) {
    evento.preventDefault()
    if (enviandoRef.current) return
    enviandoRef.current = true
    setEnviando(true)
    setError('')

    try {
      const usuarioAutenticado = await clienteDeDispositivos.iniciarSesion({ usuario, password })
      const puntoVenta = await resolverPuntoVentaDelDispositivo(dispositivo)

      if (!puntoVenta) {
        setError('El punto de venta de este dispositivo ya no existe. Contactá a un administrador.')
        return
      }

      onSesion(usuarioAutenticado, puntoVenta)
    } catch (e) {
      if (e instanceof ErrorApi && e.codigo === 'dispositivo_no_vinculado') {
        onDispositivoInvalido()
        return
      }
      setError(e instanceof ErrorApi ? e.message : 'No se pudo iniciar sesión.')
      setPassword('')
    } finally {
      enviandoRef.current = false
      setEnviando(false)
    }
  }

  return (
    <div className="d-flex align-items-center justify-content-center min-vh-100 p-3">
      <div className="card rounded-0 w-100" style={{ maxWidth: 480 }}>
        <div className="card-body">
          <h1 className="h3 text-center ways-brand mb-1">Ways</h1>
          <p className="text-muted text-center mb-1">{dispositivo.empresa.nombre}</p>
          <p className="text-muted text-center mb-4">
            PV {dispositivo.puntoVenta.numero} — {dispositivo.puntoVenta.nombre} · {dispositivo.nombre}
          </p>

          <form onSubmit={enviar} autoComplete="off" noValidate>
            <input
              type="text"
              className="form-control mb-3 rounded-0"
              placeholder="Usuario"
              value={usuario}
              disabled={enviando}
              onChange={(e) => setUsuario(e.target.value)}
              autoFocus
              required
            />
            <input
              type="password"
              className="form-control mb-3 rounded-0"
              placeholder="Contraseña"
              value={password}
              disabled={enviando}
              onChange={(e) => setPassword(e.target.value)}
              required
            />

            {error && <div className="text-danger text-center mb-3">{error}</div>}

            <button type="submit" className="btn btn-lg btn-success form-control rounded-0" disabled={enviando}>
              {enviando ? 'Ingresando…' : 'Ingresar'}
            </button>
          </form>
        </div>
      </div>
    </div>
  )
}
