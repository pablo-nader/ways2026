import { useCallback, useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { PARAMETROS_CONOCIDOS } from '../api/tipos'
import type { EmpresaListado, ParametroAlta, ParametroListado, ParametroResuelto, PuntoVentaListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

/**
 * Editor de `parametros` (ADR-13): punto de venta gana sobre empresa, empresa gana sobre el
 * default declarado. `ITenantActual` todavía no carga una "empresa actual" en la sesión
 * (ADR-10 — la selección de empresa/punto de venta es una etapa operativa posterior), así que
 * la empresa se elige acá de un desplegable poblado por `GET /api/empresas` (etapa 4B): antes
 * de que ese endpoint existiera, esta pantalla pedía el id a mano — ya no hace falta.
 */
export function Parametros() {
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[]>([])
  const [idEmpresa, setIdEmpresa] = useState<number | null>(null)
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

  // Al entrar, trae las empresas del propio tenant (esta pantalla es admin-only, ver App.tsx)
  // y los puntos de venta, para poblar los dos desplegables sin pedir ningún id a mano.
  useEffect(() => {
    let vigente = true

    Promise.all([clienteDeOrganizacion.listarEmpresas(), clienteDeOrganizacion.listarPuntosVenta()])
      .then(([listadoEmpresas, listadoPuntosVenta]) => {
        if (!vigente) return
        setEmpresas(listadoEmpresas)
        setPuntosVenta(listadoPuntosVenta)
        if (listadoEmpresas.length > 0) {
          setIdEmpresa(listadoEmpresas[0].id)
        }
      })
      .catch((e) => {
        if (!vigente) return
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las empresas.')
      })

    return () => {
      vigente = false
    }
  }, [])

  const cargarListado = useCallback(async (empresa: number) => {
    setCargando(true)
    setError('')
    try {
      setItems(await api.get<ParametroListado[]>(`/parametros?idEmpresa=${empresa}`))
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los parámetros.')
    } finally {
      setCargando(false)
    }
  }, [])

  useEffect(() => {
    if (idEmpresa !== null) {
      void cargarListado(idEmpresa)
    }
  }, [idEmpresa, cargarListado])

  const puntosVentaDeLaEmpresa = puntosVenta.filter((p) => p.idEmpresa === idEmpresa)

  async function establecer(evento: FormEvent) {
    evento.preventDefault()
    if (idEmpresa === null) return

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

      await api.put(`/parametros?idEmpresa=${idEmpresa}`, datos)
      setAviso(`Se guardó "${clave}".`)
      setValorTexto('')
      await cargarListado(idEmpresa)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar el parámetro.')
    } finally {
      setGuardando(false)
    }
  }

  async function probarResolucion(evento: FormEvent) {
    evento.preventDefault()
    if (idEmpresa === null) return

    setResolviendo(true)
    setError('')
    setResuelto(null)

    try {
      const parametros = idPuntoVenta === '' ? '' : `&idPuntoVenta=${idPuntoVenta}`
      setResuelto(await api.get<ParametroResuelto>(`/parametros/${clave}?idEmpresa=${idEmpresa}${parametros}`))
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

        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}

        {empresas === null ? (
          <Cargando />
        ) : empresas.length === 0 ? (
          <p className="text-muted text-center py-4">No hay empresas visibles para configurar parámetros.</p>
        ) : (
          <>
            <div className="row g-3 align-items-end mb-4">
              <div className="col-auto">
                <label className="form-label" htmlFor="p-empresa">
                  Empresa
                </label>
                <select
                  id="p-empresa"
                  className="form-select rounded-0"
                  value={idEmpresa ?? ''}
                  onChange={(e) => setIdEmpresa(Number(e.target.value))}
                >
                  {empresas.map((e) => (
                    <option key={e.id} value={e.id}>
                      {e.razonSocial}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            <form className="row g-3 border p-3 mb-4 bg-white" onSubmit={establecer}>
              <div className="col-12">
                <strong>Crear o editar un parámetro</strong>
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
                  Punto de venta
                </label>
                <select
                  id="p-puntoventa"
                  className="form-select rounded-0"
                  value={idPuntoVenta}
                  onChange={(e) => setIdPuntoVenta(e.target.value)}
                >
                  <option value="">Toda la empresa (default)</option>
                  {puntosVentaDeLaEmpresa.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.nombre}
                    </option>
                  ))}
                </select>
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
                    {items.map((p) => {
                      const puntoVenta = puntosVenta.find((pv) => pv.id === p.idPuntoVenta)
                      return (
                        <tr key={p.id}>
                          <td>{p.clave}</td>
                          <td>{p.valor}</td>
                          <td>{puntoVenta ? puntoVenta.nombre : 'Toda la empresa'}</td>
                        </tr>
                      )
                    })}
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
