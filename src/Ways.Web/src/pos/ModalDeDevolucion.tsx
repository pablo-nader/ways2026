import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { clienteDeDevoluciones, type PrevisualizacionDeDevolucion, type TicketDevolvible } from '../api/devoluciones'
import type { ComprobanteEmitido, VentaDeTurnoListado } from '../api/tipos'
import { clienteDeVentas } from '../api/ventas'
import { Cargando } from '../componentes/Cargando'
import { Modal } from '../componentes/Modal'
import { fechaHoraATexto } from '../formato/fechas'
import { formatearImporte } from '../formato/importes'
import { ticketDeDevolucion, type ContextoDeImpresion } from '../impresion/plantillas'
import { useMontado } from '../paginas/familias/useMontado'
import {
  copiaDelMotivo,
  errorDeEmisionDeDevolucion,
  errorDeLecturaDeTicket,
  errorDeVistaPreviaDeDevolucion,
  firmaDeLaSeleccion,
  lineaPorCodigoDeBarra,
  lineasDeLaDevolucion,
  numeroDeTicketTipeado,
  solicitudDeDevolucion,
  sumarPorEscaneo,
  ticketsRecientes,
} from './devolucion'
import { ListaDeTicketsRecientes } from './ListaDeTicketsRecientes'
import { TablaDeLineasDeDevolucion } from './TablaDeLineasDeDevolucion'

/** Espera desde el último cambio de la selección hasta pedir la vista previa. */
const ESPERA_DE_LA_PREVIA_MS = 300

export type ImpresionDeDevolucion = {
  contexto: ContextoDeImpresion
  encolar: (descripcion: string, bytes: Uint8Array) => void
}

type Props = {
  /** Punto de venta de la sesión: el que se propone al buscar por número. */
  idPuntoVenta: number
  puntosVenta: readonly { id: number; nombre: string }[]
  /** Turno abierto, para ofrecer sus últimos tickets; sin turno solo se busca por número. */
  idTurno: number | null
  /** Medio de pago en efectivo del tenant; `''` si no hay ninguno configurado. */
  idMedioEfectivo: number | ''
  /** Solo en el POS de escritorio: sin impresora no se imprime nada. */
  impresion?: ImpresionDeDevolucion
  alCerrar: () => void
}

type Previa = { firma: string; estado: 'cargando' } | { firma: string; estado: 'error'; mensaje: string } | ({ firma: string; estado: 'ok' } & PrevisualizacionDeDevolucion)

type Resultado = { comprobante: ComprobanteEmitido; reintegro: number; impresionFallida: boolean }

const MENSAJE_DE_RELECTURA_FALLIDA = 'No se pudo volver a leer el ticket. Cerrá y volvé a abrir la devolución antes de confirmar.'

/**
 * Devolución en el POS: elegir el ticket de origen (por número o de los últimos del turno), cuánto devolver de cada línea, ver el
 * efectivo a reintegrar y emitir la nota de crédito. Siempre con conexión: no hay cola offline de devoluciones.
 *
 * Concurrencia (`react-async-state`): cada lectura lleva su generación y solo la última pedida se aplica; la vista previa solo vale
 * para la selección que la pidió, y confirmar queda bloqueado hasta que coincide. La emisión toma la guarda de reentrancia por ref
 * antes de su primer `await` y deja todo inerte hasta que responde; no hay otra acción que pueda reemplazar lo que escribe su respuesta.
 * Si la emisión termina con resultado incierto, el modal queda inerte y solo se puede cerrar: volver a abrirlo relee lo que quedó registrado.
 */
export function ModalDeDevolucion({ idPuntoVenta, puntosVenta, idTurno, idMedioEfectivo, impresion, alCerrar }: Props) {
  const idFormulario = useId()
  const montado = useMontado()

  const [idPuntoVentaDeBusqueda, setIdPuntoVentaDeBusqueda] = useState(idPuntoVenta)
  const [numeroTipeado, setNumeroTipeado] = useState('')
  const [ticket, setTicket] = useState<TicketDevolvible | null>(null)
  const [leyendoTicket, setLeyendoTicket] = useState(false)
  const [errorDeLectura, setErrorDeLectura] = useState('')
  const generacionDeTicket = useRef(0)

  const [recientes, setRecientes] = useState<VentaDeTurnoListado[] | null>(null)
  const [errorDeRecientes, setErrorDeRecientes] = useState('')
  const [reintentoDeRecientes, setReintentoDeRecientes] = useState(0)

  const [textos, setTextos] = useState<Record<number, string>>({})
  const [codigoEscaneado, setCodigoEscaneado] = useState('')
  const [avisoDeEscaneo, setAvisoDeEscaneo] = useState('')
  const [previa, setPrevia] = useState<Previa | null>(null)
  const generacionDePrevia = useRef(0)
  const firmaReleida = useRef<string | null>(null)

  const [avisoDeRelectura, setAvisoDeRelectura] = useState('')
  const [errorDeRelectura, setErrorDeRelectura] = useState('')
  const [errorDeEmision, setErrorDeEmision] = useState('')
  /** La emisión no se sabe si quedó registrada: sin clave de idempotencia, otra emisión podría reintegrar dos veces. Estado terminal. */
  const [resultadoIncierto, setResultadoIncierto] = useState(false)
  const [emitiendo, setEmitiendo] = useState(false)
  const emitiendoRef = useRef(false)
  const [resultado, setResultado] = useState<Resultado | null>(null)

  useEffect(() => {
    if (idTurno === null) return
    let vigente = true
    setErrorDeRecientes('')
    clienteDeVentas
      .listarPorTurno(idTurno)
      .then((ventas) => {
        if (vigente) setRecientes(ticketsRecientes(ventas))
      })
      .catch(() => {
        if (!vigente) return
        setRecientes([])
        setErrorDeRecientes('No se pudieron leer los últimos tickets del turno. Podés buscar el ticket por su número.')
      })
    return () => {
      vigente = false
    }
  }, [idTurno, reintentoDeRecientes])

  const seleccion = useMemo(() => (ticket !== null && ticket.devolvible ? lineasDeLaDevolucion(ticket.lineas, textos) : null), [ticket, textos])
  const firma = ticket !== null && seleccion !== null ? firmaDeLaSeleccion(ticket.id, seleccion.lineas) : null

  /** Relee el ticket (sin tocar lo tipeado: una cantidad que ya no entra queda marcada con su error). */
  async function releerTicket(idTicket: number, aviso: string) {
    const generacion = ++generacionDeTicket.current
    setErrorDeRelectura('')
    try {
      const actualizado = await clienteDeDevoluciones.obtener(idTicket)
      if (generacionDeTicket.current !== generacion || !montado.current) return
      setTicket(actualizado)
      setAvisoDeRelectura(aviso)
    } catch {
      if (generacionDeTicket.current !== generacion || !montado.current) return
      setErrorDeRelectura(MENSAJE_DE_RELECTURA_FALLIDA)
    }
  }

  useEffect(() => {
    if (ticket === null || seleccion === null || firma === null) {
      generacionDePrevia.current += 1
      setPrevia(null)
      return
    }
    const generacion = ++generacionDePrevia.current
    setPrevia({ firma, estado: 'cargando' })
    const temporizador = setTimeout(() => {
      clienteDeDevoluciones
        .previsualizar({ idPuntoVenta: ticket.idPuntoVenta, idComprobanteAsociado: ticket.id, lineas: seleccion.lineas })
        .then((respuesta) => {
          if (generacionDePrevia.current !== generacion) return
          setPrevia({ firma, estado: 'ok', ...respuesta })
        })
        .catch((e) => {
          if (generacionDePrevia.current !== generacion) return
          const rechazo = errorDeVistaPreviaDeDevolucion(e)
          setPrevia({ firma, estado: 'error', mensaje: rechazo.mensaje })
          if (rechazo.releer && firmaReleida.current !== firma) {
            firmaReleida.current = firma
            void releerTicket(ticket.id, rechazo.mensaje)
          }
        })
    }, ESPERA_DE_LA_PREVIA_MS)
    return () => {
      clearTimeout(temporizador)
      generacionDePrevia.current += 1
    }
    // `releerTicket` solo usa refs y setters estables.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ticket, seleccion, firma])

  async function leerTicket(pedir: () => Promise<TicketDevolvible>) {
    const generacion = ++generacionDeTicket.current
    setLeyendoTicket(true)
    setErrorDeLectura('')
    try {
      const leido = await pedir()
      if (generacionDeTicket.current !== generacion || !montado.current) return
      setTicket(leido)
      setTextos({})
      setCodigoEscaneado('')
      setAvisoDeEscaneo('')
      setAvisoDeRelectura('')
      setErrorDeRelectura('')
      setErrorDeEmision('')
      firmaReleida.current = null
    } catch (e) {
      if (generacionDeTicket.current !== generacion || !montado.current) return
      setErrorDeLectura(errorDeLecturaDeTicket(e))
    } finally {
      if (generacionDeTicket.current === generacion && montado.current) setLeyendoTicket(false)
    }
  }

  function buscarPorNumero() {
    const numero = numeroDeTicketTipeado(numeroTipeado)
    if (numero === null) {
      setErrorDeLectura('Ingresá el número del ticket (por ejemplo 12 o 0001-00000012).')
      return
    }
    void leerTicket(() => clienteDeDevoluciones.buscarPorNumero(idPuntoVentaDeBusqueda, numero))
  }

  function elegirOtroTicket() {
    generacionDeTicket.current += 1
    setTicket(null)
    setLeyendoTicket(false)
    setTextos({})
    setErrorDeLectura('')
    setAvisoDeRelectura('')
    setErrorDeRelectura('')
    setErrorDeEmision('')
  }

  function cambiarCantidad(posicion: number, texto: string) {
    setTextos((previos) => ({ ...previos, [posicion]: texto }))
    setAvisoDeEscaneo('')
    setAvisoDeRelectura('')
    setErrorDeEmision('')
  }

  function escanear() {
    if (ticket === null) return
    const codigo = codigoEscaneado
    setCodigoEscaneado('')
    if (codigo.trim() === '') return
    const posicion = lineaPorCodigoDeBarra(ticket.lineas, codigo)
    if (posicion === -1) {
      setAvisoDeEscaneo('Ese código no está en el ticket.')
      return
    }
    const { texto, aviso } = sumarPorEscaneo(ticket.lineas[posicion], textos[posicion] ?? '')
    setTextos((previos) => ({ ...previos, [posicion]: texto }))
    setAvisoDeEscaneo(aviso ?? '')
    setAvisoDeRelectura('')
    setErrorDeEmision('')
  }

  async function confirmar() {
    if (emitiendoRef.current) return
    if (ticket === null || seleccion === null || previa === null || previa.estado !== 'ok' || idMedioEfectivo === '') return
    emitiendoRef.current = true
    setEmitiendo(true)
    setErrorDeEmision('')
    setAvisoDeRelectura('')
    const origen = ticket
    const elegido = seleccion
    const aprobada = previa
    try {
      let comprobante: ComprobanteEmitido
      try {
        comprobante = await clienteDeVentas.emitir(
          solicitudDeDevolucion({ ticket: origen, lineas: elegido.lineas, reintegro: aprobada.reintegro, idMedioEfectivo }),
        )
      } catch (e) {
        if (!montado.current) return
        const rechazo = errorDeEmisionDeDevolucion(e)
        setErrorDeEmision(rechazo.mensaje)
        if (rechazo.incierto) setResultadoIncierto(true)
        else if (rechazo.releer) void releerTicket(origen.id, '')
        return
      }
      // La devolución ya quedó registrada: el ticket y el cajón salen aunque la pantalla se haya ido, y una falla al imprimir no la desdice.
      let impresionFallida = false
      try {
        impresion?.encolar(
          'el ticket de devolución',
          ticketDeDevolucion(
            {
              numeroVisible: comprobante.numeroVisible,
              fecha: comprobante.fecha,
              ticketAsociado: origen.numeroVisible,
              lineas: elegido.posiciones.map((posicion, i) => ({
                descripcion: origen.lineas[posicion].descripcion,
                cantidad: elegido.lineas[i].cantidad,
                credito: aprobada.lineas[i]?.credito ?? 0,
              })),
              reintegro: aprobada.reintegro,
            },
            impresion.contexto,
          ),
        )
      } catch {
        impresionFallida = true
      }
      if (!montado.current) return
      setResultado({ comprobante, reintegro: aprobada.reintegro, impresionFallida })
    } finally {
      emitiendoRef.current = false
      if (montado.current) setEmitiendo(false)
    }
  }

  const bloqueado = emitiendo || leyendoTicket || resultadoIncierto
  const creditos: Record<number, number> = {}
  if (previa?.estado === 'ok' && previa.firma === firma && seleccion !== null) {
    seleccion.posiciones.forEach((posicion, i) => {
      const linea = previa.lineas[i]
      if (linea !== undefined) creditos[posicion] = linea.credito
    })
  }
  const previaVigente = previa !== null && previa.firma === firma ? previa : null
  const puedeConfirmar = previaVigente?.estado === 'ok' && idMedioEfectivo !== '' && errorDeRelectura === '' && !bloqueado

  if (resultado !== null) {
    return (
      <Modal
        titulo="Devolución"
        tamano="lg"
        restaurarFoco={false}
        onCerrar={alCerrar}
        pie={
          <button type="button" className="btn btn-primary" autoFocus onClick={alCerrar}>
            Cerrar
          </button>
        }
      >
        <div className="alert alert-success mb-2" role="status">
          Devolución {resultado.comprobante.numeroVisible} registrada. Reintegrá {formatearImporte(resultado.reintegro, { simbolo: true })} en efectivo.
        </div>
        {resultado.impresionFallida && (
          <div className="alert alert-warning mb-2" role="alert">
            No se pudo imprimir el ticket de la devolución. La devolución ya está registrada: no la repitas.
          </div>
        )}
        {impresion === undefined && <p className="text-muted mb-0">Esta caja no imprime tickets.</p>}
      </Modal>
    )
  }

  return (
    <Modal
      titulo="Devolución"
      tamano="xl"
      desplazable
      ocupado={emitiendo}
      restaurarFoco={false}
      onCerrar={alCerrar}
      pie={
        <>
          <button type="button" className="btn btn-outline-secondary" disabled={emitiendo} onClick={alCerrar}>
            {resultadoIncierto ? 'Cerrar' : 'Cancelar'}
          </button>
          {ticket !== null && ticket.devolvible && (
            <button type="button" className="btn btn-primary" disabled={!puedeConfirmar} onClick={() => void confirmar()}>
              {emitiendo ? 'Registrando…' : 'Confirmar devolución'}
            </button>
          )}
        </>
      }
    >
      {ticket === null ? (
        <>
          <form
            className="row g-2 align-items-end mb-3"
            onSubmit={(evento) => {
              evento.preventDefault()
              buscarPorNumero()
            }}
          >
            {puntosVenta.length > 1 && (
              <div className="col-12 col-md-4">
                <label htmlFor={`${idFormulario}-pv`} className="form-label">
                  Punto de venta
                </label>
                <select
                  id={`${idFormulario}-pv`}
                  className="form-select"
                  value={idPuntoVentaDeBusqueda}
                  disabled={bloqueado}
                  onChange={(evento) => setIdPuntoVentaDeBusqueda(Number(evento.target.value))}
                >
                  {puntosVenta.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.nombre}
                    </option>
                  ))}
                </select>
              </div>
            )}
            <div className="col-12 col-md-5">
              <label htmlFor={`${idFormulario}-numero`} className="form-label">
                Número de ticket
              </label>
              <input
                id={`${idFormulario}-numero`}
                className="form-control"
                inputMode="numeric"
                autoComplete="off"
                autoFocus
                value={numeroTipeado}
                disabled={bloqueado}
                onChange={(evento) => {
                  setNumeroTipeado(evento.target.value)
                  setErrorDeLectura('')
                }}
              />
            </div>
            <div className="col-auto">
              <button type="submit" className="btn btn-primary" disabled={bloqueado || numeroTipeado.trim() === ''}>
                {leyendoTicket ? 'Buscando…' : 'Buscar'}
              </button>
            </div>
          </form>
          {errorDeLectura !== '' && (
            <div className="alert alert-danger" role="alert">
              {errorDeLectura}
            </div>
          )}
          {idTurno !== null && (
            <>
              <h3 className="h6">Últimos tickets del turno</h3>
              <ListaDeTicketsRecientes
                tickets={recientes}
                error={errorDeRecientes}
                bloqueado={bloqueado}
                alElegir={(idTicket) => void leerTicket(() => clienteDeDevoluciones.obtener(idTicket))}
                alReintentar={() => {
                  setRecientes(null)
                  setReintentoDeRecientes((n) => n + 1)
                }}
              />
            </>
          )}
        </>
      ) : (
        <>
          <div className="d-flex flex-wrap justify-content-between align-items-center gap-2 mb-3">
            <div>
              <strong>Ticket {ticket.numeroVisible}</strong>
              <span className="text-muted">
                {' '}
                · {fechaHoraATexto(ticket.fecha)} · {ticket.nombreCliente ?? 'Consumidor final'} · {formatearImporte(ticket.total, { simbolo: true })}
              </span>
            </div>
            <button type="button" className="btn btn-sm btn-outline-secondary" disabled={bloqueado} onClick={elegirOtroTicket}>
              Elegir otro ticket
            </button>
          </div>

          {!ticket.devolvible ? (
            <div className="alert alert-warning mb-0" role="alert">
              {copiaDelMotivo(ticket.motivo)}
            </div>
          ) : (
            <>
              <div className="mb-3">
                <label htmlFor={`${idFormulario}-codigo`} className="form-label">
                  Escanear código de barras
                </label>
                <input
                  id={`${idFormulario}-codigo`}
                  className="form-control"
                  autoComplete="off"
                  autoFocus
                  value={codigoEscaneado}
                  disabled={bloqueado}
                  onChange={(evento) => setCodigoEscaneado(evento.target.value)}
                  onKeyDown={(evento) => {
                    if (evento.key !== 'Enter') return
                    evento.preventDefault()
                    escanear()
                  }}
                />
                {avisoDeEscaneo !== '' && <div className="form-text text-warning">{avisoDeEscaneo}</div>}
              </div>

              <TablaDeLineasDeDevolucion
                lineas={ticket.lineas}
                textos={textos}
                creditos={creditos}
                bloqueado={bloqueado}
                alCambiarCantidad={cambiarCantidad}
              />

              <div className="d-flex justify-content-end align-items-baseline gap-3 mt-3">
                <span>A reintegrar en efectivo</span>
                <strong className="fs-4" aria-live="polite">
                  {previaVigente?.estado === 'ok' ? formatearImporte(previaVigente.reintegro, { simbolo: true }) : '—'}
                </strong>
              </div>
              {previaVigente?.estado === 'cargando' && <Cargando />}
              {previaVigente?.estado === 'error' && (
                <div className="alert alert-danger mt-2 mb-0" role="alert">
                  {previaVigente.mensaje}
                </div>
              )}
              {idMedioEfectivo === '' && (
                <div className="alert alert-danger mt-2 mb-0" role="alert">
                  No hay un medio de pago en efectivo configurado: la devolución se reintegra en efectivo.
                </div>
              )}
            </>
          )}
          {avisoDeRelectura !== '' && (
            <div className="alert alert-info mt-3 mb-0" role="status">
              {avisoDeRelectura}
            </div>
          )}
          {errorDeRelectura !== '' && (
            <div className="alert alert-danger mt-3 mb-0" role="alert">
              {errorDeRelectura}
            </div>
          )}
          {errorDeEmision !== '' && (
            <div className="alert alert-danger mt-3 mb-0" role="alert">
              {errorDeEmision}
            </div>
          )}
        </>
      )}
    </Modal>
  )
}
