import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ErrorApi } from '../api/cliente'
import { etiquetaDeTenant, filtrarPorTenant, opcionesDeTenant, SIN_FILTRO } from '../api/organizacion'
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

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'

/**
 * ABM de usuarios. La columna "Tenant" rinde el nombre del tenant de la cuenta o el literal
 * "Plataforma" cuando `idTenant` es null — esa copia la pone la web, nunca el servidor
 * (design D14), y el discriminador es `idTenant`, no el nombre: un `nombreTenant` nulo con
 * `idTenant` presente es un huérfano (tenant dado de baja), no personal de plataforma
 * (Reconciliación 9). El filtro por tenant deriva sus opciones de las filas ya cargadas, así
 * que un admin de tenant nunca puede enumerar el nombre de otro tenant (spec S5).
 */
export function Usuarios() {
  const { usuario: actual } = useAuth()

  const [pagina, setPagina] = useState<PaginaDe<UsuarioListado> | null>(null)
  const [roles, setRoles] = useState<RolListado[]>([])
  const [busqueda, setBusqueda] = useState('')
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [ocupado, setOcupado] = useState(false)
  const [filtroTenant, setFiltroTenant] = useState(SIN_FILTRO)

  /** Contrato de invalidación: ver `Tenants.tsx` — mismo patrón en las cuatro pantallas raíz. */
  const generacion = useRef(0)

  const cargar = useCallback(async (token: number, termino: string, propagar = false) => {
    setCargando(true)
    try {
      const parametros = termino ? `?busqueda=${encodeURIComponent(termino)}` : ''
      const respuesta = await api.get<PaginaDe<UsuarioListado>>(`/usuarios${parametros}`)
      if (generacion.current !== token) return
      setPagina(respuesta)
      setError('')
    } catch (e) {
      if (generacion.current !== token) return
      if (propagar) throw e
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los usuarios.')
    } finally {
      if (generacion.current === token) setCargando(false)
    }
  }, [])

  function buscar(termino: string) {
    if (ocupado) return
    void cargar(++generacion.current, termino)
  }

  useEffect(() => {
    void cargar(++generacion.current, '')
    api.get<RolListado[]>('/roles').then(setRoles).catch(() => setRoles([]))
  }, [cargar])

  async function guardar() {
    if (!formulario || ocupado) return

    const datos = formulario
    const token = ++generacion.current
    setOcupado(true)
    setError('')
    setAviso('')

    let mensajeOk: string
    try {
      if (datos.id === null) {
        const alta: CrearUsuario = {
          usuario: datos.usuario,
          mail: datos.mail,
          rolId: datos.rolId,
          password: datos.password,
          estado: datos.estado,
        }
        await api.post('/usuarios', alta)
        mensajeOk = `Usuario "${datos.usuario}" creado.`
      } else {
        const edicion: ActualizarUsuario = {
          usuario: datos.usuario,
          mail: datos.mail,
          rolId: datos.rolId,
          estado: datos.estado,
        }
        await api.put(`/usuarios/${datos.id}`, edicion)

        if (datos.password) {
          await api.post(`/usuarios/${datos.id}/password`, { passwordNueva: datos.password })
        }
        mensajeOk = `Usuario "${datos.usuario}" actualizado.`
      }
    } catch (e) {
      if (generacion.current !== token) return
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
      setOcupado(false)

      return
    }

    if (generacion.current !== token) return
    setFormulario(null)
    await refrescarTrasEscribir(token, mensajeOk)
  }

  /** El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya
   * commiteó nunca se reporta como fallida (`react-async-state` regla 6). */
  async function refrescarTrasEscribir(token: number, mensajeOk: string) {
    if (generacion.current !== token) return

    setAviso(mensajeOk)
    try {
      await cargar(token, busqueda, true)
    } catch {
      if (generacion.current === token) setAviso(`${mensajeOk} ${AVISO_REFRESCO_FALLIDO}`)
    } finally {
      if (generacion.current === token) setOcupado(false)
    }
  }

  async function accion(construirPromesa: () => Promise<unknown>, mensajeOk: string) {
    if (ocupado) return

    const token = ++generacion.current
    setOcupado(true)
    setError('')
    setAviso('')
    try {
      await construirPromesa()
    } catch (e) {
      if (generacion.current !== token) return
      setError(e instanceof ErrorApi ? e.message : 'No se pudo completar la acción.')
      setOcupado(false)

      return
    }

    await refrescarTrasEscribir(token, mensajeOk)
  }

  function eliminar(u: UsuarioListado) {
    if (ocupado) return
    if (!confirm(`¿Dar de baja al usuario "${u.usuario}"?`)) return
    void accion(() => api.delete(`/usuarios/${u.id}`), `Usuario "${u.usuario}" dado de baja.`)
  }

  // El backend valida igual; esto solo evita mostrar botones que van a fallar.
  const puedeEditar = (u: UsuarioListado) =>
    u.rolId !== ROL.Root || actual?.rolId === ROL.Root

  // Derivación pura sobre la página YA CARGADA: sin fetch nuevo y sin parámetro de consulta.
  const filas = pagina?.items ?? []
  const opcionesTenant = opcionesDeTenant(filas)
  const tenantVigente = opcionesTenant.some((o) => o.valor === filtroTenant) ? filtroTenant : SIN_FILTRO
  const visibles = filtrarPorTenant(filas, tenantVigente)

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <input
        type="search"
        className="form-control form-control-sm rounded-0"
        placeholder="Buscar usuario o mail…"
        value={busqueda}
        onChange={(e) => setBusqueda(e.target.value)}
        onKeyDown={(e) => e.key === 'Enter' && buscar(busqueda)}
        disabled={ocupado}
      />
      <button
        type="button"
        className="btn btn-sm btn-outline-light rounded-0"
        onClick={() => buscar(busqueda)}
        disabled={ocupado}
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
        disabled={ocupado}
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
            key={formulario.id ?? 'nuevo'}
            valor={formulario}
            roles={roles}
            guardando={ocupado}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={() => setFormulario(null)}
          />
        )}

        {cargando ? (
          <Cargando />
        ) : (
          <>
            <div className="row g-2 mb-3">
              <div className="col-md-4">
                <label className="form-label" htmlFor="u-filtro-tenant">
                  Tenant
                </label>
                <select
                  id="u-filtro-tenant"
                  className="form-select rounded-0"
                  value={tenantVigente}
                  onChange={(e) => setFiltroTenant(e.target.value)}
                >
                  <option value={SIN_FILTRO}>Todos</option>
                  {opcionesTenant.map((o) => (
                    <option key={o.valor} value={o.valor}>
                      {o.etiqueta}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            <div className="table-responsive">
              <table className="table table-striped table-hover table-bordered align-middle">
                <thead>
                  <tr>
                    <th>ID</th>
                    <th>Usuario</th>
                    <th>Mail</th>
                    <th>Tenant</th>
                    <th>Rol</th>
                    <th>Estado</th>
                    <th>Última conexión</th>
                    <th className="text-end">Acciones</th>
                  </tr>
                </thead>
                <tbody>
                  {visibles.map((u) => (
                    <tr key={u.id}>
                      <td>{String(u.id).padStart(4, '0')}</td>
                      <td>{u.usuario}</td>
                      <td>{u.mail}</td>
                      <td>{etiquetaDeTenant(u)}</td>
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
                            disabled={ocupado}
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
                                () => api.post(`/usuarios/${u.id}/desbloquear`),
                                `Usuario "${u.usuario}" desbloqueado.`,
                              )
                            }
                            disabled={ocupado}
                          >
                            Desbloquear
                          </button>
                        )}
                        {puedeEditar(u) && u.rolId !== ROL.Root && u.id !== actual?.id && (
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-danger rounded-0"
                            onClick={() => eliminar(u)}
                            disabled={ocupado}
                          >
                            Baja
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                  {visibles.length === 0 && (
                    <tr>
                      <td colSpan={8} className="text-center text-muted py-4">
                        No hay usuarios que coincidan con la búsqueda.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </>
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
