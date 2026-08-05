import { useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router'
import { aSolicitudDeMovimiento, clienteDeCaja, importeValidoParaTipo, motivoValido } from '../api/caja'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import {
  CATEGORIAS_GASTO,
  TIPOS_MOVIMIENTO_CAJA,
  type CategoriaGasto,
  type MedioPagoAlta,
  type MedioPagoListado,
  type PuntoVentaListado,
  type ResumenDeTurno,
  type TipoMovimientoCaja,
  type TurnoResumen,
} from '../api/tipos'
import { Box } from '../componentes/Box'

const CLAVE_PUNTO_VENTA = 'ways.caja.idPuntoVenta'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

function leerPuntoVentaGuardado(): number | null {
  try {
    const crudo = localStorage.getItem(CLAVE_PUNTO_VENTA)
    return crudo ? Number(crudo) : null
  } catch {
    return null
  }
}

function guardarPuntoVentaSeleccionado(id: number) {
  try {
    localStorage.setItem(CLAVE_PUNTO_VENTA, String(id))
  } catch {
    // localStorage puede no estar disponible (modo privado del navegador) — la selección
    // simplemente no persiste entre sesiones, el resto de la pantalla sigue funcionando.
  }
}

function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

function etiquetaCategoriaGasto(categoria: CategoriaGasto): string {
  return CATEGORIAS_GASTO.find((c) => c.valor === categoria)?.etiqueta ?? categoria
}

type PropsFormularioApertura = {
  idPuntoVenta: number
  onAbierto: (turno: TurnoResumen) => void
  onEscribiendoCambio: (valor: boolean) => void
}

/** Apertura de turno (spec: turnos-de-caja / Apertura Creates An Open Turno With Its Fondo):
 * fondo inicial + observaciones opcionales, `idPuntoVenta` siempre lo trae la selección de
 * arriba, nunca un campo editable acá. */
function FormularioApertura({ idPuntoVenta, onAbierto, onEscribiendoCambio }: PropsFormularioApertura) {
  const [fondoInicial, setFondoInicial] = useState('')
  const [observaciones, setObservaciones] = useState('')
  const [abriendo, setAbriendo] = useState(false)
  const abriendoRef = useRef(false)
  const [error, setError] = useState('')

  async function abrir() {
    // regla 9 (react-async-state): guard de reentrancia de primera línea — un doble click en el
    // mismo tick le gana al re-render que deshabilita el botón.
    if (abriendoRef.current) return

    const fondo = Number(fondoInicial)
    if (fondoInicial.trim() === '' || !Number.isFinite(fondo) || fondo < 0) {
      setError('El fondo inicial tiene que ser un número mayor o igual a 0.')
      return
    }

    abriendoRef.current = true
    setAbriendo(true)
    onEscribiendoCambio(true)
    setError('')

    try {
      const turno = await clienteDeCaja.abrir({
        idPuntoVenta,
        fondoInicial: fondo,
        observaciones: observaciones.trim() === '' ? null : observaciones.trim(),
      })
      onAbierto(turno)
    } catch (e) {
      if (e instanceof ErrorApi && e.codigo === 'turno_ya_abierto') {
        // Autocuración (judgment-day slice-6, judge B): otra pestaña/cajero ganó la carrera de
        // apertura entre que esta pestaña cargó el formulario y el click. En vez de dejar un
        // error + formulario obsoleto (el turno YA está abierto, reintentar solo repetiría el
        // mismo 409), se vuelve a consultar el turno abierto real y se muestra ese panel.
        try {
          const turnoReal = await clienteDeCaja.obtenerAbierto(idPuntoVenta)
          if (turnoReal) {
            onAbierto(turnoReal)
          } else {
            setError('El turno ya está abierto, pero no se pudo confirmar cuál — actualizá la página.')
          }
        } catch {
          setError('El turno ya está abierto, pero no se pudo confirmar cuál — actualizá la página.')
        }
      } else {
        setError(e instanceof ErrorApi ? e.message : 'No se pudo abrir el turno.')
      }
    } finally {
      abriendoRef.current = false
      setAbriendo(false)
      onEscribiendoCambio(false)
    }
  }

  const bloqueado = abriendo

  return (
    <div>
      <p className="text-muted">No hay un turno abierto en este punto de venta.</p>
      {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}

      <div className="row g-2 align-items-end" style={{ maxWidth: 640 }}>
        <div className="col-md-4">
          <label className="form-label" htmlFor="caja-fondo-inicial">
            Fondo inicial
          </label>
          <input
            id="caja-fondo-inicial"
            type="number"
            step="0.01"
            min="0"
            className="form-control rounded-0"
            value={fondoInicial}
            disabled={bloqueado}
            onChange={(e) => setFondoInicial(e.target.value)}
          />
        </div>
        <div className="col-md-5">
          <label className="form-label" htmlFor="caja-observaciones-apertura">
            Observaciones
          </label>
          <input
            id="caja-observaciones-apertura"
            type="text"
            className="form-control rounded-0"
            value={observaciones}
            disabled={bloqueado}
            onChange={(e) => setObservaciones(e.target.value)}
          />
        </div>
        <div className="col-md-3">
          <button type="button" className="btn btn-primary rounded-0 w-100" disabled={bloqueado} onClick={abrir}>
            {abriendo ? 'Abriendo…' : 'Abrir turno'}
          </button>
        </div>
      </div>
    </div>
  )
}

type PropsPanelTurnoAbierto = {
  turno: TurnoResumen
  medios: MedioPagoListado[] | null
  errorMedios: string
  onEscribiendoCambio: (valor: boolean) => void
}

/** Turno abierto: movimientos (retiro/refuerzo/apertura de cajón) + resumen parcial — misma
 * derivación que el cierre (spec: arqueo-de-cierre / Resumen Parcial Uses The Same Derivation As
 * Cierre). El componente entero se remonta por `key={turno.id}` en el padre (regla 8): ningún
 * estado de acá (formulario de movimiento, resumen, generación) sobrevive a un cambio de turno. */
function PanelTurnoAbierto({ turno, medios, errorMedios, onEscribiendoCambio }: PropsPanelTurnoAbierto) {
  const [resumen, setResumen] = useState<ResumenDeTurno | null>(null)
  const [cargandoResumen, setCargandoResumen] = useState(false)
  const [errorResumen, setErrorResumen] = useState('')
  // regla 2: gatea CADA respuesta de resumen (la carga inicial y cada refetch posterior a un
  // movimiento) — una respuesta desactualizada nunca puede pisar la más reciente.
  const generacionResumenRef = useRef(0)

  const [tipoMovimiento, setTipoMovimiento] = useState<TipoMovimientoCaja>('Retiro')
  const [importeMovimiento, setImporteMovimiento] = useState('')
  const [motivoMovimiento, setMotivoMovimiento] = useState('')
  const [registrando, setRegistrando] = useState(false)
  const registrandoRef = useRef(false)
  const [errorMovimiento, setErrorMovimiento] = useState('')

  const medioPorId = useMemo(() => {
    const indice: Record<number, MedioPagoListado> = {}
    for (const m of medios ?? []) indice[m.id] = m
    return indice
  }, [medios])

  function cargarResumen() {
    const miGeneracion = (generacionResumenRef.current += 1)
    setCargandoResumen(true)
    setErrorResumen('')

    clienteDeCaja
      .obtenerResumen(turno.id)
      .then((datos) => {
        if (generacionResumenRef.current !== miGeneracion) return
        setResumen(datos)
      })
      .catch((e) => {
        if (generacionResumenRef.current !== miGeneracion) return
        setResumen(null)
        setErrorResumen(e instanceof ErrorApi ? e.message : 'No se pudo cargar el resumen del turno.')
      })
      .finally(() => {
        if (generacionResumenRef.current !== miGeneracion) return
        setCargandoResumen(false)
      })
  }

  useEffect(() => {
    cargarResumen()
    // Corre una única vez por montaje: `turno.id` es estable durante toda la vida de esta
    // instancia (el padre remonta el componente entero cuando cambia, regla 8).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [turno.id])

  async function registrarMovimiento() {
    // regla 9: guard de reentrancia de primera línea.
    if (registrandoRef.current) return

    const importeCandidato = tipoMovimiento === 'AperturaCajon' ? 0 : Number(importeMovimiento)

    if (!motivoValido(motivoMovimiento)) {
      setErrorMovimiento('El motivo tiene que tener al menos 5 caracteres.')
      return
    }
    if (!importeValidoParaTipo(tipoMovimiento, importeCandidato)) {
      setErrorMovimiento(
        tipoMovimiento === 'AperturaCajon'
          ? 'La apertura de cajón no lleva importe: siempre viaja en 0.'
          : 'El importe tiene que ser mayor a 0.',
      )
      return
    }

    registrandoRef.current = true
    setRegistrando(true)
    onEscribiendoCambio(true)
    setErrorMovimiento('')

    // regla 3: bumpear la generación del resumen ANTES de la escritura — cualquier resumen en
    // vuelo desde antes de este movimiento queda obsoleto aunque su respuesta llegue después de
    // que este movimiento se registre.
    generacionResumenRef.current += 1

    try {
      await clienteDeCaja.registrarMovimiento(
        turno.id,
        aSolicitudDeMovimiento(tipoMovimiento, importeMovimiento, motivoMovimiento),
      )
      setImporteMovimiento('')
      setMotivoMovimiento('')
    } catch (e) {
      setErrorMovimiento(e instanceof ErrorApi ? e.message : 'No se pudo registrar el movimiento.')
    } finally {
      registrandoRef.current = false
      setRegistrando(false)
      onEscribiendoCambio(false)
    }

    // regla 6: el refetch del resumen queda aislado del try/catch de la escritura — corre tanto en
    // éxito como en falla, porque el bump de generación previo a la escritura deja huérfano
    // cualquier resumen en vuelo y solo un nuevo pedido lo cierra (si no, "Calculando…" queda
    // colgado para siempre). Un error acá no debe pisar `errorMovimiento`.
    cargarResumen()
  }

  return (
    <div>
      <div className="row g-3 mb-3 align-items-end">
        <div className="col-md-3">
          <div className="small text-muted">Estado</div>
          <div>
            <span className="badge bg-success">Turno abierto</span>
          </div>
        </div>
        <div className="col-md-3">
          <div className="small text-muted">Apertura</div>
          <div>{formatearFechaHora(turno.fechaApertura)}</div>
        </div>
        <div className="col-md-3">
          <div className="small text-muted">Fondo inicial</div>
          <div>{formatearMoneda(turno.fondoInicial)}</div>
        </div>
        <div className="col-md-3 text-md-end">
          {/* stage-6-turnos-caja (Slice 7, design: Web Composition): entrada a la pantalla de
              cierre — el turno lo identifica la URL, nunca un selector propio de esa pantalla. */}
          <Link
            className={`btn btn-outline-danger btn-sm rounded-0${registrando ? ' disabled' : ''}`}
            aria-disabled={registrando}
            to={`/caja/cierre?idTurno=${turno.id}`}
            onClick={(e) => {
              if (registrando) e.preventDefault()
            }}
          >
            Cerrar turno
          </Link>
        </div>
      </div>

      {turno.observaciones && <p className="text-muted small">{turno.observaciones}</p>}

      <div className="row g-3">
        <div className="col-lg-6">
          <h6>Movimiento de caja</h6>
          {errorMovimiento && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorMovimiento}</div>}

          <div className="mb-2">
            <label className="form-label" htmlFor="caja-tipo-movimiento">
              Tipo de movimiento
            </label>
            <select
              id="caja-tipo-movimiento"
              className="form-select rounded-0"
              value={tipoMovimiento}
              disabled={registrando}
              onChange={(e) => {
                setTipoMovimiento(e.target.value as TipoMovimientoCaja)
                setErrorMovimiento('')
              }}
            >
              {TIPOS_MOVIMIENTO_CAJA.map((t) => (
                <option key={t.valor} value={t.valor}>
                  {t.etiqueta}
                </option>
              ))}
            </select>
          </div>

          <div className="mb-2">
            <label className="form-label" htmlFor="caja-importe-movimiento">
              Importe
            </label>
            <input
              id="caja-importe-movimiento"
              type="number"
              step="0.01"
              min="0"
              className="form-control rounded-0"
              value={tipoMovimiento === 'AperturaCajon' ? '0' : importeMovimiento}
              disabled={registrando || tipoMovimiento === 'AperturaCajon'}
              onChange={(e) => setImporteMovimiento(e.target.value)}
            />
          </div>

          <div className="mb-2">
            <label className="form-label" htmlFor="caja-motivo-movimiento">
              Motivo
            </label>
            <input
              id="caja-motivo-movimiento"
              type="text"
              className="form-control rounded-0"
              value={motivoMovimiento}
              disabled={registrando}
              onChange={(e) => setMotivoMovimiento(e.target.value)}
            />
            <div className="form-text">Mínimo 5 caracteres.</div>
          </div>

          <button
            type="button"
            className="btn btn-primary rounded-0"
            disabled={registrando}
            onClick={registrarMovimiento}
          >
            {registrando ? 'Registrando…' : 'Registrar movimiento'}
          </button>
        </div>

        <div className="col-lg-6">
          <h6>Resumen parcial</h6>
          {errorMedios && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorMedios}</div>}
          {errorResumen && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorResumen}</div>}
          {cargandoResumen && <p className="text-muted">Calculando…</p>}

          {!cargandoResumen && resumen && (
            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Medio</th>
                    <th className="text-end">Esperado</th>
                  </tr>
                </thead>
                <tbody>
                  {resumen.medios.map((m) => (
                    <tr key={m.idMedioPago}>
                      <td>
                        {medioPorId[m.idMedioPago]?.nombre ?? `Medio #${m.idMedioPago}`}
                        {m.idMedioPago === resumen.idMedioAncla && (
                          <span className="badge bg-secondary ms-1">efectivo</span>
                        )}
                      </td>
                      <td className="text-end">{formatearMoneda(m.importeEsperado)}</td>
                    </tr>
                  ))}
                  {resumen.medios.length === 0 && (
                    <tr>
                      <td colSpan={2} className="text-center text-muted">
                        Todavía no hay actividad en este turno.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>

      {/* follow-up "Resumen parcial D6-content enrichment" (legacy doc 01 D6 "Ver Parcial"):
          tickets, ingresos por área y egresos por categoría/área + retiros — contenido de reporte
          aditivo, nunca alimenta la derivación del arqueo de arriba. */}
      {!cargandoResumen && resumen && (
        <div className="row g-3 mt-1">
          <div className="col-lg-4">
            <h6>Tickets</h6>
            <div className="small text-muted">Cantidad</div>
            <div className="mb-2">{resumen.cantidadTickets}</div>
            <div className="small text-muted">Primer ticket</div>
            <div className="mb-2">
              {resumen.primerTicket
                ? `#${resumen.primerTicket.numero} · ${formatearFechaHora(resumen.primerTicket.fecha)}`
                : '—'}
            </div>
            <div className="small text-muted">Último ticket</div>
            <div>
              {resumen.ultimoTicket
                ? `#${resumen.ultimoTicket.numero} · ${formatearFechaHora(resumen.ultimoTicket.fecha)}`
                : '—'}
            </div>
          </div>

          <div className="col-lg-4">
            <h6>Ingresos por área</h6>
            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Área</th>
                    <th className="text-end">Total</th>
                  </tr>
                </thead>
                <tbody>
                  {resumen.ingresosPorArea.map((a) => (
                    <tr key={a.idArea}>
                      <td>{a.nombreArea}</td>
                      <td className="text-end">{formatearMoneda(a.total)}</td>
                    </tr>
                  ))}
                  {resumen.ingresosPorArea.length === 0 && (
                    <tr>
                      <td colSpan={2} className="text-center text-muted">
                        Todavía no hay ingresos en este turno.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </div>

          <div className="col-lg-4">
            <h6>Egresos</h6>
            <div className="row g-2">
              <div className="col-6">
                <div className="small text-muted mb-1">Por categoría</div>
                <div className="table-responsive">
                  <table className="table table-sm table-striped table-bordered align-middle">
                    <thead>
                      <tr>
                        <th>Categoría</th>
                        <th className="text-end">Total</th>
                      </tr>
                    </thead>
                    <tbody>
                      {resumen.egresos.porCategoria.length === 0 && resumen.egresos.retiros === 0 ? (
                        <tr>
                          <td colSpan={2} className="text-center text-muted">
                            Todavía no hay egresos en este turno.
                          </td>
                        </tr>
                      ) : (
                        <>
                          {resumen.egresos.porCategoria.map((e) => (
                            <tr key={e.categoria}>
                              <td>{etiquetaCategoriaGasto(e.categoria)}</td>
                              <td className="text-end">{formatearMoneda(e.total)}</td>
                            </tr>
                          ))}
                          <tr>
                            <td>Retiros</td>
                            <td className="text-end">{formatearMoneda(resumen.egresos.retiros)}</td>
                          </tr>
                        </>
                      )}
                    </tbody>
                  </table>
                </div>
              </div>

              <div className="col-6">
                <div className="small text-muted mb-1">Por área</div>
                <div className="table-responsive">
                  <table className="table table-sm table-striped table-bordered align-middle">
                    <thead>
                      <tr>
                        <th>Área</th>
                        <th className="text-end">Total</th>
                      </tr>
                    </thead>
                    <tbody>
                      {resumen.egresos.porArea.map((a) => (
                        <tr key={a.idArea ?? 'sin-area'}>
                          <td>{a.nombreArea}</td>
                          <td className="text-end">{formatearMoneda(a.total)}</td>
                        </tr>
                      ))}
                      {resumen.egresos.porArea.length === 0 && (
                        <tr>
                          <td colSpan={2} className="text-center text-muted">
                            Todavía no hay egresos en este turno.
                          </td>
                        </tr>
                      )}
                    </tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

/**
 * Pantalla de caja (stage-6-turnos-caja, Slice 6, design: Web Composition): estado del turno del
 * punto de venta seleccionado, apertura cuando no hay uno abierto, movimientos físicos fuera de
 * la venta y el resumen parcial en vivo — misma derivación que el cierre (Slice 7). Precedente de
 * forma: `Pos.tsx`. El resumen (`ServicioDeResumenDeTurno`) también expone el contenido D6
 * (cantidad de tickets, primer/último ticket, ingresos por área y egresos por categoría/área +
 * retiros; follow-up "Resumen parcial D6-content enrichment") — ver el doc-comment de
 * `ResumenDeTurno` en `api/tipos.ts`.
 */
export function Caja() {
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [idPuntoVenta, setIdPuntoVenta] = useState<number | ''>('')
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [errorMedios, setErrorMedios] = useState('')

  const [turno, setTurno] = useState<TurnoResumen | null>(null)
  const [cargandoTurno, setCargandoTurno] = useState(false)
  const [errorTurno, setErrorTurno] = useState('')
  const generacionTurnoRef = useRef(0)

  // regla 9: mientras la apertura o un movimiento tienen una escritura en vuelo, el selector de
  // punto de venta —el único recurso que ambos formularios podrían superponer, porque cambiarlo
  // reemplaza el turno entero de abajo— queda inerte. Cada formulario sigue dueño de su propio
  // flag de "en curso" (regla 5); esto es solo la señal combinada para ese selector puntual.
  const [escribiendo, setEscribiendo] = useState(false)

  // Carga inicial: puntos de venta (selector explícito, mismo criterio que Pos.tsx) y medios de
  // pago (para etiquetar el resumen parcial) — cada uno con su propio try/catch.
  useEffect(() => {
    let vigente = true

    clienteDeOrganizacion
      .listarPuntosVenta()
      .then((lista) => {
        if (!vigente) return
        setPuntosVenta(lista)
        const guardado = leerPuntoVentaGuardado()
        const porDefecto = lista.find((p) => p.id === guardado) ?? lista[0] ?? null
        setIdPuntoVenta(porDefecto ? porDefecto.id : '')
      })
      .catch((e) => {
        if (!vigente) return
        setPuntosVenta([])
        setErrorPuntosVenta(
          e instanceof ErrorApi ? e.message : 'No se pudieron cargar los puntos de venta. Seleccioná uno para operar.',
        )
      })

    clienteMediosPago
      .listar(false)
      .then((lista) => {
        if (!vigente) return
        setMedios(lista)
      })
      .catch((e) => {
        if (!vigente) return
        setMedios([])
        setErrorMedios(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los medios de pago.')
      })

    return () => {
      vigente = false
    }
  }, [])

  // regla 2: cada cambio de punto de venta dispara una nueva consulta del turno abierto — una
  // respuesta desactualizada (de un punto de venta que el cajero ya dejó de mirar) nunca puede
  // pisar la más reciente.
  useEffect(() => {
    if (idPuntoVenta === '') {
      setTurno(null)
      setErrorTurno('')
      return
    }

    const miGeneracion = (generacionTurnoRef.current += 1)
    let vigente = true
    setCargandoTurno(true)
    setErrorTurno('')

    clienteDeCaja
      .obtenerAbierto(idPuntoVenta)
      .then((t) => {
        if (!vigente || generacionTurnoRef.current !== miGeneracion) return
        setTurno(t)
      })
      .catch((e) => {
        if (!vigente || generacionTurnoRef.current !== miGeneracion) return
        setTurno(null)
        setErrorTurno(
          e instanceof ErrorApi ? e.message : 'No se pudo consultar el turno abierto de este punto de venta.',
        )
      })
      .finally(() => {
        if (!vigente || generacionTurnoRef.current !== miGeneracion) return
        setCargandoTurno(false)
      })

    return () => {
      vigente = false
    }
  }, [idPuntoVenta])

  function cambiarPuntoVenta(id: number) {
    if (escribiendo) return
    setIdPuntoVenta(id)
    guardarPuntoVentaSeleccionado(id)
  }

  function turnoAbierto(nuevoTurno: TurnoResumen) {
    // La apertura recién confirmada es la fuente más autoritativa posible: bumpear la
    // generación invalida cualquier GET …/abierto que siguiera en vuelo desde antes.
    generacionTurnoRef.current += 1
    setErrorTurno('')
    setTurno(nuevoTurno)
  }

  return (
    <div className="container-fluid py-4">
      <div className="row g-3">
        <div className="col-12">
          <Box titulo="Caja">
            {errorPuntosVenta && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorPuntosVenta}</div>}

            <div className="mb-3" style={{ maxWidth: 320 }}>
              <label className="form-label" htmlFor="caja-punto-venta">
                Punto de venta
              </label>
              <select
                id="caja-punto-venta"
                className="form-select rounded-0"
                value={idPuntoVenta}
                disabled={puntosVenta === null || escribiendo}
                onChange={(e) => cambiarPuntoVenta(Number(e.target.value))}
              >
                {puntosVenta === null && <option value="">Cargando…</option>}
                {puntosVenta !== null && puntosVenta.length === 0 && (
                  <option value="">Sin puntos de venta disponibles</option>
                )}
                {puntosVenta?.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.nombre}
                  </option>
                ))}
              </select>
            </div>

            {errorTurno && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorTurno}</div>}

            {idPuntoVenta !== '' && (
              // regla 8: la clave por turno (o "sin-turno" mientras no hay ninguno) remonta todo
              // el subárbol — un turno nuevo nunca hereda el formulario de movimiento ni el
              // resumen del turno anterior.
              <div key={turno?.id ?? 'sin-turno'}>
                {cargandoTurno && <p className="text-muted">Consultando el turno…</p>}

                {!cargandoTurno && turno === null && (
                  <FormularioApertura
                    idPuntoVenta={idPuntoVenta}
                    onAbierto={turnoAbierto}
                    onEscribiendoCambio={setEscribiendo}
                  />
                )}

                {!cargandoTurno && turno !== null && (
                  <PanelTurnoAbierto
                    turno={turno}
                    medios={medios}
                    errorMedios={errorMedios}
                    onEscribiendoCambio={setEscribiendo}
                  />
                )}
              </div>
            )}
          </Box>
        </div>
      </div>
    </div>
  )
}
