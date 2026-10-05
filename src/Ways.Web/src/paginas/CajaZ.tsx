import { useEffect, useRef, useState } from 'react'
import { useParams } from 'react-router'
import { clienteDeCaja, rutasDeExportacionDeCaja } from '../api/caja'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import type { DetalleDeTurno, ResumenDeCierrePorRetiro, TurnoResumen } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { InsigniaDeRecalculo } from '../componentes/InsigniaDeRecalculo'
import { VistaDeTicket } from '../componentes/VistaDeTicket'
import type { LineaDeTicket } from '../impresion/escpos'
import { enEscritorio, imprimir } from '../impresion/impresora'
import { cierreDeTurno, lineasDeCierreDeTurno } from '../impresion/plantillas'
import type { ContextoDeImpresion } from '../impresion/plantillas'
import { formatearImporte } from '../formato/importes'
import { etiquetaDeOrigenFondos } from './utilidadesGastosDelTurno'

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

/**
 * Caja Z (stage-11-exportacion-reportes, Slice 6b, design: Web Composition) — detalle del turno
 * cerrado: el mismo `ResumenDeTurno` que `/caja/cierre` usó para derivar el arqueo, más los
 * tickets y gastos del turno, con descarga XLSX. Misma política que `/caja`
 * (`Politicas.OperacionDePos`): el cajero puede leer su propio cierre — mismo gate que
 * `GET .../resumen`.
 *
 * `GET .../detalle` no discrimina por turno-ownership (spec historico-de-cajas: A Vendedor
 * Downloads Their Own Turno's Z-Report; verificado en la Slice 5b — `OperacionDePos` es un gate
 * de rol solo, sin claim de PV ni de turno): esta pantalla tampoco intenta un bloqueo cross-turno
 * que la API no tiene — cualquier Vendedor/Supervisor/Admin autenticado que conozca el id ve el
 * detalle, igual que del lado del servidor.
 *
 * stage-desktop-pos: `contextoDeImpresion` es el mismo seam opcional que `CierreDeCaja.tsx` —
 * `undefined` en la app web normal (sin cambio de comportamiento), habilita un botón
 * "Reimprimir" ESC/POS acá solo dentro de Tauri (`enEscritorio()`).
 *
 * "Reimprimir ticket de cierre" reimprime el MISMO ticket que el POS imprime al cerrar el turno
 * (`cierreDeTurno` sobre `GET .../resumen-de-cierre`): en Tauri sale por la térmica; en la web, por
 * el diálogo de impresión del navegador con solo el ticket en el papel. Solo se ofrece con el
 * turno cerrado.
 */
type PropsCajaZ = { contextoDeImpresion?: ContextoDeImpresion }

export function CajaZ({ contextoDeImpresion }: PropsCajaZ = {}) {
  const { id } = useParams<{ id: string }>()
  const idTurno = id !== undefined ? Number(id) : Number.NaN
  const idTurnoValido = Number.isFinite(idTurno)

  const { usuario } = useAuth()

  const [detalle, setDetalle] = useState<DetalleDeTurno | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [errorDescarga, setErrorDescarga] = useState('')
  const generacionRef = useRef(0)

  const [errorImpresion, setErrorImpresion] = useState('')

  const [turno, setTurno] = useState<TurnoResumen | null>(null)
  const [errorTurno, setErrorTurno] = useState('')
  const [imprimiendoCierre, setImprimiendoCierre] = useState(false)
  const imprimiendoCierreRef = useRef(false)
  const [errorCierre, setErrorCierre] = useState('')
  const [lineasAImprimir, setLineasAImprimir] = useState<LineaDeTicket[] | null>(null)

  const enTermica = contextoDeImpresion !== undefined && enEscritorio()

  async function contextoDeLaWeb(resumen: ResumenDeCierrePorRetiro): Promise<ContextoDeImpresion> {
    // El nombre de la empresa es un dato de cabecera: sin él el ticket sale igual, con la línea vacía.
    const puntosVenta = await clienteDeOrganizacion.listarPuntosVenta().catch(() => [])
    const puntoVenta = puntosVenta.find((p) => p.id === resumen.puntoVenta.id)
    return {
      empresa: puntoVenta?.razonSocialEmpresa ?? puntoVenta?.nombreTenant ?? '',
      puntoVenta: `PV ${resumen.puntoVenta.numero} — ${resumen.puntoVenta.nombre}`,
      cajero: usuario?.usuario ?? '—',
    }
  }

  async function reimprimirCierre() {
    if (imprimiendoCierreRef.current) return
    imprimiendoCierreRef.current = true
    setImprimiendoCierre(true)
    setErrorCierre('')
    setErrorImpresion('')
    const miGeneracion = generacionRef.current
    try {
      const resumen = await clienteDeCaja.obtenerResumenDeCierre(idTurno)
      const contexto = contextoDeImpresion ?? (await contextoDeLaWeb(resumen))
      if (generacionRef.current !== miGeneracion) return
      if (enTermica) {
        const resultado = await imprimir(cierreDeTurno(resumen, contexto))
        if (generacionRef.current !== miGeneracion) return
        setErrorImpresion(resultado.ok ? '' : resultado.mensaje)
      } else {
        setLineasAImprimir(lineasDeCierreDeTurno(resumen, contexto))
      }
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setErrorCierre(e instanceof ErrorApi ? e.message : 'No se pudo obtener el ticket de cierre.')
    } finally {
      imprimiendoCierreRef.current = false
      setImprimiendoCierre(false)
    }
  }

  useEffect(() => {
    if (!lineasAImprimir) return
    const terminar = () => setLineasAImprimir(null)
    document.body.classList.add('imprimiendo-ticket')
    window.addEventListener('afterprint', terminar, { once: true })
    window.print()
    return () => {
      window.removeEventListener('afterprint', terminar)
      document.body.classList.remove('imprimiendo-ticket')
    }
  }, [lineasAImprimir])

  useEffect(() => {
    if (!idTurnoValido) return
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')
    setTurno(null)
    setErrorTurno('')
    setErrorCierre('')
    setLineasAImprimir(null)

    clienteDeCaja
      .obtenerTurno(idTurno)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setTurno(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setErrorTurno(e instanceof ErrorApi ? e.message : 'No se pudo determinar el estado del turno.')
      })

    clienteDeCaja
      .obtenerDetalle(idTurno)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setDetalle(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setDetalle(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el detalle del turno.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [idTurno, idTurnoValido])

  if (!idTurnoValido) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Caja Z" variante="warning">
          <p className="text-muted mb-0">No se especificó el turno a mostrar.</p>
        </Box>
      </div>
    )
  }

  return (
    <div className="container-fluid py-4">
      <Box
        titulo={`Caja Z — turno #${idTurno}`}
        variante="inverse"
        herramientas={
          <div className="d-flex gap-2">
            <button type="button" className="btn btn-sm btn-outline-secondary d-print-none" onClick={() => window.print()}>
              Imprimir
            </button>
            <BotonDeDescarga
              ruta={rutasDeExportacionDeCaja.detalleDeTurno(idTurno)}
              etiqueta="Descargar"
              onError={setErrorDescarga}
              onInicio={() => setErrorDescarga('')}
              className="btn btn-sm btn-outline-secondary d-print-none"
            />
            {turno?.estado === 'Cerrado' && (
              <button
                type="button"
                className="btn btn-sm btn-outline-secondary d-print-none"
                disabled={imprimiendoCierre}
                onClick={() => void reimprimirCierre()}
              >
                {imprimiendoCierre ? 'Imprimiendo…' : 'Reimprimir ticket de cierre'}
              </button>
            )}
          </div>
        }
      >
        {/* Vista de impresión (design decisión 13: mismo componente, `@media print`, sin ruta ni
            fetch dedicados): equivalente del encabezado de los exports XLSX — generado por/cuándo.
            El turno ya está en el título de `Box`, que se imprime igual. */}
        <div className="d-none d-print-block mb-3">
          <div className="small">
            Generado: {new Date().toLocaleString('es-AR')} — {usuario?.usuario ?? '—'}
          </div>
        </div>

        {errorDescarga && <div className="alert alert-danger py-1 px-2 small mb-2">{errorDescarga}</div>}
        {errorTurno && (
          <div className="alert alert-warning py-1 px-2 small mb-2 d-print-none">
            {errorTurno} No se ofrece la reimpresión del ticket de cierre.
          </div>
        )}
        {errorCierre && <div className="alert alert-danger py-1 px-2 small mb-2 d-print-none">{errorCierre}</div>}
        {errorImpresion && (
          <div className="alert alert-warning py-1 px-2 small mb-2">No se pudo imprimir: {errorImpresion}</div>
        )}
        {error && (
          <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2">
            <span>{error}</span>
          </div>
        )}

        {cargando && !detalle && <Cargando />}

        {detalle && (
          <>
            {detalle.fechaRecalculo !== null && (
              <div className="mb-2">
                <InsigniaDeRecalculo fechaRecalculo={detalle.fechaRecalculo} />
              </div>
            )}
            <div className="row g-3 mb-3">
              <div className="col-md-3">
                <div className="text-muted small">Tickets</div>
                <div className="fs-5">{detalle.resumen.cantidadTickets}</div>
              </div>
              <div className="col-md-3">
                <div className="text-muted small">Primer ticket</div>
                <div>
                  {detalle.resumen.primerTicket
                    ? `${detalle.resumen.primerTicket.codigo} #${detalle.resumen.primerTicket.numero}`
                    : '—'}
                </div>
              </div>
              <div className="col-md-3">
                <div className="text-muted small">Último ticket</div>
                <div>
                  {detalle.resumen.ultimoTicket
                    ? `${detalle.resumen.ultimoTicket.codigo} #${detalle.resumen.ultimoTicket.numero}`
                    : '—'}
                </div>
              </div>
              <div className="col-md-3">
                <div className="text-muted small">Retiros</div>
                <div>{formatearMoneda(detalle.resumen.egresos.retiros)}</div>
              </div>
            </div>

            <h6>Medios de pago (esperado)</h6>
            <div className="table-responsive mb-3">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Medio</th>
                    <th className="text-end">Esperado</th>
                  </tr>
                </thead>
                <tbody>
                  {detalle.resumen.medios.map((m) => (
                    <tr key={m.idMedioPago}>
                      <td>Medio #{m.idMedioPago}</td>
                      <td className="text-end">{formatearMoneda(m.importeEsperado)}</td>
                    </tr>
                  ))}
                  {detalle.resumen.medios.length === 0 && (
                    <tr>
                      <td colSpan={2} className="text-center text-muted">
                        Este turno no tuvo actividad: no hay ningún medio arqueado.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <h6>Tickets</h6>
            <div className="table-responsive mb-3">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Número</th>
                    <th>Fecha</th>
                    <th>Estado</th>
                    <th className="text-end">Total</th>
                  </tr>
                </thead>
                <tbody>
                  {detalle.tickets.map((t) => (
                    <tr key={t.id}>
                      <td>{t.numeroVisible}</td>
                      <td>{formatearFechaHora(t.fecha)}</td>
                      <td>{t.estado}</td>
                      <td className="text-end">{formatearMoneda(t.total)}</td>
                    </tr>
                  ))}
                  {detalle.tickets.length === 0 && (
                    <tr>
                      <td colSpan={4} className="text-center text-muted py-3">
                        Sin tickets.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <h6>Gastos</h6>
            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Fecha</th>
                    <th>Categoría</th>
                    <th className="text-end">Importe</th>
                  </tr>
                </thead>
                <tbody>
                  {detalle.gastos.map((g) => (
                    <tr key={g.id}>
                      <td>{formatearFechaHora(g.fecha)}</td>
                      <td>
                        {g.categoria}
                        {g.origenFondos === 'Tesoreria' && <span className="badge bg-secondary ms-2">{etiquetaDeOrigenFondos(g.origenFondos)}</span>}
                      </td>
                      <td className="text-end">{formatearMoneda(g.importe)}</td>
                    </tr>
                  ))}
                  {detalle.gastos.length === 0 && (
                    <tr>
                      <td colSpan={3} className="text-center text-muted py-3">
                        Sin gastos.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </>
        )}
      </Box>
      {lineasAImprimir && <VistaDeTicket lineas={lineasAImprimir} />}
    </div>
  )
}
