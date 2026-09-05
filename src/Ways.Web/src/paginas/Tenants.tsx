import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import type { EstadoTenant, TenantListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'

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
  const [ocupado, setOcupado] = useState<number | null>(null)

  /**
   * Contrato de invalidación (`react-async-state` reglas 2-4): TODA operación que supersede lo
   * que hay en pantalla incrementa la generación ANTES de tocar el estado, y toda aplicación de
   * estado posterior a un `await` — incluido el `finally` que apaga las banderas y el rethrow del
   * refresco — la vuelve a chequear. Mientras hay una escritura en vuelo (`ocupado`), la pantalla
   * bloquea todas las acciones que podrían supersederla (regla 9), así que la generación queda
   * para el desfasaje de LECTURAS.
   */
  const generacion = useRef(0)

  const cargar = useCallback(async (token: number, propagar = false) => {
    setCargando(true)
    try {
      const filas = await clienteDeOrganizacion.listarTenants()
      if (generacion.current !== token) return
      setItems(filas)
      setError('')
    } catch (e) {
      if (generacion.current !== token) return
      if (propagar) throw e
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los tenants.')
    } finally {
      if (generacion.current === token) setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar(++generacion.current)
  }, [cargar])

  /** El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya
   * commiteó nunca se reporta como fallida (`react-async-state` regla 6). */
  async function refrescarTrasEscribir(token: number, mensajeOk: string) {
    if (generacion.current !== token) return

    setAviso(mensajeOk)
    try {
      await cargar(token, true)
    } catch {
      if (generacion.current === token) setAviso(`${mensajeOk} ${AVISO_REFRESCO_FALLIDO}`)
    } finally {
      if (generacion.current === token) setOcupado(null)
    }
  }

  async function guardarNombre() {
    if (!edicion || ocupado !== null) return

    const { id, nombre } = edicion
    const token = ++generacion.current
    setOcupado(id)
    setError('')
    setAviso('')
    try {
      await clienteDeOrganizacion.editarTenant(id, { nombre })
    } catch (e) {
      if (generacion.current !== token) return
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
      setOcupado(null)

      return
    }

    if (generacion.current !== token) return
    setEdicion(null)
    await refrescarTrasEscribir(token, `Se actualizó el tenant "${nombre}".`)
  }

  async function cambiarEstado(tenant: TenantListado, accion: 'suspenderTenant' | 'reactivarTenant') {
    if (ocupado !== null) return

    const verbo = accion === 'suspenderTenant' ? 'suspender' : 'reactivar'
    if (!confirm(`¿${verbo === 'suspender' ? 'Suspender' : 'Reactivar'} el tenant "${tenant.nombre}"?`)) return

    const token = ++generacion.current
    setOcupado(tenant.id)
    setError('')
    setAviso('')
    try {
      await clienteDeOrganizacion[accion](tenant.id)
    } catch (e) {
      if (generacion.current !== token) return
      setError(e instanceof ErrorApi ? e.message : 'No se pudo completar la acción.')
      setOcupado(null)

      return
    }

    await refrescarTrasEscribir(
      token,
      `Tenant "${tenant.nombre}" ${verbo === 'suspender' ? 'suspendido' : 'reactivado'}.`,
    )
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
                disabled={ocupado !== null}
                required
              />
            </div>
            <div className="col-12 d-flex gap-2">
              <button type="submit" className="btn btn-success rounded-0" disabled={ocupado !== null}>
                {ocupado !== null ? 'Guardando…' : 'Guardar'}
              </button>
              <button
                type="button"
                className="btn btn-outline-secondary rounded-0"
                onClick={() => setEdicion(null)}
                disabled={ocupado !== null}
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
                  <th className="text-end">Empresas</th>
                  <th className="text-end">Puntos de venta</th>
                  <th className="text-end">Usuarios</th>
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
                    <td className="text-end">{t.cantidadEmpresas}</td>
                    <td className="text-end">{t.cantidadPuntosVenta}</td>
                    <td className="text-end">{t.cantidadUsuarios}</td>
                    <td>{new Date(t.createdAt).toLocaleDateString('es-AR')}</td>
                    <td className="text-end text-nowrap">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-primary rounded-0 me-1"
                        onClick={() => setEdicion({ id: t.id, nombre: t.nombre })}
                        disabled={ocupado !== null}
                      >
                        Editar
                      </button>
                      {t.estado === 'Activo' && (
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-warning rounded-0"
                          onClick={() => cambiarEstado(t, 'suspenderTenant')}
                          disabled={ocupado !== null}
                        >
                          Suspender
                        </button>
                      )}
                      {t.estado === 'Suspendido' && (
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-success rounded-0"
                          onClick={() => cambiarEstado(t, 'reactivarTenant')}
                          disabled={ocupado !== null}
                        >
                          Reactivar
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
                {items.length === 0 && (
                  <tr>
                    <td colSpan={8} className="text-center text-muted py-4">
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
