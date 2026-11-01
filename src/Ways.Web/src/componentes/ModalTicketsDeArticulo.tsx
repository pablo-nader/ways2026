import { useEffect, useRef, useState, type RefObject } from 'react'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import { clienteDeReportes, type FiltrosDeTicketsDeArticulo } from '../api/reportes'
import type { ComprobanteEmitido, MedioPagoAlta, MedioPagoListado, TicketsDeArticulo } from '../api/tipos'
import { clienteDeVentas } from '../api/ventas'
import { fechaHoraEnZonaATexto, isoATexto } from '../formato/fechas'
import { formatearImporte } from '../formato/importes'
import { BotonIcono } from './BotonIcono'
import { Cargando } from './Cargando'
import { ModalDetalleDeVenta } from './DetalleDeVenta'
import { Modal } from './Modal'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

const SIN_MEDIOS: MedioPagoListado[] = []

function formatearCantidad(cantidad: number): string {
  return formatearImporte(cantidad, { decimales: Number.isInteger(cantidad) ? 0 : 3 })
}

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

/** Los nombres de los medios solo nombran pagos: sin catálogo el detalle sale igual con el rótulo de respaldo. */
function cargarMedios(): Promise<MedioPagoListado[]> {
  return clienteMediosPago.listar(false).catch(() => SIN_MEDIOS)
}

type PropsDetalle = {
  idTicket: number
  nombreCliente: string | undefined
  disparador: HTMLElement | null
  onCerrar: () => void
}

/** Detalle de solo lectura de un ticket. Se monta con `key` por ticket: cambiar de ticket descarta la respuesta del
 * anterior y nunca se ve el error o el comprobante de otro. */
function DetalleDelTicket({ idTicket, nombreCliente, disparador, onCerrar }: PropsDetalle) {
  const [comprobante, setComprobante] = useState<ComprobanteEmitido | null>(null)
  const [medios, setMedios] = useState<MedioPagoListado[]>(SIN_MEDIOS)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')

  useEffect(() => {
    let vigente = true
    Promise.all([clienteDeVentas.obtener(idTicket), cargarMedios()])
      .then(([venta, listaDeMedios]) => {
        if (!vigente) return
        setMedios(listaDeMedios)
        setComprobante(venta)
      })
      .catch((e) => {
        if (!vigente) return
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el detalle de la venta.')
      })
      .finally(() => {
        if (vigente) setCargando(false)
      })
    return () => {
      vigente = false
    }
  }, [idTicket])

  return (
    <ModalDetalleDeVenta
      comprobante={comprobante}
      nombreCliente={nombreCliente}
      cargando={cargando}
      error={error}
      medios={medios}
      puedeReimprimirAca={false}
      reimprimiendo={false}
      errorReimprimir=""
      disparador={disparador}
      onReimprimir={() => {}}
      onCerrar={onCerrar}
    />
  )
}

type Props = {
  idArticulo: number
  filtros: FiltrosDeTicketsDeArticulo
  /** Ticket abierto en detalle, o `null` si se ve solo la lista. Lo gobierna el dueño (la URL). */
  idTicket: number | null
  onAbrirTicket: (idComprobante: number) => void
  onCerrarTicket: () => void
  onCerrar: () => void
  /** Destino de foco al cerrar cuando el modal se abrió sin un disparador vivo (por ejemplo, desde un enlace). */
  focoDeReserva?: RefObject<HTMLElement | null>
}

/**
 * Tickets donde se vendió un artículo en el recorte de un reporte, y el detalle de solo lectura de cada uno.
 *
 * La lectura se gatea con una bandera por ejecución del efecto (`react-async-state` regla 2): cambiar de filtros o
 * reintentar vuelve vigente solo a la última pedida. El dueño lo monta con `key` por artículo, así que cambiar de
 * artículo parte de un estado limpio.
 */
export function ModalTicketsDeArticulo({
  idArticulo,
  filtros,
  idTicket,
  onAbrirTicket,
  onCerrarTicket,
  onCerrar,
  focoDeReserva,
}: Props) {
  const { idEmpresa, idPuntoVenta, desde, hasta } = filtros
  const [datos, setDatos] = useState<TicketsDeArticulo | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [reintento, setReintento] = useState(0)

  const disparadorDelTicketRef = useRef<HTMLElement | null>(null)

  useEffect(() => {
    let vigente = true
    setCargando(true)
    setError('')
    clienteDeReportes
      .ticketsDeArticulo(idArticulo, { idEmpresa, idPuntoVenta, desde, hasta })
      .then((respuesta) => {
        if (!vigente) return
        setDatos(respuesta)
      })
      .catch((e) => {
        if (!vigente) return
        setDatos(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los tickets del artículo.')
      })
      .finally(() => {
        if (vigente) setCargando(false)
      })
    return () => {
      vigente = false
    }
  }, [idArticulo, idEmpresa, idPuntoVenta, desde, hasta, reintento])

  const visibles = datos
  const ticketEnDetalle = idTicket === null ? undefined : visibles?.tickets.find((t) => t.idComprobante === idTicket)
  const titulo = visibles ? `Tickets de ${visibles.descripcion}` : 'Tickets del artículo'

  return (
    <>
      <Modal
        titulo={titulo}
        tamano="xl"
        desplazable
        focoDeReserva={focoDeReserva}
        etiquetaCerrar="Cerrar tickets"
        onCerrar={onCerrar}
        pie={
          <button type="button" className="btn btn-secondary" onClick={onCerrar}>
            Cerrar
          </button>
        }
      >
        {cargando && <Cargando />}
        {!cargando && error && (
          <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2">
            <span>{error}</span>
            <button type="button" className="btn btn-sm btn-outline-danger" onClick={() => setReintento((n) => n + 1)}>
              Reintentar
            </button>
          </div>
        )}

        {!cargando && !error && visibles && (
          <>
            <p className="text-muted small">
              Del {isoATexto(visibles.desde)} al {isoATexto(visibles.hasta)} · {visibles.zonaHoraria}
            </p>
            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Fecha</th>
                    <th>Comprobante</th>
                    <th>Punto de venta</th>
                    <th>Cliente</th>
                    <th className="text-end">Cantidad</th>
                    <th className="text-end">Total</th>
                    <th>
                      <span className="visually-hidden">Detalle</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {visibles.tickets.map((t) => (
                    <tr key={t.idComprobante}>
                      <td>{fechaHoraEnZonaATexto(t.fecha, visibles.zonaHoraria)}</td>
                      <td>{t.comprobante}</td>
                      <td>{t.puntoVenta}</td>
                      <td>{t.cliente ?? 'Consumidor final'}</td>
                      <td className="text-end">{formatearCantidad(t.cantidad)}</td>
                      <td className="text-end">{formatearMoneda(t.total)}</td>
                      <td>
                        <BotonIcono
                          icono="detalle"
                          etiqueta={`Ver el detalle del ticket ${t.comprobante}`}
                          disabled={idTicket !== null}
                          onClick={(e) => {
                            disparadorDelTicketRef.current = e.currentTarget
                            onAbrirTicket(t.idComprobante)
                          }}
                        />
                      </td>
                    </tr>
                  ))}
                  {visibles.tickets.length === 0 && (
                    <tr>
                      <td colSpan={7} className="text-center text-muted py-4">
                        No hay tickets de este artículo en el período.
                      </td>
                    </tr>
                  )}
                </tbody>
                {visibles.tickets.length > 0 && (
                  <tfoot>
                    <tr className="fw-bold">
                      <td colSpan={4}>Total</td>
                      <td className="text-end">{formatearCantidad(visibles.cantidadTotal)}</td>
                      <td className="text-end">{formatearMoneda(visibles.total)}</td>
                      <td />
                    </tr>
                  </tfoot>
                )}
              </table>
            </div>
          </>
        )}
      </Modal>

      {idTicket !== null && (
        <DetalleDelTicket
          key={idTicket}
          idTicket={idTicket}
          nombreCliente={ticketEnDetalle ? (ticketEnDetalle.cliente ?? 'Consumidor final') : undefined}
          disparador={disparadorDelTicketRef.current}
          onCerrar={onCerrarTicket}
        />
      )}
    </>
  )
}
