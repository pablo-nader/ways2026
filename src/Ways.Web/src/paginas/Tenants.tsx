import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import type { EstadoTenant, TenantListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

/**
 * ABM (parcial) de tenants — plataforma-only. El alta completa (tenant + empresa + punto de
 * venta + plantilla + admin) vive en <c>NuevoTenant.tsx</c> (aprovisionamiento, ADR-16): acá
 * solo se lista, se edita el nombre y se suspende/reactiva. No hay baja: `EstadoTenant.Baja`
 * no tiene acción dedicada en esta etapa (ver `ServicioDeOrganizacion`).
 */
export function Tenants() {
  const [items, setItems] = useState<TenantListado[]>([])
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [edicion, setEdicion] = useState<{ id: number; nombre: string } | null>(null)
  const [guardando, setGuardando] = useState(false)

  const cargar = useCallback(async () => {
    setCargando(true)
    setError('')
    try {
      setItems(await clienteDeOrganizacion.listarTenants())
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los tenants.')
    } finally {
      setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar()
  }, [cargar])

  async function guardarNombre() {
    if (!edicion) return

    setGuardando(true)
    setError('')
    setAviso('')
    try {
      await clienteDeOrganizacion.editarTenant(edicion.id, { nombre: edicion.nombre })
      setAviso(`Se actualizó el tenant "${edicion.nombre}".`)
      setEdicion(null)
      await cargar()
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
    } finally {
      setGuardando(false)
    }
  }

  async function cambiarEstado(tenant: TenantListado, accion: 'suspenderTenant' | 'reactivarTenant') {
    const verbo = accion === 'suspenderTenant' ? 'suspender' : 'reactivar'
    if (!confirm(`¿${verbo === 'suspender' ? 'Suspender' : 'Reactivar'} el tenant "${tenant.nombre}"?`)) return

    setError('')
    setAviso('')
    try {
      await clienteDeOrganizacion[accion](tenant.id)
      setAviso(`Tenant "${tenant.nombre}" ${verbo === 'suspender' ? 'suspendido' : 'reactivado'}.`)
      await cargar()
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo completar la acción.')
    }
  }

  const herramientas = (
    <nav className="p-2">
      <Link to="/organizacion/nuevo-tenant" className="btn btn-sm btn-success rounded-0 text-nowrap">
        Nuevo tenant
      </Link>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Tenants" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}

        {edicion && (
          <form
            className="row g-3 border p-3 mb-4 bg-white"
            onSubmit={(e) => {
              e.preventDefault()
              guardarNombre()
            }}
          >
            <div className="col-12">
              <strong>Editando tenant {edicion.id}</strong>
            </div>
            <div className="col-md-4">
              <label className="form-label" htmlFor="t-nombre">
                Nombre
              </label>
              <input
                id="t-nombre"
                className="form-control rounded-0"
                maxLength={150}
                value={edicion.nombre}
                onChange={(e) => setEdicion({ ...edicion, nombre: e.target.value })}
                required
              />
            </div>
            <div className="col-12 d-flex gap-2">
              <button type="submit" className="btn btn-success rounded-0" disabled={guardando}>
                {guardando ? 'Guardando…' : 'Guardar'}
              </button>
              <button
                type="button"
                className="btn btn-outline-secondary rounded-0"
                onClick={() => setEdicion(null)}
                disabled={guardando}
              >
                Cancelar
              </button>
            </div>
          </form>
        )}

        {cargando ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th>ID</th>
                  <th>Nombre</th>
                  <th>Estado</th>
                  <th>Creado</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {items.map((t) => (
                  <tr key={t.id}>
                    <td>{String(t.id).padStart(4, '0')}</td>
                    <td>{t.nombre}</td>
                    <td>
                      <EtiquetaEstado estado={t.estado} />
                    </td>
                    <td>{new Date(t.createdAt).toLocaleDateString('es-AR')}</td>
                    <td className="text-end text-nowrap">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-primary rounded-0 me-1"
                        onClick={() => setEdicion({ id: t.id, nombre: t.nombre })}
                      >
                        Editar
                      </button>
                      {t.estado === 'Activo' && (
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-warning rounded-0"
                          onClick={() => cambiarEstado(t, 'suspenderTenant')}
                        >
                          Suspender
                        </button>
                      )}
                      {t.estado === 'Suspendido' && (
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-success rounded-0"
                          onClick={() => cambiarEstado(t, 'reactivarTenant')}
                        >
                          Reactivar
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
                {items.length === 0 && (
                  <tr>
                    <td colSpan={5} className="text-center text-muted py-4">
                      No hay tenants cargados.
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

function EtiquetaEstado({ estado }: { estado: EstadoTenant }) {
  const clase =
    estado === 'Activo' ? 'text-bg-success' : estado === 'Suspendido' ? 'text-bg-warning' : 'text-bg-secondary'

  return <span className={`badge rounded-0 ${clase}`}>{estado}</span>
}
