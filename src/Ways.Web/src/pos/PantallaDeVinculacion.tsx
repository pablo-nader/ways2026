import { useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { clienteDeDispositivos } from '../api/dispositivos'
import type { DispositivoActual } from '../api/dispositivos'
import { api, ErrorApi } from '../api/cliente'
import { guardarCredencialDeDispositivo } from '../api/entornoTauri'
import { clienteDeOrganizacion } from '../api/organizacion'
import { puedeGestionarCatalogos } from '../api/tipos'
import type { PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

type Props = { alVinculado: (dispositivo: DispositivoActual) => void }

/** El primer dispositivo vinculado no tiene sesión de ningún tipo: primero entra un Admin con
 * mail/password (el login normal de la app), después elige el punto de venta y le pone un nombre
 * al equipo. `elegir-pv` guarda la sesión de admin YA autenticada — de ahí sale `listarPuntosVenta`
 * sin pedir nada más. */
type Paso = { paso: 'login' } | { paso: 'elegir-pv'; puntosVenta: PuntoVentaListado[] }

/**
 * Pantalla de vinculación del POS de escritorio (stage-desktop-pos) — corre una única vez por
 * equipo. Login admin → elegir PV + nombre → `POST /api/dispositivos` → logout del admin (la
 * sesión que sigue es la del cajero, nunca la de quien vinculó) → `LoginDeDispositivo`.
 */
export function PantallaDeVinculacion({ alVinculado }: Props) {
  const [paso, setPaso] = useState<Paso>({ paso: 'login' })
  const [mail, setMail] = useState('')
  const [password, setPassword] = useState('')
  const [idPuntoVenta, setIdPuntoVenta] = useState('')
  const [nombreDispositivo, setNombreDispositivo] = useState('')
  const [error, setError] = useState('')
  const [enviando, setEnviando] = useState(false)
  const enviandoRef = useRef(false)

  async function iniciarSesionAdmin(evento: FormEvent) {
    evento.preventDefault()
    if (enviandoRef.current) return
    enviandoRef.current = true
    setEnviando(true)
    setError('')

    try {
      const usuario = await api.post<UsuarioAutenticado>('/auth/login', { mail, password })

      if (!puedeGestionarCatalogos(usuario.rolId)) {
        setError('Se necesita un usuario administrador para vincular el dispositivo.')
        await api.post('/auth/logout').catch(() => undefined)
        return
      }

      const puntosVenta = await clienteDeOrganizacion.listarPuntosVenta()
      setPaso({ paso: 'elegir-pv', puntosVenta })
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo iniciar sesión.')
      setPassword('')
    } finally {
      enviandoRef.current = false
      setEnviando(false)
    }
  }

  async function vincular(evento: FormEvent) {
    evento.preventDefault()
    if (enviandoRef.current || idPuntoVenta === '' || nombreDispositivo.trim() === '') return
    enviandoRef.current = true
    setEnviando(true)
    setError('')

    try {
      const vinculado = await clienteDeDispositivos.vincular({
        idPuntoVenta: Number(idPuntoVenta),
        nombre: nombreDispositivo.trim(),
      })
      // El secreto viaja en el cuerpo UNA sola vez (dto-contract-honesty) — se lo entrega a Rust
      // para que lo persista en su propio archivo antes de seguir. No hace nada fuera de Tauri.
      await guardarCredencialDeDispositivo(vinculado.secreto)
      // La sesión de admin ya cumplió su propósito: se cierra antes de avisar, así el próximo
      // paso (login del cajero) arranca sin ninguna sesión activa.
      await api.post('/auth/logout').catch(() => undefined)
      alVinculado(vinculado.datos)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo vincular el dispositivo.')
    } finally {
      enviandoRef.current = false
      setEnviando(false)
    }
  }

  if (paso.paso === 'elegir-pv') {
    return (
      <div className="d-flex align-items-center justify-content-center min-vh-100 p-3">
        <div className="card rounded-0 w-100" style={{ maxWidth: 480 }}>
          <div className="card-body">
            <h1 className="h4 text-center mb-4">Vincular este equipo</h1>

            <form onSubmit={vincular} noValidate>
              <div className="mb-3">
                <label className="form-label" htmlFor="vinculacion-pv">
                  Punto de venta
                </label>
                <select
                  id="vinculacion-pv"
                  className="form-select rounded-0"
                  value={idPuntoVenta}
                  disabled={enviando}
                  onChange={(e) => setIdPuntoVenta(e.target.value)}
                  required
                >
                  <option value="">Elegí un punto de venta…</option>
                  {paso.puntosVenta.map((pv) => (
                    <option key={pv.id} value={pv.id}>
                      {pv.nombre}
                    </option>
                  ))}
                </select>
              </div>

              <div className="mb-3">
                <label className="form-label" htmlFor="vinculacion-nombre">
                  Nombre del equipo
                </label>
                <input
                  id="vinculacion-nombre"
                  type="text"
                  className="form-control rounded-0"
                  placeholder="Caja 1"
                  value={nombreDispositivo}
                  disabled={enviando}
                  onChange={(e) => setNombreDispositivo(e.target.value)}
                  required
                />
              </div>

              {error && <div className="text-danger text-center mb-3">{error}</div>}

              <button
                type="submit"
                className="btn btn-lg btn-success form-control rounded-0"
                disabled={enviando || idPuntoVenta === '' || nombreDispositivo.trim() === ''}
              >
                {enviando ? 'Vinculando…' : 'Vincular'}
              </button>
            </form>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="d-flex align-items-center justify-content-center min-vh-100 p-3">
      <div className="card rounded-0 w-100" style={{ maxWidth: 480 }}>
        <div className="card-body">
          <h1 className="h4 text-center mb-1">Vincular este equipo</h1>
          <p className="text-muted text-center mb-4">Ingresá con un usuario administrador para vincularlo a un punto de venta.</p>

          <form onSubmit={iniciarSesionAdmin} autoComplete="off" noValidate>
            <input
              type="email"
              className="form-control mb-3 rounded-0"
              placeholder="Correo electrónico"
              value={mail}
              disabled={enviando}
              onChange={(e) => setMail(e.target.value)}
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
              {enviando ? 'Ingresando…' : 'Continuar'}
            </button>
          </form>
        </div>
      </div>
    </div>
  )
}
