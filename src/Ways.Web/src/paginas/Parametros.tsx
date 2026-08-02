import { useCallback, useState } from 'react'
import type { FormEvent } from 'react'
import { api, ErrorApi } from '../api/cliente'
import { PARAMETROS_CONOCIDOS } from '../api/tipos'
import type { ParametroAlta, ParametroListado, ParametroResuelto } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

/**
 * Editor de `parametros` (ADR-13): punto de venta gana sobre empresa, empresa gana sobre el
 * default declarado. `ITenantActual` todavía no carga una "empresa actual" en la sesión (ADR-10
 * — la selección de empresa/punto de venta es una etapa operativa posterior), así que esta
 * pantalla pide el id de empresa a mano en vez de un selector: en esta etapa cada tenant tiene
 * una sola empresa (la que crea el aprovisionamiento), así que sigue siendo una UX razonable
 * sin depender de un endpoint de listado que todavía no existe.
 */
export function Parametros() {
  const [idEmpresa, setIdEmpresa] = useState('')
  const [empresaConsultada, setEmpresaConsultada] = useState<number | null>(null)
  const [items, setItems] = useState<ParametroListado[]>([])
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')

  const [clave, setClave] = useState(PARAMETROS_CONOCIDOS[0].clave)
  const [valorTexto, setValorTexto] = useState('')
  const [idPuntoVenta, setIdPuntoVenta] = useState('')
  const [guardando, setGuardando] = useState(false)

  const [resuelto, setResuelto] = useState<ParametroResuelto | null>(null)
  const [resolviendo, setResolviendo] = useState(false)

  const cargarListado = useCallback(async (empresa: number) => {
    setCargando(true)
    setError('')
    try {
      setItems(await api.get<ParametroListado[]>(`/parametros?idEmpresa=${empresa}`))
      setEmpresaConsultada(empresa)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los parámetros.')
    } finally {
      setCargando(false)
    }
  }, [])

  async function buscar(evento: FormEvent) {
    evento.preventDefault()
    const empresa = Number(idEmpresa)
    if (!Number.isFinite(empresa) || empresa <= 0) {
      setError('Ingresá un id de empresa válido.')
      return
    }
    await cargarListado(empresa)
  }

  async function establecer(evento: FormEvent) {
    evento.preventDefault()
    if (empresaConsultada === null) return

    setGuardando(true)
    setError('')
    setAviso('')

    const conocido = PARAMETROS_CONOCIDOS.find((p) => p.clave === clave)

    try {
      const datos: ParametroAlta = {
        clave,
        valor: JSON.stringify(conocido?.tipo === 'entero' ? Math.trunc(Number(valorTexto)) : Number(valorTexto)),
        idPuntoVenta: idPuntoVenta === '' ? null : Number(idPuntoVenta),
      }

      await api.put(`/parametros?idEmpresa=${empresaConsultada}`, datos)
      setAviso(`Se guardó "${clave}".`)
      setValorTexto('')
      await cargarListado(empresaConsultada)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar el parámetro.')
    } finally {
      setGuardando(false)
    }
  }

  async function probarResolucion(evento: FormEvent) {
    evento.preventDefault()
    if (empresaConsultada === null) return

    setResolviendo(true)
    setError('')
    setResuelto(null)

    try {
      const parametros = idPuntoVenta === '' ? '' : `&idPuntoVenta=${idPuntoVenta}`
      setResuelto(
        await api.get<ParametroResuelto>(`/parametros/${clave}?idEmpresa=${empresaConsultada}${parametros}`),
      )
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo resolver el parámetro.')
    } finally {
      setResolviendo(false)
    }
  }

  return (
    <div className="container-fluid py-4">
      <Box titulo="Parámetros operativos" variante="inverse">
        <p className="text-muted">
          Tolerancia de pago, vuelto máximo, adicional de recarga, tickets en espera. Un valor sin punto de venta
          es el default de la empresa; uno con punto de venta lo pisa solo para ese local.
        </p>

        <form className="row g-3 align-items-end mb-4" onSubmit={buscar}>
          <div className="col-auto">
            <label className="form-label" htmlFor="p-idempresa">
              Id de empresa
            </label>
            <input
              id="p-idempresa"
              type="number"
              className="form-control rounded-0"
              value={idEmpresa}
              onChange={(e) => setIdEmpresa(e.target.value)}
              required
            />
          </div>
          <div className="col-auto">
            <button type="submit" className="btn btn-outline-primary rounded-0">
              Ver parámetros
            </button>
          </div>
        </form>

        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}

        {empresaConsultada !== null && (
          <>
            <form className="row g-3 border p-3 mb-4 bg-white" onSubmit={establecer}>
              <div className="col-12">
                <strong>Crear o editar un parámetro de la empresa {empresaConsultada}</strong>
              </div>

              <div className="col-md-4">
                <label className="form-label" htmlFor="p-clave">
                  Clave
                </label>
                <select
                  id="p-clave"
                  className="form-select rounded-0"
                  value={clave}
                  onChange={(e) => setClave(e.target.value)}
                >
                  {PARAMETROS_CONOCIDOS.map((p) => (
                    <option key={p.clave} value={p.clave}>
                      {p.etiqueta}
                    </option>
                  ))}
                </select>
              </div>

              <div className="col-md-3">
                <label className="form-label" htmlFor="p-valor">
                  Valor
                </label>
                <input
                  id="p-valor"
                  type="number"
                  step={PARAMETROS_CONOCIDOS.find((p) => p.clave === clave)?.tipo === 'entero' ? '1' : '0.01'}
                  className="form-control rounded-0"
                  placeholder={`Default: ${PARAMETROS_CONOCIDOS.find((p) => p.clave === clave)?.porDefecto}`}
                  value={valorTexto}
                  onChange={(e) => setValorTexto(e.target.value)}
                  required
                />
              </div>

              <div className="col-md-3">
                <label className="form-label" htmlFor="p-puntoventa">
                  Punto de venta (opcional)
                </label>
                <input
                  id="p-puntoventa"
                  type="number"
                  className="form-control rounded-0"
                  placeholder="Vacío = default de la empresa"
                  value={idPuntoVenta}
                  onChange={(e) => setIdPuntoVenta(e.target.value)}
                />
              </div>

              <div className="col-md-2 d-flex align-items-end gap-2">
                <button type="submit" className="btn btn-success rounded-0" disabled={guardando}>
                  {guardando ? 'Guardando…' : 'Guardar'}
                </button>
                <button
                  type="button"
                  className="btn btn-outline-secondary rounded-0"
                  onClick={probarResolucion}
                  disabled={resolviendo}
                >
                  {resolviendo ? 'Probando…' : 'Probar'}
                </button>
              </div>
            </form>

            {resuelto && (
              <div className="alert alert-info rounded-0">
                Valor resuelto para «{resuelto.clave}»: <strong>{resuelto.valor}</strong>
              </div>
            )}

            {cargando ? (
              <Cargando />
            ) : (
              <div className="table-responsive">
                <table className="table table-striped table-hover table-bordered align-middle">
                  <thead>
                    <tr>
                      <th>Clave</th>
                      <th>Valor</th>
                      <th>Alcance</th>
                    </tr>
                  </thead>
                  <tbody>
                    {items.map((p) => (
                      <tr key={p.id}>
                        <td>{p.clave}</td>
                        <td>{p.valor}</td>
                        <td>{p.idPuntoVenta === null ? 'Toda la empresa' : `Punto de venta ${p.idPuntoVenta}`}</td>
                      </tr>
                    ))}
                    {items.length === 0 && (
                      <tr>
                        <td colSpan={3} className="text-center text-muted py-4">
                          Esta empresa no tiene parámetros configurados; se usan los defaults declarados.
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}
      </Box>
    </div>
  )
}
