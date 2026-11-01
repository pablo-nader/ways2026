import { useEffect, useMemo, useRef, useState } from 'react'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import { textoDeMediosDelPago } from '../api/lineasDePago'
import type { ComprobanteEmitido, MedioPagoAlta, MedioPagoListado, PagoDeTurno, TicketDeTurno } from '../api/tipos'
import { clienteDeVentas } from '../api/ventas'
import { fechaHoraATexto } from '../formato/fechas'
import { formatearImporte } from '../formato/importes'
import { etiquetaDeEstadoVenta, puedeReimprimir } from '../paginas/utilidadesVentasDelTurno'
import { etiquetaDeOrigenFondos } from '../paginas/utilidadesPagosDelTurno'
import { BotonIcono } from './BotonIcono'
import { EstadoDeVentaEnFila, ModalDetalleDeVenta } from './DetalleDeVenta'
import { EncabezadoOrdenable, useOrdenamiento, type ColumnasOrdenables } from './grilla'

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function formatearFechaHora(iso: string): string {
  return fechaHoraATexto(iso)
}

const COLUMNAS_DE_TICKETS: ColumnasOrdenables<TicketDeTurno> = {
  numero: { valor: (t) => t.numeroVisible },
  fecha: { valor: (t) => new Date(t.fecha) },
  cliente: { valor: (t) => t.nombreCliente },
  total: { valor: (t) => t.total },
  medios: { valor: (t) => t.mediosDePago.map((m) => m.nombre).join(', ') },
  estado: { valor: (t) => etiquetaDeEstadoVenta(t.estado) },
}

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

const MENSAJE_VENTA_ANULADA_NO_REIMPRIME = 'La venta fue anulada, no se puede reimprimir.'

function mensajeDeErrorDetalle(e: unknown): string {
  return e instanceof ErrorApi ? e.message : 'No se pudo cargar el detalle de la venta.'
}

function mensajeDeErrorReimprimir(e: unknown): string {
  return e instanceof ErrorApi ? e.message : 'No se pudo obtener el comprobante para reimprimir.'
}

/** Los nombres de los medios solo nombran pagos y resuelven el cajón: sin catálogo el detalle y el
 * ticket salen igual, con el rótulo de respaldo de cada pantalla. */
function cargarMedios(): Promise<MedioPagoListado[]> {
  return clienteMediosPago.listar(false).catch(() => [] as MedioPagoListado[])
}

/** Los nombres de los medios vienen en las líneas del pago; una respuesta sin líneas no los trae. */
function textoDeMedios(pago: PagoDeTurno): string {
  return (pago.lineas?.length ?? 0) > 0 ? textoDeMediosDelPago(pago) : '—'
}

const COLUMNAS_DE_PAGOS: ColumnasOrdenables<PagoDeTurno> = {
  fecha: { valor: (g) => new Date(g.fecha) },
  categoria: { valor: (g) => g.categoria },
  medio: { valor: (g) => textoDeMedios(g) },
  importe: { valor: (g) => g.importe },
}

type Props = {
  tickets: TicketDeTurno[]
  pagos: PagoDeTurno[]
  /** Definido ⇒ cada ticket emitido ofrece "Reimprimir". Recibe el comprobante recién pedido al
   * servidor (nunca el del listado) y puede rechazar la promesa para mostrar el error en la fila.
   * La lista nunca ofrece anular: un turno cerrado no se toca. */
  alReimprimir?: (comprobante: ComprobanteEmitido, medios: MedioPagoListado[]) => void | Promise<void>
}

/** Las listas de tickets y de pagos del detalle de un turno cerrado: las comparten la pantalla
 * "Caja Z" y el modal de detalle de "Cierres de caja" del POS. Cada ticket se puede ver en detalle
 * y, si `alReimprimir` está definido, reimprimir; nunca anular.
 *
 * El detalle lleva su generación (`react-async-state` regla 2/3): cerrar o abrir otro invalida la
 * respuesta en vuelo. La reimpresión se traba por ticket con un `ref` sincrónico (regla 11) y, igual
 * que `VentasDelTurno`, vuelve a pedir la venta: si quedó anulada no se imprime nada. */
export function DetalleDeTurnoListas({ tickets, pagos, alReimprimir }: Props) {
  const [estadosActualizados, setEstadosActualizados] = useState<Record<number, TicketDeTurno['estado']>>({})
  const ticketsVigentes = useMemo(
    () => tickets.map((t) => (t.id in estadosActualizados ? { ...t, estado: estadosActualizados[t.id] } : t)),
    [tickets, estadosActualizados],
  )
  const ordenTickets = useOrdenamiento(ticketsVigentes, COLUMNAS_DE_TICKETS)
  const ordenPagos = useOrdenamiento(pagos, COLUMNAS_DE_PAGOS)

  const [idDetalle, setIdDetalle] = useState<number | null>(null)
  const [comprobanteDetalle, setComprobanteDetalle] = useState<ComprobanteEmitido | null>(null)
  const [mediosDetalle, setMediosDetalle] = useState<MedioPagoListado[]>([])
  const [cargandoDetalle, setCargandoDetalle] = useState(false)
  const [errorDetalle, setErrorDetalle] = useState('')
  const detalleGeneracionRef = useRef(0)
  const disparadorDetalleRef = useRef<HTMLElement | null>(null)

  const [idsReimprimiendo, setIdsReimprimiendo] = useState<Set<number>>(new Set())
  const idsReimprimiendoRef = useRef<Set<number>>(new Set())
  const [erroresReimprimir, setErroresReimprimir] = useState<Record<number, string>>({})

  useEffect(() => {
    const generacion = detalleGeneracionRef
    return () => {
      generacion.current += 1
    }
  }, [])

  const bloqueado = idDetalle !== null

  function abrirDetalle(ticket: TicketDeTurno, disparador: HTMLElement) {
    if (bloqueado) return
    disparadorDetalleRef.current = disparador
    const miGeneracion = (detalleGeneracionRef.current += 1)
    setIdDetalle(ticket.id)
    setComprobanteDetalle(null)
    setErrorDetalle('')
    setCargandoDetalle(true)
    void (async () => {
      try {
        const [comprobante, medios] = await Promise.all([clienteDeVentas.obtener(ticket.id), cargarMedios()])
        if (detalleGeneracionRef.current !== miGeneracion) return
        setMediosDetalle(medios)
        setComprobanteDetalle(comprobante)
      } catch (e) {
        if (detalleGeneracionRef.current !== miGeneracion) return
        setErrorDetalle(mensajeDeErrorDetalle(e))
      } finally {
        if (detalleGeneracionRef.current === miGeneracion) setCargandoDetalle(false)
      }
    })()
  }

  function cerrarDetalle() {
    detalleGeneracionRef.current += 1
    setIdDetalle(null)
    setComprobanteDetalle(null)
    setErrorDetalle('')
    setCargandoDetalle(false)
  }

  async function reimprimir(id: number) {
    if (!alReimprimir || idsReimprimiendoRef.current.has(id)) return
    idsReimprimiendoRef.current.add(id)
    setIdsReimprimiendo((prev) => new Set(prev).add(id))
    setErroresReimprimir((prev) => {
      if (!(id in prev)) return prev
      const { [id]: _omitido, ...resto } = prev
      return resto
    })

    try {
      const [comprobante, medios] = await Promise.all([clienteDeVentas.obtener(id), cargarMedios()])
      if (comprobante.estado !== 'Emitido') {
        setEstadosActualizados((prev) => ({ ...prev, [id]: comprobante.estado }))
        setComprobanteDetalle((prev) => (prev && prev.id === id ? comprobante : prev))
        setErroresReimprimir((prev) => ({ ...prev, [id]: MENSAJE_VENTA_ANULADA_NO_REIMPRIME }))
        return
      }
      await alReimprimir(comprobante, medios)
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

  const ticketEnDetalle = idDetalle !== null ? ticketsVigentes.find((t) => t.id === idDetalle) : undefined

  return (
    <>
      <h6>Tickets</h6>
      <div className="table-responsive mb-3">
        <table className="table table-sm table-striped table-bordered align-middle">
          <thead>
            <tr>
              <EncabezadoOrdenable columna="numero" orden={ordenTickets.orden} onAlternar={ordenTickets.alternar}>
                Número
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="fecha" orden={ordenTickets.orden} onAlternar={ordenTickets.alternar}>
                Fecha
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="cliente" orden={ordenTickets.orden} onAlternar={ordenTickets.alternar}>
                Cliente
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="total" orden={ordenTickets.orden} onAlternar={ordenTickets.alternar} className="text-end">
                Total
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="medios" orden={ordenTickets.orden} onAlternar={ordenTickets.alternar}>
                Medios de pago
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="estado" orden={ordenTickets.orden} onAlternar={ordenTickets.alternar}>
                Estado
              </EncabezadoOrdenable>
              <th>Acciones</th>
            </tr>
          </thead>
          <tbody>
            {ordenTickets.filas.map((t) => (
              <tr key={t.id}>
                <td>{t.numeroVisible}</td>
                <td>{formatearFechaHora(t.fecha)}</td>
                <td>{t.nombreCliente}</td>
                <td className="text-end">{formatearMoneda(t.total)}</td>
                <td>{t.mediosDePago.map((m) => m.nombre).join(', ') || '—'}</td>
                <td>
                  <EstadoDeVentaEnFila venta={t} />
                </td>
                <td>
                  <div className="d-flex flex-wrap gap-2">
                    <BotonIcono icono="detalle" disabled={bloqueado} onClick={(e) => abrirDetalle(t, e.currentTarget)} />
                    {alReimprimir && puedeReimprimir(t) && (
                      <BotonIcono
                        icono="imprimir"
                        etiqueta="Reimprimir"
                        title="Reimprimir"
                        ocupado={idsReimprimiendo.has(t.id)}
                        disabled={bloqueado || idsReimprimiendo.has(t.id)}
                        onClick={() => void reimprimir(t.id)}
                      />
                    )}
                  </div>
                  {erroresReimprimir[t.id] && <div className="small text-danger mt-1">{erroresReimprimir[t.id]}</div>}
                </td>
              </tr>
            ))}
            {tickets.length === 0 && (
              <tr>
                <td colSpan={7} className="text-center text-muted py-3">
                  Sin tickets.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      {idDetalle !== null && (
        <ModalDetalleDeVenta
          comprobante={comprobanteDetalle}
          nombreCliente={ticketEnDetalle?.nombreCliente}
          cargando={cargandoDetalle}
          error={errorDetalle}
          medios={mediosDetalle}
          puedeReimprimirAca={Boolean(alReimprimir) && (comprobanteDetalle?.estado ?? ticketEnDetalle?.estado) === 'Emitido'}
          reimprimiendo={idsReimprimiendo.has(idDetalle)}
          errorReimprimir={erroresReimprimir[idDetalle] ?? ''}
          disparador={disparadorDetalleRef.current}
          onReimprimir={() => void reimprimir(idDetalle)}
          onCerrar={cerrarDetalle}
        />
      )}

      <h6>Pagos</h6>
      <div className="table-responsive">
        <table className="table table-sm table-striped table-bordered align-middle">
          <thead>
            <tr>
              <EncabezadoOrdenable columna="fecha" orden={ordenPagos.orden} onAlternar={ordenPagos.alternar}>
                Fecha
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="categoria" orden={ordenPagos.orden} onAlternar={ordenPagos.alternar}>
                Categoría
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="medio" orden={ordenPagos.orden} onAlternar={ordenPagos.alternar}>
                Medio de pago
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="importe" orden={ordenPagos.orden} onAlternar={ordenPagos.alternar} className="text-end">
                Importe
              </EncabezadoOrdenable>
            </tr>
          </thead>
          <tbody>
            {ordenPagos.filas.map((g) => (
              <tr key={g.id}>
                <td>{formatearFechaHora(g.fecha)}</td>
                <td>
                  {g.categoria}
                  {g.origenFondos === 'Tesoreria' && <span className="badge bg-secondary ms-2">{etiquetaDeOrigenFondos(g.origenFondos)}</span>}
                </td>
                <td>{textoDeMedios(g)}</td>
                <td className="text-end">{formatearMoneda(g.importe)}</td>
              </tr>
            ))}
            {pagos.length === 0 && (
              <tr>
                <td colSpan={4} className="text-center text-muted py-3">
                  Sin pagos.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </>
  )
}
