import { useCallback, useEffect, useReducer, useRef, useState } from 'react'
import { Link } from 'react-router'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeCatalogo } from '../api/catalogos'
import { clienteDeClientes } from '../api/clientes'
import {
  aPagosDeVenta,
  calcularExcedente,
  calcularFaltante,
  filaPagoVacia,
  filasAPagosConVuelto,
  medioDisponibleParaCliente,
  validarPagosLocal,
  type FilaPago,
} from '../api/pagos'
import { aSolicitudDeFacturacionDeRemitos, clienteDeRemitos, filtrosDeRemitosVacios, reducirSeleccionDeRemitos, totalDeRemitosElegidos } from '../api/remitos'
import type { ClienteListado, MedioPagoAlta, MedioPagoListado, ParametroResuelto, PuntoVentaListado, RemitoListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

function etiquetaDeCliente(c: ClienteListado): string {
  const nombreCompleto = c.razonSocial ?? [c.nombre, c.apellido].filter(Boolean).join(' ')
  return `#${c.numero} — ${nombreCompleto}`
}

function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

function etiquetaDeCampoFila(prefijo: string, medioDeFila: MedioPagoListado | null, idFila: number): string {
  return `${prefijo} de ${medioDeFila?.nombre ?? 'medio de pago'} (fila ${idFila})`
}

/**
 * Consolidación de remitos (stage-17-presupuestos-y-remitos, Slice 8; design.md:419-421): elegí
 * cliente + punto de venta, listá sus remitos `emitido` sin ligar, multi-selección con el total
 * sumado, tomá pago(s) con las mismas filas del POS, y postea `POST /api/remitos/facturacion`.
 *
 * **Degradación pre-aprobada** (design.md, "Slices 7/8 overflow"): si este slice desborda, el
 * multi-select cae a de-a-uno — la API ya admite un array de un solo id, así que esta pantalla no
 * necesitaría ningún cambio de contrato para degradar, solo perdería el checkbox "elegir todos".
 * Registrado acá para que la próxima persona que la toque sepa que el corte ya está pre-aprobado y
 * no es una regresión si algún día se aplica.
 */
export function FacturarRemitos() {
  // ---- referencia (puntos de venta/clientes/medios) -----------------------------------------------
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [clientes, setClientes] = useState<ClienteListado[] | null>(null)
  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [errorReferencia, setErrorReferencia] = useState('')

  useEffect(() => {
    let vigente = true
    api
      .get<PuntoVentaListado[]>('/puntos-venta')
      .then((lista) => vigente && setPuntosVenta(lista))
      .catch((e) => {
        setPuntosVenta([])
        setErrorReferencia((prev) => (prev ? prev : e instanceof ErrorApi ? e.message : 'No se pudieron cargar los puntos de venta.'))
      })

    clienteDeClientes
      .listar('', false)
      .then((p) => vigente && setClientes(p.items))
      .catch((e) => {
        setClientes([])
        setErrorReferencia((prev) => (prev ? prev : e instanceof ErrorApi ? e.message : 'No se pudieron cargar los clientes.'))
      })

    clienteMediosPago
      .listar(false)
      .then((lista) => vigente && setMedios(lista))
      .catch((e) => {
        setMedios([])
        setErrorReferencia((prev) => (prev ? prev : e instanceof ErrorApi ? e.message : 'No se pudieron cargar los medios de pago.'))
      })

    return () => {
      vigente = false
    }
  }, [])

  const referenciaOk = puntosVenta !== null && clientes !== null && medios !== null && errorReferencia === ''
  const medioPorId: Record<number, MedioPagoListado> = {}
  for (const m of medios ?? []) medioPorId[m.id] = m

  // ---- picker cliente + punto de venta ------------------------------------------------------------
  const [idPuntoVenta, setIdPuntoVenta] = useState<number | ''>('')
  const [idCliente, setIdCliente] = useState<number | ''>('')
  const clienteElegido = clientes?.find((c) => c.id === idCliente) ?? null
  const puntoVentaElegido = puntosVenta?.find((pv) => pv.id === idPuntoVenta) ?? null

  // ---- listado de remitos `emitido` sin ligar del par cliente/PV elegido ---------------------------
  const [remitos, setRemitos] = useState<RemitoListado[] | null>(null)
  const [cargandoRemitos, setCargandoRemitos] = useState(false)
  const [errorRemitos, setErrorRemitos] = useState('')
  const generacionRemitosRef = useRef(0)

  const cargarRemitos = useCallback(() => {
    if (idPuntoVenta === '' || idCliente === '') {
      setRemitos(null)
      return
    }

    const miGeneracion = (generacionRemitosRef.current += 1)
    setCargandoRemitos(true)
    setErrorRemitos('')

    clienteDeRemitos
      .listar({ ...filtrosDeRemitosVacios(), idPuntoVenta, idCliente, estado: 'Emitido', tamanio: 200 })
      .then((pagina) => {
        if (generacionRemitosRef.current !== miGeneracion) return
        setRemitos(pagina.items)
      })
      .catch((e) => {
        if (generacionRemitosRef.current !== miGeneracion) return
        setRemitos(null)
        setErrorRemitos(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los remitos.')
      })
      .finally(() => {
        if (generacionRemitosRef.current !== miGeneracion) return
        setCargandoRemitos(false)
      })
  }, [idPuntoVenta, idCliente])

  useEffect(() => {
    cargarRemitos()
  }, [cargarRemitos])

  // ---- multi-select (task 8.8: reducer puro, `reducirSeleccionDeRemitos`) --------------------------
  const [seleccionados, dispatchSeleccion] = useReducer(reducirSeleccionDeRemitos, [] as number[])

  // Un cambio de cliente/PV invalida cualquier selección previa — un remito de OTRO par ya no
  // aparece en la lista, así que dejarlo "elegido" mandaría un id fantasma al servidor.
  useEffect(() => {
    dispatchSeleccion({ tipo: 'limpiar' })
  }, [idPuntoVenta, idCliente])

  const remitosElegidos = (remitos ?? []).filter((r) => seleccionados.includes(r.id))
  const total = totalDeRemitosElegidos(remitosElegidos)
  const todosElegidos = (remitos ?? []).length > 0 && seleccionados.length === (remitos ?? []).length

  // ---- parámetros de pago (tolerancia/vuelto), por punto de venta — mismo criterio que Pos.tsx ----
  const [parametros, setParametros] = useState<{ toleranciaPago: number; vueltoMaximo: number } | null>(null)
  const [errorParametros, setErrorParametros] = useState('')
  const generacionParametrosRef = useRef(0)

  useEffect(() => {
    if (!puntoVentaElegido) {
      setParametros(null)
      setErrorParametros('')
      return
    }

    const generacion = (generacionParametrosRef.current += 1)
    let vigente = true

    Promise.all([
      api.get<ParametroResuelto>(`/parametros/tolerancia_pago?idEmpresa=${puntoVentaElegido.idEmpresa}&idPuntoVenta=${puntoVentaElegido.id}`),
      api.get<ParametroResuelto>(`/parametros/vuelto_maximo?idEmpresa=${puntoVentaElegido.idEmpresa}&idPuntoVenta=${puntoVentaElegido.id}`),
    ])
      .then(([tolerancia, vuelto]) => {
        if (!vigente || generacionParametrosRef.current !== generacion) return
        setParametros({ toleranciaPago: Number(tolerancia.valor), vueltoMaximo: Number(vuelto.valor) })
        setErrorParametros('')
      })
      .catch((e) => {
        if (!vigente || generacionParametrosRef.current !== generacion) return
        setParametros(null)
        setErrorParametros(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los parámetros de pago. No se puede facturar.')
      })

    return () => {
      vigente = false
    }
  }, [puntoVentaElegido])

  // ---- panel de pagos — mismas filas/mappers puros del POS ------------------------------------------
  const proximaFilaPagoIdRef = useRef(1)
  const [filasPago, setFilasPago] = useState<FilaPago[]>(() => [filaPagoVacia(proximaFilaPagoIdRef.current++)])
  const [observaciones, setObservaciones] = useState('')

  const [facturando, setFacturando] = useState(false)
  const facturandoRef = useRef(false)
  const [errorFacturar, setErrorFacturar] = useState('')
  const [facturado, setFacturado] = useState<{ numeroVisible: string; total: number } | null>(null)

  const ocupado = facturando

  function agregarFilaPago() {
    if (ocupado) return
    const id = proximaFilaPagoIdRef.current++
    setFilasPago((prev) => [...prev, filaPagoVacia(id)])
  }

  function quitarFilaPago(id: number) {
    if (ocupado) return
    setFilasPago((prev) => prev.filter((f) => f.id !== id))
  }

  function cambiarMedioDeFila(id: number, idMedioPago: number | '') {
    if (ocupado) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, idMedioPago, vueltoManual: '' } : f)))
  }

  function cambiarImporteDeFila(id: number, importe: string) {
    if (ocupado) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, importe } : f)))
  }

  function cambiarReferenciaDeFila(id: number, referencia: string) {
    if (ocupado) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, referencia } : f)))
  }

  function cambiarVueltoDeFila(id: number, vueltoManual: string) {
    if (ocupado) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, vueltoManual } : f)))
  }

  const pagosConVuelto = filasAPagosConVuelto(filasPago, medioPorId, total)
  const faltante = calcularFaltante(total, pagosConVuelto)
  const excedente = calcularExcedente(total, pagosConVuelto)

  const rechazoLocal =
    seleccionados.length === 0 || !clienteElegido || !puntoVentaElegido || !parametros
      ? null
      : validarPagosLocal({
          total,
          pagos: pagosConVuelto,
          toleranciaPago: parametros.toleranciaPago,
          vueltoMaximo: parametros.vueltoMaximo,
          esConsumidorFinal: clienteElegido.esConsumidorFinal,
          saldoCliente: clienteElegido.saldo,
          limiteCredito: clienteElegido.limiteCredito,
          creditoIlimitado: clienteElegido.creditoIlimitado,
        })

  const puedeFacturar =
    referenciaOk && seleccionados.length > 0 && !!clienteElegido && !!puntoVentaElegido && !!parametros && rechazoLocal === null && !ocupado

  async function facturar() {
    // react-async-state regla 9: guard de reentrancia de primera línea + bloqueo de ventana
    // completa (regla 5) — un doble click no puede emitir dos TXR ni mover CC dos veces.
    if (facturandoRef.current) return
    if (!puedeFacturar || idPuntoVenta === '') return

    facturandoRef.current = true
    setFacturando(true)
    setErrorFacturar('')

    try {
      const solicitud = aSolicitudDeFacturacionDeRemitos(idPuntoVenta, seleccionados, aPagosDeVenta(pagosConVuelto), observaciones)
      const comprobante = await clienteDeRemitos.facturar(solicitud)
      facturandoRef.current = false
      setFacturando(false)
      setFacturado({ numeroVisible: comprobante.numeroVisible, total: comprobante.total })
      dispatchSeleccion({ tipo: 'limpiar' })
      // regla 6: el refetch posterior vive aislado de este try/catch — un 201 confirmado nunca se
      // reporta como fallo aunque el refresco de la lista falle.
      cargarRemitos()
    } catch (e) {
      facturandoRef.current = false
      setFacturando(false)
      setErrorFacturar(e instanceof ErrorApi ? e.message : 'No se pudo facturar los remitos seleccionados.')
    }
  }

  function nuevaConsolidacion() {
    if (ocupado) return
    setFacturado(null)
    setFilasPago([filaPagoVacia(proximaFilaPagoIdRef.current++)])
    setObservaciones('')
  }

  return (
    <div className="container-fluid py-4">
      <Box
        titulo="Facturar remitos"
        variante="inverse"
        herramientas={
          <Link className="btn btn-sm btn-outline-light rounded-0" to="/remitos">
            Volver a remitos
          </Link>
        }
      >
        {errorReferencia && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorReferencia}</div>}

        {facturado ? (
          <div className="alert alert-success rounded-0">
            <p className="mb-2">
              Comprobante <strong>{facturado.numeroVisible}</strong> emitido por {formatearMoneda(facturado.total)}.
            </p>
            <button type="button" className="btn btn-primary btn-sm rounded-0" onClick={nuevaConsolidacion}>
              Facturar otro grupo
            </button>
          </div>
        ) : (
          <>
            <div className="row g-2 mb-3">
              <div className="col-md-4">
                <label className="form-label" htmlFor="facturar-punto-venta">
                  Punto de venta
                </label>
                <select
                  id="facturar-punto-venta"
                  className="form-select rounded-0"
                  value={idPuntoVenta}
                  disabled={!referenciaOk || ocupado}
                  onChange={(e) => setIdPuntoVenta(e.target.value === '' ? '' : Number(e.target.value))}
                >
                  <option value="">Elegir…</option>
                  {(puntosVenta ?? []).map((pv) => (
                    <option key={pv.id} value={pv.id}>
                      {pv.nombre}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-4">
                <label className="form-label" htmlFor="facturar-cliente">
                  Cliente
                </label>
                <select
                  id="facturar-cliente"
                  className="form-select rounded-0"
                  value={idCliente}
                  disabled={!referenciaOk || ocupado}
                  onChange={(e) => setIdCliente(e.target.value === '' ? '' : Number(e.target.value))}
                >
                  <option value="">Elegir…</option>
                  {(clientes ?? []).map((c) => (
                    <option key={c.id} value={c.id}>
                      {etiquetaDeCliente(c)}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            {idPuntoVenta === '' || idCliente === '' ? (
              <p className="text-muted">Elegí un punto de venta y un cliente para ver sus remitos emitidos sin facturar.</p>
            ) : (
              <>
                {cargandoRemitos && <Cargando />}
                {errorRemitos && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorRemitos}</div>}

                {!cargandoRemitos && remitos && (
                  <>
                    <div className="table-responsive mb-3">
                      <table className="table table-sm table-bordered align-middle">
                        <thead>
                          <tr>
                            <th style={{ width: 40 }}>
                              <input
                                type="checkbox"
                                aria-label="Elegir todos"
                                checked={todosElegidos}
                                disabled={ocupado || remitos.length === 0}
                                onChange={(e) =>
                                  dispatchSeleccion(
                                    e.target.checked ? { tipo: 'elegirTodos', ids: remitos.map((r) => r.id) } : { tipo: 'limpiar' },
                                  )
                                }
                              />
                            </th>
                            <th>Número</th>
                            <th>Fecha de emisión</th>
                            <th className="text-end">Total</th>
                          </tr>
                        </thead>
                        <tbody>
                          {remitos.map((r) => (
                            <tr key={r.id}>
                              <td>
                                <input
                                  type="checkbox"
                                  aria-label={`Elegir remito ${r.numeroFormateado ?? `#${r.id}`}`}
                                  checked={seleccionados.includes(r.id)}
                                  disabled={ocupado}
                                  onChange={() => dispatchSeleccion({ tipo: 'alternar', id: r.id })}
                                />
                              </td>
                              <td>{r.numeroFormateado ?? `#${r.id}`}</td>
                              <td>{new Date(r.fechaEmision).toLocaleDateString('es-AR')}</td>
                              <td className="text-end">{formatearMoneda(r.total)}</td>
                            </tr>
                          ))}
                          {remitos.length === 0 && (
                            <tr>
                              <td colSpan={4} className="text-center text-muted py-3">
                                Este cliente no tiene remitos emitidos sin facturar en este punto de venta.
                              </td>
                            </tr>
                          )}
                        </tbody>
                      </table>
                    </div>

                    {remitos.length > 0 && (
                      <>
                        <div className="d-flex justify-content-between mb-3">
                          <strong>Total a facturar ({seleccionados.length} remito(s))</strong>
                          <strong>{formatearMoneda(total)}</strong>
                        </div>

                        <div className="mb-3">
                          <label className="form-label" htmlFor="facturar-observaciones">
                            Observaciones
                          </label>
                          <input
                            id="facturar-observaciones"
                            type="text"
                            className="form-control rounded-0"
                            value={observaciones}
                            disabled={ocupado}
                            onChange={(e) => setObservaciones(e.target.value)}
                          />
                        </div>

                        {errorParametros && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorParametros}</div>}
                        {errorFacturar && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorFacturar}</div>}

                        <h6>Pagos</h6>
                        {filasPago.map((fila) => {
                          const medioDeFila = fila.idMedioPago === '' ? null : (medioPorId[fila.idMedioPago] ?? null)
                          const pagoDeFila = pagosConVuelto.find((p) => p.idFila === fila.id) ?? null
                          const vueltoMostrado = fila.vueltoManual !== '' ? fila.vueltoManual : String(pagoDeFila?.vuelto ?? 0)

                          return (
                            <div className="row g-2 mb-2 align-items-center" key={fila.id}>
                              <div className="col-4">
                                <select
                                  className="form-select form-select-sm rounded-0"
                                  aria-label="Medio de pago"
                                  value={fila.idMedioPago}
                                  disabled={ocupado || medios === null}
                                  onChange={(e) => cambiarMedioDeFila(fila.id, e.target.value === '' ? '' : Number(e.target.value))}
                                >
                                  <option value="">Elegir medio…</option>
                                  {(medios ?? [])
                                    .filter((m) => medioDisponibleParaCliente(m, clienteElegido?.esConsumidorFinal ?? false))
                                    .map((m) => (
                                      <option key={m.id} value={m.id}>
                                        {m.nombre}
                                      </option>
                                    ))}
                                </select>
                              </div>
                              <div className="col-3">
                                <input
                                  type="number"
                                  step="0.01"
                                  min="0"
                                  className="form-control form-control-sm rounded-0"
                                  aria-label={etiquetaDeCampoFila('Importe', medioDeFila, fila.id)}
                                  value={fila.importe}
                                  disabled={ocupado}
                                  onChange={(e) => cambiarImporteDeFila(fila.id, e.target.value)}
                                />
                              </div>
                              <div className="col-3">
                                <input
                                  type="text"
                                  className="form-control form-control-sm rounded-0"
                                  aria-label={etiquetaDeCampoFila('Referencia', medioDeFila, fila.id)}
                                  placeholder={medioDeFila?.requiereReferencia ? 'Referencia (requerida)' : 'Referencia'}
                                  value={fila.referencia}
                                  disabled={ocupado || !medioDeFila?.requiereReferencia}
                                  onChange={(e) => cambiarReferenciaDeFila(fila.id, e.target.value)}
                                />
                              </div>
                              <div className="col-2 d-flex align-items-center gap-1">
                                <input
                                  type="number"
                                  step="0.01"
                                  min="0"
                                  className="form-control form-control-sm rounded-0"
                                  aria-label={etiquetaDeCampoFila('Vuelto', medioDeFila, fila.id)}
                                  value={vueltoMostrado}
                                  disabled={ocupado || !medioDeFila?.admiteVuelto}
                                  onChange={(e) => cambiarVueltoDeFila(fila.id, e.target.value)}
                                />
                                {filasPago.length > 1 && (
                                  <button
                                    type="button"
                                    className="btn btn-sm btn-outline-danger rounded-0"
                                    disabled={ocupado}
                                    aria-label="Quitar medio de pago"
                                    onClick={() => quitarFilaPago(fila.id)}
                                  >
                                    ×
                                  </button>
                                )}
                              </div>
                            </div>
                          )
                        })}

                        <button type="button" className="btn btn-outline-secondary btn-sm rounded-0 mb-3" disabled={ocupado} onClick={agregarFilaPago}>
                          + Agregar medio de pago
                        </button>

                        <div className="d-flex justify-content-between small">
                          <span>Falta</span>
                          <span>{formatearMoneda(faltante)}</span>
                        </div>
                        <div className="d-flex justify-content-between small mb-2">
                          <span>Vuelto</span>
                          <span>{formatearMoneda(excedente)}</span>
                        </div>

                        {rechazoLocal && <div className="alert alert-warning rounded-0 py-1 px-2 small">{rechazoLocal.mensaje}</div>}

                        <button type="button" className="btn btn-primary rounded-0" disabled={!puedeFacturar} onClick={facturar}>
                          {facturando ? 'Facturando…' : 'Facturar'}
                        </button>
                      </>
                    )}
                  </>
                )}
              </>
            )}
          </>
        )}
      </Box>
    </div>
  )
}
