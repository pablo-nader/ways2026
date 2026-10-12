import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import {
  clienteDeCuentaCorriente,
  disponibilidadPrevia,
  etiquetaDeMovimiento,
  motivoDeRangoInvalido,
  nombreCompletoDeCliente,
  rangoDeLosUltimosDias,
} from '../api/cuentaCorriente'
import type { ClienteListado, ComprobanteEmitido, EstadoDeCuenta, MedioPagoListado, PuntoVentaListado } from '../api/tipos'
import { fechaHoraATexto, isoATexto } from '../formato/fechas'
import { formatearImporte } from '../formato/importes'
import { CampoFecha } from './CampoFecha'
import { Cargando } from './Cargando'
import { Modal } from './Modal'
import { ModalPagoACuenta } from './ModalPagoACuenta'

const DIAS_DE_MOVIMIENTOS_POR_DEFECTO = 7

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function etiquetaDeCliente(cliente: ClienteListado): string {
  return `#${cliente.numero} — ${nombreCompletoDeCliente(cliente)}`
}

type Rango = { desde: string; hasta: string }

/** El estado de cuenta de un cliente para el rango que se pidió: sirve para mostrar solo lo que
 * corresponde al cliente elegido, y para decir de qué fechas es la tabla. */
type EstadoCargado = { idCliente: number; rango: Rango; datos: EstadoDeCuenta }

/** Lo que el POS necesita para imprimir el recibo de un pago a cuenta recién registrado. */
export type PagoACuentaRegistrado = {
  comprobante: ComprobanteEmitido
  cliente: ClienteListado
  /** Saldo que mostraba la pantalla antes de registrar el pago. */
  saldoEnPantalla: number
  /** Saldo que informó el servidor tras el pago; `null` si no se pudo leer. */
  saldoPosterior: number | null
}

type Props = {
  clienteInicial: ClienteListado | null
  puntoVenta: PuntoVentaListado
  medios: MedioPagoListado[]
  onCerrar: () => void
  /** Se avisa apenas se registra el pago, para que la venta en curso refresque el saldo de su cliente. */
  onPagoRegistrado: (idCliente: number) => void
  /** Se avisa una sola vez por pago, cuando se conoce el saldo posterior o se supo que no se puede leer. */
  onReciboListo?: (pago: PagoACuentaRegistrado) => void
}

/**
 * Consulta rápida de saldo y movimientos + pago a cuenta desde el POS, sin salir de la venta en
 * curso. Cliente por defecto: el ya elegido en la venta, salvo que sea Consumidor Final (sin cuenta
 * corriente real): ahí arranca en el buscador. El punto de venta lo fija el turno de la sesión
 * (`puntoVentaFijo` de `ModalPagoACuenta`), nunca un selector.
 *
 * Los movimientos son los de un rango de fechas (por defecto, los últimos siete días). Cada consulta
 * lleva su generación: una respuesta desactualizada nunca pisa la más reciente, pero el aviso del
 * saldo posterior al pago se entrega igual, porque el recibo no depende de lo que haya en pantalla.
 */
export function ModalCuentaCorrientePos({ clienteInicial, puntoVenta, medios, onCerrar, onPagoRegistrado, onReciboListo }: Props) {
  const [clienteElegido, setClienteElegido] = useState<ClienteListado | null>(
    clienteInicial && !clienteInicial.esConsumidorFinal ? clienteInicial : null,
  )

  const [terminoBusqueda, setTerminoBusqueda] = useState('')
  const [opcionesBusqueda, setOpcionesBusqueda] = useState<ClienteListado[]>([])
  const [buscando, setBuscando] = useState(false)
  const [errorBusqueda, setErrorBusqueda] = useState('')
  const generacionBusquedaRef = useRef(0)

  const [rangoInicial] = useState(() => rangoDeLosUltimosDias(DIAS_DE_MOVIMIENTOS_POR_DEFECTO))
  const [desde, setDesde] = useState(rangoInicial.desde)
  const [hasta, setHasta] = useState(rangoInicial.hasta)
  const motivoDeRango = motivoDeRangoInvalido(desde, hasta)

  const [estado, setEstado] = useState<EstadoCargado | null>(null)
  const [cargandoEstado, setCargandoEstado] = useState(false)
  const [errorEstado, setErrorEstado] = useState('')
  const generacionEstadoRef = useRef(0)
  // Cliente y rango de la última consulta lanzada: el efecto de abajo no repite una consulta que ya
  // se pidió (por ejemplo, la que dispara el propio registro de un pago).
  const consultaPedidaRef = useRef<string | null>(null)

  const [modalPagoAbierto, setModalPagoAbierto] = useState(false)
  const [aviso, setAviso] = useState('')

  // react-async-state regla 2: cada consulta toma su generación; una respuesta de un cliente o un
  // rango ya abandonados nunca pisa la más reciente. `alResolver` se invoca siempre, con el saldo o
  // `null` si falló, aunque la respuesta ya no sea la vigente.
  const cargarEstado = useCallback((idCliente: number, rango: Rango, alResolver?: (saldo: number | null) => void) => {
    const miGeneracion = (generacionEstadoRef.current += 1)
    consultaPedidaRef.current = `${idCliente}|${rango.desde}|${rango.hasta}`
    setCargandoEstado(true)
    setErrorEstado('')

    clienteDeCuentaCorriente
      .obtenerEstado(idCliente, rango.desde, rango.hasta, false)
      .then(
        (datos) => ({ datos, error: null }),
        (e: unknown) => ({ datos: null, error: e }),
      )
      .then((resultado) => {
        alResolver?.(resultado.datos ? resultado.datos.header.saldo : null)
        if (generacionEstadoRef.current !== miGeneracion) return
        if (resultado.datos) {
          setEstado({ idCliente, rango, datos: resultado.datos })
        } else {
          setEstado(null)
          setErrorEstado(resultado.error instanceof ErrorApi ? resultado.error.message : 'No se pudo cargar el estado de cuenta.')
        }
        setCargandoEstado(false)
      })
  }, [])

  const idClienteElegido = clienteElegido?.id ?? null
  useEffect(() => {
    if (idClienteElegido === null) {
      consultaPedidaRef.current = null
      return
    }
    if (motivoDeRango !== null) {
      // Un rango inválido no consulta, e invalida la consulta que pudiera estar en vuelo.
      generacionEstadoRef.current += 1
      consultaPedidaRef.current = null
      setCargandoEstado(false)
      setErrorEstado('')
      return
    }
    if (consultaPedidaRef.current === `${idClienteElegido}|${desde}|${hasta}`) return
    cargarEstado(idClienteElegido, { desde, hasta })
  }, [idClienteElegido, desde, hasta, motivoDeRango, cargarEstado])

  async function buscar() {
    if (buscando) return
    const miGeneracion = (generacionBusquedaRef.current += 1)
    setBuscando(true)
    setErrorBusqueda('')
    try {
      const pagina = await clienteDeClientes.listar(terminoBusqueda, false)
      if (generacionBusquedaRef.current !== miGeneracion) return
      // El Consumidor Final no tiene cuenta corriente real: nunca aparece como resultado acá,
      // aunque el término de búsqueda lo matchee.
      setOpcionesBusqueda(pagina.items.filter((c) => !c.esConsumidorFinal))
    } catch (e) {
      if (generacionBusquedaRef.current !== miGeneracion) return
      setErrorBusqueda(e instanceof ErrorApi ? e.message : 'No se pudieron buscar clientes.')
    } finally {
      if (generacionBusquedaRef.current === miGeneracion) setBuscando(false)
    }
  }

  function elegirCliente(id: number) {
    const encontrado = opcionesBusqueda.find((c) => c.id === id)
    if (!encontrado) return
    setAviso('')
    setClienteElegido(encontrado)
  }

  const estadoVigente = estado !== null && estado.idCliente === idClienteElegido ? estado : null
  const disponibilidad = estadoVigente
    ? disponibilidadPrevia(estadoVigente.datos.header.saldo, estadoVigente.datos.header.limiteCredito, estadoVigente.datos.header.creditoIlimitado)
    : null

  if (modalPagoAbierto && estadoVigente && clienteElegido) {
    const cliente = clienteElegido
    const saldoEnPantalla = estadoVigente.datos.header.saldo
    return (
      <ModalPagoACuenta
        idCliente={cliente.id}
        puntosVenta={[puntoVenta]}
        puntoVentaFijo={puntoVenta.id}
        medios={medios}
        header={estadoVigente.datos.header}
        onCerrar={() => setModalPagoAbierto(false)}
        onAntesDeEscribir={() => {
          // regla 3: bumpear la generación ANTES de la escritura; un refetch en vuelo de un cliente
          // ya abandonado no puede pisar el que dispara `onRegistrado` más abajo.
          generacionEstadoRef.current += 1
        }}
        onRegistrado={(comprobante) => {
          setModalPagoAbierto(false)
          setAviso(`Pago registrado: comprobante ${comprobante.numeroVisible}.`)
          // El pago recién hecho cae dentro del rango por defecto: se vuelve a él para verlo.
          const rango = rangoDeLosUltimosDias(DIAS_DE_MOVIMIENTOS_POR_DEFECTO)
          setDesde(rango.desde)
          setHasta(rango.hasta)
          // regla 6: el refetch queda aislado del try/catch de la escritura del modal de pago.
          cargarEstado(cliente.id, rango, (saldoPosterior) =>
            onReciboListo?.({ comprobante, cliente, saldoEnPantalla, saldoPosterior }),
          )
          // Si el cliente pagado es el de la venta en curso, su copia del saldo quedó vieja.
          onPagoRegistrado(cliente.id)
        }}
      />
    )
  }

  return (
    <Modal titulo="Cuenta corriente" tamano="lg" desplazable restaurarFoco={false} onCerrar={onCerrar}>
      <div className="d-flex justify-content-between align-items-center gap-2 mb-3">
        {clienteElegido ? <strong>{etiquetaDeCliente(clienteElegido)}</strong> : <span />}
        <div className="d-flex gap-2">
          {clienteElegido && (
            <button type="button" className="btn btn-outline-secondary btn-sm" onClick={() => setClienteElegido(null)}>
              Cambiar cliente
            </button>
          )}
          <button
            type="button"
            className="btn btn-primary btn-sm"
            disabled={!clienteElegido || cargandoEstado || estadoVigente === null}
            onClick={() => setModalPagoAbierto(true)}
          >
            Registrar pago
          </button>
        </div>
      </div>

      {!clienteElegido ? (
        <>
          <div className="input-group input-group-sm mb-3">
            <input
              type="search"
              className="form-control"
              placeholder="Buscar cliente…"
              aria-label="Buscar cliente de cuenta corriente"
              value={terminoBusqueda}
              disabled={buscando}
              onChange={(e) => setTerminoBusqueda(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), buscar())}
            />
            <button type="button" className="btn btn-outline-primary" disabled={buscando} onClick={buscar}>
              {buscando ? 'Buscando…' : 'Buscar'}
            </button>
          </div>
          {errorBusqueda && <div className="alert alert-danger py-1 px-2 small">{errorBusqueda}</div>}
          {opcionesBusqueda.length > 0 && (
            <select
              className="form-select"
              aria-label="Resultados de la búsqueda"
              size={Math.min(opcionesBusqueda.length, 6)}
              value=""
              onChange={(e) => elegirCliente(Number(e.target.value))}
            >
              <option value="" disabled>
                Elegir…
              </option>
              {opcionesBusqueda.map((c) => (
                <option key={c.id} value={c.id}>
                  {etiquetaDeCliente(c)}
                </option>
              ))}
            </select>
          )}
        </>
      ) : (
        <>
          {aviso && <div className="alert alert-success py-1 px-2 small">{aviso}</div>}
          {errorEstado && <div className="alert alert-danger py-1 px-2 small">{errorEstado}</div>}

          {cargandoEstado && <Cargando texto="Cargando estado de cuenta…" />}

          {!cargandoEstado && estadoVigente && (
            <div className="row g-3 mb-3">
              <div className="col-4">
                <div className="small text-muted">Saldo</div>
                <div className="fs-5">{formatearMoneda(estadoVigente.datos.header.saldo)}</div>
              </div>
              <div className="col-4">
                <div className="small text-muted">Límite</div>
                <div>{estadoVigente.datos.header.creditoIlimitado ? 'Ilimitado' : formatearMoneda(estadoVigente.datos.header.limiteCredito)}</div>
              </div>
              <div className="col-4">
                <div className="small text-muted">Disponible</div>
                <div>{disponibilidad === null ? 'Ilimitado' : formatearMoneda(disponibilidad)}</div>
              </div>
            </div>
          )}

          <h6>Movimientos</h6>
          <div className="row g-2 align-items-end mb-3">
            <div className="col-6 col-md-4">
              <label className="form-label small mb-1" htmlFor="cc-pos-desde">
                Desde
              </label>
              <CampoFecha id="cc-pos-desde" className="form-control form-control-sm" value={desde} onChange={setDesde} />
            </div>
            <div className="col-6 col-md-4">
              <label className="form-label small mb-1" htmlFor="cc-pos-hasta">
                Hasta
              </label>
              <CampoFecha id="cc-pos-hasta" className="form-control form-control-sm" value={hasta} onChange={setHasta} />
            </div>
          </div>

          {motivoDeRango !== null && <div className="alert alert-warning py-1 px-2 small">{motivoDeRango}</div>}

          {motivoDeRango === null && !cargandoEstado && estadoVigente && (
            <>
              {estadoVigente.datos.movimientos.length === 0 ? (
                <p className="text-muted small">
                  Sin movimientos entre el {isoATexto(estadoVigente.rango.desde)} y el {isoATexto(estadoVigente.rango.hasta)}.
                </p>
              ) : (
                <div className="table-responsive">
                  <table className="table table-sm">
                    <thead>
                      <tr>
                        <th>Fecha</th>
                        <th>Tipo</th>
                        <th className="text-end">Importe</th>
                        <th className="text-end">Saldo</th>
                      </tr>
                    </thead>
                    <tbody>
                      {estadoVigente.datos.movimientos.map((m) => (
                        <tr key={m.id}>
                          <td>{fechaHoraATexto(m.fecha)}</td>
                          <td>{etiquetaDeMovimiento(m)}</td>
                          <td className="text-end">{formatearMoneda(m.importe)}</td>
                          <td className="text-end">{formatearMoneda(m.saldoResultante)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          )}
        </>
      )}
    </Modal>
  )
}
