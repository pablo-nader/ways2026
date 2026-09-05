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

type AccionDeEstado = 'suspenderTenant' | 'reactivarTenant'

/** Lo que la puerta de confirmación tiene pendiente. Las tres acciones de esta pantalla que
 * escriben sobre una fila entera pasan por ella; editar el nombre tiene su propio formulario. */
type Confirmacion =
  | { tipo: 'baja'; fila: TenantListado }
  | { tipo: 'estado'; fila: TenantListado; accion: AccionDeEstado }

const COPIA_DE_ESTADO: Readonly<
  Record<AccionDeEstado, { pregunta: string; confirmar: string; enCurso: string; hecho: string }>
> = {
  suspenderTenant: {
    pregunta: 'Suspender',
    confirmar: 'Confirmar suspensión',
    enCurso: 'Suspendiendo…',
    hecho: 'suspendido',
  },
  reactivarTenant: {
    pregunta: 'Reactivar',
    confirmar: 'Confirmar reactivación',
    enCurso: 'Reactivando…',
    hecho: 'reactivado',
  },
}

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
   * Acción pendiente de confirmación. La puerta es MODAL: mientras hay una, `bloqueado` deja
   * inerte todo el resto de la pantalla, así que nada puede supersederla. El token de la escritura
   * NO se toma acá — la ventana en la que el operador decide es humana, y un token acuñado al
   * abrir llega viejo a un DELETE que igual sale: se acuña al CONFIRMAR.
   *
   * Suspender y reactivar comparten la puerta con la baja (`react-async-state` regla 10 dentro de
   * un mismo archivo): estaban con `confirm()` nativo, que no se puede dejar inerte mientras la
   * escritura está en vuelo.
   */
  const [confirmacion, setConfirmacion] = useState<Confirmacion | null>(null)

  /**
   * Contrato de invalidación (`react-async-state` reglas 2-4): toda operación que puede pisar una
   * LECTURA en vuelo —el montaje y cada escritura— acuña una generación nueva ANTES de tocar el
   * estado, y toda aplicación de estado posterior a un `await` la vuelve a chequear.
   *
   * Lo que NO la incrementa, y por qué: abrir o cerrar el formulario de edición, y abrir o cancelar
   * la puerta de confirmación. Ninguna de las cuatro supersede nada —no hay escritura detrás de
   * ellas— y sí podían clavar la pantalla: `cancelar` con una lectura en vuelo la descartaba y,
   * como el `finally` de `cargar` está gateado por generación, "Cargando…" quedaba para siempre.
   *
   * Mientras hay una escritura en vuelo o una puerta abierta (`bloqueado`), la pantalla deja inerte
   * TODA acción que podría supersederlas (regla 9). Aun así, cada escritura apaga `ocupado` en un
   * `finally` UNGATED que cubre todas sus salidas: un `return` temprano adentro de la escritura
   * dejaba la bandera prendida y la pantalla congelada.
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

  async function cambiarEstado(tenant: TenantListado, accion: AccionDeEstado) {
    if (ocupadoRef.current) return

    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(tenant.id)
    setError('')
    setAviso('')
    try {
      try {
        await clienteDeOrganizacion[accion](tenant.id)
      } catch (e) {
        setError(e instanceof ErrorApi ? e.message : 'No se pudo completar la acción.')

        return
      }

      setConfirmacion(null)
      await refrescarTrasEscribir(
        token,
        `Tenant "${tenant.nombre}" ${COPIA_DE_ESTADO[accion].hecho}.`,
        AVISO_REFRESCO_FALLIDO,
      )
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  /** Abre la puerta de confirmación. NO acuña generación —no hay escritura todavía— y re-chequea
   * `ocupadoRef` además del `disabled` del botón: un doble click en el mismo tick le gana al
   * re-render (`react-async-state` regla 9). */
  function pedirConfirmacion(pendiente: Confirmacion) {
    if (ocupadoRef.current) return

    setConfirmacion(pendiente)
    setError('')
    setAviso('')
  }

  /** Cancelar no supersede NADA: solo cierra la puerta. Por eso no acuña generación —hacerlo
   * descartaba una lectura en vuelo y dejaba la pantalla clavada en "Cargando…"— y limpia los dos
   * avisos, en simetría con la apertura: un 409 en rojo al lado de una puerta que ya no está es un
   * banner huérfano. */
  function cancelarConfirmacion() {
    if (ocupadoRef.current) return

    setConfirmacion(null)
    setError('')
    setAviso('')
  }

  function confirmar() {
    if (!confirmacion) return

    if (confirmacion.tipo === 'baja') {
      void darDeBaja(confirmacion.fila)

      return
    }

    void cambiarEstado(confirmacion.fila, confirmacion.accion)
  }

  async function darDeBaja(fila: TenantListado) {
    if (ocupadoRef.current) return

    // El token se acuña ACÁ, como primera sentencia síncrona de la escritura (`react-async-state`
    // regla 2): el que se acuñaba al abrir la puerta llegaba viejo a un DELETE que salía igual, y
    // el chequeo posterior a la red se tragaba tanto el 204 como el 409.
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(fila.id)
    setError('')
    setAviso('')
    try {
      try {
        await clienteDeOrganizacion.eliminarTenant(fila.id)
      } catch (e) {
        // Un rechazo SIEMPRE se rinde y la puerta queda ABIERTA con el motivo al lado: el operador
        // ve por qué no se pudo sin perder de vista qué estaba por dar de baja. La copia la elige
        // el `codigo`, nunca el `mensaje` — que igual se rinde, porque es lo único que nombra qué
        // bloquea.
        setError(copiaDeFalloDeBaja(e, 'el tenant'))

        return
      }

      // Un 204 SIEMPRE cierra la puerta y refresca. La generación solo gobierna el REFRESCO, que es
      // una lectura: nunca el desenlace de la escritura que acaba de commitear.
      setConfirmacion(null)
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

  /** La puerta abierta bloquea la pantalla entera, no solo la escritura en vuelo: es lo que hace
   * que nada pueda acuñar una generación nueva mientras el operador decide (regla 9). */
  const bloqueado = ocupado !== null || confirmacion !== null

  // Un `<Link>` no admite `disabled`: la clase `disabled` de Bootstrap le apaga los eventos de
  // puntero y `tabIndex={-1}` lo saca del recorrido de tabulación, que es lo que la puerta modal
  // necesita — nada alcanzable afuera mientras está abierta.
  const herramientas = (
    <nav className="p-2">
      <Link
        to="/organizacion/nuevo-tenant"
        className={`btn btn-sm btn-success rounded-0 text-nowrap${bloqueado ? ' disabled' : ''}`}
        aria-disabled={bloqueado}
        tabIndex={bloqueado ? -1 : undefined}
      >
        Nuevo tenant
      </Link>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Tenants" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}

        {confirmacion && (
          <ConfirmacionDeBaja
            titulo={`el tenant "${confirmacion.fila.nombre}"`}
            pregunta={
              confirmacion.tipo === 'baja' ? undefined : COPIA_DE_ESTADO[confirmacion.accion].pregunta
            }
            arrastra={confirmacion.tipo === 'baja' ? arrastreDeTenant(confirmacion.fila) : undefined}
            nota={confirmacion.tipo === 'baja' ? undefined : null}
            etiquetaConfirmar={
              confirmacion.tipo === 'baja' ? undefined : COPIA_DE_ESTADO[confirmacion.accion].confirmar
            }
            etiquetaEnCurso={
              confirmacion.tipo === 'baja' ? undefined : COPIA_DE_ESTADO[confirmacion.accion].enCurso
            }
            ocupado={ocupado !== null}
            onConfirmar={confirmar}
            onCancelar={cancelarConfirmacion}
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
                disabled={bloqueado}
                required
              />
            </div>
            <div className="col-12 d-flex gap-2">
              <button type="submit" className="btn btn-success rounded-0" disabled={bloqueado}>
                {ocupado !== null ? 'Guardando…' : 'Guardar'}
              </button>
              <button
                type="button"
                className="btn btn-outline-secondary rounded-0"
                onClick={() => setEdicion(null)}
                disabled={bloqueado}
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
                        disabled={bloqueado}
                      >
                        Editar
                      </button>
                      {t.estado === 'Activo' && (
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-warning rounded-0 me-1"
                          onClick={() => pedirConfirmacion({ tipo: 'estado', fila: t, accion: 'suspenderTenant' })}
                          disabled={bloqueado}
                        >
                          Suspender
                        </button>
                      )}
                      {t.estado === 'Suspendido' && (
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-success rounded-0 me-1"
                          onClick={() => pedirConfirmacion({ tipo: 'estado', fila: t, accion: 'reactivarTenant' })}
                          disabled={bloqueado}
                        >
                          Reactivar
                        </button>
                      )}
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger rounded-0"
                        onClick={() => pedirConfirmacion({ tipo: 'baja', fila: t })}
                        disabled={bloqueado}
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
