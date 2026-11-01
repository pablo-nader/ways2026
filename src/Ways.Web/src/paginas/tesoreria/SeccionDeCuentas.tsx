import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { ErrorApi } from '../../api/cliente'
import type { ResultadoDeAjusteDeCuenta } from '../../api/cuentasTesoreria'
import { clienteDeReportes, rangoUltimosSieteDias } from '../../api/reportes'
import { puedeGestionarCatalogos } from '../../api/tipos'
import type { CuentaConSaldo, CuentasDeLaEmpresa, LibroDeCuenta } from '../../api/tipos'
import { useAuth } from '../../auth/useAuth'
import { Box } from '../../componentes/Box'
import { CampoFecha } from '../../componentes/CampoFecha'
import { Cargando } from '../../componentes/Cargando'
import { ModalDeAjusteDeCuenta } from '../../componentes/ModalDeAjusteDeCuenta'
import { formatearImporte } from '../../formato/importes'
import { fechaHoraATexto } from '../../formato/fechas'
import {
  avisoDeMediosSinCuenta,
  cuentaElegidaVigente,
  descuentosDelRango,
  etiquetaDeTipoMovimientoCuenta,
  formatearPorcentaje,
  instanteFueraDelRango,
  nombreDelEmpleado,
  nombreDelMedio,
} from './cuentas'
import { pluralizar } from '../../formato/pluralizar'

const TAMANIO_DE_PAGINA = 25
const RUTA_ABM_DE_CUENTAS = '/caja/cuentas-tesoreria'

function moneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

type Props = {
  /** Empresa elegida en el libro de efectivo; `null` mientras no hay ninguna. */
  idEmpresa: number | null
}

/**
 * Cuentas digitales de tesorería (doc 10 §7): saldo de cada cuenta de la empresa, aviso de los medios electrónicos que no
 * entran a ninguna y libro de la cuenta elegida con su rango, saldo de apertura y totales. Misma política de lectura que el
 * libro de efectivo (Supervisor y Admin); el ajuste de saldo es solo del Admin.
 *
 * `react-async-state`: las cuentas y el libro tienen cada uno su generación, y una respuesta tardía de una empresa, una cuenta,
 * un rango o una página anteriores se descarta, y al cambiar de cuenta el libro anterior se limpia en el mismo evento. El
 * ajuste lo escribe `ModalDeAjusteDeCuenta`, que deja inerte su ventana mientras escribe; acá solo se refresca después.
 */
export function SeccionDeCuentas({ idEmpresa }: Props) {
  const { usuario } = useAuth()
  const esAdmin = usuario !== null && puedeGestionarCatalogos(usuario.rolId)

  const [cuentas, setCuentas] = useState<CuentasDeLaEmpresa | null>(null)
  const [cargandoCuentas, setCargandoCuentas] = useState(false)
  const [errorCuentas, setErrorCuentas] = useState('')
  const generacionCuentasRef = useRef(0)

  const [idCuenta, setIdCuenta] = useState<number | null>(null)
  // `null` = el rango por defecto, que se recalcula en cada carga (los últimos siete días hasta hoy); una edición del usuario lo fija.
  const [rangoElegido, setRangoElegido] = useState<{ desde: string; hasta: string } | null>(null)
  // Se calcula una vez por carga y de ahí salen tanto la consulta como los campos de fecha: no pueden discrepar al pasar la medianoche.
  const [rangoPorDefecto, setRangoPorDefecto] = useState(rangoUltimosSieteDias)
  const desdeElegido = rangoElegido?.desde ?? null
  const hastaElegido = rangoElegido?.hasta ?? null
  const [pagina, setPagina] = useState(1)
  const [recarga, setRecarga] = useState(0)

  const [libro, setLibro] = useState<LibroDeCuenta | null>(null)
  const [cargandoLibro, setCargandoLibro] = useState(false)
  const [errorLibro, setErrorLibro] = useState('')
  const generacionLibroRef = useRef(0)

  const [modalAbierto, setModalAbierto] = useState(false)
  const [confirmacion, setConfirmacion] = useState('')
  const [avisoIncierto, setAvisoIncierto] = useState('')

  const cuentaElegida: CuentaConSaldo | null = cuentas?.cuentas.find((c) => c.id === idCuenta) ?? null

  const cargarCuentas = useCallback(() => {
    const miGeneracion = (generacionCuentasRef.current += 1)
    if (idEmpresa === null) {
      setCuentas(null)
      setCargandoCuentas(false)
      setErrorCuentas('')
      return
    }

    setCargandoCuentas(true)
    setErrorCuentas('')

    clienteDeReportes
      .cuentasDeTesoreria(idEmpresa)
      .then((datos) => {
        if (generacionCuentasRef.current !== miGeneracion) return
        setCuentas(datos)
        setIdCuenta((actual) => cuentaElegidaVigente(datos, actual))
      })
      .catch((e) => {
        if (generacionCuentasRef.current !== miGeneracion) return
        setCuentas(null)
        setErrorCuentas(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las cuentas.')
      })
      .finally(() => {
        if (generacionCuentasRef.current !== miGeneracion) return
        setCargandoCuentas(false)
      })
  }, [idEmpresa])

  useEffect(() => {
    // Otra empresa: nada de lo de la anterior sigue valiendo.
    setCuentas(null)
    setIdCuenta(null)
    setLibro(null)
    setModalAbierto(false)
    setConfirmacion('')
    setAvisoIncierto('')
    cargarCuentas()
  }, [cargarCuentas])

  useEffect(() => {
    const miGeneracion = (generacionLibroRef.current += 1)
    if (idCuenta === null) {
      setCargandoLibro(false)
      setErrorLibro('')
      return
    }

    setCargandoLibro(true)
    setErrorLibro('')

    const porDefecto = rangoUltimosSieteDias()
    setRangoPorDefecto(porDefecto)
    const rango = desdeElegido === null || hastaElegido === null ? porDefecto : { desde: desdeElegido, hasta: hastaElegido }

    clienteDeReportes
      .libroDeCuenta(idCuenta, { desde: rango.desde, hasta: rango.hasta, pagina, tamanio: TAMANIO_DE_PAGINA })
      .then((datos) => {
        if (generacionLibroRef.current !== miGeneracion) return
        setLibro(datos)
      })
      .catch((e) => {
        if (generacionLibroRef.current !== miGeneracion) return
        setLibro(null)
        setErrorLibro(e instanceof ErrorApi ? e.message : 'No se pudo cargar el libro de la cuenta.')
      })
      .finally(() => {
        if (generacionLibroRef.current !== miGeneracion) return
        setCargandoLibro(false)
      })
  }, [idCuenta, desdeElegido, hastaElegido, pagina, recarga])

  function elegirCuenta(id: number) {
    if (id === idCuenta) return
    setLibro(null)
    setPagina(1)
    setConfirmacion('')
    setAvisoIncierto('')
    setIdCuenta(id)
  }

  function cambiarRango(cambios: Partial<{ desde: string; hasta: string }>) {
    setRangoElegido((previo) => ({ ...(previo ?? rangoPorDefecto), ...cambios }))
    setPagina(1)
  }

  function refrescar() {
    cargarCuentas()
    setRecarga((n) => n + 1)
  }

  function alAjustar(resultado: ResultadoDeAjusteDeCuenta) {
    setModalAbierto(false)
    setAvisoIncierto('')
    // El rango que se va a mostrar tras el refresco: el elegido o el de los últimos siete días hasta hoy. Con fechas monótonas el
    // ajuste puede llevar una fecha futura (si la cola de la cuenta la tiene) y quedar fuera también del rango por defecto.
    const fuera = instanteFueraDelRango(resultado.movimiento.fecha, rangoElegido ?? rangoUltimosSieteDias())
    setConfirmacion(
      `Ajuste registrado. Saldo de ${cuentaElegida?.nombre ?? 'la cuenta'}: ${moneda(resultado.saldoActual)}.` +
        (fuera ? ' El ajuste quedó fuera del rango mostrado: ampliá las fechas para verlo en el libro.' : ''),
    )
    refrescar()
  }

  function alSerIncierto() {
    setModalAbierto(false)
    setConfirmacion('')
    setAvisoIncierto(
      `No se pudo confirmar el ajuste de ${cuentaElegida?.nombre ?? 'la cuenta'}. Ya se volvió a cargar el libro: verificá si figura antes de reintentarlo.`,
    )
    refrescar()
  }

  const totalPaginas = libro ? Math.max(1, Math.ceil(libro.total / libro.tamanio)) : 1
  const aviso = cuentas ? avisoDeMediosSinCuenta(cuentas.mediosSinCuenta) : null

  return (
    <Box
      titulo="Cuentas"
      variante="inverse"
      herramientas={
        esAdmin ? (
          <Link className="btn btn-sm btn-outline-light" to={RUTA_ABM_DE_CUENTAS}>
            Administrar cuentas
          </Link>
        ) : undefined
      }
    >
      {idEmpresa === null ? (
        <p className="text-muted text-center py-3">Elegí una empresa para ver sus cuentas.</p>
      ) : (
        <>
          {errorCuentas && (
            <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2">
              <span>{errorCuentas}</span>
              <button type="button" className="btn btn-sm btn-outline-danger" onClick={cargarCuentas}>
                Reintentar
              </button>
            </div>
          )}

          {confirmacion && (
            <div className="alert alert-success py-2 px-3 small d-flex justify-content-between align-items-center">
              <span>{confirmacion}</span>
              <button type="button" className="btn btn-sm btn-outline-success" onClick={() => setConfirmacion('')}>
                Cerrar aviso
              </button>
            </div>
          )}

          {avisoIncierto && (
            <div className="alert alert-warning py-2 px-3 small d-flex justify-content-between align-items-center">
              <span>{avisoIncierto}</span>
              <button type="button" className="btn btn-sm btn-outline-secondary" onClick={() => setAvisoIncierto('')}>
                Ya lo revisé
              </button>
            </div>
          )}

          {cargandoCuentas && !cuentas && <Cargando />}

          {cuentas && (
            <>
              {aviso && <div className="alert alert-warning py-2 px-3 small">{aviso}</div>}

              {cuentas.cuentas.length === 0 ? (
                <p className="text-muted text-center py-3">Esta empresa todavía no tiene cuentas de tesorería.</p>
              ) : (
                <div className="row g-2 mb-3">
                  {cuentas.cuentas.map((c) => (
                    <div className="col-md-4 col-lg-3" key={c.id}>
                      <button
                        type="button"
                        className={`btn w-100 text-start ${c.id === idCuenta ? 'btn-primary' : 'btn-outline-secondary'}`}
                        aria-pressed={c.id === idCuenta}
                        onClick={() => elegirCuenta(c.id)}
                      >
                        <div className="fw-semibold">
                          {c.nombre}
                          {!c.activo && <span className="ms-1 small">(inactiva)</span>}
                        </div>
                        <div className="fs-5">{moneda(c.saldoActual)}</div>
                        <div className="small">
                          {c.fechaUltimoMovimiento === null
                            ? 'Sin movimientos'
                            : `Último movimiento: ${fechaHoraATexto(c.fechaUltimoMovimiento)}`}
                        </div>
                      </button>
                    </div>
                  ))}
                </div>
              )}
            </>
          )}

          {cuentaElegida && (
            <section aria-label={`Libro de ${cuentaElegida.nombre}`}>
              <div className="d-flex justify-content-between align-items-center mb-2">
                <h6 className="m-0">Libro de {cuentaElegida.nombre}</h6>
                {esAdmin && (
                  <button type="button" className="btn btn-sm btn-primary" onClick={() => setModalAbierto(true)}>
                    Ajustar saldo
                  </button>
                )}
              </div>

              <div className="row g-2 align-items-end mb-3">
                <div className="col-md-3">
                  <label className="form-label" htmlFor="cuentas-desde">
                    Desde
                  </label>
                  <CampoFecha
                    id="cuentas-desde"
                    className="form-control"
                    value={rangoElegido?.desde ?? rangoPorDefecto.desde}
                    onChange={(valor) => cambiarRango({ desde: valor })}
                  />
                </div>
                <div className="col-md-3">
                  <label className="form-label" htmlFor="cuentas-hasta">
                    Hasta
                  </label>
                  <CampoFecha
                    id="cuentas-hasta"
                    className="form-control"
                    value={rangoElegido?.hasta ?? rangoPorDefecto.hasta}
                    onChange={(valor) => cambiarRango({ hasta: valor })}
                  />
                </div>
              </div>

              {errorLibro && (
                <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2">
                  <span>{errorLibro}</span>
                  <button type="button" className="btn btn-sm btn-outline-danger" onClick={() => setRecarga((n) => n + 1)}>
                    Reintentar
                  </button>
                </div>
              )}

              {cargandoLibro && !libro && <Cargando />}

              {libro && (
                <>
                  <dl className="row small mb-3" aria-label="Totales del período">
                    <dt className="col-6 col-md-3">Saldo de apertura</dt>
                    <dd className="col-6 col-md-3 text-end">{moneda(libro.saldoDeApertura)}</dd>
                    <dt className="col-6 col-md-3">Bruto cobrado</dt>
                    <dd className="col-6 col-md-3 text-end">{moneda(libro.totales.cobrado)}</dd>
                    <dt className="col-6 col-md-3">Descuentos</dt>
                    <dd className="col-6 col-md-3 text-end">{moneda(descuentosDelRango(libro.totales))}</dd>
                    <dt className="col-6 col-md-3">Comisión</dt>
                    <dd className="col-6 col-md-3 text-end">{moneda(libro.totales.comision)}</dd>
                    <dt className="col-6 col-md-3">Retención IIBB</dt>
                    <dd className="col-6 col-md-3 text-end">{moneda(libro.totales.retencionIibb)}</dd>
                    <dt className="col-6 col-md-3">Retención IVA</dt>
                    <dd className="col-6 col-md-3 text-end">{moneda(libro.totales.retencionIva)}</dd>
                    <dt className="col-6 col-md-3">Retención Ganancias</dt>
                    <dd className="col-6 col-md-3 text-end">{moneda(libro.totales.retencionGanancias)}</dd>
                    <dt className="col-6 col-md-3">Ajustes</dt>
                    <dd className="col-6 col-md-3 text-end">{moneda(libro.totales.ajustes)}</dd>
                    {libro.totales.otros !== 0 && (
                      <>
                        <dt className="col-6 col-md-3">Otros movimientos</dt>
                        <dd className="col-6 col-md-3 text-end">{moneda(libro.totales.otros)}</dd>
                      </>
                    )}
                    <dt className="col-6 col-md-3">Neto del período</dt>
                    <dd className="col-6 col-md-3 text-end">{moneda(libro.totales.neto)}</dd>
                    <dt className="col-6 col-md-3">Saldo al cierre</dt>
                    <dd className="col-6 col-md-3 text-end">{moneda(libro.saldoAlCierre)}</dd>
                  </dl>

                  <div className="table-responsive">
                    <table className="table table-sm table-striped table-bordered align-middle">
                      <thead>
                        <tr>
                          <th>Fecha</th>
                          <th>Tipo</th>
                          <th>Concepto</th>
                          <th>Medio</th>
                          <th className="text-end">Ingreso</th>
                          <th className="text-end">Egreso</th>
                          <th className="text-end">Final</th>
                          <th>Turno</th>
                          <th>Empleado</th>
                        </tr>
                      </thead>
                      <tbody>
                        {libro.items.map((m) => (
                          <tr key={m.id}>
                            <td>{fechaHoraATexto(m.fecha)}</td>
                            <td>{etiquetaDeTipoMovimientoCuenta(m.tipo)}</td>
                            <td>
                              {m.concepto}
                              {m.porcentajeAplicado !== null && m.baseBruta !== null && (
                                <div className="small text-muted">
                                  {formatearPorcentaje(m.porcentajeAplicado)} sobre {moneda(m.baseBruta)}
                                </div>
                              )}
                            </td>
                            <td>{nombreDelMedio(m)}</td>
                            <td className="text-end">{moneda(m.ingreso)}</td>
                            <td className="text-end">{moneda(m.egreso)}</td>
                            <td className="text-end">{moneda(m.final)}</td>
                            <td>{m.idTurnoCaja === null ? '—' : `#${m.idTurnoCaja}`}</td>
                            <td>{nombreDelEmpleado(m)}</td>
                          </tr>
                        ))}
                        {libro.items.length === 0 && (
                          <tr>
                            <td colSpan={9} className="text-center text-muted py-4">
                              No hay movimientos en este período.
                            </td>
                          </tr>
                        )}
                      </tbody>
                    </table>
                  </div>

                  <div className="d-flex justify-content-between align-items-center">
                    <span className="small text-muted">
                      Página {libro.pagina} de {totalPaginas} — {pluralizar(libro.total, 'movimiento')}
                    </span>
                    <div className="d-flex gap-2">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-secondary"
                        aria-label="Página anterior del libro de la cuenta"
                        disabled={libro.pagina <= 1 || cargandoLibro}
                        onClick={() => setPagina((p) => Math.max(1, p - 1))}
                      >
                        Anterior
                      </button>
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-secondary"
                        aria-label="Página siguiente del libro de la cuenta"
                        disabled={libro.pagina >= totalPaginas || cargandoLibro}
                        onClick={() => setPagina((p) => p + 1)}
                      >
                        Siguiente
                      </button>
                    </div>
                  </div>
                </>
              )}
            </section>
          )}
        </>
      )}

      {modalAbierto && cuentaElegida && idEmpresa !== null && (
        <ModalDeAjusteDeCuenta
          idEmpresa={idEmpresa}
          cuenta={cuentaElegida}
          onCerrar={() => setModalAbierto(false)}
          onAjustado={alAjustar}
          onIncierto={alSerIncierto}
        />
      )}
    </Box>
  )
}
