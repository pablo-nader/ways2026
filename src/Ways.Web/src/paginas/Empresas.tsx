import { useCallback, useEffect, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import type { EmpresaListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { useAuth } from '../auth/useAuth'
import { ROL } from '../api/tipos'

type Formulario = { id: number; razonSocial: string; nombreFantasia: string; cuit: string }

/**
 * Lectura/edición de empresas — sin alta ni baja (ambas siguen siendo plataforma-only vía
 * aprovisionamiento, `NuevoTenant.tsx`). El backend ya filtra por alcance: la plataforma ve
 * todas las empresas de todos los tenants, un admin de tenant solo ve la(s) propia(s) — esta
 * pantalla no filtra nada por su cuenta.
 */
export function Empresas() {
  const { usuario } = useAuth()
  const esPlataforma = usuario?.rolId === ROL.Root

  const [items, setItems] = useState<EmpresaListado[]>([])
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [guardando, setGuardando] = useState(false)

  const cargar = useCallback(async () => {
    setCargando(true)
    setError('')
    try {
      setItems(await clienteDeOrganizacion.listarEmpresas())
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las empresas.')
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
      await clienteDeOrganizacion.editarEmpresa(formulario.id, {
        razonSocial: formulario.razonSocial,
        nombreFantasia: formulario.nombreFantasia || null,
        cuit: formulario.cuit || null,
      })
      setAviso(`Se actualizó "${formulario.razonSocial}".`)
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
                  <th>Razón social</th>
                  <th>Nombre de fantasía</th>
                  <th>CUIT</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {items.map((e) => (
                  <tr key={e.id}>
                    <td>{String(e.id).padStart(4, '0')}</td>
                    {esPlataforma && <td>{e.idTenant}</td>}
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
                      >
                        Editar
                      </button>
                    </td>
                  </tr>
                ))}
                {items.length === 0 && (
                  <tr>
                    <td colSpan={esPlataforma ? 6 : 5} className="text-center text-muted py-4">
                      No hay empresas cargadas.
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
