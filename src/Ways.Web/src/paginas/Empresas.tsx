import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { copiaDeFalloDeBaja } from '../api/bajas'
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
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { useAuth } from '../auth/useAuth'
import { ROL } from '../api/tipos'

type Formulario = { id: number; razonSocial: string; nombreFantasia: string; cuit: string }

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const AVISO_REFRESCO_FALLIDO_BAJA =
  'Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.'

/** `EmpresaListado` no trae un contador de puntos de venta, así que la puerta no puede decir
 * cuántos son. Se nombra la familia sin cantidad —y "activos", que es lo que efectivamente cae en
 * la cascada— en vez de inventar un número que la fila no tiene. */
const ARRASTRE_DE_EMPRESA = ['Sus puntos de venta activos'] as const

/**
 * Lectura/edición/baja de empresas — el alta sigue siendo plataforma-only vía aprovisionamiento
 * (`NuevoTenant.tsx`); la baja (etapa 20) la puede ejecutar también un admin de tenant sobre las
 * propias, porque `DELETE /api/empresas/{id}` reusa la policy del grupo
 * (`GestionDeOrganizacion`) y esta pantalla ya está detrás de esos mismos roles.
 * El backend ya filtra por alcance: la plataforma ve
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
  /** Baja pendiente de confirmación: ver `Tenants.tsx`. La puerta es MODAL —`bloqueado` deja
   * inerte el resto de la pantalla mientras está abierta— y NO acuña token: eso lo hace la
   * escritura, al confirmar. */
  const [baja, setBaja] = useState<EmpresaListado | null>(null)

  /** Contrato de invalidación: ver `Tenants.tsx` — mismo patrón en las cuatro pantallas raíz. */
  const generacion = useRef(0)
  /**
   * Espejo SÍNCRONO de `ocupado`, y la ÚNICA guarda de re-entrancia válida: ver `Tenants.tsx`.
   * Dos clicks del MISMO tick leen el mismo render, así que el estado los deja pasar a los dos.
   */
  const ocupadoRef = useRef(false)

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

  /** El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya
   * commiteó nunca se reporta como fallida (`react-async-state` regla 6). NO apaga `ocupado`: de
   * eso se ocupa el `finally` ungated de cada escritura — ver `Tenants.tsx`. */
  async function refrescarTrasEscribir(token: number, mensajeOk: string, avisoDeFallo: string) {
    if (generacion.current !== token) return

    setAviso(mensajeOk)
    try {
      await cargar(token, true)
    } catch {
      if (generacion.current === token) setAviso(`${mensajeOk} ${avisoDeFallo}`)
    }
  }

  async function guardar() {
    if (!formulario || ocupadoRef.current) return

    const datos = formulario
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(datos.id)
    setError('')
    setAviso('')
    try {
      try {
        await clienteDeOrganizacion.editarEmpresa(datos.id, {
          razonSocial: datos.razonSocial,
          nombreFantasia: datos.nombreFantasia || null,
          cuit: datos.cuit || null,
        })
      } catch (e) {
        if (generacion.current === token) setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')

        return
      }

      if (generacion.current !== token) return

      setFormulario(null)
      await refrescarTrasEscribir(token, `Se actualizó "${datos.razonSocial}".`, AVISO_REFRESCO_FALLIDO)
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  /** Ver `Tenants.tsx`: mismo patrón de puerta, mismo contrato de invalidación, misma re-entrancia.
   * Abrir NO acuña generación: no hay escritura todavía. */
  function pedirBaja(empresa: EmpresaListado) {
    if (ocupadoRef.current) return

    setBaja(empresa)
    setError('')
    setAviso('')
  }

  /** Cancelar no supersede nada: solo cierra la puerta. Por eso no acuña generación —hacerlo
   * descartaba una lectura en vuelo y clavaba la pantalla en "Cargando…"— y limpia los dos avisos,
   * en simetría con la apertura. */
  function cancelarBaja() {
    if (ocupadoRef.current) return

    setBaja(null)
    setError('')
    setAviso('')
  }

  async function confirmarBaja() {
    if (!baja || ocupadoRef.current) return

    // El token se acuña ACÁ, primera sentencia síncrona de la escritura (`react-async-state`
    // regla 2): ver `Tenants.tsx`.
    const fila = baja
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(fila.id)
    setError('')
    setAviso('')
    try {
      try {
        await clienteDeOrganizacion.eliminarEmpresa(fila.id)
      } catch (e) {
        // Un rechazo SIEMPRE se rinde, con la puerta abierta al lado del motivo.
        setError(copiaDeFalloDeBaja(e, 'la empresa'))

        return
      }

      // Un 204 SIEMPRE cierra la puerta y refresca; la generación solo gobierna el REFRESCO.
      setBaja(null)
      await refrescarTrasEscribir(
        token,
        `Se dio de baja la empresa "${fila.razonSocial}".`,
        AVISO_REFRESCO_FALLIDO_BAJA,
      )
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  /** La puerta abierta bloquea la pantalla entera, no solo la escritura en vuelo: ver `Tenants.tsx`. */
  const bloqueado = ocupado !== null || baja !== null

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

        {baja && (
          <ConfirmacionDeBaja
            titulo={`la empresa "${baja.razonSocial}"`}
            arrastra={ARRASTRE_DE_EMPRESA}
            ocupado={ocupado !== null}
            onConfirmar={confirmarBaja}
            onCancelar={cancelarBaja}
          />
        )}

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
                disabled={bloqueado}
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
                disabled={bloqueado}
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
                disabled={bloqueado}
              />
            </div>
            <div className="col-12 d-flex gap-2">
              <button type="submit" className="btn btn-success rounded-0" disabled={bloqueado}>
                {ocupado !== null ? 'Guardando…' : 'Guardar'}
              </button>
              <button
                type="button"
                className="btn btn-outline-secondary rounded-0"
                onClick={() => setFormulario(null)}
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
                    disabled={bloqueado}
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
                      <td className="text-end text-nowrap">
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-primary rounded-0 me-1"
                          onClick={() =>
                            setFormulario({
                              id: e.id,
                              razonSocial: e.razonSocial,
                              nombreFantasia: e.nombreFantasia ?? '',
                              cuit: e.cuit ?? '',
                            })
                          }
                          disabled={bloqueado}
                        >
                          Editar
                        </button>
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-danger rounded-0"
                          onClick={() => pedirBaja(e)}
                          disabled={bloqueado}
                        >
                          Baja
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
