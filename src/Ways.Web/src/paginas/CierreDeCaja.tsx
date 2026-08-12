import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { aSolicitudDeCierre, conteosCompletos, conteoValido, diferenciaPrevia } from '../api/arqueo'
import { clienteDeCaja } from '../api/caja'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import type { MedioPagoAlta, MedioPagoListado, ResumenDeTurno, TurnoConArqueos } from '../api/tipos'
import { Box } from '../componentes/Box'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

/**
 * Pantalla de cierre de turno (stage-6-turnos-caja, Slice 7, design: Web Composition —
 * `CierreDeCaja.tsx`): resumen del turno a cerrar, un conteo declarado por medio arqueable,
 * confirmación de irreversibilidad y el comprobante Z al terminar. El turno lo trae la ruta
 * (`?idTurno=`), nunca un selector propio — se navega acá desde el panel del turno abierto en
 * `Caja.tsx`.
 *
 * Es la pantalla con más obligaciones de `react-async-state` de toda la etapa (reglas 1, 4, 5, 6,
 * 7, 9): un cierre es irreversible, así que un doble submit es el peor defecto que puede tener.
 */
export function CierreDeCaja() {
  const [searchParams] = useSearchParams()
  const crudo = searchParams.get('idTurno')
  const idTurno = crudo !== null && crudo.trim() !== '' ? Number(crudo) : Number.NaN
  const idTurnoValido = Number.isFinite(idTurno)

  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [errorMedios, setErrorMedios] = useState('')

  const [resumen, setResumen] = useState<ResumenDeTurno | null>(null)
  const [cargandoResumen, setCargandoResumen] = useState(true)
  const [errorResumen, setErrorResumen] = useState('')

  // regla 1: un único record mutado SIEMPRE por updater funcional — ningún helper de acá lee el
  // estado del componente para armar el próximo valor.
  const [conteos, setConteos] = useState<Record<number, string>>({})
  const [observaciones, setObservaciones] = useState('')
  const [confirmado, setConfirmado] = useState(false)

  const [cerrando, setCerrando] = useState(false)
  const cerrandoRef = useRef(false)
  const generacionCierreRef = useRef(0)
  const [errorCierre, setErrorCierre] = useState('')

  // El comprobante Z viene en la propia respuesta 2xx del POST de cierre (design: The Cierre
  // Transaction) — `zReporte !== null` ES la señal de "el turno ya cerró", no hace falta un
  // booleano aparte ni un GET aislado después (react-async-state regla 6: un cierre 2xx que
  // dependiera de un fetch posterior para mostrar sus propios datos podría reportarse sin datos
  // por una falla ajena al cierre en sí).
  const [zReporte, setZReporte] = useState<TurnoConArqueos | null>(null)

  const medioPorId = useMemo(() => {
    const indice: Record<number, MedioPagoListado> = {}
    for (const m of medios ?? []) indice[m.id] = m
    return indice
  }, [medios])

  useEffect(() => {
    if (!idTurnoValido) return
    let vigente = true

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

    clienteDeCaja
      .obtenerResumen(idTurno)
      .then((datos) => {
        if (!vigente) return
        setResumen(datos)
      })
      .catch((e) => {
        if (!vigente) return
        setResumen(null)
        setErrorResumen(e instanceof ErrorApi ? e.message : 'No se pudo cargar el resumen del turno.')
      })
      .finally(() => {
        if (!vigente) return
        setCargandoResumen(false)
      })

    return () => {
      vigente = false
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [idTurnoValido])

  function cambiarConteo(idMedioPago: number, valor: string) {
    if (cerrandoRef.current) return
    setConteos((prev) => ({ ...prev, [idMedioPago]: valor }))
  }

  const errorCarga = errorMedios || errorResumen
  const cargaLista = !cargandoResumen && resumen !== null && medios !== null
  const puedeFinalizar =
    cargaLista && errorCarga === '' && confirmado && conteosCompletos(resumen?.medios ?? [], conteos) && !cerrando

  async function finalizarCierre() {
    // regla 9: guard de reentrancia de primera línea — un doble click en el mismo tick le gana
    // al re-render que deshabilita el botón.
    if (cerrandoRef.current) return
    if (!resumen || !puedeFinalizar) return

    const miGeneracion = (generacionCierreRef.current += 1)
    cerrandoRef.current = true
    setCerrando(true)
    setErrorCierre('')

    const solicitud = aSolicitudDeCierre(resumen.medios, conteos, observaciones)

    try {
      const conArqueos = await clienteDeCaja.cerrar(idTurno, solicitud)
      // regla 4/6: el `finally` que libera `cerrando` está gateado por generación. El POST ya
      // devolvió 2xx con el comprobante Z completo — se usa directo, sin un GET aislado después
      // que podría fallar y dejar el cierre exitoso sin datos que el servidor ya mandó.
      if (generacionCierreRef.current !== miGeneracion) return
      setZReporte(conArqueos)
      cerrandoRef.current = false
      setCerrando(false)
    } catch (e) {
      if (generacionCierreRef.current !== miGeneracion) return

      if (e instanceof ErrorApi && (e.codigo === 'arqueo_incompleto' || e.codigo === 'medio_sin_actividad_en_el_turno')) {
        // El checklist en pantalla quedó desactualizado contra el servidor (otro movimiento entró
        // al turno entre que se cargó el resumen y este intento) — se refresca el resumen para
        // volver a mostrar el set real de medios arqueables. Los conteos ya tipeados sobreviven
        // tal cual están (siguen indexados por `idMedioPago`, ningún reset acá) para los medios
        // que siguen presentes.
        setErrorCierre(e.message)
        clienteDeCaja
          .obtenerResumen(idTurno)
          .then((datos) => {
            if (generacionCierreRef.current !== miGeneracion) return
            setResumen(datos)
            setErrorResumen('')
          })
          .catch(() => {
            if (generacionCierreRef.current !== miGeneracion) return
            setErrorResumen('No se pudo actualizar el resumen del turno con los datos más recientes. Actualizá la página.')
          })
          .finally(() => {
            if (generacionCierreRef.current !== miGeneracion) return
            cerrandoRef.current = false
            setCerrando(false)
          })
        return
      }

      setErrorCierre(e instanceof ErrorApi ? e.message : 'No se pudo cerrar el turno.')
      cerrandoRef.current = false
      setCerrando(false)
    }
  }

  if (!idTurnoValido) {
    return (
      <div className="container-fluid py-4">
        <div className="row g-3">
          <div className="col-12">
            <Box titulo="Cierre de turno" variante="warning">
              <p className="text-muted">No se especificó el turno a cerrar.</p>
              <Link className="btn btn-outline-secondary rounded-0" to="/caja">
                Volver a caja
              </Link>
            </Box>
          </div>
        </div>
      </div>
    )
  }

  if (zReporte) {
    return (
      <div className="container-fluid py-4">
        <div className="row g-3">
          <div className="col-12">
            <Box titulo={`Turno #${idTurno} cerrado`} variante="success">
              <div className="row g-3 mb-3">
                <div className="col-md-4">
                  <div className="small text-muted">Apertura</div>
                  <div>{formatearFechaHora(zReporte.fechaApertura)}</div>
                </div>
                <div className="col-md-4">
                  <div className="small text-muted">Cierre</div>
                  <div>{zReporte.fechaCierre ? formatearFechaHora(zReporte.fechaCierre) : '—'}</div>
                </div>
                <div className="col-md-4">
                  <div className="small text-muted">Fondo inicial</div>
                  <div>{formatearMoneda(zReporte.fondoInicial)}</div>
                </div>
              </div>

              <div className="table-responsive">
                <table className="table table-sm table-striped table-bordered align-middle">
                  <thead>
                    <tr>
                      <th>Medio</th>
                      <th className="text-end">Esperado</th>
                      <th className="text-end">Declarado</th>
                      <th className="text-end">Diferencia</th>
                    </tr>
                  </thead>
                  <tbody>
                    {zReporte.arqueos.map((a) => (
                      <tr key={a.idMedioPago}>
                        <td>{medioPorId[a.idMedioPago]?.nombre ?? `Medio #${a.idMedioPago}`}</td>
                        <td className="text-end">{formatearMoneda(a.importeEsperado)}</td>
                        <td className="text-end">{formatearMoneda(a.importeDeclarado)}</td>
                        <td className="text-end">{formatearMoneda(a.diferencia)}</td>
                      </tr>
                    ))}
                    {zReporte.arqueos.length === 0 && (
                      <tr>
                        <td colSpan={4} className="text-center text-muted">
                          Este turno no tuvo actividad: no hay ningún medio arqueado.
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>

              {/* stage-11-exportacion-reportes (Slice 6b, design: Web Composition, "link from
                  the just-closed turno to its Caja Z screen"): mismo gate OperacionDePos que
                  /caja/turnos/:id/z, el cajero recién cerró este turno. */}
              <div className="d-flex gap-2">
                <Link className="btn btn-outline-secondary rounded-0" to="/caja">
                  Volver a caja
                </Link>
                <Link className="btn btn-primary rounded-0" to={`/caja/turnos/${idTurno}/z`}>
                  Ver Caja Z
                </Link>
              </div>
            </Box>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="container-fluid py-4">
      <div className="row g-3">
        <div className="col-12">
          <Box titulo={`Cierre de turno #${idTurno}`}>
            {errorCarga && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorCarga}</div>}
            {errorCierre && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorCierre}</div>}

            {cargandoResumen && <p className="text-muted">Cargando el resumen del turno…</p>}

            {/* regla 7: la falla de medios/resumen deja el botón VISIBLE pero realmente
                deshabilitado (`puedeFinalizar` exige `errorCarga === ''`) — nunca lo esconde,
                para que el aviso de arriba tenga un enforcement real detrás. */}
            {!cargandoResumen && (
              <>
                {resumen && (
                  <div className="table-responsive">
                    <table className="table table-sm table-striped table-bordered align-middle">
                      <thead>
                        <tr>
                          <th>Medio</th>
                          <th className="text-end">Esperado</th>
                          <th style={{ width: 160 }}>Declarado</th>
                          <th className="text-end">Diferencia</th>
                        </tr>
                      </thead>
                      <tbody>
                        {resumen.medios.map((m) => {
                          const valor = conteos[m.idMedioPago] ?? ''
                          const nombreMedio = medioPorId[m.idMedioPago]?.nombre ?? `Medio #${m.idMedioPago}`
                          return (
                            <tr key={m.idMedioPago}>
                              <td>
                                {nombreMedio}
                                {m.idMedioPago === resumen.idMedioAncla && (
                                  <span className="badge bg-secondary ms-1">efectivo</span>
                                )}
                              </td>
                              <td className="text-end">{formatearMoneda(m.importeEsperado)}</td>
                              <td>
                                <input
                                  type="number"
                                  step="0.01"
                                  min="0"
                                  className="form-control form-control-sm rounded-0"
                                  aria-label={`Declarado de ${nombreMedio}`}
                                  value={valor}
                                  disabled={cerrando}
                                  onChange={(e) => cambiarConteo(m.idMedioPago, e.target.value)}
                                />
                              </td>
                              <td className="text-end">
                                {conteoValido(valor)
                                  ? formatearMoneda(diferenciaPrevia(m.importeEsperado, Number(valor)))
                                  : '—'}
                              </td>
                            </tr>
                          )
                        })}
                        {resumen.medios.length === 0 && (
                          <tr>
                            <td colSpan={4} className="text-center text-muted">
                              Este turno no tuvo actividad: no hay ningún medio para arquear.
                            </td>
                          </tr>
                        )}
                      </tbody>
                    </table>
                  </div>
                )}

                <div className="mb-3">
                  <label className="form-label" htmlFor="cierre-observaciones">
                    Observaciones
                  </label>
                  <input
                    id="cierre-observaciones"
                    type="text"
                    className="form-control rounded-0"
                    value={observaciones}
                    disabled={cerrando || !resumen}
                    onChange={(e) => setObservaciones(e.target.value)}
                  />
                </div>

                <div className="form-check mb-3">
                  <input
                    id="cierre-confirmacion"
                    type="checkbox"
                    className="form-check-input rounded-0"
                    checked={confirmado}
                    disabled={cerrando || !resumen}
                    onChange={(e) => setConfirmado(e.target.checked)}
                  />
                  <label className="form-check-label" htmlFor="cierre-confirmacion">
                    Confirmo que estoy cerrando este turno de forma definitiva. El cierre es irreversible: no se puede
                    reabrir ni corregir después.
                  </label>
                </div>

                <div className="d-flex gap-2">
                  <button
                    type="button"
                    className="btn btn-danger rounded-0"
                    disabled={!puedeFinalizar}
                    onClick={finalizarCierre}
                  >
                    {cerrando ? 'Cerrando…' : 'Finalizar cierre'}
                  </button>
                  {!cerrando && (
                    <Link className="btn btn-outline-secondary rounded-0" to="/caja">
                      Cancelar
                    </Link>
                  )}
                </div>
              </>
            )}
          </Box>
        </div>
      </div>
    </div>
  )
}
