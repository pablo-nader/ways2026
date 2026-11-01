import { useEffect, useRef, useState } from 'react'
import { clienteDeCaja } from '../api/caja'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import { clienteDeVentas } from '../api/ventas'
import { ROL } from '../api/tipos'
import type {
  ComprobanteEmitido,
  DatosDeFacturaFiscal,   MedioPagoAlta,
  MedioPagoListado,
  TurnoResumen,
  VentaDeTurnoListado,
} from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'
import { BotonIcono, Icono } from '../componentes/BotonIcono'
import { Box } from '../componentes/Box'
import { CampoImporte } from '../componentes/CampoImporte'
import { Cargando } from '../componentes/Cargando'
import { EncabezadoOrdenable, useOrdenamiento, type ColumnasOrdenables } from '../componentes/grilla'
import { Modal } from '../componentes/Modal'
import { EstadoDeVentaEnFila, ModalDetalleDeVenta } from '../componentes/DetalleDeVenta'
import {
  FILTROS_VACIOS,
  etiquetaDeEstadoVenta,
  esAlcanzable,
  filtrarVentas,
  formatearFechaHora,
  formatearMoneda,
  hayFiltrosActivos,
  mediosDisponibles,
  puedeAnular,
  puedeReimprimir,
  puedeReimprimirFactura,
  puedeReimprimirNotaDeCredito,
  totalesDeVentas,
  totalesPorMedioDeVentas,
} from './utilidadesVentasDelTurno'
import type { EstadoFiltro, FiltrosDeVentasDelTurno } from './utilidadesVentasDelTurno'
import { pluralizar } from '../formato/pluralizar'
import { toast } from '../toasts/toasts'

const COLUMNAS_DE_VENTAS: ColumnasOrdenables<VentaDeTurnoListado> = {
  numero: { valor: (v) => v.numeroVisible },
  fecha: { valor: (v) => new Date(v.fecha) },
  cliente: { valor: (v) => v.nombreCliente },
  total: { valor: (v) => v.total },
  medios: { valor: (v) => v.mediosDePago.map((m) => m.nombre).join(', ') },
  estado: { valor: (v) => etiquetaDeEstadoVenta(v.estado) },
}

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

function mensajeDeErrorAnulacion(e: unknown): string {
  if (e instanceof ErrorApi) {
    if (e.estado === 403) return 'No tenés permiso para anular esta venta.'
    if (e.estado === 409 && e.codigo === 'venta_con_factura_en_tramite') return 'La factura está en trámite en ARCA; probá de nuevo en unos minutos.'
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
const MENSAJE_FACTURA_NO_DISPONIBLE = 'La factura de esta venta no está disponible para reimprimir.'
const MENSAJE_NC_NO_DISPONIBLE = 'La nota de crédito de esta venta no está disponible para reimprimir.'
const MENSAJE_NC_PENDIENTE = 'La nota de crédito quedó pendiente; se emitirá sola.'
const MENSAJE_NC_RECHAZADA = 'ARCA rechazó la nota de crédito.'
const MENSAJE_NC_NO_IMPRIMIBLE = 'La nota de crédito no está disponible para imprimir.'

type Props = {
  /** Reimpresión de escritorio (stage-desktop-pos): definido ⇒ el shell sabe imprimir — sin él,
   * ni la fila ni el modal de detalle ofrecen "Imprimir" (spec: la acción solo existe si hay
   * quién la sirva, nunca un botón que no hace nada). `medios` va junto al comprobante porque
   * `ticketDeVenta`/`algunPagoEnEfectivo` los necesitan para resolver nombre y comportamiento de
   * cada pago — la propia pantalla ya los tiene cargados para el modal de detalle. */
  alReimprimir?: (comprobante: ComprobanteEmitido, medios: MedioPagoListado[]) => void
  /** Reimpresión de la factura aprobada de una venta: definido ⇒ las filas con factura aprobada ofrecen "Reimprimir factura".
   * La factura sale del comprobante que devuelve el servidor al hacer click, nunca de lo que muestre el listado. */
  alReimprimirFactura?: (comprobante: ComprobanteEmitido, factura: DatosDeFacturaFiscal, medios: MedioPagoListado[]) => void
  /** Impresión de la nota de crédito aprobada de una venta anulada: al anular (primera impresión) y desde "Reimprimir NC".
   * Definido ⇒ se imprime sola al anular y las filas con NC aprobada ofrecen "Reimprimir NC". */
  alImprimirNotaDeCredito?: (
    comprobante: ComprobanteEmitido,
    notaDeCredito: DatosDeFacturaFiscal,
    facturaAsociada: DatosDeFacturaFiscal | null,
    medios: MedioPagoListado[],
    opciones: { reimpresion: boolean },
  ) => void
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
export function VentasDelTurno({ alReimprimir, alReimprimirFactura, alImprimirNotaDeCredito }: Props = {}) {
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
  const refrescarRef = useRef<HTMLButtonElement>(null)
  const cancelarAnularRef = useRef<HTMLButtonElement>(null)
  /** Se levanta al cerrar el modal de anulación: el foco vuelve recién cuando la pantalla deja de
   * estar bloqueada, porque hasta entonces el botón de la fila (y "Actualizar") siguen deshabilitados. */
  const focoPendienteRef = useRef(false)
  const [anulando, setAnulando] = useState(false)
  /** Estado fiscal de la nota de crédito o fallo de una anulación que terminó cuando su listado ya no era el de la pantalla. */
  const [avisoDeAnulacion, setAvisoDeAnulacion] = useState<{ error: boolean; texto: string } | null>(null)
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
    // La confirmación pertenece al listado que se está reemplazando: no sobrevive al cambio de contexto.
    if (filaAAnular) {
      disparadorRef.current = null
      focoPendienteRef.current = true
    }
    setFilaAAnular(null)
    setErrorAnular('')
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
    focoPendienteRef.current = true
    setFilaAAnular(null)
    setErrorAnular('')
  }

  /** Imprime la NC solo si ARCA ya la aprobó (con la factura anulada como comprobante asociado) y devuelve el aviso que
   * corresponda; sin NC en la respuesta (venta sin factura, servidor anterior) no hace ni dice nada. */
  function imprimirNotaDeCreditoDeAnulacion(anulada: ComprobanteEmitido): { error: boolean; texto: string } | null {
    const nc = anulada.fiscal?.notaDeCredito
    if (!nc) return null
    if (nc.estado === 'pendiente') return { error: false, texto: MENSAJE_NC_PENDIENTE }
    if (nc.estado === 'rechazado') return { error: true, texto: MENSAJE_NC_RECHAZADA }
    if (nc.estado !== 'aprobado') return null
    if (!nc.comprobante) return { error: true, texto: MENSAJE_NC_NO_IMPRIMIBLE }
    alImprimirNotaDeCredito?.(anulada, nc.comprobante, anulada.fiscal?.comprobante ?? null, medios, { reimpresion: false })
    return null
  }

  async function confirmarAnular() {
    if (anulandoRef.current || !filaAAnular) return
    const fila = filaAAnular
    anulandoRef.current = true
    setAnulando(true)
    setErrorAnular('')
    const miGeneracion = (generacionRef.current += 1)

    try {
      const anulada = await clienteDeVentas.anular(fila.id)
      // La NC ya está emitida en el servidor: se imprime aunque la pantalla haya cambiado de listado mientras tanto.
      const avisoDeNotaDeCredito = imprimirNotaDeCreditoDeAnulacion(anulada)
      if (generacionRef.current !== miGeneracion) {
        if (avisoDeNotaDeCredito) {
          setAvisoDeAnulacion({ error: avisoDeNotaDeCredito.error, texto: `La venta ${fila.numeroVisible} se anuló. ${avisoDeNotaDeCredito.texto}` })
        } else {
          toast.exito(`La venta ${fila.numeroVisible} se anuló.`)
        }
        return
      }
      if (avisoDeNotaDeCredito) {
        setAvisoDeAnulacion({ error: avisoDeNotaDeCredito.error, texto: `La venta ${fila.numeroVisible} se anuló. ${avisoDeNotaDeCredito.texto}` })
      }
      focoPendienteRef.current = true
      setFilaAAnular(null)
      if (turno) await cargarVentas(turno.id, miGeneracion)
    } catch (e) {
      if (generacionRef.current !== miGeneracion) {
        setAvisoDeAnulacion({ error: true, texto: `No se pudo anular la venta ${fila.numeroVisible}: ${mensajeDeErrorAnulacion(e)}` })
        return
      }
      setErrorAnular(mensajeDeErrorAnulacion(e))
    } finally {
      anulandoRef.current = false
      setAnulando(false)
    }
  }

  /** Tras un error el botón enfocado quedó deshabilitado durante la escritura y el foco cayó al
   * documento: vuelve a "Cancelar", dentro del modal. */
  useEffect(() => {
    if (errorAnular && !anulando) cancelarAnularRef.current?.focus()
  }, [errorAnular, anulando])

  /** Devuelve el foco al botón de la fila que abrió la confirmación; si esa fila ya no tiene botón
   * (la venta quedó anulada), a "Actualizar", que es estable. */
  useEffect(() => {
    if (bloqueado || !focoPendienteRef.current) return
    focoPendienteRef.current = false
    const destino = esAlcanzable(disparadorRef.current) ? disparadorRef.current : refrescarRef.current
    destino?.focus()
  }, [bloqueado])

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
   * del listado y, si está abierto, el detalle) para que el botón "Imprimir" desaparezca, y
   * se muestra un error explícito en vez de fallar en silencio. */
  async function reimprimirVenta(id: number, comoFactura = false) {
    if (comoFactura ? !alReimprimirFactura : !alReimprimir) return
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

      if (comoFactura) {
        // Sin factura aprobada en la respuesta no se imprime nada: un ticket en su lugar pasaría por la factura.
        const factura = comprobante.fiscal?.estado === 'aprobado' ? comprobante.fiscal.comprobante : null
        if (!factura) {
          setVentas((prev) => prev.map((v) => (v.id === id ? { ...v, estadoFiscal: comprobante.fiscal?.estado } : v)))
          setErroresReimprimir((prev) => ({ ...prev, [id]: MENSAJE_FACTURA_NO_DISPONIBLE }))
          return
        }
        alReimprimirFactura?.(comprobante, factura, medios)
        return
      }

      alReimprimir?.(comprobante, medios)
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

  /** "Reimprimir NC": también vuelve a pedir la venta y solo imprime con la NC aprobada y su comprobante en la respuesta;
   * nunca imprime un ticket ni una factura en su lugar. Comparte con las demás reimpresiones el bloqueo por fila. */
  async function reimprimirNotaDeCredito(id: number) {
    if (!alImprimirNotaDeCredito) return
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
      const nc = comprobante.fiscal?.notaDeCredito
      setVentas((prev) =>
        prev.map((v) => (v.id === id ? { ...v, estado: comprobante.estado, estadoNotaDeCredito: nc?.estado ?? null } : v)),
      )
      if (nc?.estado !== 'aprobado' || !nc.comprobante) {
        setErroresReimprimir((prev) => ({ ...prev, [id]: MENSAJE_NC_NO_DISPONIBLE }))
        return
      }
      alImprimirNotaDeCredito(comprobante, nc.comprobante, comprobante.fiscal?.comprobante ?? null, medios, { reimpresion: true })
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
  const ordenDeVentas = useOrdenamiento(ventasFiltradas, COLUMNAS_DE_VENTAS)
  const totales = totalesDeVentas(ventasFiltradas)
  const totalesPorMedio = totalesPorMedioDeVentas(ventasFiltradas)
  const opcionesDeMedio = mediosDisponibles(ventas)
  const ventaEnDetalle = idDetalle !== null ? ventas.find((v) => v.id === idDetalle) : undefined

  const herramientas = (
    <BotonIcono
      ref={refrescarRef}
      icono="actualizar"
      ocupado={buscandoTurno || cargandoVentas}
      disabled={bloqueado}
      onClick={() => void cargarTurnoYVentas()}
    />
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Ventas del turno" variante="inverse" herramientas={herramientas}>
        {avisoDeAnulacion && (
          <div
            className={`alert ${avisoDeAnulacion.error ? 'alert-danger' : 'alert-warning'} d-flex justify-content-between align-items-center gap-2`}
            role={avisoDeAnulacion.error ? 'alert' : 'status'}
          >
            <span>{avisoDeAnulacion.texto}</span>
            <BotonIcono icono="cerrar" etiqueta="Cerrar aviso" title="Cerrar aviso" onClick={() => setAvisoDeAnulacion(null)} />
          </div>
        )}

        {!puntoVenta && <div className="alert alert-warning mb-0">No hay un punto de venta asociado a este dispositivo.</div>}

        {puntoVenta && errorTurno && <div className="alert alert-danger">{errorTurno}</div>}

        {puntoVenta && !errorTurno && buscandoTurno && !turno && <Cargando />}

        {puntoVenta && !errorTurno && !buscandoTurno && !turno && (
          <div className="alert alert-warning mb-0">No hay un turno abierto en este punto de venta.</div>
        )}

        {turno && (
          <>
            {errorVentas && <div className="alert alert-danger">{errorVentas}</div>}
            {errorMedios && <div className="alert alert-warning py-1 px-2 small">{errorMedios}</div>}

            {cargandoVentas && ventas.length === 0 && <Cargando />}

            <div className="d-flex flex-wrap gap-3 align-items-center mb-3">
              {totalesPorMedio.length === 0 && <span className="small text-muted">Sin cobros para totalizar.</span>}
              {totalesPorMedio.map((t) => (
                <span key={t.idMedioPago} className="badge bg-secondary fs-6">
                  {t.nombre}: {formatearMoneda(t.total)}
                </span>
              ))}
              <span className="fw-bold">Total general: {formatearMoneda(totales.total)}</span>
              <span className="small text-muted">
                {pluralizar(totales.cantidad, 'venta')}
              </span>
            </div>

            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <EncabezadoOrdenable columna="numero" orden={ordenDeVentas.orden} onAlternar={ordenDeVentas.alternar}>
                      Número
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="fecha" orden={ordenDeVentas.orden} onAlternar={ordenDeVentas.alternar}>
                      Fecha
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="cliente" orden={ordenDeVentas.orden} onAlternar={ordenDeVentas.alternar}>
                      Cliente
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="total" orden={ordenDeVentas.orden} onAlternar={ordenDeVentas.alternar} className="text-end">
                      Total
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="medios" orden={ordenDeVentas.orden} onAlternar={ordenDeVentas.alternar}>
                      Medios de pago
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="estado" orden={ordenDeVentas.orden} onAlternar={ordenDeVentas.alternar}>
                      Estado
                    </EncabezadoOrdenable>
                    <th>Acciones</th>
                  </tr>
                  <tr>
                    <th>
                      <input
                        type="text"
                        className="form-control form-control-sm"
                        aria-label="Filtrar por número"
                        value={filtros.numero}
                        onChange={(e) => setFiltros((prev) => ({ ...prev, numero: e.target.value }))}
                      />
                    </th>
                    <th>
                      <input
                        type="text"
                        className="form-control form-control-sm"
                        aria-label="Filtrar por fecha"
                        value={filtros.fecha}
                        onChange={(e) => setFiltros((prev) => ({ ...prev, fecha: e.target.value }))}
                      />
                    </th>
                    <th>
                      <input
                        type="text"
                        className="form-control form-control-sm"
                        aria-label="Filtrar por cliente"
                        value={filtros.cliente}
                        onChange={(e) => setFiltros((prev) => ({ ...prev, cliente: e.target.value }))}
                      />
                    </th>
                    <th>
                      <div className="d-flex gap-1">
                        <CampoImporte
                          className="form-control form-control-sm"
                          aria-label="Total mínimo"
                          placeholder="Mín."
                          valor={filtros.totalMinimo}
                          onChange={(valor) => setFiltros((prev) => ({ ...prev, totalMinimo: valor }))}
                        />
                        <CampoImporte
                          className="form-control form-control-sm"
                          aria-label="Total máximo"
                          placeholder="Máx."
                          valor={filtros.totalMaximo}
                          onChange={(valor) => setFiltros((prev) => ({ ...prev, totalMaximo: valor }))}
                        />
                      </div>
                    </th>
                    <th>
                      <select
                        className="form-select form-select-sm"
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
                        className="form-select form-select-sm"
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
                      <BotonIcono
                        icono="limpiar"
                        disabled={!hayFiltrosActivos(filtros)}
                        onClick={() => setFiltros(FILTROS_VACIOS)}
                      />
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {ordenDeVentas.filas.map((v) => (
                    <tr key={v.id}>
                      <td>{v.numeroVisible}</td>
                      <td>{formatearFechaHora(v.fecha)}</td>
                      <td>{v.nombreCliente}</td>
                      <td className="text-end">{formatearMoneda(v.total)}</td>
                      <td>{v.mediosDePago.map((m) => m.nombre).join(', ') || '—'}</td>
                      <td>
                        <EstadoDeVentaEnFila venta={v} />
                      </td>
                      <td>
                        <div className="d-flex flex-wrap gap-2">
                          <BotonIcono icono="detalle" disabled={bloqueado} onClick={(e) => abrirDetalle(v, e.currentTarget)} />
                          {puedeAnular(v, rolId) && (
                            <BotonIcono icono="anular" disabled={bloqueado} onClick={(e) => pedirAnular(v, e.currentTarget)} />
                          )}
                          {alReimprimir && puedeReimprimir(v) && (
                            <BotonIcono
                              icono="imprimir"
                              ocupado={idsReimprimiendo.has(v.id)}
                              disabled={bloqueado || idsReimprimiendo.has(v.id)}
                              onClick={() => void reimprimirVenta(v.id)}
                            />
                          )}
                          {alReimprimirFactura && puedeReimprimirFactura(v) && (
                            <button
                              type="button"
                              className="btn btn-sm btn-outline-secondary d-inline-flex align-items-center gap-1"
                              aria-busy={idsReimprimiendo.has(v.id) || undefined}
                              disabled={bloqueado || idsReimprimiendo.has(v.id)}
                              onClick={() => void reimprimirVenta(v.id, true)}
                            >
                              <Icono icono="imprimir" />
                              Reimprimir factura
                            </button>
                          )}
                          {alImprimirNotaDeCredito && puedeReimprimirNotaDeCredito(v) && (
                            <button
                              type="button"
                              className="btn btn-sm btn-outline-secondary d-inline-flex align-items-center gap-1"
                              aria-busy={idsReimprimiendo.has(v.id) || undefined}
                              disabled={bloqueado || idsReimprimiendo.has(v.id)}
                              onClick={() => void reimprimirNotaDeCredito(v.id)}
                            >
                              <Icono icono="imprimir" />
                              Reimprimir NC
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
              <Modal
                titulo="Confirmar anulación"
                ocupado={anulando}
                restaurarFoco={false}
                etiquetaCerrar="Cerrar confirmación"
                onCerrar={cancelarAnular}
                pie={
                  <>
                    <button
                      ref={cancelarAnularRef}
                      type="button"
                      className="btn btn-outline-secondary"
                      autoFocus
                      disabled={anulando}
                      onClick={cancelarAnular}
                    >
                      Cancelar
                    </button>
                    <button type="button" className="btn btn-danger" disabled={anulando} onClick={() => void confirmarAnular()}>
                      {anulando ? 'Anulando…' : 'Anular venta'}
                    </button>
                  </>
                }
              >
                <p className="mb-2">
                  <strong>
                    ¿Anular la venta {filaAAnular.numeroVisible} por {formatearMoneda(filaAAnular.total)}?
                  </strong>
                </p>
                <p className="mb-0">Se revierte el stock y la cuenta corriente en la misma operación. No existe restaurar.</p>
                {errorAnular && (
                  <div className="alert alert-danger mt-3 mb-0" role="alert">
                    {errorAnular}
                  </div>
                )}
              </Modal>
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
