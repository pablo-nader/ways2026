import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { ErrorApi } from '../api/cliente'
import { arrastreDeTenant, copiaDeFalloDeBaja } from '../api/bajas'
import { clienteDeOrganizacion } from '../api/organizacion'
import type { EstadoTenant, TenantListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const AVISO_REFRESCO_FALLIDO_BAJA =
  'Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.'

/**
 * ABM (parcial) de tenants — plataforma-only. El alta completa (tenant + empresa + punto de
 * venta + plantilla + admin) vive en <c>NuevoTenant.tsx</c> (aprovisionamiento, ADR-16): acá
 * se lista, se edita el nombre, se suspende/reactiva y —desde la etapa 20— se da de baja
 * lógicamente, con su empresa, sus puntos de venta y sus usuarios en la misma cascada.
 */
export function Tenants() {
  const [items, setItems] = useState<TenantListado[]>([])
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [edicion, setEdicion] = useState<{ id: number; nombre: string } | null>(null)
  const [ocupado, setOcupado] = useState<number | null>(null)
  /**
   * Baja pendiente de confirmación, con el token de generación capturado al ABRIR la puerta: la
   * ventana en la que el operador decide es asíncrona, así que el token de la escritura se toma
   * antes de que la puerta se abra y no cuando confirma. Todo lo que supersede esa puerta
   * —cancelar, abrirla sobre otra fila, cualquier otra escritura— incrementa la generación, deja
   * este token viejo y hace que el DELETE que salga de una puerta superseded no aplique nada.
   */
  const [baja, setBaja] = useState<{ fila: TenantListado; token: number } | null>(null)

  /**
   * Contrato de invalidación (`react-async-state` reglas 2-4): toda operación que puede pisar una
   * LECTURA en vuelo —el montaje, cada escritura y la apertura/cierre de la puerta de baja—
   * incrementa la generación ANTES de tocar el estado, y toda aplicación de estado posterior a un
   * `await` la vuelve a chequear.
   *
   * Abrir el formulario de edición y cancelarlo NO la incrementan, a diferencia de lo que pide la
   * regla 3 en el caso general: formulario y tabla son porciones de estado INDEPENDIENTES, así que
   * ninguna lectura en vuelo queda superseded por abrirlo o cerrarlo. Incrementarla ahí
   * descartaría esa carga y, como el `finally` de `cargar` está gateado por generación, dejaría la
   * pantalla clavada en "Cargando…" para siempre. La puerta de baja SÍ la incrementa porque su
   * token es el de la escritura que va a salir de ella, y eso no puede clavar la pantalla: `cargar`
   * apaga la tabla de forma síncrona, así que mientras hay una lectura en vuelo no hay ningún
   * botón que apretar.
   *
   * Mientras hay una escritura en vuelo (`ocupado`) la pantalla bloquea todas las acciones que
   * podrían supersederla (regla 9). Aun así, cada escritura apaga `ocupado` en un `finally`
   * UNGATED que cubre todas sus salidas, incluidas las que se van por generación vieja: un `return`
   * de generación adentro de la escritura dejaba la bandera prendida y la pantalla congelada.
   */
  const generacion = useRef(0)
  /**
   * Espejo SÍNCRONO de `ocupado`, y la ÚNICA guarda de re-entrancia válida. El estado no sirve:
   * dos clicks del MISMO tick leen el mismo render y los dos ven `ocupado` en `null`, así que
   * `if (ocupado !== null) return` los deja pasar a los dos y salen dos escrituras (regla 9 —
   * el atributo `disabled` tampoco alcanza, porque solo existe después del re-render). El ref se
   * escribe antes del primer `await` y se apaga en el mismo `finally` ungated.
   */
  const ocupadoRef = useRef(false)

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
   * commiteó nunca se reporta como fallida (`react-async-state` regla 6). NO apaga `ocupado`: de
   * eso se ocupa el `finally` ungated de cada escritura, que es el único punto de apagado y el
   * que cubre también las salidas por generación vieja. */
  async function refrescarTrasEscribir(token: number, mensajeOk: string, avisoDeFallo: string) {
    if (generacion.current !== token) return

    setAviso(mensajeOk)
    try {
      await cargar(token, true)
    } catch {
      if (generacion.current === token) setAviso(`${mensajeOk} ${avisoDeFallo}`)
    }
  }

  async function guardarNombre() {
    if (!edicion || ocupadoRef.current) return

    const { id, nombre } = edicion
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(id)
    setError('')
    setAviso('')
    try {
      try {
        await clienteDeOrganizacion.editarTenant(id, { nombre })
      } catch (e) {
        if (generacion.current === token) setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')

        return
      }

      if (generacion.current !== token) return

      setEdicion(null)
      await refrescarTrasEscribir(token, `Se actualizó el tenant "${nombre}".`, AVISO_REFRESCO_FALLIDO)
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  async function cambiarEstado(tenant: TenantListado, accion: 'suspenderTenant' | 'reactivarTenant') {
    if (ocupadoRef.current) return

    const verbo = accion === 'suspenderTenant' ? 'suspender' : 'reactivar'
    if (!confirm(`¿${verbo === 'suspender' ? 'Suspender' : 'Reactivar'} el tenant "${tenant.nombre}"?`)) return

    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(tenant.id)
    setError('')
    setAviso('')
    try {
      try {
        await clienteDeOrganizacion[accion](tenant.id)
      } catch (e) {
        if (generacion.current === token) {
          setError(e instanceof ErrorApi ? e.message : 'No se pudo completar la acción.')
        }

        return
      }

      await refrescarTrasEscribir(
        token,
        `Tenant "${tenant.nombre}" ${verbo === 'suspender' ? 'suspendido' : 'reactivado'}.`,
        AVISO_REFRESCO_FALLIDO,
      )
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  /** Abre la puerta de confirmación. Re-chequea `ocupado` además del `disabled` del botón: un
   * doble click en el mismo tick le gana al re-render (`react-async-state` regla 9). */
  function pedirBaja(tenant: TenantListado) {
    if (ocupadoRef.current) return

    setBaja({ fila: tenant, token: ++generacion.current })
    setError('')
    setAviso('')
  }

  function cancelarBaja() {
    if (ocupadoRef.current) return

    ++generacion.current
    setBaja(null)
  }

  async function confirmarBaja() {
    if (!baja || ocupadoRef.current) return

    const { fila, token } = baja
    ocupadoRef.current = true
    setOcupado(fila.id)
    setError('')
    setAviso('')
    try {
      try {
        await clienteDeOrganizacion.eliminarTenant(fila.id)
      } catch (e) {
        // La puerta queda ABIERTA con el motivo al lado: el operador ve por qué no se pudo sin
        // perder de vista qué estaba por dar de baja. La copia la elige el `codigo`, nunca el
        // `mensaje` — que igual se rinde, porque es lo único que nombra qué bloquea.
        if (generacion.current === token) setError(copiaDeFalloDeBaja(e, 'el tenant'))

        return
      }

      if (generacion.current !== token) return

      setBaja(null)
      await refrescarTrasEscribir(
        token,
        `Se dio de baja el tenant "${fila.nombre}".`,
        AVISO_REFRESCO_FALLIDO_BAJA,
      )
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
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

        {baja && (
          <ConfirmacionDeBaja
            titulo={`el tenant "${baja.fila.nombre}"`}
            arrastra={arrastreDeTenant(baja.fila)}
            ocupado={ocupado !== null}
            onConfirmar={confirmarBaja}
            onCancelar={cancelarBaja}
          />
        )}

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
                          className="btn btn-sm btn-outline-warning rounded-0 me-1"
                          onClick={() => cambiarEstado(t, 'suspenderTenant')}
                          disabled={ocupado !== null}
                        >
                          Suspender
                        </button>
                      )}
                      {t.estado === 'Suspendido' && (
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-success rounded-0 me-1"
                          onClick={() => cambiarEstado(t, 'reactivarTenant')}
                          disabled={ocupado !== null}
                        >
                          Reactivar
                        </button>
                      )}
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger rounded-0"
                        onClick={() => pedirBaja(t)}
                        disabled={ocupado !== null}
                      >
                        Baja
                      </button>
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
