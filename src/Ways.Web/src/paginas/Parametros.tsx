import { useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { PARAMETROS_CONOCIDOS, ZONAS_HORARIAS_OFRECIDAS } from '../api/tipos'
import type {
  EmpresaListado,
  ParametroAlta,
  ParametroListado,
  ParametroResuelto,
  PuntoVentaListado,
  SucursalListado,
} from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { atributosDeCampoDecimal, atributosDeCampoEntero, parsearNumeroDecimal } from '../formato/numeros'
import { toast } from '../toasts/toasts'

/**
 * Editor de `parametros` (ADR-13). Cada clave es de un alcance: las de sucursal (zona horaria, lotes, días de alerta) se
 * configuran por sucursal y las comparten todas sus cajas; las de caja, por punto de venta. En los dos casos el valor sin
 * nivel es el default de la empresa, que gana sobre el default declarado. `ITenantActual` todavía no carga una "empresa
 * actual" en la sesión (ADR-10 — la selección de empresa/punto de venta es una etapa operativa posterior), así que la
 * empresa se elige acá de un desplegable poblado por `GET /api/empresas` (etapa 4B).
 */
export function Parametros() {
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[]>([])
  const [sucursales, setSucursales] = useState<SucursalListado[]>([])
  const [idEmpresa, setIdEmpresa] = useState<number | null>(null)
  const [items, setItems] = useState<ParametroListado[]>([])
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')

  const [clave, setClave] = useState(PARAMETROS_CONOCIDOS[0].clave)
  const [valorTexto, setValorTexto] = useState('')
  const [idPuntoVenta, setIdPuntoVenta] = useState('')
  const [idSucursal, setIdSucursal] = useState('')
  const [guardando, setGuardando] = useState(false)
  // Re-entrada síncrona del guardado: `guardando` es estado y un segundo submit en el mismo tick lo ve todavía en false.
  const guardandoRef = useRef(false)

  const [resuelto, setResuelto] = useState<ParametroResuelto | null>(null)
  const [resolviendo, setResolviendo] = useState(false)

  // Generaciones de las lecturas: la última INICIADA gana. `generacionListado` lo invalida también cambiar de empresa;
  // `generacionResolucion`, cambiar de clave, de empresa o de nivel (el valor probado ya no corresponde a lo elegido).
  const generacionListado = useRef(0)
  const generacionResolucion = useRef(0)

  // Al entrar, trae las empresas del propio tenant (esta pantalla es admin-only, ver App.tsx), sus puntos de venta y sus
  // sucursales, para poblar los desplegables sin pedir ningún id a mano.
  useEffect(() => {
    let vigente = true

    Promise.all([
      clienteDeOrganizacion.listarEmpresas(),
      clienteDeOrganizacion.listarPuntosVenta(),
      clienteDeOrganizacion.listarSucursales(),
    ])
      .then(([listadoEmpresas, listadoPuntosVenta, listadoSucursales]) => {
        if (!vigente) return
        setEmpresas(listadoEmpresas)
        setPuntosVenta(listadoPuntosVenta)
        setSucursales(listadoSucursales)
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

  const cargarListado = useCallback(async (empresa: number, trasGuardar = false) => {
    const miGeneracion = ++generacionListado.current
    setCargando(true)
    if (!trasGuardar) setError('')
    try {
      const listado = await api.get<ParametroListado[]>(`/parametros?idEmpresa=${empresa}`)
      if (generacionListado.current !== miGeneracion) return
      setItems(listado)
    } catch (e) {
      if (generacionListado.current !== miGeneracion) return
      if (trasGuardar) {
        setError('Se guardó, pero no se pudo actualizar el listado. Recargá la pantalla para verlo.')
      } else {
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los parámetros.')
      }
    } finally {
      if (generacionListado.current === miGeneracion) setCargando(false)
    }
  }, [])

  useEffect(() => {
    if (idEmpresa !== null) {
      void cargarListado(idEmpresa)
    }
  }, [idEmpresa, cargarListado])

  const conocidoSeleccionado = PARAMETROS_CONOCIDOS.find((p) => p.clave === clave)
  const esDeSucursal = conocidoSeleccionado?.alcance === 'sucursal'

  // Cambiar de clave resetea el valor tipeado: un `<select>` de zona horaria no puede arrancar
  // en '' (no matchea ninguna opción), un checkbox arranca en 'false' (mismo criterio: no puede
  // quedar en un estado sin mapeo), y un numérico arranca limpio para que el placeholder con el
  // default declarado sea visible. También suelta el nivel elegido (una sucursal no es una caja) y el valor probado.
  useEffect(() => {
    const conocido = PARAMETROS_CONOCIDOS.find((p) => p.clave === clave)
    if (conocido?.tipo === 'texto') setValorTexto(ZONAS_HORARIAS_OFRECIDAS[0].id)
    else if (conocido?.tipo === 'booleano') setValorTexto('false')
    else setValorTexto('')
    setIdPuntoVenta('')
    setIdSucursal('')
    generacionResolucion.current += 1
    setResuelto(null)
    setResolviendo(false)
  }, [clave])

  const puntosVentaDeLaEmpresa = puntosVenta.filter((p) => p.idEmpresa === idEmpresa)
  const sucursalesDeLaEmpresa = sucursales.filter((s) => s.idEmpresa === idEmpresa)

  function elegirEmpresa(id: number) {
    generacionResolucion.current += 1
    setResuelto(null)
    setResolviendo(false)
    setIdPuntoVenta('')
    setIdSucursal('')
    setIdEmpresa(id)
  }

  function elegirNivel(cambiar: () => void) {
    generacionResolucion.current += 1
    setResuelto(null)
    setResolviendo(false)
    cambiar()
  }

  async function establecer(evento: FormEvent) {
    evento.preventDefault()
    if (idEmpresa === null || guardandoRef.current) return

    const numeroDelValor = parsearNumeroDecimal(valorTexto, { permitirNegativo: true })
    const esNumerico = conocidoSeleccionado?.tipo !== 'texto' && conocidoSeleccionado?.tipo !== 'booleano'
    setError('')
    if (esNumerico && numeroDelValor === null) {
      setError('El valor tiene que ser un número (el separador decimal puede ser "." o ",").')
      return
    }

    guardandoRef.current = true
    setGuardando(true)

    let guardado = false
    try {
      const valor =
        conocidoSeleccionado?.tipo === 'texto'
          ? JSON.stringify(valorTexto)
          : conocidoSeleccionado?.tipo === 'booleano'
            ? JSON.stringify(valorTexto === 'true')
            : JSON.stringify(
                conocidoSeleccionado?.tipo === 'entero' ? Math.trunc(numeroDelValor ?? 0) : (numeroDelValor ?? 0),
              )

      const datos: ParametroAlta = {
        clave,
        valor,
        idPuntoVenta: esDeSucursal || idPuntoVenta === '' ? null : Number(idPuntoVenta),
        idSucursal: !esDeSucursal || idSucursal === '' ? null : Number(idSucursal),
      }

      await api.put(`/parametros?idEmpresa=${idEmpresa}`, datos)
      guardado = true
      toast.exito(`Se guardó "${clave}".`)
      setValorTexto(
        conocidoSeleccionado?.tipo === 'texto'
          ? ZONAS_HORARIAS_OFRECIDAS[0].id
          : conocidoSeleccionado?.tipo === 'booleano'
            ? 'false'
            : '',
      )
    } catch (e) {
      toast.error(e instanceof ErrorApi ? e.message : 'No se pudo guardar el parámetro.')
    }

    // La escritura ya está confirmada: un fallo al refrescar el listado no la reporta como fallida.
    if (guardado) {
      await cargarListado(idEmpresa, true)
    }

    guardandoRef.current = false
    setGuardando(false)
  }

  async function probarResolucion(evento: FormEvent) {
    evento.preventDefault()
    if (idEmpresa === null) return

    const miGeneracion = ++generacionResolucion.current
    setResolviendo(true)
    setError('')
    setResuelto(null)

    try {
      const nivel = esDeSucursal
        ? idSucursal === ''
          ? ''
          : `&idSucursal=${idSucursal}`
        : idPuntoVenta === ''
          ? ''
          : `&idPuntoVenta=${idPuntoVenta}`
      const respuesta = await api.get<ParametroResuelto>(`/parametros/${clave}?idEmpresa=${idEmpresa}${nivel}`)
      if (generacionResolucion.current !== miGeneracion) return
      setResuelto(respuesta)
    } catch (e) {
      if (generacionResolucion.current !== miGeneracion) return
      setError(e instanceof ErrorApi ? e.message : 'No se pudo resolver el parámetro.')
    } finally {
      if (generacionResolucion.current === miGeneracion) setResolviendo(false)
    }
  }

  function alcanceDeLaFila(p: ParametroListado): string {
    if (p.idSucursal !== null) {
      const sucursal = sucursales.find((s) => s.id === p.idSucursal)
      return sucursal ? `Sucursal ${sucursal.nombre}` : `Sucursal ${p.idSucursal}`
    }
    if (p.idPuntoVenta !== null) {
      const puntoVenta = puntosVenta.find((pv) => pv.id === p.idPuntoVenta)
      return puntoVenta ? puntoVenta.nombre : `Punto de venta ${p.idPuntoVenta}`
    }
    return 'Toda la empresa'
  }

  return (
    <div className="container-fluid py-4">
      <Box titulo="Parámetros operativos" variante="inverse">
        <p className="text-muted">
          Tolerancia de pago, adicional de recarga, tickets en espera, importe del envío. La zona horaria, el control de
          lotes y los días de alerta son de la sucursal y los comparten todas sus cajas; el resto se puede pisar por
          punto de venta. Un valor sin sucursal ni punto de venta es el default de la empresa.
        </p>

        {error && <div className="alert alert-danger">{error}</div>}

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
                  className="form-select"
                  value={idEmpresa ?? ''}
                  disabled={guardando}
                  onChange={(e) => elegirEmpresa(Number(e.target.value))}
                >
                  {empresas.map((e) => (
                    <option key={e.id} value={e.id}>
                      {e.razonSocial}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            <form className="row g-3 border p-3 mb-4 bg-body" onSubmit={establecer}>
              <div className="col-12">
                <strong>Crear o editar un parámetro</strong>
              </div>

              <div className="col-md-4">
                <label className="form-label" htmlFor="p-clave">
                  Clave
                </label>
                <select
                  id="p-clave"
                  className="form-select"
                  value={clave}
                  disabled={guardando}
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
                {conocidoSeleccionado?.tipo === 'texto' ? (
                  <select
                    id="p-valor"
                    className="form-select"
                    value={valorTexto}
                    disabled={guardando}
                    onChange={(e) => setValorTexto(e.target.value)}
                    required
                  >
                    {ZONAS_HORARIAS_OFRECIDAS.map((z) => (
                      <option key={z.id} value={z.id}>
                        {z.etiqueta}
                      </option>
                    ))}
                  </select>
                ) : conocidoSeleccionado?.tipo === 'booleano' ? (
                  <div className="form-check form-switch mt-2">
                    <input
                      id="p-valor"
                      type="checkbox"
                      className="form-check-input"
                      checked={valorTexto === 'true'}
                      disabled={guardando}
                      onChange={(e) => setValorTexto(e.target.checked ? 'true' : 'false')}
                    />
                    <span className="small text-muted ms-1">{valorTexto === 'true' ? 'Habilitado' : 'Deshabilitado'}</span>
                  </div>
                ) : (
                  <input
                    id="p-valor"
                    {...(conocidoSeleccionado?.tipo === 'entero'
                      ? atributosDeCampoEntero({ permitirNegativo: true })
                      : atributosDeCampoDecimal({ decimales: 2, permitirNegativo: true }))}
                    className="form-control"
                    placeholder={`Default: ${conocidoSeleccionado?.porDefecto}`}
                    value={valorTexto}
                    disabled={guardando}
                    onChange={(e) => setValorTexto(e.target.value)}
                    required
                  />
                )}
              </div>

              <div className="col-md-3">
                {esDeSucursal ? (
                  <>
                    <label className="form-label" htmlFor="p-sucursal">
                      Sucursal
                    </label>
                    <select
                      id="p-sucursal"
                      className="form-select"
                      value={idSucursal}
                      disabled={guardando}
                      onChange={(e) => elegirNivel(() => setIdSucursal(e.target.value))}
                    >
                      <option value="">Toda la empresa (default)</option>
                      {sucursalesDeLaEmpresa.map((s) => (
                        <option key={s.id} value={s.id}>
                          {s.nombre}
                        </option>
                      ))}
                    </select>
                  </>
                ) : (
                  <>
                    <label className="form-label" htmlFor="p-puntoventa">
                      Punto de venta
                    </label>
                    <select
                      id="p-puntoventa"
                      className="form-select"
                      value={idPuntoVenta}
                      disabled={guardando}
                      onChange={(e) => elegirNivel(() => setIdPuntoVenta(e.target.value))}
                    >
                      <option value="">Toda la empresa (default)</option>
                      {puntosVentaDeLaEmpresa.map((p) => (
                        <option key={p.id} value={p.id}>
                          {p.nombre}
                        </option>
                      ))}
                    </select>
                  </>
                )}
              </div>

              <div className="col-md-2 d-flex align-items-end gap-2">
                <button type="submit" className="btn btn-success" disabled={guardando}>
                  {guardando ? 'Guardando…' : 'Guardar'}
                </button>
                <button
                  type="button"
                  className="btn btn-outline-secondary"
                  onClick={probarResolucion}
                  disabled={resolviendo || guardando}
                >
                  {resolviendo ? 'Probando…' : 'Probar'}
                </button>
              </div>
            </form>

            {resuelto && (
              <div className="alert alert-info">
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
                        <td>{alcanceDeLaFila(p)}</td>
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
