import { useCallback, useEffect, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
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

/**
 * Lectura/edición de puntos de venta — mismo patrón que <c>Empresas.tsx</c>: sin alta ni baja
 * (plataforma-only vía aprovisionamiento), el backend ya filtra por alcance.
 * <c>idEmpresa</c> no se edita acá: es estructural (a qué empresa pertenece), no descriptivo.
 */
export function PuntosVenta() {
  const { usuario } = useAuth()
  const esPlataforma = usuario?.rolId === ROL.Root

  const [items, setItems] = useState<PuntoVentaListado[]>([])
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [guardando, setGuardando] = useState(false)

  const cargar = useCallback(async () => {
    setCargando(true)
    setError('')
    try {
      setItems(await clienteDeOrganizacion.listarPuntosVenta())
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los puntos de venta.')
    } finally {
      setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar()
  }, [cargar])

  async function guardar() {
    if (!formulario) return

    setGuardando(true)
    setError('')
    setAviso('')
    try {
      await clienteDeOrganizacion.editarPuntoVenta(formulario.id, {
        nombre: formulario.nombre,
        domicilio: formulario.domicilio || null,
        horario: formulario.horario || null,
        whatsapp: formulario.whatsapp || null,
        instagram: formulario.instagram || null,
        facebook: formulario.facebook || null,
        web: formulario.web || null,
      })
      setAviso(`Se actualizó "${formulario.nombre}".`)
      setFormulario(null)
      await cargar()
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
    } finally {
      setGuardando(false)
    }
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
              />
            </div>
            <div className="col-12 d-flex gap-2">
              <button type="submit" className="btn btn-success rounded-0" disabled={guardando}>
                {guardando ? 'Guardando…' : 'Guardar'}
              </button>
              <button
                type="button"
                className="btn btn-outline-secondary rounded-0"
                onClick={() => setFormulario(null)}
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
                  {esPlataforma && <th>Tenant</th>}
                  <th>Empresa</th>
                  <th>Nombre</th>
                  <th>Domicilio</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {items.map((p) => (
                  <tr key={p.id}>
                    <td>{String(p.id).padStart(4, '0')}</td>
                    {esPlataforma && <td>{p.idTenant}</td>}
                    <td>{p.idEmpresa}</td>
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
                      >
                        Editar
                      </button>
                    </td>
                  </tr>
                ))}
                {items.length === 0 && (
                  <tr>
                    <td colSpan={esPlataforma ? 6 : 5} className="text-center text-muted py-4">
                      No hay puntos de venta cargados.
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
