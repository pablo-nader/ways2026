import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import {
  clienteDeOrganizacion,
  etiquetaDeTenant,
  filtrarPorTenant,
  opcionesDeTenant,
  seleccionVigente,
  SIN_FILTRO,
} from '../api/organizacion'
import type { EmpresaListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { useAuth } from '../auth/useAuth'
import { ROL } from '../api/tipos'

type Formulario = { id: number; razonSocial: string; nombreFantasia: string; cuit: string }

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'

/**
 * Lectura/edición de empresas — sin alta ni baja (ambas siguen siendo plataforma-only vía
 * aprovisionamiento, `NuevoTenant.tsx`). El backend ya filtra por alcance: la plataforma ve
 * todas las empresas de todos los tenants, un admin de tenant solo ve la(s) propia(s) — esta
 * pantalla no filtra nada por su cuenta contra el servidor. El filtro por tenant de abajo opera
 * sobre la lista YA CARGADA y sus opciones salen de esas mismas filas (design D15), así que no
 * puede delatar un tenant fuera del alcance del actor.
 */
export function Empresas() {
  const { usuario } = useAuth()
  const esPlataforma = usuario?.rolId === ROL.Root

  const [items, setItems] = useState<EmpresaListado[]>([])
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [ocupado, setOcupado] = useState<number | null>(null)
  const [filtroTenant, setFiltroTenant] = useState(SIN_FILTRO)

  /** Contrato de invalidación: ver `Tenants.tsx` — mismo patrón en las cuatro pantallas raíz. */
  const generacion = useRef(0)

  const cargar = useCallback(async (token: number, propagar = false) => {
    setCargando(true)
    try {
      const filas = await clienteDeOrganizacion.listarEmpresas()
      if (generacion.current !== token) return
      setItems(filas)
      setFiltroTenant((prev) => seleccionVigente(opcionesDeTenant(filas), prev))
      setError('')
    } catch (e) {
      if (generacion.current !== token) return
      if (propagar) throw e
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las empresas.')
    } finally {
      if (generacion.current === token) setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar(++generacion.current)
  }, [cargar])

  async function guardar() {
    if (!formulario || ocupado !== null) return

    const datos = formulario
    const token = ++generacion.current
    setOcupado(datos.id)
    setError('')
    setAviso('')
    try {
      await clienteDeOrganizacion.editarEmpresa(datos.id, {
        razonSocial: datos.razonSocial,
        nombreFantasia: datos.nombreFantasia || null,
        cuit: datos.cuit || null,
      })
    } catch (e) {
      if (generacion.current !== token) return
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
      setOcupado(null)

      return
    }

    // El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya
    // commiteó nunca se reporta como fallida (`react-async-state` regla 6).
    //
    // `ocupado` se apaga en el `finally` SIN mirar la generación, igual que en `Tenants.tsx` y
    // `Usuarios.tsx`: mientras hay una escritura en vuelo la pantalla bloquea todo lo que podría
    // supersederla (regla 9), así que la bandera nunca puede ser la de una operación más nueva —
    // y salir por el chequeo de generación la dejaría prendida para siempre.
    const mensajeOk = `Se actualizó "${datos.razonSocial}".`
    try {
      if (generacion.current !== token) return

      setFormulario(null)
      setAviso(mensajeOk)
      await cargar(token, true)
    } catch {
      if (generacion.current === token) setAviso(`${mensajeOk} ${AVISO_REFRESCO_FALLIDO}`)
    } finally {
      setOcupado(null)
    }
  }

  // Las opciones se derivan de las filas cargadas; si la selección vigente desaparece de la lista
  // (un refresco trajo otras filas), el filtro cae a "todos". Además de derivarlo acá, `cargar`
  // escribe la reconciliación en el ESTADO: si solo se derivara, la selección inválida seguiría
  // viva y se reaplicaría sola en cuanto la opción reapareciera.
  const opcionesTenant = opcionesDeTenant(items)
  const tenantVigente = seleccionVigente(opcionesTenant, filtroTenant)
  const visibles = filtrarPorTenant(items, tenantVigente)

  return (
    <div className="container-fluid py-4">
      <Box titulo="Empresas" variante="inverse">
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}

        {formulario && (
          <form
            className="row g-3 border p-3 mb-4 bg-white"
            onSubmit={(e) => {
              e.preventDefault()
              guardar()
            }}
          >
            <div className="col-12">
              <strong>Editando empresa {formulario.id}</strong>
            </div>
            <div className="col-md-4">
              <label className="form-label" htmlFor="e-razon">
                Razón social
              </label>
              <input
                id="e-razon"
                className="form-control rounded-0"
                maxLength={150}
                value={formulario.razonSocial}
                onChange={(e) => setFormulario({ ...formulario, razonSocial: e.target.value })}
                disabled={ocupado !== null}
                required
              />
            </div>
            <div className="col-md-4">
              <label className="form-label" htmlFor="e-fantasia">
                Nombre de fantasía
              </label>
              <input
                id="e-fantasia"
                className="form-control rounded-0"
                maxLength={150}
                value={formulario.nombreFantasia}
                onChange={(e) => setFormulario({ ...formulario, nombreFantasia: e.target.value })}
                disabled={ocupado !== null}
              />
            </div>
            <div className="col-md-3">
              <label className="form-label" htmlFor="e-cuit">
                CUIT
              </label>
              <input
                id="e-cuit"
                className="form-control rounded-0"
                maxLength={13}
                value={formulario.cuit}
                onChange={(e) => setFormulario({ ...formulario, cuit: e.target.value })}
                disabled={ocupado !== null}
              />
            </div>
            <div className="col-12 d-flex gap-2">
              <button type="submit" className="btn btn-success rounded-0" disabled={ocupado !== null}>
                {ocupado !== null ? 'Guardando…' : 'Guardar'}
              </button>
              <button
                type="button"
                className="btn btn-outline-secondary rounded-0"
                onClick={() => setFormulario(null)}
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
          <>
            {/* El filtro por tenant se rinde con el mismo criterio que la COLUMNA de tenant: solo
                para un actor de plataforma. Un admin de tenant ve una sola opción —la suya— y
                filtrar por ella no angosta nada. */}
            {esPlataforma && (
              <div className="row g-2 mb-3">
                <div className="col-md-4">
                  <label className="form-label" htmlFor="e-filtro-tenant">
                    Tenant
                  </label>
                  <select
                    id="e-filtro-tenant"
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
            )}

            <div className="table-responsive">
              <table className="table table-striped table-hover table-bordered align-middle">
                <thead>
                  <tr>
                    <th>ID</th>
                    {esPlataforma && <th>Tenant</th>}
                    <th>Razón social</th>
                    <th>Nombre de fantasía</th>
                    <th>CUIT</th>
                    <th className="text-end">Acciones</th>
                  </tr>
                </thead>
                <tbody>
                  {visibles.map((e) => (
                    <tr key={e.id}>
                      <td>{String(e.id).padStart(4, '0')}</td>
                      {esPlataforma && <td>{etiquetaDeTenant(e)}</td>}
                      <td>{e.razonSocial}</td>
                      <td>{e.nombreFantasia ?? '—'}</td>
                      <td>{e.cuit ?? '—'}</td>
                      <td className="text-end">
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-primary rounded-0"
                          onClick={() =>
                            setFormulario({
                              id: e.id,
                              razonSocial: e.razonSocial,
                              nombreFantasia: e.nombreFantasia ?? '',
                              cuit: e.cuit ?? '',
                            })
                          }
                          disabled={ocupado !== null}
                        >
                          Editar
                        </button>
                      </td>
                    </tr>
                  ))}
                  {visibles.length === 0 && (
                    <tr>
                      <td colSpan={esPlataforma ? 6 : 5} className="text-center text-muted py-4">
                        No hay empresas cargadas.
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
