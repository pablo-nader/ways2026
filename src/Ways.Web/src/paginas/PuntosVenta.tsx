import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import {
  clienteDeOrganizacion,
  ETIQUETA_SIN_DUENIO,
  etiquetaDeTenant,
  filtrarPorEmpresa,
  filtrarPorTenant,
  opcionesDeEmpresa,
  opcionesDeTenant,
  SIN_FILTRO,
} from '../api/organizacion'
import type { PuntoVentaListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { useAuth } from '../auth/useAuth'
import { ROL } from '../api/tipos'

type Formulario = {
  id: number
  nombre: string
  domicilio: string
  horario: string
  whatsapp: string
  instagram: string
  facebook: string
  web: string
}

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'

/**
 * Lectura/edición de puntos de venta — mismo patrón que <c>Empresas.tsx</c>: sin alta ni baja
 * (plataforma-only vía aprovisionamiento), el backend ya filtra por alcance.
 * <c>idEmpresa</c> no se edita acá: es estructural (a qué empresa pertenece), no descriptivo.
 * Los dos filtros (tenant y empresa) operan sobre la lista YA CARGADA y sus opciones salen de
 * esas mismas filas (design D15).
 */
export function PuntosVenta() {
  const { usuario } = useAuth()
  const esPlataforma = usuario?.rolId === ROL.Root

  const [items, setItems] = useState<PuntoVentaListado[]>([])
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [ocupado, setOcupado] = useState<number | null>(null)
  const [filtroTenant, setFiltroTenant] = useState(SIN_FILTRO)
  const [filtroEmpresa, setFiltroEmpresa] = useState(SIN_FILTRO)

  /** Contrato de invalidación: ver `Tenants.tsx` — mismo patrón en las cuatro pantallas raíz. */
  const generacion = useRef(0)

  const cargar = useCallback(async (token: number, propagar = false) => {
    setCargando(true)
    try {
      const filas = await clienteDeOrganizacion.listarPuntosVenta()
      if (generacion.current !== token) return
      setItems(filas)
      setError('')
    } catch (e) {
      if (generacion.current !== token) return
      if (propagar) throw e
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los puntos de venta.')
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
      await clienteDeOrganizacion.editarPuntoVenta(datos.id, {
        nombre: datos.nombre,
        domicilio: datos.domicilio || null,
        horario: datos.horario || null,
        whatsapp: datos.whatsapp || null,
        instagram: datos.instagram || null,
        facebook: datos.facebook || null,
        web: datos.web || null,
      })
    } catch (e) {
      if (generacion.current !== token) return
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
      setOcupado(null)

      return
    }

    if (generacion.current !== token) return

    // El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya
    // commiteó nunca se reporta como fallida (`react-async-state` regla 6).
    const mensajeOk = `Se actualizó "${datos.nombre}".`
    setFormulario(null)
    setAviso(mensajeOk)
    try {
      await cargar(token, true)
    } catch {
      if (generacion.current === token) setAviso(`${mensajeOk} ${AVISO_REFRESCO_FALLIDO}`)
    } finally {
      if (generacion.current === token) setOcupado(null)
    }
  }

  // Derivación pura, sin efectos: elegir un tenant ANGOSTA las opciones de empresa (design D15).
  // El fallback a "todas" cubre además el caso en que un refresco se llevó puesta la fila que
  // sostenía la opción elegida, sin dejar el `<select>` apuntando a una opción inexistente.
  const opcionesTenant = opcionesDeTenant(items)
  const tenantVigente = opcionesTenant.some((o) => o.valor === filtroTenant) ? filtroTenant : SIN_FILTRO
  const opcionesEmpresa = opcionesDeEmpresa(items, tenantVigente)
  const empresaVigente = opcionesEmpresa.some((o) => o.valor === filtroEmpresa) ? filtroEmpresa : SIN_FILTRO
  const visibles = filtrarPorEmpresa(filtrarPorTenant(items, tenantVigente), empresaVigente)

  /** Elegir un tenant LIMPIA la empresa seleccionada cuando esa empresa ya no le pertenece
   * (design D15) — si le sigue perteneciendo, la selección se respeta. El updater se arma desde
   * `prev`, nunca leyendo el estado por closure (`react-async-state` regla 1). */
  function cambiarFiltroDeTenant(valor: string) {
    setFiltroTenant(valor)
    setFiltroEmpresa((prev) =>
      prev === SIN_FILTRO || opcionesDeEmpresa(items, valor).some((o) => o.valor === prev)
        ? prev
        : SIN_FILTRO,
    )
  }

  return (
    <div className="container-fluid py-4">
      <Box titulo="Puntos de venta" variante="inverse">
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
              <strong>Editando punto de venta {formulario.id}</strong>
            </div>
            <div className="col-md-3">
              <label className="form-label" htmlFor="pv-nombre">
                Nombre
              </label>
              <input
                id="pv-nombre"
                className="form-control rounded-0"
                maxLength={150}
                value={formulario.nombre}
                onChange={(e) => setFormulario({ ...formulario, nombre: e.target.value })}
                disabled={ocupado !== null}
                required
              />
            </div>
            <div className="col-md-4">
              <label className="form-label" htmlFor="pv-domicilio">
                Domicilio
              </label>
              <input
                id="pv-domicilio"
                className="form-control rounded-0"
                maxLength={255}
                value={formulario.domicilio}
                onChange={(e) => setFormulario({ ...formulario, domicilio: e.target.value })}
                disabled={ocupado !== null}
              />
            </div>
            <div className="col-md-3">
              <label className="form-label" htmlFor="pv-horario">
                Horario
              </label>
              <input
                id="pv-horario"
                className="form-control rounded-0"
                maxLength={255}
                value={formulario.horario}
                onChange={(e) => setFormulario({ ...formulario, horario: e.target.value })}
                disabled={ocupado !== null}
              />
            </div>
            <div className="col-md-2">
              <label className="form-label" htmlFor="pv-whatsapp">
                WhatsApp
              </label>
              <input
                id="pv-whatsapp"
                className="form-control rounded-0"
                maxLength={30}
                value={formulario.whatsapp}
                onChange={(e) => setFormulario({ ...formulario, whatsapp: e.target.value })}
                disabled={ocupado !== null}
              />
            </div>
            <div className="col-md-3">
              <label className="form-label" htmlFor="pv-instagram">
                Instagram
              </label>
              <input
                id="pv-instagram"
                className="form-control rounded-0"
                maxLength={150}
                value={formulario.instagram}
                onChange={(e) => setFormulario({ ...formulario, instagram: e.target.value })}
                disabled={ocupado !== null}
              />
            </div>
            <div className="col-md-3">
              <label className="form-label" htmlFor="pv-facebook">
                Facebook
              </label>
              <input
                id="pv-facebook"
                className="form-control rounded-0"
                maxLength={150}
                value={formulario.facebook}
                onChange={(e) => setFormulario({ ...formulario, facebook: e.target.value })}
                disabled={ocupado !== null}
              />
            </div>
            <div className="col-md-3">
              <label className="form-label" htmlFor="pv-web">
                Sitio web
              </label>
              <input
                id="pv-web"
                className="form-control rounded-0"
                maxLength={255}
                value={formulario.web}
                onChange={(e) => setFormulario({ ...formulario, web: e.target.value })}
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
            <div className="row g-2 mb-3">
              <div className="col-md-4">
                <label className="form-label" htmlFor="pv-filtro-tenant">
                  Tenant
                </label>
                <select
                  id="pv-filtro-tenant"
                  className="form-select rounded-0"
                  value={tenantVigente}
                  onChange={(e) => cambiarFiltroDeTenant(e.target.value)}
                >
                  <option value={SIN_FILTRO}>Todos</option>
                  {opcionesTenant.map((o) => (
                    <option key={o.valor} value={o.valor}>
                      {o.etiqueta}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-4">
                <label className="form-label" htmlFor="pv-filtro-empresa">
                  Empresa
                </label>
                <select
                  id="pv-filtro-empresa"
                  className="form-select rounded-0"
                  value={empresaVigente}
                  onChange={(e) => setFiltroEmpresa(e.target.value)}
                >
                  <option value={SIN_FILTRO}>Todas</option>
                  {opcionesEmpresa.map((o) => (
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
                    {esPlataforma && <th>Tenant</th>}
                    <th>Empresa</th>
                    <th>Nombre</th>
                    <th>Domicilio</th>
                    <th className="text-end">Acciones</th>
                  </tr>
                </thead>
                <tbody>
                  {visibles.map((p) => (
                    <tr key={p.id}>
                      <td>{String(p.id).padStart(4, '0')}</td>
                      {esPlataforma && <td>{etiquetaDeTenant(p)}</td>}
                      <td>{p.razonSocialEmpresa ?? ETIQUETA_SIN_DUENIO}</td>
                      <td>{p.nombre}</td>
                      <td>{p.domicilio ?? '—'}</td>
                      <td className="text-end">
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-primary rounded-0"
                          onClick={() =>
                            setFormulario({
                              id: p.id,
                              nombre: p.nombre,
                              domicilio: p.domicilio ?? '',
                              horario: p.horario ?? '',
                              whatsapp: p.whatsapp ?? '',
                              instagram: p.instagram ?? '',
                              facebook: p.facebook ?? '',
                              web: p.web ?? '',
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
                        No hay puntos de venta cargados.
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
