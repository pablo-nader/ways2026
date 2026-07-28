import { useCallback, useEffect, useState } from 'react'
import { api, ErrorApi } from '../api/cliente'
import { ESTADOS_USUARIO, ROL } from '../api/tipos'
import type {
  ActualizarUsuario,
  CrearUsuario,
  EstadoUsuario,
  PaginaDe,
  RolListado,
  UsuarioListado,
} from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { useAuth } from '../auth/useAuth'

type Formulario = {
  id: number | null
  usuario: string
  mail: string
  rolId: number
  estado: EstadoUsuario
  password: string
}

const FORMULARIO_VACIO: Formulario = {
  id: null,
  usuario: '',
  mail: '',
  rolId: ROL.Vendedor,
  estado: 'Activo',
  password: '',
}

export function Usuarios() {
  const { usuario: actual } = useAuth()

  const [pagina, setPagina] = useState<PaginaDe<UsuarioListado> | null>(null)
  const [roles, setRoles] = useState<RolListado[]>([])
  const [busqueda, setBusqueda] = useState('')
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [guardando, setGuardando] = useState(false)

  const cargar = useCallback(async (termino: string) => {
    setCargando(true)
    setError('')
    try {
      const parametros = termino ? `?busqueda=${encodeURIComponent(termino)}` : ''
      setPagina(await api.get<PaginaDe<UsuarioListado>>(`/usuarios${parametros}`))
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los usuarios.')
    } finally {
      setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar('')
    api.get<RolListado[]>('/roles').then(setRoles).catch(() => setRoles([]))
  }, [cargar])

  async function guardar() {
    if (!formulario) return

    setGuardando(true)
    setError('')
    setAviso('')

    try {
      if (formulario.id === null) {
        const datos: CrearUsuario = {
          usuario: formulario.usuario,
          mail: formulario.mail,
          rolId: formulario.rolId,
          password: formulario.password,
          estado: formulario.estado,
        }
        await api.post('/usuarios', datos)
        setAviso(`Usuario "${formulario.usuario}" creado.`)
      } else {
        const datos: ActualizarUsuario = {
          usuario: formulario.usuario,
          mail: formulario.mail,
          rolId: formulario.rolId,
          estado: formulario.estado,
        }
        await api.put(`/usuarios/${formulario.id}`, datos)

        if (formulario.password) {
          await api.post(`/usuarios/${formulario.id}/password`, {
            passwordNueva: formulario.password,
          })
        }
        setAviso(`Usuario "${formulario.usuario}" actualizado.`)
      }

      setFormulario(null)
      await cargar(busqueda)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
    } finally {
      setGuardando(false)
    }
  }

  async function accion(promesa: Promise<unknown>, mensajeOk: string) {
    setError('')
    setAviso('')
    try {
      await promesa
      setAviso(mensajeOk)
      await cargar(busqueda)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo completar la acción.')
    }
  }

  function eliminar(u: UsuarioListado) {
    if (!confirm(`¿Dar de baja al usuario "${u.usuario}"?`)) return
    void accion(api.delete(`/usuarios/${u.id}`), `Usuario "${u.usuario}" dado de baja.`)
  }

  // El backend valida igual; esto solo evita mostrar botones que van a fallar.
  const puedeEditar = (u: UsuarioListado) =>
    u.rolId !== ROL.Root || actual?.rolId === ROL.Root

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <input
        type="search"
        className="form-control form-control-sm rounded-0"
        placeholder="Buscar usuario o mail…"
        value={busqueda}
        onChange={(e) => setBusqueda(e.target.value)}
        onKeyDown={(e) => e.key === 'Enter' && cargar(busqueda)}
      />
      <button
        type="button"
        className="btn btn-sm btn-outline-light rounded-0"
        onClick={() => cargar(busqueda)}
      >
        Buscar
      </button>
      <button
        type="button"
        className="btn btn-sm btn-success rounded-0 text-nowrap"
        onClick={() => {
          setFormulario({ ...FORMULARIO_VACIO })
          setAviso('')
          setError('')
        }}
      >
        Nuevo
      </button>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Usuarios" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}

        {formulario && (
          <FormularioUsuario
            valor={formulario}
            roles={roles}
            guardando={guardando}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={() => setFormulario(null)}
          />
        )}

        {cargando ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th>ID</th>
                  <th>Usuario</th>
                  <th>Mail</th>
                  <th>Rol</th>
                  <th>Estado</th>
                  <th>Última conexión</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {pagina?.items.map((u) => (
                  <tr key={u.id}>
                    <td>{String(u.id).padStart(4, '0')}</td>
                    <td>{u.usuario}</td>
                    <td>{u.mail}</td>
                    <td>{u.rol}</td>
                    <td>
                      <EtiquetaEstado estado={u.estado} />
                    </td>
                    <td>
                      {u.ultimaConexion
                        ? new Date(u.ultimaConexion).toLocaleString('es-AR')
                        : '—'}
                    </td>
                    <td className="text-end text-nowrap">
                      {puedeEditar(u) && (
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-primary rounded-0 me-1"
                          onClick={() => {
                            setFormulario({
                              id: u.id,
                              usuario: u.usuario,
                              mail: u.mail,
                              rolId: u.rolId,
                              estado: u.estado,
                              password: '',
                            })
                            setAviso('')
                            setError('')
                          }}
                        >
                          Editar
                        </button>
                      )}
                      {u.estado === 'Bloqueado' && (
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-warning rounded-0 me-1"
                          onClick={() =>
                            accion(
                              api.post(`/usuarios/${u.id}/desbloquear`),
                              `Usuario "${u.usuario}" desbloqueado.`,
                            )
                          }
                        >
                          Desbloquear
                        </button>
                      )}
                      {puedeEditar(u) && u.rolId !== ROL.Root && u.id !== actual?.id && (
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-danger rounded-0"
                          onClick={() => eliminar(u)}
                        >
                          Baja
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
                {pagina?.items.length === 0 && (
                  <tr>
                    <td colSpan={7} className="text-center text-muted py-4">
                      No hay usuarios que coincidan con la búsqueda.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </Box>
    </div>
  )
}

function EtiquetaEstado({ estado }: { estado: EstadoUsuario }) {
  const clase =
    estado === 'Activo'
      ? 'text-bg-success'
      : estado === 'Bloqueado'
        ? 'text-bg-danger'
        : 'text-bg-secondary'

  return <span className={`badge rounded-0 ${clase}`}>{estado}</span>
}

function FormularioUsuario({
  valor,
  roles,
  guardando,
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: Formulario
  roles: RolListado[]
  guardando: boolean
  onCambio: (f: Formulario) => void
  onGuardar: () => void
  onCancelar: () => void
}) {
  const esNuevo = valor.id === null

  return (
    <form
      className="row g-3 border p-3 mb-4 bg-white"
      autoComplete="off"
      onSubmit={(e) => {
        e.preventDefault()
        onGuardar()
      }}
    >
      <div className="col-12">
        <strong>{esNuevo ? 'Nuevo usuario' : `Editando usuario ${valor.id}`}</strong>
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="f-usuario">
          Usuario
        </label>
        <input
          id="f-usuario"
          className="form-control rounded-0"
          maxLength={40}
          value={valor.usuario}
          onChange={(e) => onCambio({ ...valor, usuario: e.target.value })}
          required
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="f-mail">
          Mail
        </label>
        <input
          id="f-mail"
          type="email"
          className="form-control rounded-0"
          maxLength={255}
          value={valor.mail}
          onChange={(e) => onCambio({ ...valor, mail: e.target.value })}
          required
        />
      </div>

      <div className="col-md-2">
        <label className="form-label" htmlFor="f-rol">
          Rol
        </label>
        <select
          id="f-rol"
          className="form-select rounded-0"
          value={valor.rolId}
          onChange={(e) => onCambio({ ...valor, rolId: Number(e.target.value) })}
        >
          {roles.map((r) => (
            <option key={r.id} value={r.id}>
              {r.nombre}
            </option>
          ))}
        </select>
      </div>

      <div className="col-md-2">
        <label className="form-label" htmlFor="f-estado">
          Estado
        </label>
        <select
          id="f-estado"
          className="form-select rounded-0"
          value={valor.estado}
          onChange={(e) => onCambio({ ...valor, estado: e.target.value as EstadoUsuario })}
        >
          {ESTADOS_USUARIO.map((e) => (
            <option key={e} value={e}>
              {e}
            </option>
          ))}
        </select>
      </div>

      <div className="col-md-2">
        <label className="form-label" htmlFor="f-password">
          Contraseña
        </label>
        <input
          id="f-password"
          type="password"
          className="form-control rounded-0"
          placeholder={esNuevo ? 'Mínimo 8 caracteres' : 'Dejar vacío para no cambiar'}
          value={valor.password}
          onChange={(e) => onCambio({ ...valor, password: e.target.value })}
          required={esNuevo}
        />
      </div>

      <div className="col-12 d-flex gap-2">
        <button type="submit" className="btn btn-success rounded-0" disabled={guardando}>
          {guardando ? 'Guardando…' : 'Guardar'}
        </button>
        <button
          type="button"
          className="btn btn-outline-secondary rounded-0"
          onClick={onCancelar}
          disabled={guardando}
        >
          Cancelar
        </button>
      </div>
    </form>
  )
}
