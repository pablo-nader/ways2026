import { useEffect, useRef, useState } from 'react'
import { clienteDeCaja } from '../api/caja'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import { clienteDeVentas } from '../api/ventas'
import { ROL } from '../api/tipos'
import type { ComprobanteEmitido, MedioPagoAlta, MedioPagoListado, TurnoResumen, VentaDeTurnoListado } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'
import { Box } from '../componentes/Box'
import { CampoImporte } from '../componentes/CampoImporte'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { Modal } from '../componentes/Modal'
import {
  FILTROS_VACIOS,
  claseDeBadgeDeEstadoVenta,
  etiquetaDeEstadoVenta,
  filtrarVentas,
  formatearFechaHora,
  formatearMoneda,
  hayFiltrosActivos,
  mediosDisponibles,
  nombreDeMedio,
  puedeAnular,
  puedeReimprimir,
  totalesDeVentas,
  totalesPorMedioDeVentas,
} from './utilidadesVentasDelTurno'
import type { EstadoFiltro, FiltrosDeVentasDelTurno } from './utilidadesVentasDelTurno'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

function mensajeDeErrorAnulacion(e: unknown): string {
  if (e instanceof ErrorApi) {
    if (e.estado === 403) return 'No tenés permiso para anular esta venta.'
    return e.message
  }
  return 'No se pudo anular la venta.'
}

function mensajeDeErrorDetalle(e: unknown): string {
  return e instanceof ErrorApi ? e.message : 'No se pudo cargar el detalle de la venta.'
}

function mensajeDeErrorReimprimir(e: unknown): string {
  return e instanceof ErrorApi ? e.message : 'No se pudo obtener el comprobante para reimprimir.'
}

const MENSAJE_VENTA_ANULADA_NO_REIMPRIME = 'La venta fue anulada, no se puede reimprimir.'

type PropsModalDetalle = {
  comprobante: ComprobanteEmitido | null
  /** `ComprobanteEmitido` no lleva el nombre del cliente (solo `idCliente`) — se toma de la fila
   * ya cargada del listado (`ventaEnDetalle.nombreCliente`), disponible desde que se abre el
   * modal, sin esperar el `GET /api/ventas/{id}`. */
  nombreCliente: string | undefined
  cargando: boolean
  error: string
  medios: MedioPagoListado[]
  puedeReimprimirAca: boolean
  reimprimiendo: boolean
  errorReimprimir: string
  /** Botón que abrió el modal, capturado síncronamente en `VentasDelTurno.abrirDetalle` (nunca
   * leído acá con `document.activeElement`: react-async-state regla 12). El foco se restaura en
   * el cleanup del efecto de abajo — mismo criterio que `ConfirmacionDeBaja`: para cuando ese
   * cleanup corre, el commit que sacó `disabled` del disparador ya se aplicó, así que
   * `esAlcanzable` ve el DOM al día (un `.focus()` síncrono en el handler de cierre vería
   * todavía el `disabled` del render anterior). */
  disparador: HTMLElement | null
  onReimprimir: () => void
  onCerrar: () => void
}

/** Mismo criterio que `ConfirmacionDeBaja.esAlcanzable`: un disparador sirve para devolverle el
 * foco solo si sigue en el documento y sigue siendo operable. */
function esAlcanzable(elemento: HTMLElement | null): elemento is HTMLElement {
  return elemento !== null && elemento.isConnected && !elemento.matches(':disabled')
}

/**
 * Modal de detalle de una venta, montado sobre `Modal`: Escape/X/"Cerrar" cierran.
 * `restaurarFoco={false}` porque el disparador sigue deshabilitado mientras corre la restauración
 * de `Modal` (fase de layout del commit de cierre); la de acá corre después de ese commit.
 */
function ModalDetalleDeVenta({
  comprobante,
  nombreCliente,
  cargando,
  error,
  medios,
  puedeReimprimirAca,
  reimprimiendo,
  errorReimprimir,
  disparador,
  onReimprimir,
  onCerrar,
}: PropsModalDetalle) {
  // Restaura el foco al desmontar (cierre por Escape/X/"Cerrar") — ver el doc-comment de
  // `disparador` arriba. Un efecto normal (no layout): el commit que quitó `disabled` del
  // disparador corre ANTES de este cleanup, nunca al revés.
  useEffect(() => {
    return () => {
      if (esAlcanzable(disparador)) disparador.focus()
    }
  }, [disparador])

  const titulo = comprobante ? `Detalle de la venta ${comprobante.numeroVisible}` : 'Detalle de la venta'

  return (
    <Modal
      titulo={titulo}
      tamano="lg"
      restaurarFoco={false}
      etiquetaCerrar="Cerrar detalle"
      onCerrar={onCerrar}
      pie={
        <>
          {puedeReimprimirAca && (
            <button
              type="button"
              className="btn btn-outline-secondary rounded-0"
              disabled={cargando || reimprimiendo}
              onClick={onReimprimir}
            >
              {reimprimiendo ? 'Reimprimiendo…' : 'Reimprimir'}
            </button>
          )}
          <button type="button" className="btn btn-secondary rounded-0" onClick={onCerrar}>
            Cerrar
          </button>
        </>
      }
    >
      {cargando && <Cargando texto="Cargando detalle…" />}
      {!cargando && error && <div className="alert alert-danger rounded-0">{error}</div>}

      {!cargando && !error && comprobante && (
        <>
          <dl className="row mb-3">
            <dt className="col-3">Fecha</dt>
            <dd className="col-9">{formatearFechaHora(comprobante.fecha)}</dd>
            <dt className="col-3">Cliente</dt>
            <dd className="col-9">{nombreCliente ?? '—'}</dd>
            <dt className="col-3">Estado</dt>
            <dd className="col-9">
              <span className={`badge rounded-0 ${claseDeBadgeDeEstadoVenta(comprobante.estado)}`}>
                {etiquetaDeEstadoVenta(comprobante.estado)}
              </span>
            </dd>
          </dl>

          <div className="table-responsive">
            <table className="table table-sm table-bordered align-middle">
              <thead>
                <tr>
                  <th>Descripción</th>
                  <th className="text-end">Cantidad</th>
                  <th className="text-end">Precio unit.</th>
                  <th className="text-end">Descuento</th>
                  <th className="text-end">Total</th>
                </tr>
              </thead>
              <tbody>
                {comprobante.items.map((item) => (
                  <tr key={item.orden}>
                    <td>
                      {item.descripcion}
                      {item.codigoLote && <span className="text-muted"> — Lote {item.codigoLote}</span>}
                    </td>
                    <td className="text-end">{item.cantidad}</td>
                    <td className="text-end">{formatearMoneda(item.precioUnitario)}</td>
                    <td className="text-end">{formatearMoneda(item.descuento)}</td>
                    <td className="text-end">{formatearMoneda(item.total)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="table-responsive">
            <table className="table table-sm table-bordered align-middle">
              <thead>
                <tr>
                  <th>Medio</th>
                  <th className="text-end">Importe</th>
                  <th>Referencia</th>
                  <th className="text-end">Vuelto</th>
                </tr>
              </thead>
              <tbody>
                {comprobante.pagos.map((pago, indice) => (
                  <tr key={`${pago.idMedioPago}-${indice}`}>
                    <td>{nombreDeMedio(pago.idMedioPago, medios)}</td>
                    <td className="text-end">{formatearMoneda(pago.importe)}</td>
                    <td>{pago.referencia ?? '—'}</td>
                    <td className="text-end">{pago.vuelto > 0 ? formatearMoneda(pago.vuelto) : '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="d-flex justify-content-end gap-4 small">
            <span>Subtotal: {formatearMoneda(comprobante.subtotal)}</span>
            <span>Descuento: {formatearMoneda(comprobante.descuentoTotal)}</span>
            <strong>Total: {formatearMoneda(comprobante.total)}</strong>
          </div>

          {errorReimprimir && <div className="alert alert-danger rounded-0 mt-3 mb-0 py-1 px-2 small">{errorReimprimir}</div>}
        </>
      )}
    </Modal>
  )
}

type Props = {
  /** Reimpresión de escritorio (stage-desktop-pos): definido ⇒ el shell sabe imprimir — sin él,
   * ni la fila ni el modal de detalle ofrecen "Reimprimir" (spec: la acción solo existe si hay
   * quién la sirva, nunca un botón que no hace nada). `medios` va junto al comprobante porque
   * `ticketDeVenta`/`algunPagoEnEfectivo` los necesitan para resolver nombre y comportamiento de
   * cada pago — la propia pantalla ya los tiene cargados para el modal de detalle. */
  alReimprimir?: (comprobante: ComprobanteEmitido, medios: MedioPagoListado[]) => void
}

/**
 * "Ventas del turno" (stage-desktop-pos, POS de escritorio): consulta las ventas del turno
 * ABIERTO del punto de venta fijo del dispositivo y permite anular una — mismo endpoint que
 * `POST /api/ventas/{id}/anulacion` (ya existente, sin `motivo`: revierte stock y cuenta corriente
 * en la misma transacción, nunca hay "restaurar").
 *
 * `generacionRef` es COMPARTIDO entre la carga inicial/refresco y la anulación (mismo criterio que
 * `Remito.tsx`/`ShellPos.tsx`): una respuesta desactualizada de cualquiera de las dos nunca pisa un
 * estado más nuevo. Mientras el modal de confirmación de anulación O el modal de detalle está
 * montado, O una anulación está en vuelo, TODA la pantalla (refrescar + cada botón de fila) queda
 * inerte — react-async-state regla 9/13.
 *
 * El detalle (`GET /api/ventas/{id}`) tiene su PROPIO token (`detalleGeneracionRef`), separado del
 * de arriba: abrir otro detalle o cerrar el modal invalida cualquier respuesta en vuelo del
 * anterior (react-async-state regla 2/3) sin afectar el refresco del listado.
 */
export function VentasDelTurno({ alReimprimir }: Props = {}) {
  const { puntoVenta } = usePuntoVenta()
  const { usuario } = useAuth()
  // Rol más restrictivo (Root, que nunca opera el POS) sin sesión resuelta todavía — mismo
  // criterio defensivo que el resto del shell: `usuario`/`puntoVenta` son `| null` a nivel de
  // tipo (contextos genéricos), aunque `ShellPos` siempre los provee ya resueltos acá.
  const rolId = usuario?.rolId ?? ROL.Root

  const [turno, setTurno] = useState<TurnoResumen | null>(null)
  const [buscandoTurno, setBuscandoTurno] = useState(true)
  const [errorTurno, setErrorTurno] = useState('')

  const [ventas, setVentas] = useState<VentaDeTurnoListado[]>([])
  const [cargandoVentas, setCargandoVentas] = useState(false)
  const [errorVentas, setErrorVentas] = useState('')

  const generacionRef = useRef(0)

  const [filaAAnular, setFilaAAnular] = useState<VentaDeTurnoListado | null>(null)
  const disparadorRef = useRef<HTMLElement | null>(null)
  const [anulando, setAnulando] = useState(false)
  const anulandoRef = useRef(false)
  const [errorAnular, setErrorAnular] = useState('')

  const [filtros, setFiltros] = useState<FiltrosDeVentasDelTurno>(FILTROS_VACIOS)

  // Catálogo de medios de pago — cargado una sola vez al montar (no depende de `puntoVenta`, el
  // catálogo de medios es del tenant): hace falta para nombrar cada pago del detalle y para
  // reimprimir (`ticketDeVenta`/`algunPagoEnEfectivo` lo piden). Falla en silencio hacia un
  // catálogo vacío con un aviso propio — react-async-state regla 7: el fallback de nombre
  // (`Medio #id`, ya establecido en `impresion/plantillas.ts`) sigue dejando operar la pantalla,
  // pero el aviso deja claro que los nombres podrían faltar.
  const [medios, setMedios] = useState<MedioPagoListado[]>([])
  const [errorMedios, setErrorMedios] = useState('')

  // ---- Detalle (GET /api/ventas/{id}) ----------------------------------------------------------
  const [idDetalle, setIdDetalle] = useState<number | null>(null)
  const [comprobanteDetalle, setComprobanteDetalle] = useState<ComprobanteEmitido | null>(null)
  const [cargandoDetalle, setCargandoDetalle] = useState(false)
  const [errorDetalle, setErrorDetalle] = useState('')
  const detalleGeneracionRef = useRef(0)
  const disparadorDetalleRef = useRef<HTMLElement | null>(null)

  // ---- Reimprimir (por fila, y desde el propio modal de detalle) -------------------------------
  const [idsReimprimiendo, setIdsReimprimiendo] = useState<Set<number>>(new Set())
  const idsReimprimiendoRef = useRef<Set<number>>(new Set())
  const [erroresReimprimir, setErroresReimprimir] = useState<Record<number, string>>({})

  async function cargarVentas(idTurno: number, miGeneracion: number) {
    setCargandoVentas(true)
    setErrorVentas('')
    try {
      const filas = await clienteDeVentas.listarPorTurno(idTurno)
      if (generacionRef.current !== miGeneracion) return
      setVentas(filas)
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setVentas([])
      setErrorVentas(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las ventas del turno.')
    } finally {
      if (generacionRef.current === miGeneracion) setCargandoVentas(false)
    }
  }

  async function cargarTurnoYVentas() {
    if (!puntoVenta) return
    const miGeneracion = (generacionRef.current += 1)
    setBuscandoTurno(true)
    setErrorTurno('')

    try {
      const turnoAbierto = await clienteDeCaja.obtenerAbierto(puntoVenta.id)
      if (generacionRef.current !== miGeneracion) return
      setTurno(turnoAbierto)

      if (!turnoAbierto) {
        setVentas([])
        return
      }
      await cargarVentas(turnoAbierto.id, miGeneracion)
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setTurno(null)
      setVentas([])
      setErrorTurno(e instanceof ErrorApi ? e.message : 'No se pudo consultar el turno abierto.')
    } finally {
      if (generacionRef.current === miGeneracion) setBuscandoTurno(false)
    }
  }

  useEffect(() => {
    void cargarTurnoYVentas()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [puntoVenta?.id])

  useEffect(() => {
    let vigente = true
    clienteMediosPago
      .listar(false)
      .then((lista) => {
        if (vigente) setMedios(lista)
      })
      .catch(() => {
        if (vigente) setErrorMedios('No se pudo cargar el catálogo de medios de pago: los nombres de pago podrían faltar.')
      })
    return () => {
      vigente = false
    }
  }, [])

  const ocupado = buscandoTurno || cargandoVentas || anulando
  const bloqueado = ocupado || filaAAnular !== null || idDetalle !== null

  function pedirAnular(fila: VentaDeTurnoListado, disparador: HTMLElement) {
    if (bloqueado) return
    disparadorRef.current = disparador
    setErrorAnular('')
    setFilaAAnular(fila)
  }

  function cancelarAnular() {
    setFilaAAnular(null)
    setErrorAnular('')
  }

  async function confirmarAnular() {
    if (anulandoRef.current || !filaAAnular) return
    const fila = filaAAnular
    anulandoRef.current = true
    setAnulando(true)
    setErrorAnular('')
    const miGeneracion = (generacionRef.current += 1)

    try {
      await clienteDeVentas.anular(fila.id)
      if (generacionRef.current !== miGeneracion) return
      setFilaAAnular(null)
      if (turno) await cargarVentas(turno.id, miGeneracion)
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setErrorAnular(mensajeDeErrorAnulacion(e))
    } finally {
      anulandoRef.current = false
      if (generacionRef.current === miGeneracion) setAnulando(false)
    }
  }

  // ---- Detalle ------------------------------------------------------------------------------

  async function cargarDetalle(id: number, miGeneracion: number) {
    try {
      const comprobante = await clienteDeVentas.obtener(id)
      if (detalleGeneracionRef.current !== miGeneracion) return
      setComprobanteDetalle(comprobante)
    } catch (e) {
      if (detalleGeneracionRef.current !== miGeneracion) return
      setErrorDetalle(mensajeDeErrorDetalle(e))
    } finally {
      if (detalleGeneracionRef.current === miGeneracion) setCargandoDetalle(false)
    }
  }

  function abrirDetalle(venta: VentaDeTurnoListado, disparador: HTMLElement) {
    if (bloqueado) return
    disparadorDetalleRef.current = disparador
    const miGeneracion = (detalleGeneracionRef.current += 1)
    setIdDetalle(venta.id)
    setComprobanteDetalle(null)
    setErrorDetalle('')
    setCargandoDetalle(true)
    void cargarDetalle(venta.id, miGeneracion)
  }

  function cerrarDetalle() {
    // Invalida cualquier fetch en vuelo ANTES de tocar el estado visible (regla 3): una respuesta
    // que llegue después de este cierre nunca lo reabre ni pisa lo que se muestre después. El
    // foco vuelve al disparador en el cleanup de `ModalDetalleDeVenta` al desmontar, no acá.
    detalleGeneracionRef.current += 1
    setIdDetalle(null)
    setComprobanteDetalle(null)
    setErrorDetalle('')
    setCargandoDetalle(false)
  }

  // ---- Reimprimir -----------------------------------------------------------------------------

  /** JD-E1-2 (judgment-day ronda 0, CRITICAL confirmado por los dos jueces): SIEMPRE vuelve a
   * pedir el comprobante — nunca reusa `comprobanteDetalle` ni ningún otro estado ya en pantalla.
   * La venta pudo anularse DESPUÉS de que el listado o el detalle se cargaron; reimprimir contra
   * ese estado viejo imprimiría un ticket de una venta que ya no es válida. Si la respuesta fresca
   * dice `Anulado`, no se imprime nada: se refresca lo que la pantalla venía mostrando (la fila
   * del listado y, si está abierto, el detalle) para que el botón de "Reimprimir" desaparezca, y
   * se muestra un error explícito en vez de fallar en silencio. */
  async function reimprimirVenta(id: number) {
    if (!alReimprimir) return
    if (idsReimprimiendoRef.current.has(id)) return
    idsReimprimiendoRef.current.add(id)
    setIdsReimprimiendo((prev) => new Set(prev).add(id))
    setErroresReimprimir((prev) => {
      if (!(id in prev)) return prev
      const { [id]: _omitido, ...resto } = prev
      return resto
    })

    try {
      const comprobante = await clienteDeVentas.obtener(id)

      if (comprobante.estado !== 'Emitido') {
        setVentas((prev) => prev.map((v) => (v.id === id ? { ...v, estado: comprobante.estado } : v)))
        setComprobanteDetalle((prev) => (prev && prev.id === id ? comprobante : prev))
        setErroresReimprimir((prev) => ({ ...prev, [id]: MENSAJE_VENTA_ANULADA_NO_REIMPRIME }))
        return
      }

      alReimprimir(comprobante, medios)
    } catch (e) {
      setErroresReimprimir((prev) => ({ ...prev, [id]: mensajeDeErrorReimprimir(e) }))
    } finally {
      idsReimprimiendoRef.current.delete(id)
      setIdsReimprimiendo((prev) => {
        const siguiente = new Set(prev)
        siguiente.delete(id)
        return siguiente
      })
    }
  }

  const ventasFiltradas = filtrarVentas(ventas, filtros)
  const totales = totalesDeVentas(ventasFiltradas)
  const totalesPorMedio = totalesPorMedioDeVentas(ventasFiltradas)
  const opcionesDeMedio = mediosDisponibles(ventas)
  const ventaEnDetalle = idDetalle !== null ? ventas.find((v) => v.id === idDetalle) : undefined

  const herramientas = (
    <button
      type="button"
      className="btn btn-sm btn-outline-light rounded-0"
      disabled={bloqueado}
      onClick={() => void cargarTurnoYVentas()}
    >
      {buscandoTurno || cargandoVentas ? 'Actualizando…' : 'Refrescar'}
    </button>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Ventas del turno" variante="inverse" herramientas={herramientas}>
        {!puntoVenta && <div className="alert alert-warning rounded-0 mb-0">No hay un punto de venta asociado a este dispositivo.</div>}

        {puntoVenta && errorTurno && <div className="alert alert-danger rounded-0">{errorTurno}</div>}

        {puntoVenta && !errorTurno && buscandoTurno && !turno && <Cargando />}

        {puntoVenta && !errorTurno && !buscandoTurno && !turno && (
          <div className="alert alert-warning rounded-0 mb-0">No hay un turno abierto en este punto de venta.</div>
        )}

        {turno && (
          <>
            {errorVentas && <div className="alert alert-danger rounded-0">{errorVentas}</div>}
            {errorMedios && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorMedios}</div>}
            {errorAnular && !filaAAnular && <div className="alert alert-danger rounded-0">{errorAnular}</div>}

            {cargandoVentas && ventas.length === 0 && <Cargando />}

            <div className="d-flex flex-wrap gap-3 align-items-center mb-3">
              {totalesPorMedio.length === 0 && <span className="small text-muted">Sin cobros para totalizar.</span>}
              {totalesPorMedio.map((t) => (
                <span key={t.idMedioPago} className="badge bg-secondary rounded-0 fs-6">
                  {t.nombre}: {formatearMoneda(t.total)}
                </span>
              ))}
              <span className="fw-bold">Total general: {formatearMoneda(totales.total)}</span>
              <span className="small text-muted">
                {totales.cantidad} venta(s)
              </span>
            </div>

            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Número</th>
                    <th>Fecha</th>
                    <th>Cliente</th>
                    <th className="text-end">Total</th>
                    <th>Medios de pago</th>
                    <th>Estado</th>
                    <th>Acciones</th>
                  </tr>
                  <tr>
                    <th>
                      <input
                        type="text"
                        className="form-control form-control-sm rounded-0"
                        aria-label="Filtrar por número"
                        value={filtros.numero}
                        onChange={(e) => setFiltros((prev) => ({ ...prev, numero: e.target.value }))}
                      />
                    </th>
                    <th>
                      <input
                        type="text"
                        className="form-control form-control-sm rounded-0"
                        aria-label="Filtrar por fecha"
                        value={filtros.fecha}
                        onChange={(e) => setFiltros((prev) => ({ ...prev, fecha: e.target.value }))}
                      />
                    </th>
                    <th>
                      <input
                        type="text"
                        className="form-control form-control-sm rounded-0"
                        aria-label="Filtrar por cliente"
                        value={filtros.cliente}
                        onChange={(e) => setFiltros((prev) => ({ ...prev, cliente: e.target.value }))}
                      />
                    </th>
                    <th>
                      <div className="d-flex gap-1">
                        <CampoImporte
                          className="form-control form-control-sm rounded-0"
                          aria-label="Total mínimo"
                          placeholder="Mín."
                          valor={filtros.totalMinimo}
                          onChange={(valor) => setFiltros((prev) => ({ ...prev, totalMinimo: valor }))}
                        />
                        <CampoImporte
                          className="form-control form-control-sm rounded-0"
                          aria-label="Total máximo"
                          placeholder="Máx."
                          valor={filtros.totalMaximo}
                          onChange={(valor) => setFiltros((prev) => ({ ...prev, totalMaximo: valor }))}
                        />
                      </div>
                    </th>
                    <th>
                      <select
                        className="form-select form-select-sm rounded-0"
                        aria-label="Filtrar por medio de pago"
                        value={filtros.idMedioPago ?? ''}
                        onChange={(e) =>
                          setFiltros((prev) => ({ ...prev, idMedioPago: e.target.value === '' ? null : Number(e.target.value) }))
                        }
                      >
                        <option value="">Todos</option>
                        {opcionesDeMedio.map((m) => (
                          <option key={m.idMedioPago} value={m.idMedioPago}>
                            {m.nombre}
                          </option>
                        ))}
                      </select>
                    </th>
                    <th>
                      <select
                        className="form-select form-select-sm rounded-0"
                        aria-label="Filtrar por estado"
                        value={filtros.estado}
                        onChange={(e) => setFiltros((prev) => ({ ...prev, estado: e.target.value as EstadoFiltro }))}
                      >
                        <option value="Todas">Todas</option>
                        <option value="Emitido">Emitida</option>
                        <option value="Anulado">Anulada</option>
                      </select>
                    </th>
                    <th>
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-secondary rounded-0"
                        disabled={!hayFiltrosActivos(filtros)}
                        onClick={() => setFiltros(FILTROS_VACIOS)}
                      >
                        Limpiar filtros
                      </button>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {ventasFiltradas.map((v) => (
                    <tr key={v.id}>
                      <td>{v.numeroVisible}</td>
                      <td>{formatearFechaHora(v.fecha)}</td>
                      <td>{v.nombreCliente}</td>
                      <td className="text-end">{formatearMoneda(v.total)}</td>
                      <td>{v.mediosDePago.map((m) => m.nombre).join(', ') || '—'}</td>
                      <td>
                        <span className={`badge rounded-0 ${claseDeBadgeDeEstadoVenta(v.estado)}`}>
                          {etiquetaDeEstadoVenta(v.estado)}
                        </span>
                      </td>
                      <td>
                        <div className="d-flex flex-wrap gap-2">
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-secondary rounded-0"
                            disabled={bloqueado}
                            onClick={(e) => abrirDetalle(v, e.currentTarget)}
                          >
                            Detalle
                          </button>
                          {puedeAnular(v, rolId) && (
                            <button
                              type="button"
                              className="btn btn-sm btn-outline-danger rounded-0"
                              disabled={bloqueado}
                              onClick={(e) => pedirAnular(v, e.currentTarget)}
                            >
                              Anular
                            </button>
                          )}
                          {alReimprimir && puedeReimprimir(v) && (
                            <button
                              type="button"
                              className="btn btn-sm btn-outline-secondary rounded-0"
                              disabled={bloqueado || idsReimprimiendo.has(v.id)}
                              onClick={() => void reimprimirVenta(v.id)}
                            >
                              {idsReimprimiendo.has(v.id) ? 'Reimprimiendo…' : 'Reimprimir'}
                            </button>
                          )}
                        </div>
                        {erroresReimprimir[v.id] && <div className="small text-danger mt-1">{erroresReimprimir[v.id]}</div>}
                      </td>
                    </tr>
                  ))}
                  {!cargandoVentas && ventas.length === 0 && (
                    <tr>
                      <td colSpan={7} className="text-center text-muted py-4">
                        Este turno todavía no tiene ventas.
                      </td>
                    </tr>
                  )}
                  {!cargandoVentas && ventas.length > 0 && ventasFiltradas.length === 0 && (
                    <tr>
                      <td colSpan={7} className="text-center text-muted py-4">
                        Ninguna venta coincide con los filtros.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            {filaAAnular && (
              <div className="mt-3">
                <ConfirmacionDeBaja
                  titulo={`la venta ${filaAAnular.numeroVisible} por ${formatearMoneda(filaAAnular.total)}`}
                  pregunta="Anular"
                  nota="Se revierte el stock y la cuenta corriente en la misma operación. No existe restaurar."
                  etiquetaConfirmar="Anular venta"
                  etiquetaEnCurso="Anulando…"
                  ocupado={anulando}
                  disparador={disparadorRef.current}
                  onConfirmar={() => void confirmarAnular()}
                  onCancelar={cancelarAnular}
                />
                {errorAnular && <div className="alert alert-danger rounded-0 mt-2 mb-0">{errorAnular}</div>}
              </div>
            )}

            {idDetalle !== null && (
              <ModalDetalleDeVenta
                comprobante={comprobanteDetalle}
                nombreCliente={ventaEnDetalle?.nombreCliente}
                cargando={cargandoDetalle}
                error={errorDetalle}
                medios={medios}
                puedeReimprimirAca={Boolean(alReimprimir) && (comprobanteDetalle?.estado ?? ventaEnDetalle?.estado) === 'Emitido'}
                reimprimiendo={idsReimprimiendo.has(idDetalle)}
                errorReimprimir={erroresReimprimir[idDetalle] ?? ''}
                disparador={disparadorDetalleRef.current}
                onReimprimir={() => void reimprimirVenta(idDetalle)}
                onCerrar={cerrarDetalle}
              />
            )}
          </>
        )}
      </Box>
    </div>
  )
}
