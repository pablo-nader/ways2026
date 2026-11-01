import { useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Link, useNavigate, useSearchParams } from 'react-router'
import {
  formatearPorcentajeDeAjuste,
  formatearPorcentajeDeAjusteConSigno,
  mensajeDeRechazoDeAjusteManual,
  rotuloDeAjusteManual,
  validarPorcentajeDeAjuste,
} from '../api/ajusteManual'
import { clienteDeArticulos } from '../api/articulos'
import { clienteDeCaja } from '../api/caja'
import {
  atributosDeCampoDeCantidad,
  esCantidadValida,
  esFraccionDeArticuloPorUnidad,
  esTextoDeCantidadValido,
  fraccionaUnaUnidad,
  MENSAJE_DE_CANTIDAD_ENTERA,
  restriccionDeCantidad,
} from '../api/cantidadPorUnidad'
import {
  idLineaDestinoDeEscaneo,
  nuevoIdLinea,
  reducirCarrito,
  type AccionCarrito,
  type ArticuloParaCarrito,
  type LineaCarrito,
} from '../api/carrito'
import { clienteDeCatalogo } from '../api/catalogos'
import {
  entradaConPuntoDecimal,
  esTeclaDeAtajo,
  MENSAJE_DE_ATAJO_SIN_DATOS,
  MENSAJE_DE_CANTIDAD_DE_IMPORTE,
  MENSAJE_DE_IMPORTE_INVALIDO,
  mensajeDeAtajoSinArticulo,
  mensajeDeLineaPorImporteObsoleta,
  mensajeDeAtajoSinImporte,
  mensajeDeImporteRequerido,
} from '../api/importeDeVenta'
import { copiaDeArticuloNoVendible } from '../api/articuloNoVendible'
import { api, ErrorApi, ErrorDeRed } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import { esSaldoEstimado, nombreCompletoDeCliente, saldosDeUnPagoACuenta } from '../api/cuentaCorriente'
import { corriendoEnTauri, establecerTokenDeSesionBearer } from '../api/entornoTauri'
import { terminarSesionLocalDelPos } from '../pos/finDeSesionLocal'
import { PrecioUnitarioDeLinea } from '../pos/PrecioUnitarioDeLinea'
import { importeDeEnvioAplicado, importeDeEnvioVigente, mensajeDeRechazoDeEnvio, motivoDeEnvioPendiente } from '../pos/envio'
import { clienteDeOfertas } from '../api/ofertas'
import {
  aPagosDeVenta,
  calcularExcedente,
  calcularFaltante,
  consumoCuentaCorriente,
  filaPagoInicial,
  filaPagoVacia,
  filasAPagosConVuelto,
  filasAPagosParaCalculo,
  filasConMedioInvalidoReseteado,
  idMedioEfectivo,
  medioDisponibleParaCliente,
  sumarImportes,
  sumarVueltos,
  superaLimiteDeCredito,
  validarPagosLocal,
  type FilaPago,
} from '../api/pagos'
import { aSolicitudDeVentaDesdePresupuesto, clienteDePresupuestos } from '../api/presupuestos'
import { puedeForzarCierreSinRendicion } from '../api/tipos'
import type {
  ArticuloEscaneado,
  AtajoDeImporte,
  ClienteListado,
  ComprobanteEmitido,
  ComboAplicadoEnPrevia,
  InstantaneaDePos,
  MedioPagoAlta,
  MedioPagoListado,
  ParametroResuelto,
  PresupuestoParaVenta,
  ResumenDeCierrePorRetiro,
  SolicitudDeCierrePorRetiro,
  TeclaAtajo,
  TurnoResumen,
} from '../api/tipos'
import { comboVigenteDeLinea, rotuloDeComboAplicado } from '../api/combos'
import {
  aLineaDeCarritoDesdeEscaneo,
  aLineasDeResolucion,
  aSolicitudDeVenta,
  calcularAjustesManualesPrevia,
  calcularSubtotalPrevia,
  clienteDeVentas,
  indexarResolucionPorLinea,
  previaDeLinea,
  type PreciosPorLinea,
} from '../api/ventas'
import { useAuth } from '../auth/useAuth'
import { BotonIcono } from '../componentes/BotonIcono'
import { Box } from '../componentes/Box'
import { CampoImporte } from '../componentes/CampoImporte'
import { conTiempoLimite } from '../pos/tiempoLimite'
import { Cargando } from '../componentes/Cargando'
import { ChipDeSaldo } from '../componentes/ChipDeSaldo'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { Modal } from '../componentes/Modal'
import { ModalDeBusquedaDeArticulos } from '../componentes/ModalDeBusquedaDeArticulos'
import { ModalDeBusquedaDeCombos } from '../componentes/ModalDeBusquedaDeCombos'
import { ModalCuentaCorrientePos } from '../componentes/ModalCuentaCorrientePos'
import { ModalAltaRapidaDeCliente } from '../pos/ModalAltaRapidaDeCliente'
import { altaRapidaDeClienteDisponible } from '../pos/altaRapidaDeCliente'
import type { PagoACuentaRegistrado } from '../componentes/ModalCuentaCorrientePos'
import { SelectorConBusqueda, type OpcionDeSelector } from '../componentes/SelectorConBusqueda'
import { SelectorDeMiembrosDeCombo } from '../componentes/SelectorDeMiembrosDeCombo'
import { formatearImporte, formatearImporteConSigno } from '../formato/importes'
import { formatearNumeroDecimal, parsearNumeroDecimal } from '../formato/numeros'
import { cierreDeTurno, pulsoDeCajon, qrDeCobroMercadoPago, ticketDePagoACuenta, ticketRetiroDeEfectivo } from '../impresion/plantillas'
import type { ContextoDeImpresion } from '../impresion/plantillas'
import {
  componentesAElegir,
  motivoDeComboNoCargable,
  parsearCodigoDeCombo,
  resolverLineasDeCombo,
  rotuloDeCodigoDeCombo,
  type ComboParaCargar,
} from '../pos/cargaDeCombos'
import { construirComprobanteOfflineSintetico } from '../pos/comprobanteOfflineSintetico'
import { elegirFuenteDeCombos, fuenteDesdeApi, type ComboDeLista } from '../pos/fuenteDeCombos'
import {
  aClienteListado,
  aMedioPagoListado,
  atajosDeImporteOffline,
  buscarClientesOffline,
  formatearVejezDeInstantanea,
  lineasConImporteDeArticuloOrdinario,
  resolverAtajoEnLinea,
  resolverAtajoOffline,
  resolverEscaneoOffline,
  resolverPreviaOffline,
  todasLasLineasTienenPrecioOffline,
} from '../pos/instantaneaOffline'
import { guardarTurnoConfirmadoLocal, leerTurnoConfirmadoLocal, LIMITE_DE_ESPERA_DEL_TURNO_MS } from '../pos/turnoConfirmadoLocal'
import {
  construirNumeroVisible,
  mensajeDeRechazoOffline,
  rechazoOfflineEsDefinitivo,
  type MotivoRechazoOffline,
} from '../pos/outboxOffline'
import { clienteAdmitidoOffline, medioAdmitidoOffline } from '../pos/reglasOffline'
import { BorradorDeTicketContext } from '../pos/BorradorDeTicketContext'
import type { BorradorDeTicket } from '../pos/BorradorDeTicketContext'
import { useRegistroDeAccionesDePos } from '../pos/AccionesDePosContext'
import type { EstadoDeAccionesDePos } from '../pos/AccionesDePosContext'
import { RanuraHeaderPosContext } from '../pos/RanuraHeaderPosContext'
import { useSincronizacionOffline } from '../pos/useSincronizacionOffline'
import { clienteDeCobrosMercadoPago, mensajeDeErrorDeCobroMercadoPago } from '../api/cobrosMercadoPago'
import {
  indicacionDeCobroAprobado,
  MENSAJE_DE_VARIOS_COBROS,
  planDeCobroMercadoPago,
  TEXTO_POR_CIERRE_SIN_APROBAR,
  vistaDelCobro,
} from '../pos/cobroMercadoPago'
import { cobrarConMercadoPago, cobroAAPartar, resolverResultadoDelCobro, sanarReservaYaVendida, type CobroAAPartar } from '../pos/cobrarConMercadoPago'
import type { CobroSinVenta, ReservaDeCobro } from '../pos/reservaDeCobro'
import { useModalDeCobroMercadoPago } from '../pos/useModalDeCobroMercadoPago'
import { useConfirmacionDeReusoDeCobro } from '../pos/useConfirmacionDeReusoDeCobro'
import { conFiscalSoloSiSeguroSeFactura, ESPERA_DE_LA_FACTURA_MS, ventaSeguroSeFactura } from '../pos/facturacionAlCobrar'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'
import { pluralizar } from '../formato/pluralizar'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

/** Un combo que llegó al carrito cuando el cobro ya había empezado no se agrega: se avisa en vez de descartarlo en silencio. */
const MENSAJE_DE_COMBO_CON_COBRO_EN_CURSO = 'No se agregó el combo: hay un cobro en curso.'
/** Se agrega al aviso de un artículo que no apareció cuando tampoco se pudieron leer los combos con ese código. */
const MENSAJE_DE_COMBOS_SIN_LEER = 'No se pudieron leer los combos.'

/** Escaneo en el servidor. Un importe con coma (`150,50*500`) que el servidor rechaza se reintenta UNA vez con punto: para un
 * artículo común el servidor lee la coma como separador de miles, así que la corrección solo se prueba después de su rechazo. */
async function escanearEnElServidor(entrada: string): Promise<ArticuloEscaneado> {
  try {
    return await clienteDeArticulos.escanear(entrada)
  } catch (e) {
    const conPunto = e instanceof ErrorApi && e.codigo === 'importe_invalido' ? entradaConPuntoDecimal(entrada) : null
    if (conPunto === null) throw e
    return clienteDeArticulos.escanear(conPunto)
  }
}

function fusionarOpcionesCliente(opciones: ClienteListado[], seleccionado: ClienteListado | null): ClienteListado[] {
  if (!seleccionado) return opciones
  return opciones.some((c) => c.id === seleccionado.id) ? opciones : [seleccionado, ...opciones]
}

function etiquetaDeCliente(c: ClienteListado): string {
  const nombreCompleto = c.razonSocial ?? [c.nombre, c.apellido].filter(Boolean).join(' ')
  return `#${c.numero} — ${nombreCompleto}`
}

type OpcionDeCliente = OpcionDeSelector & { cliente: ClienteListado }

function aOpcionDeCliente(cliente: ClienteListado): OpcionDeCliente {
  return { id: cliente.id, etiqueta: etiquetaDeCliente(cliente), cliente }
}

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

/** judgment-day ronda 2 (SUGGESTION): concordancia singular/plural — "1 venta(s) necesitan
 * atención" conjugaba el verbo siempre en plural, incluso con cantidad 1 ("1 venta necesitan
 * atención" es agramatical). */
/** Un cobro online que se quedó sin respuesta: el servidor pudo haberlo registrado. */
const MENSAJE_DE_COBRO_INCIERTO =
  'No se pudo confirmar si la venta se registró: el servidor no respondió. Revisá las ventas del turno antes de volver a cobrar.'

function fraseVentasNecesitanAtencion(cantidad: number): string {
  return cantidad === 1 ? '1 venta necesita atención' : `${cantidad} ventas necesitan atención`
}

/** Etiqueta de un campo de una fila de pago: el nombre del medio solo no alcanza (dos filas
 * pueden compartir medio, ej. un split de efectivo) — se le suma `idFila` para que cada input
 * tenga un `aria-label` unívoco en pantalla. */
function etiquetaDeCampoFila(prefijo: string, medioDeFila: MedioPagoListado | null, idFila: number): string {
  return `${prefijo} de ${medioDeFila?.nombre ?? 'medio de pago'} (fila ${idFila})`
}

type PropsPanelGateTurno = {
  idPuntoVenta: number
  onAbierto: (turno: TurnoResumen) => void
}

/**
 * Gate seam de checkout (stage-6-turnos-caja, Slice 7, design: Web Composition — "Pos.tsx gate
 * seam"): un `409 turno_no_abierto` de `POST /api/ventas` reemplaza el panel de cobro por esto
 * en vez de mostrar el error crudo — ofrece abrir el turno ahí mismo. Tras la apertura la venta
 * NUNCA se reintenta sola (react-async-state regla 9: un reintento automático de un checkout es
 * exactamente el defecto de doble venta que esa regla existe para evitar) — el cajero vuelve a
 * apretar "Cobrar" a mano, con el carrito y el panel de pagos intactos.
 */
function PanelGateTurno({ idPuntoVenta, onAbierto }: PropsPanelGateTurno) {
  const [fondoInicial, setFondoInicial] = useState<number | null>(null)
  const [observaciones, setObservaciones] = useState('')
  const [abriendo, setAbriendo] = useState(false)
  const abriendoRef = useRef(false)
  const [error, setError] = useState('')

  async function abrir() {
    // regla 9 (react-async-state): guard de reentrancia de primera línea.
    if (abriendoRef.current) return

    // `fondoInicial < 0` es inalcanzable: `CampoImporte` de este campo no tiene
    // `admiteNegativos`, así que nunca puede emitir un número negativo (mismo criterio que
    // `PanelAperturaDeTurnoEnModal`, CuentaCorriente.tsx).
    if (fondoInicial === null) {
      setError('El fondo inicial es obligatorio.')
      return
    }

    abriendoRef.current = true
    setAbriendo(true)
    setError('')

    try {
      const turno = await clienteDeCaja.abrir({
        idPuntoVenta,
        fondoInicial,
        observaciones: observaciones.trim() === '' ? null : observaciones.trim(),
      })
      onAbierto(turno)
    } catch (e) {
      if (e instanceof ErrorApi && e.codigo === 'turno_ya_abierto') {
        // Autocuración (mismo criterio que `FormularioApertura` en Caja.tsx, react-async-state
        // regla 10 — el mismo self-heal replicado en ambos gemelos): otra pestaña/cajero ganó la
        // carrera de apertura entre que se abrió este gate y el click. El turno YA está abierto,
        // así que la continuación de éxito es la correcta — reintentar solo repetiría el mismo
        // 409. Se vuelve a consultar el turno abierto real (esta rama nunca tuvo uno: el 409 no
        // trae el turno en el body) para poder avisarle al llamador cuál es. El carrito y los
        // pagos siguen intactos, el cajero vuelve a apretar "Cobrar" a mano (react-async-state
        // regla 9: ningún reintento automático).
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
    }
  }

  return (
    <div className="container-fluid py-4" key="gate-turno">
      <div className="row g-3">
        <div className="col-12">
          <Box titulo="No hay un turno abierto" variante="warning">
            <p className="text-muted">
              Para cobrar hace falta abrir un turno de caja en este punto de venta. El carrito y los pagos que ya
              cargaste quedan como están — al abrir el turno volvés a esta pantalla para apretar «Cobrar» de nuevo.
            </p>
            {error && <div className="alert alert-danger py-1 px-2 small">{error}</div>}

            <div className="row g-2 align-items-end" style={{ maxWidth: 640 }}>
              <div className="col-md-4">
                <label className="form-label" htmlFor="pos-gate-fondo-inicial">
                  Fondo inicial
                </label>
                <CampoImporte
                  id="pos-gate-fondo-inicial"
                  className="form-control"
                  valor={fondoInicial}
                  disabled={abriendo}
                  onChange={setFondoInicial}
                />
              </div>
              <div className="col-md-5">
                <label className="form-label" htmlFor="pos-gate-observaciones">
                  Observaciones
                </label>
                <input
                  id="pos-gate-observaciones"
                  type="text"
                  className="form-control"
                  value={observaciones}
                  disabled={abriendo}
                  onChange={(e) => setObservaciones(e.target.value)}
                />
              </div>
              <div className="col-md-3">
                <button type="button" className="btn btn-primary w-100" disabled={abriendo} onClick={abrir}>
                  {abriendo ? 'Abriendo…' : 'Abrir turno'}
                </button>
              </div>
            </div>
          </Box>
        </div>
      </div>
    </div>
  )
}

type PropsConfirmacionDeCobro = {
  total: number
  pagado: number
  vuelto: number
  /** judgment-day ronda 1 (K4): bajo vista previa fallida, `total`/`pagado`/`vuelto` no son
   * confiables (el total sale de la propia suma de importes, nunca del servidor) — mostrar
   * "$0,00" ahí sería un número inventado que el cajero podría leer como una confirmación real.
   * Con esto en `true`, el diálogo NO renderiza montos: solo el aviso, y `onFinalizar` sigue
   * disparando `cobrar()` de siempre (el servidor es la autoridad final del total, mismo criterio
   * que el resto de la pantalla bajo vista previa fallida). */
  previaFallida: boolean
  ocupado: boolean
  onFinalizar: () => void
  onCancelar: () => void
}

/**
 * Diálogo de confirmación de F9 (stage-pos-atajos-cobro): a diferencia de `ConfirmacionDeBaja`
 * (que arma su propio listener de teclado), este panel NO tiene su propio listener de F9/F10/
 * Escape — el único vive en `PantallaPos` (un segundo listener propio acá respondería al mismo
 * evento una segunda vez). El foco por defecto al montar va a "Cancelar", mismo criterio
 * conservador que `ConfirmacionDeBaja`. Mientras `ocupado` (el propio `cobrar()` en vuelo), ambos
 * botones quedan inertes — regla 13 de react-async-state: un cancelar no debe suceder a nada
 * mientras la escritura está en curso.
 *
 * judgment-day ronda 1 (K3): SÍ tiene su propia trampa de foco — `role="alertdialog"
 * aria-modal="true"` sin una trampa real es mentira para tecnología asistiva (Tab podría escapar
 * a un control de `PantallaPos` que ya debería ser inalcanzable). Con exactamente dos controles
 * focusables, Tab y Shift+Tab hacen lo mismo: alternar al otro — no hace falta mirar `shiftKey`.
 *
 * stage-pos-modales-de-cobro: se renderiza como un modal centrado con backdrop (idioma de
 * `ModalDeBusquedaDeArticulos`) en vez de vivir inline dentro del panel "Datos de la venta" — el
 * llamador ya no lo envuelve en ningún contenedor propio, así el panel nunca cambia de tamaño
 * cuando el diálogo aparece.
 *
 * No usa `Modal` a propósito: su Escape sumaría un segundo cierre al único listener de F9/F10/
 * Escape de `PantallaPos`, su botón "Cerrar" rompería la alternancia exacta Finalizar↔Cancelar,
 * y su devolución de foco competiría con la de `disparadorConfirmacionRef`.
 */
function ConfirmacionDeCobro({ total, pagado, vuelto, previaFallida, ocupado, onFinalizar, onCancelar }: PropsConfirmacionDeCobro) {
  const finalizarRef = useRef<HTMLButtonElement>(null)
  const cancelarRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    cancelarRef.current?.focus()
  }, [])

  function atraparTab(evento: React.KeyboardEvent<HTMLDivElement>) {
    if (evento.key !== 'Tab') return
    evento.preventDefault()
    evento.stopPropagation()
    const siguiente = document.activeElement === finalizarRef.current ? cancelarRef.current : finalizarRef.current
    siguiente?.focus()
  }

  return (
    <>
      <div
        className="modal d-block"
        tabIndex={-1}
        role="alertdialog"
        aria-modal="true"
        aria-label="¿Finalizar venta?"
        onKeyDown={atraparTab}
      >
        <div className="modal-dialog modal-dialog-centered" role="document">
          <div className="modal-content">
            <div className="modal-body">
              <p className="mb-2">
                <strong>¿Finalizar venta?</strong>
              </p>
              {previaFallida ? (
                <p className="mb-3">
                  Total no disponible (no se pudo calcular la previa) — se confirma recién al cobrar.
                </p>
              ) : (
                <ul className="mb-3 list-unstyled">
                  <li>Total: {formatearMoneda(total)}</li>
                  <li>Pagado: {formatearMoneda(pagado)}</li>
                  <li>Vuelto: {formatearMoneda(vuelto)}</li>
                </ul>
              )}
              <div className="d-flex gap-2">
                <button
                  ref={finalizarRef}
                  type="button"
                  className="btn btn-success"
                  disabled={ocupado}
                  aria-keyshortcuts="F9"
                  onClick={onFinalizar}
                >
                  {ocupado ? 'Finalizando…' : 'Finalizar (F9)'}
                </button>
                <button
                  ref={cancelarRef}
                  type="button"
                  className="btn btn-outline-secondary"
                  disabled={ocupado}
                  aria-keyshortcuts="F10"
                  onClick={onCancelar}
                >
                  Cancelar (F10)
                </button>
              </div>
            </div>
          </div>
        </div>
      </div>
      <div className="modal-backdrop show" />
    </>
  )
}

/** Un medio de pago aplicado a la venta recién emitida, ya resuelto a lo único que necesita el
 * modal "Venta finalizada" (nombre + importe) — nunca el `MedioPagoListado` completo. */
type ResumenDeMedioAplicado = { nombre: string; importe: number }

/** Un item emitido cuyo lote venció (`ItemEmitido.loteVencido`) — solo lo que necesita el aviso
 * del modal para identificarlo (descripción + código de lote cuando está disponible). */
type ItemVencidoResumen = { descripcion: string; codigoLote: string | null }

/** Datos que necesita el modal "Venta finalizada" tras un cobro exitoso (stage-pos-modales-de-
 * cobro) — reemplaza a la vieja pantalla de resumen completa. El carrito y el panel de pagos ya
 * quedaron reseteados para cuando este estado se setea (ver `cobrar()`): acá solo sobrevive lo
 * que el modal muestra. `vuelto` sale de `sumarVueltos(comprobante.pagos)` — la respuesta del
 * servidor, nunca un recálculo local (mismo criterio que el resto de la pantalla: "el servidor
 * es la autoridad final del total"). `itemsVencidos` espeja `ItemEmitido.loteVencido` — spec
 * comprobantes-venta, "Expired Lot Sale Warns, Never Blocks": "The response MUST carry a warning
 * flag identifying the expired line so the POS can display it prominently" — vacío en el 100% de
 * las ventas sin ningún lote vencido, lo único que llena este aviso. */
type ResumenVentaFinalizada = {
  numeroVisible: string
  total: number
  medios: ResumenDeMedioAplicado[]
  vuelto: number
  itemsVencidos: ItemVencidoResumen[]
  /** stage-pos-venta-offline-web: `true` cuando `cobrar()` armó este resumen del comprobante
   * SINTÉTICO del outbox offline, nunca de una respuesta real del servidor — el modal lo muestra
   * para que el cajero nunca confunda "ya sincronizado" con "guardado localmente, en cola". */
  pendienteDeSincronizar: boolean
  /** Venta local con cuenta corriente encolada sin respuesta del servidor sobre el límite de
   * crédito: aviso que no bloquea nada (la venta ya quedó registrada). `null` en cualquier otro
   * caso. */
  avisoLimiteDeCredito: string | null
}

type PropsVentaFinalizada = ResumenVentaFinalizada & {
  modoPresupuesto: boolean
  onCerrar: () => void
  /** Dispara el mismo camino que tipear en el input de código + Enter (`escanear`, con el código
   * ya resuelto — nunca depende de que `entradaEscaneo` lo tenga). Nunca se invoca bajo
   * `?idPresupuesto=` (ver `modoPresupuesto` más abajo). */
  onEscanearCodigo: (codigo: string) => void
  /** Deja un código sin escanear esperando en el input de código (F9/"Aceptar" con buffer no
   * vacío) — nunca se invoca bajo `?idPresupuesto=`. */
  onDejarCodigoEnInput: (codigo: string) => void
}

/**
 * Modal "Venta finalizada" (stage-pos-modales-de-cobro): reemplaza a la pantalla de resumen de
 * ticket completa que reemplazaba toda la pantalla hasta que el cajero apretaba "Nueva venta" —
 * el carrito ya se reseteó para cuando este modal aparece (`cobrar()`), así que cerrarlo nunca
 * requiere ese paso extra. Mismo criterio de foco que `ConfirmacionDeCobro`: un solo control
 * focusable ("Aceptar"), autofocado al montar, Tab/Shift+Tab lo mantienen ahí (la pantalla de
 * atrás sigue con su navbar montada, así que sin esta trampa `aria-modal="true"` sería mentira
 * para tecnología asistiva — regla 13 de react-async-state).
 *
 * judgment-day ronda 0: con "Aceptar" autofocado, el Enter final de un código de barras leído por
 * pistola (que termina en un Enter físico) activaba el botón por su cuenta — el código escaneado
 * se perdía. `bufferRef` acumula cada tecla imprimible de una sola posición (sin Ctrl/Alt/Meta)
 * mientras el modal está abierto (nada más tiene foco de texto acá): Enter cierra el modal Y
 * dispara `onEscanearCodigo` con lo acumulado (mismo camino que tipear en el input + Enter,
 * `N*código` incluido — es el mismo string, nunca se interpreta acá). F9 o "Aceptar" con buffer
 * no vacío cierran SIN escanear: el texto queda esperando en el input de código
 * (`onDejarCodigoEnInput`), nunca se pierde ni se envía a medias. Bajo `?idPresupuesto=` el
 * buffer se descarta sin más — `onCerrar` navega afuera de esta pantalla, no queda ningún input
 * al que devolvérselo.
 *
 * `evento.stopPropagation()` en cada rama (mismo criterio que `ConfirmacionDeCobro.atraparTab`):
 * coordina esto con el listener global de F9 de `PantallaPos` (`f9Ref`) — mientras el foco esté
 * adentro del modal (el trap de Tab lo garantiza), la tecla nunca llega a burbujear hasta
 * `document`, así que ninguna tecla se maneja dos veces.
 *
 * No usa `Modal` a propósito: Escape no la cierra (solo Aceptar/Enter/F9), no tiene botón
 * "Cerrar" ni cierre por fondo (ambos saltearían `cerrarSinEscanear` y el buffer), y el buffer
 * necesita el keydown de todo el diálogo, que `Modal` no expone.
 */
function VentaFinalizada({
  numeroVisible,
  total,
  medios,
  vuelto,
  itemsVencidos,
  pendienteDeSincronizar,
  avisoLimiteDeCredito,
  modoPresupuesto,
  onCerrar,
  onEscanearCodigo,
  onDejarCodigoEnInput,
}: PropsVentaFinalizada) {
  const aceptarRef = useRef<HTMLButtonElement>(null)
  const bufferEscaneoRef = useRef('')

  useEffect(() => {
    aceptarRef.current?.focus()
  }, [])

  /** F9 o clic en "Aceptar": cierra SIN escanear — si venía un código a medio tipear/escanear, se
   * lo devuelve al input de código en vez de perderlo (nunca bajo `?idPresupuesto=`: no queda
   * pantalla a la que devolvérselo). */
  function cerrarSinEscanear() {
    const codigo = bufferEscaneoRef.current
    bufferEscaneoRef.current = ''
    onCerrar()
    if (!modoPresupuesto && codigo) onDejarCodigoEnInput(codigo)
  }

  function manejarTeclado(evento: React.KeyboardEvent<HTMLDivElement>) {
    if (evento.key === 'Tab') {
      evento.preventDefault()
      evento.stopPropagation()
      aceptarRef.current?.focus()
      return
    }

    if (evento.key === 'Enter') {
      // Sin este preventDefault, el propio Enter activaría por su cuenta "Aceptar" (ya
      // autofocado) además de esta rama — el cierre se dispararía dos veces.
      evento.preventDefault()
      evento.stopPropagation()
      const codigo = bufferEscaneoRef.current
      bufferEscaneoRef.current = ''
      onCerrar()
      if (!modoPresupuesto && codigo) onEscanearCodigo(codigo)
      return
    }

    if (evento.key === 'F9') {
      // K2 (heredado de `ConfirmacionDeCobro`): un F9 sostenido no debe repetir el cierre.
      if (evento.repeat) {
        evento.preventDefault()
        evento.stopPropagation()
        return
      }
      evento.preventDefault()
      evento.stopPropagation()
      cerrarSinEscanear()
      return
    }

    if (evento.key.length === 1 && !evento.ctrlKey && !evento.altKey && !evento.metaKey) {
      evento.preventDefault()
      evento.stopPropagation()
      bufferEscaneoRef.current += evento.key
    }
  }

  return (
    <>
      <div
        className="modal d-block"
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-label="Venta finalizada"
        onKeyDown={manejarTeclado}
      >
        <div className="modal-dialog modal-dialog-centered" role="document">
          <div className="modal-content">
            <div className="modal-header">
              <h5 className="modal-title">Venta finalizada</h5>
            </div>
            <div className="modal-body text-center">
              {/* stage-pos-venta-offline-web: distingue "guardada en este dispositivo, en cola"
                  de "ya sincronizada" — el cajero no debería asumir lo segundo. */}
              {pendienteDeSincronizar && (
                <div className="alert alert-secondary text-start small py-2 mb-3">
                  <strong>Guardada en este dispositivo</strong> — se envía al servidor en segundo plano.
                </div>
              )}
              {avisoLimiteDeCredito && (
                <div className="alert alert-warning text-start small py-2 mb-3" role="status">
                  {avisoLimiteDeCredito}
                </div>
              )}
              {/* design decisión 12 ("Expired Lot Sale Warns, Never Blocks"): nunca bloquea la
                  venta — solo la última chance de que el operador se entere de que salió un lote
                  vencido, ahora que la vieja pantalla de resumen con el detalle de items ya no
                  existe. */}
              {itemsVencidos.length > 0 && (
                <div className="alert alert-danger text-start mb-3">
                  <strong>⚠ Se vendió un lote vencido</strong>
                  <ul className="mb-0 mt-1">
                    {itemsVencidos.map((item, indice) => (
                      <li key={indice}>
                        {item.descripcion}
                        {item.codigoLote && ` — Lote ${item.codigoLote}`}
                      </li>
                    ))}
                  </ul>
                </div>
              )}
              <p className="text-muted small mb-3">Venta {numeroVisible}</p>
              <p className="fs-4 mb-3">
                <strong>Total: {formatearMoneda(total)}</strong>
              </p>
              <div className="mb-3 text-start">
                <div className="text-muted small mb-1">Medios de pago</div>
                <ul className="list-unstyled mb-0">
                  {medios.map((medio, indice) => (
                    <li key={indice}>
                      {medio.nombre}: {formatearMoneda(medio.importe)}
                    </li>
                  ))}
                </ul>
              </div>
              <p className="fs-4 mb-0">
                <strong>Vuelto: {formatearMoneda(vuelto)}</strong>
              </p>
            </div>
            <div className="modal-footer">
              <button ref={aceptarRef} type="button" className="btn btn-primary" aria-keyshortcuts="F9" onClick={cerrarSinEscanear}>
                Aceptar <sup aria-hidden="true">(F9)</sup>
              </button>
            </div>
          </div>
        </div>
      </div>
      <div className="modal-backdrop show" />
    </>
  )
}

/** stage-desktop-pos: seam opcional para que el shell del POS de escritorio se entere de una
 * venta recién emitida (ticket ESC/POS + pulso de cajón) sin que esta pantalla sepa nada de
 * impresoras — `medios` viaja junto porque es lo único que le falta al llamador para resolver el
 * nombre/comportamiento de cada pago del comprobante (`ComprobanteEmitido.pagos` solo trae
 * `idMedioPago`). En la app web normal el prop queda `undefined` y no cambia nada. */
/** Lo que la pantalla sabe del saldo de un cliente identificado al emitir su venta, para que el ticket
 * lo muestre como dato o como estimación. */
export type SaldoParaTicket = {
  /** Cuenta corriente de sus ventas en la cola sin enviar, sin contar la que se emite; `null` si no se pudo leer. */
  consumoPendiente: number | null
  /** El saldo en pantalla vino de una lectura del servidor posterior a la selección, el último pago o la última venta. */
  saldoConfirmado: boolean
}

export type AlEmitirVenta = (
  comprobante: ComprobanteEmitido,
  cliente: ClienteListado,
  medios: MedioPagoListado[],
  saldo?: SaldoParaTicket,
) => void

/** Espera máxima por la lectura de la cola para el ticket: sin respuesta, el saldo se imprime como estimado. */
const ESPERA_DE_LA_COLA_PARA_EL_TICKET_MS = 2_000

type PropsPantallaPos = {
  idPresupuesto: number | null
  alEmitir?: AlEmitirVenta
  /** stage-pos-turno-y-foco: seam de navegación de "Cerrar caja" — la app web navega a la ruta
   * libre `/caja/cierre` con `useNavigate` (comportamiento sin cambios, "camino viejo"). El shell
   * de escritorio dejó de pasar esto (stage-pos-retiros-y-cierre-por-retiro, etapa 5): con
   * `cajaDeEscritorio` presente, "Cerrar caja" nunca navega — ver ese prop. Queda como seam
   * genérico por si algún día hace falta una navegación custom sin el resto del comportamiento
   * de escritorio. */
  alIrACerrarCaja?: (idTurno: number) => void
  /** stage-pos-retiros-y-cierre-por-retiro (etapa 5): seam opcional, SOLO el POS de escritorio lo
   * pasa (`ShellPos.tsx`) — habilita "Retirar" en los controles de turno y reemplaza "Cerrar caja"
   * por el cierre por retiro (modales en esta misma pantalla, sin navegar) en vez de la
   * navegación clásica a `CierreDeCaja`. `contexto`/`encolarImpresion` son exactamente los que ya
   * arma/expone `ShellPos` para el ticket de venta — esta pantalla arma los bytes de cada ticket
   * propio (pulso de cajón, retiro, cierre) con las plantillas de `impresion/plantillas.ts` y los
   * entrega a la MISMA cola FIFO, nunca un segundo camino de impresión. `undefined` en la app web
   * (bajo `Layout.tsx`) deja el comportamiento intacto: sin "Retirar", "Cerrar caja" sigue el
   * camino viejo de arriba. */
  cajaDeEscritorio?: CajaDeEscritorio
  /** Avisa si hay una venta en curso (carrito con ítems, cobro en vuelo o modal de caja/venta
   * abierto). El shell de escritorio lo usa para no reiniciar la app por una actualización en
   * medio de una venta. */
  alCambiarVentaEnCurso?: (enCurso: boolean) => void
}

export type CajaDeEscritorio = {
  contexto: ContextoDeImpresion
  encolarImpresion: (descripcion: string, bytes: Uint8Array) => void
}

/**
 * Pantalla del POS (stage-5-pos-ventas, Slice 7, design: POS Screen Composition) — escaneo +
 * carrito + selección de punto de venta/cliente (Slice 6) + panel de pagos, checkout (`POST
 * /api/ventas`) y ticket (Slice 7, esta entrega). Precedente de forma: `Articulos.tsx`/`Ofertas.tsx`
 * tras sus rondas de judgment-day.
 *
 * stage-17-presupuestos-y-remitos (Slice 7, design: Web composition — "the POS banner, the
 * read-only hydration, the skipped price effect, the key"): con `idPresupuesto` no nulo, la
 * pantalla entera opera en modo conversión — carrito congelado de solo lectura, sin resolución de
 * precio, `POST /api/ventas` con `idPresupuestoOrigen`. Remontada íntegra por `key` desde `Pos()`
 * (react-async-state regla 8) — ningún estado de una venta libre o de otro presupuesto sobrevive
 * al cambio de `?idPresupuesto=`, ni al cambio del punto de venta de la sesión.
 */

function PantallaPos({ idPresupuesto, alEmitir, alIrACerrarCaja, cajaDeEscritorio, alCambiarVentaEnCurso }: PropsPantallaPos) {
  const modoPresupuesto = idPresupuesto !== null
  const navigate = useNavigate()
  // Solo da forma a la copia del override de rendición del cierre por retiro (ver
  // `esSupervisorOAdmin`) — el rol del cliente nunca gatea el control. Disponible en los dos
  // montajes reales de esta pantalla: `AuthProvider` en la app web (`App.tsx`) y el
  // `AuthContext.Provider` propio del shell de escritorio (`ShellPos.tsx`).
  const { usuario } = useAuth()
  const { puntoVenta: puntoVentaDeSesion, puntosVenta } = usePuntoVenta()
  // stage-pos-caja-en-cabecera: `null` en la app web (bajo `Layout.tsx`, sin `ShellPos` que provea
  // un valor) — ahí los controles de caja se renderizan en una franja propia en vez de portalearse
  // (ver `controlesTurno` más abajo).
  const nodoRanuraHeader = useContext(RanuraHeaderPosContext)

  // stage-pos-adjustments: borrador del ticket en curso — solo venta libre (nunca bajo
  // `?idPresupuesto=`, esa conversión se rehidrata siempre fresca desde el servidor). La clave
  // usa el mismo punto de venta de SESIÓN que `Pos()` ya usa para el `key` de remontaje — nunca
  // `puntoVentaSeleccionada` (más abajo), que bajo presupuesto sale de otro lado: acá siempre es
  // el camino libre, así que ambos coinciden.
  const almacenBorradores = useContext(BorradorDeTicketContext)
  const claveBorrador = modoPresupuesto ? null : `libre:${puntoVentaDeSesion?.id ?? 'sin-pv'}`
  // Leído una sola vez, en el primer render de esta instancia (`PantallaPos` se remonta entera
  // por `key` ante cualquier cambio de punto de venta/presupuesto — ver `Pos()`) — nunca
  // releído en renders posteriores, donde el propio efecto de guardado ya lo habría pisado con
  // el estado en curso.
  const borradorInicialRef = useRef<BorradorDeTicket | null | undefined>(undefined)
  if (borradorInicialRef.current === undefined) {
    borradorInicialRef.current = claveBorrador !== null ? (almacenBorradores?.obtener(claveBorrador) ?? null) : null
  }
  const borradorInicial = borradorInicialRef.current

  const [opcionesClientes, setOpcionesClientes] = useState<ClienteListado[]>([])
  const [clienteSeleccionado, setClienteSeleccionado] = useState<ClienteListado | null>(() => borradorInicial?.clienteSeleccionado ?? null)
  // El mount effect de más abajo (Consumidor Final por defecto) no debe pisar un cliente
  // restaurado del borrador — se decide UNA sola vez, con el mismo `borradorInicial` congelado.
  const clienteRestauradoDelBorradorRef = useRef(borradorInicial?.clienteSeleccionado != null)
  // Error del cliente del presupuesto; la carga inicial tiene su propio slot (`errorCargaClientes`),
  // que la copia local del escritorio puede volver innecesario sin tocar este.
  const [errorClientes, setErrorClientes] = useState('')
  const [errorCargaClientes, setErrorCargaClientes] = useState('')
  const [errorCargaClientesEsDeRed, setErrorCargaClientesEsDeRed] = useState(false)
  // Generación de la carga inicial de clientes: una respuesta tardía de una carga anterior (la del
  // arranque, cuando ya se reintentó al volver la red) nunca pisa a la más nueva.
  const generacionCargaClientesRef = useRef(0)
  // stage-pos-adjustments: Consumidor Final cargado en el mount effect (más abajo) — el reset
  // post-venta (en `cobrar()`) no puede re-derivarlo desde el estado en ese momento, necesita este
  // valor congelado apenas se conoce.
  const consumidorFinalRef = useRef<ClienteListado | null>(null)

  // stage-pos-adjustments: `clienteSeleccionado` es un `ClienteListado` cacheado — un pago a
  // cuenta registrado desde `ModalCuentaCorrientePos` (JD-1) o un cliente restaurado desde el
  // borrador (JD-2) puede dejarlo con saldo desactualizado, y `validarPagosLocal` (más abajo) lee
  // `saldo`/`limiteCredito`/`creditoIlimitado` directo de este estado. react-async-state regla 2:
  // generación propia + chequeo del id vigente vía updater funcional — si el cajero cambió de
  // cliente mientras el refetch estaba en vuelo, la respuesta desactualizada se descarta.
  const generacionClienteSeleccionadoRef = useRef(0)
  // El `ClienteListado` que una lectura del servidor dejó como cliente de la venta. Cualquier otro objeto (de la
  // lista, de la copia local, de un borrador, o el mismo tras un pago o una venta que cambió su saldo) es no confirmado.
  const saldoConfirmadoRef = useRef<ClienteListado | null>(null)
  const refrescarClienteSeleccionado = useCallback((idCliente: number) => {
    const miGeneracion = (generacionClienteSeleccionadoRef.current += 1)
    clienteDeClientes
      .obtener(idCliente)
      .then((actualizado) => {
        if (generacionClienteSeleccionadoRef.current !== miGeneracion) return
        setClienteSeleccionado((prev) => (prev && prev.id === idCliente ? actualizado : prev))
        // Solo este objeto, el que quedó en pantalla, cuenta como saldo confirmado por el servidor.
        saldoConfirmadoRef.current = actualizado
      })
      .catch(() => {
        // silencioso: si falla el refetch, se conserva el cliente en pantalla con el saldo que
        // ya tenía — nunca se lo resetea a Consumidor Final por un error de red.
      })
  }, [])

  const [lineas, setLineas] = useState<LineaCarrito[]>(() => borradorInicial?.lineas ?? [])
  // Espejo de `lineas` para `mutarCarrito` (estable, sin deps): solo lo lee para saber sobre qué
  // línea va a sumar un escaneo y limpiarle la edición de cantidad en curso.
  const lineasRef = useRef(lineas)
  useEffect(() => {
    lineasRef.current = lineas
  }, [lineas])
  const [precios, setPrecios] = useState<PreciosPorLinea>(() => borradorInicial?.precios ?? {})
  // Combos que la vista previa aplicó al carrito (nombre, veces y ahorro): viaja siempre junto a `precios`, de
  // la misma resolución y bajo la misma guarda de generación.
  const [combosAplicados, setCombosAplicados] = useState<ComboAplicadoEnPrevia[]>([])
  const [resolviendo, setResolviendo] = useState(false)
  const [avisoPrecios, setAvisoPrecios] = useState('')
  const [reintentoPrecios, setReintentoPrecios] = useState(0)
  const generacionResolucionRef = useRef(0)
  const ultimaAccionEsEdicionRef = useRef(false)
  // judgment-day ronda 1 (WARNING): la instantánea EXACTA que resolvió la vista previa offline
  // actualmente en pantalla (`precios`) — nunca la más fresca que `sincronizacionOffline.instantanea`
  // pueda tener en el momento del click. El efecto de abajo la fija cuando la vista previa sale de
  // la instantánea (venta local primero, o el fallback sin red de la vista previa); `null` con una
  // vista previa online o sin vista previa todavía. "El precio que se mostró es el que se cobra":
  // `cobrar()` encola solo con esta referencia; sin ella, cobra online.
  const instantaneaDeLaVistaPreviaRef = useRef<InstantaneaDePos | null>(null)
  // Espejo renderizable de `instantaneaDeLaVistaPreviaRef !== null`: con una vista previa local,
  // `cobrar()` intenta el cobro local, y ahí el límite de crédito lo juzga la consulta al servidor
  // al encolar, no los datos del cliente que tiene la pantalla.
  const [previaLocal, setPreviaLocal] = useState(false)
  function congelarInstantaneaDeLaPrevia(instantanea: InstantaneaDePos | null) {
    instantaneaDeLaVistaPreviaRef.current = instantanea
    setPreviaLocal(instantanea !== null)
  }
  const [cantidadesEnEdicion, setCantidadesEnEdicion] = useState<Record<string, string>>(() => borradorInicial?.cantidadesEnEdicion ?? {})
  // Texto del ajuste manual que el cajero está tipeando, por línea: mientras no se confirma (blur o
  // Enter) no toca el carrito, y un texto inválido queda acá, a la vista, en vez de revertirse solo.
  const [ajustesEnEdicion, setAjustesEnEdicion] = useState<Record<string, string>>({})

  const [entradaEscaneo, setEntradaEscaneo] = useState('')
  const [escaneando, setEscaneando] = useState(false)
  const [errorEscaneo, setErrorEscaneo] = useState('')
  const tokenEscaneoRef = useRef(0)
  const inputEscaneoRef = useRef<HTMLInputElement>(null)
  // Gatillos EXPLÍCITOS de foco (escaneo exitoso, cerrar/agregar del buscador) — el efecto que lo
  // consume (más abajo) siempre gana, sin mirar dónde está el foco actual: son reacciones directas
  // a una acción del propio cajero sobre ESTE input. El foco inicial / al desbloquear el turno usa
  // un mecanismo aparte (ver el efecto "foco neutral" más abajo) que si respeta un foco ajeno.
  const focoPendienteRef = useRef(false)
  // F1 del shell: pide el foco por el mismo camino y despierta al efecto que lo consume.
  const [pedidosDeFoco, setPedidosDeFoco] = useState(0)

  // stage-pos-buscador-articulos: modal de búsqueda por nombre (F2, botón "Buscar" junto al de
  // código) — se abre solo con la venta libre operable (nunca bajo `?idPresupuesto=`, ni con el
  // checkout en vuelo, ni con el gate de turno o el ticket ya emitido en pantalla).
  const [buscadorAbierto, setBuscadorAbierto] = useState(false)
  const [buscadorDeCombosAbierto, setBuscadorDeCombosAbierto] = useState(false)
  // Combo con familias que esperan que el cajero reparta las unidades. `desdeCodigo`: vino de `C12` en el input de código,
  // que se vacía recién al agregarlo (cancelar deja el texto tipeado).
  const [comboPorElegir, setComboPorElegir] = useState<{ combo: ComboParaCargar; desdeCodigo: boolean } | null>(null)
  const modalDeCombosAbierto = buscadorDeCombosAbierto || comboPorElegir !== null
  // Guarda de reentrancia de primera línea de la carga por código `C12`: un segundo Enter o el atajo en el mismo tick le
  // ganan al re-render que refleja `escaneando`.
  const cargaDeComboEnCursoRef = useRef(false)

  // Lo que respondió el servidor. En el POS de escritorio, mientras no hay respuesta o si falló por
  // falta de red, la pantalla usa los de la instantánea local (`medios`/`errorMedios`, derivados
  // más abajo junto a `instantaneaDelPuntoVenta`).
  const [mediosDelServidor, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [errorMediosDelServidor, setErrorMedios] = useState('')
  const [errorMediosEsDeRed, setErrorMediosEsDeRed] = useState(false)

  // stage-pos-adjustments: modal de consulta de saldo + pago a cuenta — nunca bajo
  // `?idPresupuesto=` (esa venta congelada no tiene ningún cliente que el cajero pueda cambiar
  // acá adentro).
  const [modalCuentaCorrienteAbierto, setModalCuentaCorrienteAbierto] = useState(false)
  const [altaDeClienteAbierta, setAltaDeClienteAbierta] = useState(false)

  // Mismo criterio que `mediosDelServidor`: `parametros`/`errorParametros` se derivan más abajo.
  const [parametrosDelServidor, setParametros] = useState<{ toleranciaPago: number } | null>(null)
  const [errorParametrosDelServidor, setErrorParametros] = useState('')
  const [errorParametrosEsDeRed, setErrorParametrosEsDeRed] = useState(false)
  // Se incrementa para volver a pedir la tolerancia cuando vuelve la red (ver el efecto de
  // recarga de datos locales, más abajo).
  const [recargaDeParametros, setRecargaDeParametros] = useState(0)
  const generacionParametrosRef = useRef(0)

  // "Con envío": `null` = el cajero todavía no lo tocó en esta venta, y rige el valor por defecto del punto de venta
  // (`conEnvio`, más abajo). Cada venta nueva (cobrada o con el carrito vaciado) vuelve a `null`.
  const [conEnvioElegido, setConEnvioElegido] = useState<boolean | null>(() => borradorInicial?.conEnvio ?? null)
  // Importe de la línea Envío (`importe_envio`) del punto de venta; solo se pide con el envío tildado.
  const [importeEnvio, setImporteEnvio] = useState<number | null>(null)
  const [errorImporteEnvio, setErrorImporteEnvio] = useState(false)
  const generacionImporteEnvioRef = useRef(0)

  const proximaFilaPagoIdRef = useRef(borradorInicial?.proximoIdFilaPago ?? 1)
  const [filasPago, setFilasPago] = useState<FilaPago[]>(
    () => borradorInicial?.filasPago ?? [filaPagoInicial(proximaFilaPagoIdRef.current++, null)],
  )

  // react-async-state regla 9: mientras el checkout está en vuelo, TODO lo que podría
  // superponerse (escaneo, edición de carrito, cliente, filas de pago) queda
  // inerte — `cobrandoRef` es el guard de reentrancia de primera línea (un doble click en el
  // mismo tick le gana al re-render que deshabilita el botón), `cobrando` es lo que deshabilita
  // los controles en pantalla.
  const [cobrando, setCobrando] = useState(false)
  const cobrandoRef = useRef(false)
  const generacionCobroRef = useRef(0)
  const [errorCobro, setErrorCobro] = useState('')
  // "Facturar": apagado por defecto y se apaga solo al terminar cada venta; no viaja bajo un presupuesto.
  // `facturando` solo marca la espera acotada de la factura dentro de `cobrar()`, que ya mantiene la pantalla inerte.
  const [facturar, setFacturar] = useState(false)
  const [facturando, setFacturando] = useState(false)

  // stage-pos-atajos-cobro: F9 con el pago ya cubierto abre esta puerta antes de cobrar de
  // verdad ("¿Finalizar venta?") — clickear "Cobrar" directamente NUNCA pasa por acá (sigue
  // cobrando de inmediato, comportamiento preexistente). `disparadorConfirmacionRef` captura
  // SÍNCRONAMENTE (react-async-state regla 12) el control enfocado en el momento del F9 que
  // abrió la puerta, para poder devolverle el foco si el cajero cancela.
  const [confirmandoCobro, setConfirmandoCobro] = useState(false)
  const disparadorConfirmacionRef = useRef<HTMLElement | null>(null)

  // Confirmación de "Vaciar carrito" (clic o F10). Escape lo cancela el propio `Modal`; F10 y F9
  // los resuelve el único listener de teclado de la pantalla.
  const [vaciandoCarrito, setVaciandoCarrito] = useState(false)

  // stage-6-turnos-caja (Slice 7): gate seam del checkout — un 409 turno_no_abierto reemplaza el
  // panel de cobro por la oferta de abrir turno en vez de un error crudo (design: Web
  // Composition — "Pos.tsx gate seam").
  const [gateTurno, setGateTurno] = useState(false)

  // stage-pos-turno-y-foco: estado del turno del punto de venta, mostrado en "Datos de la venta"
  // (badge + acción "Abrir turno"/"Cerrar caja") y usado para bloquear la venta libre mientras no
  // hay un turno abierto (spec: "solo búsqueda/consulta de precio mientras el turno está
  // cerrado"). `turno === null` cubre tanto "confirmado cerrado" como "todavía no se sabe"
  // (cargando o la consulta falló) — nunca se habilita vender sin una confirmación positiva de
  // turno abierto (fail-closed), mismo criterio que el resto de las precondiciones de "Cobrar".
  // Nunca corre bajo `?idPresupuesto=` (esa venta ya viene congelada, sin escaneo/carrito propio).
  //
  // En el POS de escritorio, si la consulta falló por falta de red o no respondió dentro de
  // `LIMITE_DE_ESPERA_DEL_TURNO_MS`, rige el
  // último turno abierto que el servidor confirmó para este punto de venta (`turnoLocal`,
  // persistido en el dispositivo); `turno`/`cargandoTurno`/`errorTurno` se derivan más abajo. Ese
  // turno local nunca se inventa: solo existe si el servidor alguna vez lo confirmó.
  const [turnoDelServidor, setTurno] = useState<TurnoResumen | null>(null)
  const [cargandoTurnoDelServidor, setCargandoTurno] = useState(false)
  const [errorTurnoDelServidor, setErrorTurno] = useState('')
  const [errorTurnoEsDeRed, setErrorTurnoEsDeRed] = useState(false)
  // `false` hasta que la primera consulta termina: antes de eso `turno === null` solo significa
  // "todavía no se sabe", y el header no debe tomarlo por una caja cerrada.
  const [turnoConsultado, setTurnoConsultado] = useState(false)
  const [esperaDelTurnoVencida, setEsperaDelTurnoVencida] = useState(false)
  const generacionTurnoRef = useRef(0)

  // judgment-day ronda 1 (T2): "Cerrar caja" vuelve a consultar el turno abierto ANTES de navegar
  // (ver `irACerrarCaja`) — estado propio, separado de `cargandoTurno`/`errorTurno` (regla 14: un
  // slot de estado tiene un solo dueño; esta consulta la dispara el click, no el mount/reintentar).
  const [verificandoCierre, setVerificandoCierre] = useState(false)
  const verificandoCierreRef = useRef(false)
  const [avisoCerrarCaja, setAvisoCerrarCaja] = useState('')

  // Re-judgment (WARNING, juez A): `irACerrarCaja` hace un `await` real — si el cajero navega
  // fuera de esta pantalla (u otra cosa la desmonta) antes de que resuelva, ni el `setState` ni,
  // sobre todo, la NAVEGACIÓN posterior deben dispararse contra una pantalla que ya no está. Un
  // guard de generación no alcanza acá (la respuesta sigue siendo la más reciente, el problema es
  // que ya no hay a dónde aplicarla) — se necesita un ref de "sigue montado", seteado en `false` en
  // la limpieza del efecto de montaje.
  const montadoRef = useRef(true)
  useEffect(() => {
    montadoRef.current = true
    return () => {
      montadoRef.current = false
    }
  }, [])

  // stage-pos-retiros-y-cierre-por-retiro (etapa 5, desktop-only vía `cajaDeEscritorio`): flujo
  // "Retirar" — confirmar apertura de cajón (con su propio POST de auditoría `AperturaCajon`) y
  // recién después el monto a retirar (POST `Retiro`). `pasoRetiro` es el único dueño de "hay un
  // modal de este flujo abierto" — nunca dos booleanos separados que podrían desincronizarse.
  const [pasoRetiro, setPasoRetiro] = useState<'confirmarApertura' | 'monto' | null>(null)
  // regla 11/14: espejo sincrónico de `pasoRetiro` para las guardas de reentrancia — el estado es
  // el snapshot del render anterior y un click del mismo tick lo lee viejo. `cambiarPasoRetiro` es
  // el único escritor del slot, así que el ref y el estado nunca divergen.
  const pasoRetiroRef = useRef<'confirmarApertura' | 'monto' | null>(null)
  function cambiarPasoRetiro(paso: 'confirmarApertura' | 'monto' | null) {
    pasoRetiroRef.current = paso
    setPasoRetiro(paso)
  }
  const [registrandoAperturaRetiro, setRegistrandoAperturaRetiro] = useState(false)
  const registrandoAperturaRetiroRef = useRef(false)
  const [errorAperturaRetiro, setErrorAperturaRetiro] = useState('')
  const [montoRetiro, setMontoRetiro] = useState<number | null>(null)
  const [registrandoRetiro, setRegistrandoRetiro] = useState(false)
  const registrandoRetiroRef = useRef(false)
  const [errorRetiro, setErrorRetiro] = useState('')
  const [avisoRetiroOk, setAvisoRetiroOk] = useState('')

  // stage-pos-retiros-y-cierre-por-retiro (etapa 5, desktop-only): flujo "Cerrar caja" por
  // retiro — mismo criterio de "un solo dueño" que `pasoRetiro`. `idTurnoACerrar` es el turno que
  // `irACerrarCaja` ya reconfirmó fresco (nunca el `turno.id` potencialmente viejo del estado).
  const [pasoCierre, setPasoCierre] = useState<'confirmar' | 'monto' | null>(null)
  const [idTurnoACerrar, setIdTurnoACerrar] = useState<number | null>(null)
  const [registrandoAperturaCierre, setRegistrandoAperturaCierre] = useState(false)
  const registrandoAperturaCierreRef = useRef(false)
  const [errorAperturaCierre, setErrorAperturaCierre] = useState('')
  const [montoCierre, setMontoCierre] = useState<number | null>(null)
  const [cerrandoPorRetiro, setCerrandoPorRetiro] = useState(false)
  const cerrandoPorRetiroRef = useRef(false)
  const [errorCierrePorRetiro, setErrorCierrePorRetiro] = useState('')
  // `true` desde que `cerrarPorRetiro` devuelve un 503 `resultado_incierto` (o una falla de red)
  // hasta que se confirma el resultado real — mientras está en `true`, un reintento SOLO vuelve a
  // consultar `obtenerResumenDeCierre` (lectura, idempotente), NUNCA vuelve a postear
  // `cerrarPorRetiro` — el cierre ya pudo haber sucedido del lado del servidor.
  const [cierreIncierto, setCierreIncierto] = useState(false)
  const [recuperandoCierre, setRecuperandoCierre] = useState(false)
  const recuperandoCierreRef = useRef(false)
  // Guarda de rendición de cola del servidor (`409 rendicion_de_dispositivo_pendiente`): la guarda
  // mira TODOS los dispositivos del punto de venta, incluidos los que este equipo no puede ver. El
  // override supervisado (`forzarSinRendicion` + motivo obligatorio) se revela SOLO después de ese
  // rechazo, nunca es un control visible por defecto — mismo opt-in con motivo obligatorio y mismo
  // contrato de payload que el cierre clásico de `CierreDeCaja.tsx` (react-async-state regla 10:
  // este es el ÚNICO camino de cierre del POS de escritorio, el shell no monta esa pantalla).
  const [rendicionPendiente, setRendicionPendiente] = useState(false)
  const [forzarSinRendicion, setForzarSinRendicion] = useState(false)
  const [motivoSinRendicion, setMotivoSinRendicion] = useState('')

  // stage-pos-modales-de-cobro: reemplaza a la vieja pantalla de resumen completa — un cobro
  // exitoso ya no reemplaza toda la pantalla ni espera un click en "Nueva venta", solo muestra
  // este modal encima de una pantalla que ya se reseteó (ver `cobrar()`).
  const [ventaFinalizada, setVentaFinalizada] = useState<ResumenVentaFinalizada | null>(null)

  // stage-17-presupuestos-y-remitos (Slice 7): el presupuesto congelado que gobierna esta venta
  // bajo `?idPresupuesto=` — `null` en el camino libre. `cargandoPresupuesto`/`errorPresupuesto`
  // gatean la pantalla ANTES de mostrar el carrito congelado, mismo criterio que `errorDetalle`
  // en OrdenDeCompra.tsx.
  const [presupuesto, setPresupuesto] = useState<PresupuestoParaVenta | null>(null)
  const [cargandoPresupuesto, setCargandoPresupuesto] = useState(modoPresupuesto)
  const [errorPresupuesto, setErrorPresupuesto] = useState('')
  const tokenPresupuestoRef = useRef(0)

  useEffect(() => {
    if (!modoPresupuesto || idPresupuesto === null) return
    const miToken = (tokenPresupuestoRef.current += 1)
    let vigente = true
    setCargandoPresupuesto(true)
    setErrorPresupuesto('')

    clienteDePresupuestos
      .paraVenta(idPresupuesto)
      .then((p) => {
        if (!vigente || tokenPresupuestoRef.current !== miToken) return
        setPresupuesto(p)
      })
      .catch((e) => {
        if (!vigente || tokenPresupuestoRef.current !== miToken) return
        setErrorPresupuesto(e instanceof ErrorApi ? e.message : 'No se pudo cargar el presupuesto.')
      })
      .finally(() => {
        if (!vigente || tokenPresupuestoRef.current !== miToken) return
        setCargandoPresupuesto(false)
      })

    return () => {
      vigente = false
    }
  }, [modoPresupuesto, idPresupuesto])

  // El cliente lo trae el presupuesto por id — se hidrata el registro completo (no solo el id)
  // porque el panel de pagos necesita `esConsumidorFinal`/`saldo`/`limiteCredito`/`creditoIlimitado`
  // para su validación local, los mismos campos que la selección manual ya provee.
  const tokenClientePresupuestoRef = useRef(0)
  useEffect(() => {
    if (!presupuesto) return
    const miToken = (tokenClientePresupuestoRef.current += 1)
    let vigente = true

    clienteDeClientes
      .obtener(presupuesto.idCliente)
      .then((c) => {
        if (!vigente || tokenClientePresupuestoRef.current !== miToken) return
        setClienteSeleccionado(c)
      })
      .catch((e) => {
        if (!vigente || tokenClientePresupuestoRef.current !== miToken) return
        setErrorClientes(e instanceof ErrorApi ? e.message : 'No se pudo cargar el cliente del presupuesto.')
      })

    return () => {
      vigente = false
    }
  }, [presupuesto])

  // Bajo `?idPresupuesto=` el punto de venta lo fija el propio presupuesto, nunca la sesión (el
  // servidor recibe `idPuntoVenta` del body igual que siempre, pero acá viaja el del presupuesto).
  const idPuntoVentaDelPresupuesto = presupuesto?.idPuntoVenta ?? null
  const puntoVentaSeleccionada = modoPresupuesto
    ? (puntosVenta.find((p) => p.id === idPuntoVentaDelPresupuesto) ?? null)
    : puntoVentaDeSesion

  // stage-pos-venta-offline-web: instantánea local + outbox — nunca corre bajo `?idPresupuesto=`
  // (esa conversión ya necesita red para haber cargado el presupuesto en primer lugar, la venta
  // local no la cubre).
  const sincronizacionOffline = useSincronizacionOffline({
    idPuntoVenta: puntoVentaSeleccionada?.id ?? null,
    activo: !modoPresupuesto,
    // La instantánea es solo del POS de escritorio: en la app web la venta nunca espera el ciclo
    // de arranque.
    sincronizarAntesDeVender: corriendoEnTauri(),
  })

  // Cobro con QR de Mercado Pago: el modal es una espera dentro de `cobrar()`. "Imprimir QR" solo existe con la caja de escritorio.
  const modalDeCobroMercadoPago = useModalDeCobroMercadoPago(
    cajaDeEscritorio
      ? (qrData, cobro, numeroVisible) => {
          cajaDeEscritorio.encolarImpresion(
            'el QR de cobro',
            qrDeCobroMercadoPago({ qrData, importe: cobro.importe, numeroVisible }, cajaDeEscritorio.contexto),
          )
        }
      : undefined,
  )
  const confirmacionDeReuso = useConfirmacionDeReusoDeCobro()
  // Un cobro de Mercado Pago que quedó vivo al recargar la pantalla: se ofrece retomarlo o cancelarlo.
  const [cobroDeRecarga, setCobroDeRecarga] = useState<ReservaDeCobro | null>(null)
  // Un cobro aprobado cuya venta no se cerró y que el cajero puede apartar para liberar la caja.
  const [cobroAApartarAhora, setCobroAApartarAhora] = useState<CobroAAPartar | null>(null)
  // Cobros apartados sin venta: lista durable del dispositivo, solo de altas.
  const [cobrosSinVenta, setCobrosSinVenta] = useState<CobroSinVenta[]>([])
  const copiaLocalLeida = sincronizacionOffline.copiaLocalLeida
  const sincronizacionRef = useRef(sincronizacionOffline)
  useEffect(() => {
    sincronizacionRef.current = sincronizacionOffline
  })
  const enLineaPrevioRef = useRef(sincronizacionOffline.enLinea)
  // Una sola lectura, al terminar de leer el almacén: un cobro iniciado en esta sesión nunca se ofrece como "retomable". Con conexión,
  // una reserva cuya venta ya se registró (el servidor marca el cobro usado) se sana sin mostrar nada.
  useEffect(() => {
    if (!copiaLocalLeida) return
    let vigente = true
    void (async () => {
      const hook = sincronizacionRef.current
      const [reserva, lista] = await Promise.all([hook.leerReservaDeCobro(), hook.leerCobrosSinVenta()])
      if (!vigente) return
      setCobrosSinVenta(lista)
      if (reserva === null || reserva.estado !== 'en_curso' || reserva.idCobro === undefined) return
      if (hook.enLinea) {
        const sanada = await sanarReservaYaVendida(reserva, {
          consultar: clienteDeCobrosMercadoPago.consultar,
          consumirReservaDeCobro: hook.consumirReservaDeCobro,
        })
        if (!vigente) return
        if (sanada === 'sanada') return
      }
      setCobroDeRecarga(reserva)
    })().catch(() => undefined)
    return () => {
      vigente = false
    }
  }, [copiaLocalLeida])
  // Al recuperar la señal con un aviso de cobro retomable, se verifica si su venta ya se registró: de ser así el aviso sobra.
  useEffect(() => {
    const veniaSinSenal = !enLineaPrevioRef.current
    enLineaPrevioRef.current = sincronizacionOffline.enLinea
    if (!sincronizacionOffline.enLinea || !veniaSinSenal || cobroDeRecarga === null || cobrandoRef.current) return
    const aviso = cobroDeRecarga
    void sanarReservaYaVendida(aviso, {
      consultar: clienteDeCobrosMercadoPago.consultar,
      consumirReservaDeCobro: sincronizacionRef.current.consumirReservaDeCobro,
    })
      .then((sanada) => {
        if (sanada === 'sanada') void sincronizarConLaReservaDeCobro()
      })
      .catch(() => undefined)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sincronizacionOffline.enLinea, cobroDeRecarga])


  // Cada vez que el drenado saca ventas de la cola, lo que el servidor sabe del saldo cambió: el que está en
  // pantalla deja de estar confirmado (y los recibos y tickets en vuelo que lo leyeron antes lo saben por el
  // contador) hasta que, con señal, una lectura nueva lo reemplace.
  const descensosDeLaColaRef = useRef(0)
  // Descensos de la cola al registrarse el último pago: si cambió al imprimir su recibo, el saldo posterior leído puede no incluir lo que se drenó.
  const descensosAlPagarRef = useRef(0)
  const largoPrevioDeLaColaRef = useRef(sincronizacionOffline.outboxCount)
  useEffect(() => {
    const previo = largoPrevioDeLaColaRef.current
    largoPrevioDeLaColaRef.current = sincronizacionOffline.outboxCount
    if (sincronizacionOffline.outboxCount >= previo) return
    descensosDeLaColaRef.current += 1
    saldoConfirmadoRef.current = null
    if (sincronizacionOffline.enLinea && clienteSeleccionado && !clienteSeleccionado.esConsumidorFinal) {
      refrescarClienteSeleccionado(clienteSeleccionado.id)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sincronizacionOffline.outboxCount])

  // Venta local primero: con una instantánea de ESTE punto de venta, el escaneo se resuelve contra
  // la instantánea sin esperar la red (decisión del dueño: un precio algo viejo es aceptable, una
  // caja lenta no). La vista previa y el cobro locales son del Consumidor Final y de los clientes
  // que trae la instantánea; cualquier otro cliente cotiza y cobra por el camino online.
  const instantaneaDelPuntoVenta =
    !modoPresupuesto &&
    sincronizacionOffline.instantanea !== null &&
    sincronizacionOffline.instantanea.idPuntoVenta === puntoVentaSeleccionada?.id
      ? sincronizacionOffline.instantanea
      : null

  // Bajo un presupuesto la venta nunca lleva envío (el servidor lo rechaza con `envio_no_admitido`). El valor de arranque es
  // el flag del punto de venta; la instantánea, que se refresca, manda sobre el listado leído al iniciar la sesión.
  const conEnvio =
    !modoPresupuesto &&
    (conEnvioElegido ?? instantaneaDelPuntoVenta?.envioPorDefecto ?? puntoVentaSeleccionada?.envioPorDefecto ?? false)

  // Los atajos F5 a F8 salen de la instantánea; sin ella (la sesión de un navegador no puede pedirla, y el escritorio no la tiene antes de
  // su primera sincronización) salen de una lectura aparte del servidor, que no exige un dispositivo y se pide una vez por punto de venta.
  // Hasta leer la copia persistida no se sabe si hay instantánea: pedirla antes sería una lectura de más en cada arranque del escritorio.
  const idPuntoVentaDeLosAtajosEnLinea =
    !modoPresupuesto && sincronizacionOffline.copiaLocalLeida && instantaneaDelPuntoVenta === null ? (puntoVentaSeleccionada?.id ?? null) : null
  const [atajosEnLinea, setAtajosEnLinea] = useState<{ idPuntoVenta: number; atajos: AtajoDeImporte[] } | null>(null)
  // Generación de la lectura de atajos: la invalida el cambio de punto de venta, la llegada de la instantánea y el desmontaje, para que
  // una respuesta tardía no pise el mapa vigente. `leyendoAtajosRef` evita pedirla dos veces a la vez.
  const generacionDeAtajosRef = useRef(0)
  const leyendoAtajosRef = useRef(false)
  function leerAtajosEnLinea(idPuntoVenta: number) {
    const token = (generacionDeAtajosRef.current += 1)
    leyendoAtajosRef.current = true
    clienteDeArticulos
      .atajosDeImporte()
      .then((atajos) => {
        if (generacionDeAtajosRef.current === token) setAtajosEnLinea({ idPuntoVenta, atajos })
      })
      .catch(() => {})
      .finally(() => {
        if (generacionDeAtajosRef.current === token) leyendoAtajosRef.current = false
      })
  }
  useEffect(() => {
    if (idPuntoVentaDeLosAtajosEnLinea === null) return
    leerAtajosEnLinea(idPuntoVentaDeLosAtajosEnLinea)
    return () => {
      generacionDeAtajosRef.current += 1
      leyendoAtajosRef.current = false
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [idPuntoVentaDeLosAtajosEnLinea])
  const atajosEnLineaVigentes =
    idPuntoVentaDeLosAtajosEnLinea !== null && atajosEnLinea?.idPuntoVenta === idPuntoVentaDeLosAtajosEnLinea ? atajosEnLinea.atajos : null
  const atajosDeImporte = useMemo(
    () => (instantaneaDelPuntoVenta ? atajosDeImporteOffline(instantaneaDelPuntoVenta) : (atajosEnLineaVigentes ?? [])),
    [instantaneaDelPuntoVenta, atajosEnLineaVigentes],
  )
  // Opciones del selector de cliente y su búsqueda en la copia local: estables entre renders para que la
  // lista no recalcule el filtro de todos los clientes en cada cambio de la pantalla.
  const opcionesDeCliente = useMemo(
    () => fusionarOpcionesCliente(opcionesClientes, clienteSeleccionado).map(aOpcionDeCliente),
    [opcionesClientes, clienteSeleccionado],
  )
  const buscarClientesLocales = useCallback(
    (termino: string): OpcionDeCliente[] =>
      instantaneaDelPuntoVenta ? buscarClientesOffline(instantaneaDelPuntoVenta, termino).map((c) => aOpcionDeCliente(aClienteListado(c))) : [],
    [instantaneaDelPuntoVenta],
  )

  // Los combos salen de la instantánea cuando hay una utilizable (también sin red) y, si no, de la API.
  const idListaDeLaVenta = clienteSeleccionado?.idListaPrecio ?? null
  const idEmpresaDeLaVenta = puntoVentaSeleccionada?.idEmpresa ?? null
  // La fuente de la API vive aparte para conservar su lista de combos mientras la pantalla esté montada.
  const fuenteDeCombosDeLaApi = useMemo(() => fuenteDesdeApi(idEmpresaDeLaVenta), [idEmpresaDeLaVenta])
  const enLineaParaCombos = sincronizacionOffline.enLinea
  // Abrir el buscador de combos olvida los códigos que la API no tenía: el cajero acaba de ver la lista y puede haber cambiado.
  useEffect(() => {
    if (buscadorDeCombosAbierto) fuenteDeCombosDeLaApi.olvidarAusentes()
  }, [buscadorDeCombosAbierto, fuenteDeCombosDeLaApi])
  const fuenteDeCombos = useMemo(
    () =>
      elegirFuenteDeCombos({
        instantanea: instantaneaDelPuntoVenta,
        idListaPrecio: idListaDeLaVenta,
        idEmpresa: idEmpresaDeLaVenta,
        enLinea: enLineaParaCombos,
        api: fuenteDeCombosDeLaApi,
      }),
    [instantaneaDelPuntoVenta, idListaDeLaVenta, idEmpresaDeLaVenta, enLineaParaCombos, fuenteDeCombosDeLaApi],
  )

  // Datos locales para el POS de escritorio (decisiones 6 y 7 del dueño): medios de pago y
  // tolerancia salen de la instantánea mientras el servidor no respondió o si falló por falta de
  // red — nunca ante un rechazo real del servidor, que se sigue mostrando como hoy. Apenas llega la
  // respuesta del servidor, reemplaza a la copia local.
  const datosLocalesDisponibles = corriendoEnTauri() && instantaneaDelPuntoVenta !== null
  const mediosLocales = useMemo(
    () => (datosLocalesDisponibles && instantaneaDelPuntoVenta ? instantaneaDelPuntoVenta.mediosDePago.map(aMedioPagoListado) : null),
    [datosLocalesDisponibles, instantaneaDelPuntoVenta],
  )
  // Un rechazo real deja `mediosDelServidor` en `[]` (ver `cargarMediosDePago`): solo la espera y la
  // falta de red lo dejan en `null`.
  const usarMediosLocales = mediosDelServidor === null && mediosLocales !== null
  const medios = usarMediosLocales ? mediosLocales : mediosDelServidor
  const errorMedios = usarMediosLocales ? '' : errorMediosDelServidor

  const usarParametrosLocales =
    parametrosDelServidor === null &&
    datosLocalesDisponibles &&
    (errorParametrosDelServidor === '' || errorParametrosEsDeRed)
  const parametros =
    usarParametrosLocales && instantaneaDelPuntoVenta ? { toleranciaPago: instantaneaDelPuntoVenta.toleranciaPago } : parametrosDelServidor
  const errorParametros = usarParametrosLocales ? '' : errorParametrosDelServidor

  // Último turno abierto confirmado por el servidor para este punto de venta (solo escritorio).
  const [turnoLocal, setTurnoLocal] = useState(() =>
    corriendoEnTauri() && !modoPresupuesto && puntoVentaDeSesion ? leerTurnoConfirmadoLocal(puntoVentaDeSesion.id) : null,
  )
  const turnoLocalDelPuntoVenta = turnoLocal !== null && turnoLocal.idPuntoVenta === puntoVentaSeleccionada?.id ? turnoLocal : null
  const usarTurnoLocal =
    !modoPresupuesto &&
    turnoDelServidor === null &&
    turnoLocalDelPuntoVenta !== null &&
    ((cargandoTurnoDelServidor && esperaDelTurnoVencida) || errorTurnoEsDeRed)
  const turno = usarTurnoLocal ? turnoLocalDelPuntoVenta : turnoDelServidor
  const cargandoTurno = usarTurnoLocal ? false : cargandoTurnoDelServidor
  const errorTurno = usarTurnoLocal ? '' : errorTurnoDelServidor

  /** Lo que el servidor confirmó sobre el turno abierto: se aplica y, en el escritorio, queda como
   * la copia local (o la borra, si no hay turno abierto). Nunca se llama con el resultado de un
   * error: un fallo no confirma nada. */
  function aplicarTurnoConfirmado(confirmado: TurnoResumen | null) {
    setTurno(confirmado)
    if (!corriendoEnTauri() || modoPresupuesto || !puntoVentaSeleccionada) return
    guardarTurnoConfirmadoLocal(puntoVentaSeleccionada.id, confirmado)
    setTurnoLocal(confirmado)
  }

  // Algún dato de la pantalla viene de la copia local porque el servidor no se pudo alcanzar.
  const datosLocalesPorFaltaDeRed =
    (usarMediosLocales && errorMediosEsDeRed) ||
    (usarParametrosLocales && errorParametrosEsDeRed) ||
    (usarTurnoLocal && errorTurnoEsDeRed) ||
    (datosLocalesDisponibles && errorCargaClientesEsDeRed)

  const medioPorId = useMemo(() => {
    const indice: Record<number, MedioPagoListado> = {}
    for (const m of medios ?? []) indice[m.id] = m
    return indice
  }, [medios])

  // judgment-day ronda 2 (WARNING): salida real para una venta que "necesita atención" —
  // reintentar (seguro, idempotente por número preasignado + contenido) o descartar (con
  // confirmación explícita, nunca silenciosa — `react-async-state` regla 7: la copia de
  // `ventasConError` promete una resolución, así que el `disabled` que la habilita/deshabilita
  // tiene que estar de verdad enchufado). Guarda de reentrancia por `ref` (regla 11) y por
  // ENTIDAD (`idLocal`), no por pantalla completa: cada venta rechazada es independiente de las
  // demás (regla 5), así que reintentar una no debe inhabilitar el botón de otra.
  const idLocalesEnAccionRef = useRef<Set<string>>(new Set())
  const [idLocalEnAccion, setIdLocalEnAccion] = useState<string | null>(null)
  const [idLocalConfirmandoDescarte, setIdLocalConfirmandoDescarte] = useState<string | null>(null)
  const [disparadorDeDescarte, setDisparadorDeDescarte] = useState<HTMLElement | null>(null)
  const [errorDescarte, setErrorDescarte] = useState('')
  const ventaPorDescartar = sincronizacionOffline.ventasConError.find((v) => v.idLocal === idLocalConfirmandoDescarte) ?? null
  const descartandoVenta = ventaPorDescartar !== null

  // Si la venta deja de estar en "necesita atención" con la confirmación abierta (la reintentó o la
  // sacó un ciclo de sincronización), el pedido queda vencido: no puede reabrirse solo si reaparece.
  useEffect(() => {
    if (idLocalConfirmandoDescarte !== null && ventaPorDescartar === null) {
      setIdLocalConfirmandoDescarte(null)
      setErrorDescarte('')
      pedirFocoEnElCodigo()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [idLocalConfirmandoDescarte, ventaPorDescartar])

  async function onReintentarVentaConError(idLocal: string) {
    if (idLocalesEnAccionRef.current.has(idLocal)) return
    idLocalesEnAccionRef.current.add(idLocal)
    setIdLocalEnAccion(idLocal)
    try {
      await sincronizacionOffline.reintentarVentaConError(idLocal)
    } finally {
      idLocalesEnAccionRef.current.delete(idLocal)
      setIdLocalEnAccion(null)
    }
  }

  function onPedirDescarteDeVenta(idLocal: string, evento: React.MouseEvent<HTMLButtonElement>) {
    setDisparadorDeDescarte(evento.currentTarget)
    setErrorDescarte('')
    setIdLocalConfirmandoDescarte(idLocal)
  }

  function onCancelarDescarteDeVenta() {
    setIdLocalConfirmandoDescarte(null)
    setErrorDescarte('')
  }

  async function onConfirmarDescarteDeVenta(idLocal: string) {
    if (idLocalesEnAccionRef.current.has(idLocal)) return
    idLocalesEnAccionRef.current.add(idLocal)
    setIdLocalEnAccion(idLocal)
    setErrorDescarte('')
    let descartada = false
    try {
      descartada = await sincronizacionOffline.descartarVentaConError(idLocal)
    } finally {
      idLocalesEnAccionRef.current.delete(idLocal)
      setIdLocalEnAccion(null)
    }
    if (descartada) {
      setIdLocalConfirmandoDescarte(null)
      pedirFocoEnElCodigo()
    } else {
      setErrorDescarte('No se pudo descartar la venta. La venta sigue en "necesita atención".')
    }
  }

  // Carga inicial: clientes (para encontrar el Consumidor Final por defecto, spec: "Omitted
  // idCliente defaults to Consumidor Final") y medios de pago (panel de pagos, Slice 7). Cada uno
  // con su propio try/catch y su propia generación: que uno falle no bloquea al otro, y una
  // respuesta tardía de una carga anterior (la del arranque, cuando ya se reintentó al volver la
  // red) nunca pisa a la más nueva.
  function cargarClientesIniciales() {
    const generacionCarga = (generacionCargaClientesRef.current += 1)

    clienteDeClientes
      .listar('', false)
      .then((pagina) => {
        if (!montadoRef.current || generacionCargaClientesRef.current !== generacionCarga) return
        setOpcionesClientes(pagina.items)
        setErrorCargaClientes('')
        setErrorCargaClientesEsDeRed(false)
        // stage-17-presupuestos-y-remitos (Slice 7): bajo `?idPresupuesto=` el cliente lo trae el
        // presupuesto (efecto dedicado más abajo, hidrata el registro completo por id) — el
        // default de Consumidor Final NUNCA escribe acá bajo este modo, mismo criterio que el
        // punto de venta (que bajo este modo sale del presupuesto y no de la sesión).
        if (!modoPresupuesto) {
          const consumidorFinal = pagina.items.find((c) => c.esConsumidorFinal) ?? null
          consumidorFinalRef.current = consumidorFinal
          // stage-pos-adjustments: un cliente restaurado desde el borrador (ver más arriba) ya
          // dejó al cajero exactamente donde estaba antes de navegar afuera — el default de
          // Consumidor Final nunca lo pisa. Tampoco pisa a un cliente que el cajero eligió
          // mientras esta carga estaba en vuelo (con la copia local ya pudo elegir uno).
          if (!clienteRestauradoDelBorradorRef.current) {
            setClienteSeleccionado((prev) => (prev === null || prev.esConsumidorFinal ? consumidorFinal : prev))
          }
        }
      })
      .catch((e) => {
        if (!montadoRef.current || generacionCargaClientesRef.current !== generacionCarga) return
        setErrorCargaClientes(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los clientes.')
        setErrorCargaClientesEsDeRed(e instanceof ErrorDeRed)
      })
  }

  const generacionMediosRef = useRef(0)
  function cargarMediosDePago() {
    const generacion = (generacionMediosRef.current += 1)

    clienteMediosPago
      .listar(false)
      .then((lista) => {
        if (!montadoRef.current || generacionMediosRef.current !== generacion) return
        setMedios(lista)
        setErrorMedios('')
        setErrorMediosEsDeRed(false)
      })
      .catch((e) => {
        if (!montadoRef.current || generacionMediosRef.current !== generacion) return
        // Sin red no se fija una lista vacía: con la copia local del escritorio la pantalla sigue
        // con los medios de la instantánea (`medios`, derivado), y sin copia local el aviso de
        // abajo ya bloquea el cobro.
        if (!(e instanceof ErrorDeRed)) setMedios([])
        setErrorMedios(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los medios de pago. No se puede cobrar.')
        setErrorMediosEsDeRed(e instanceof ErrorDeRed)
      })
  }

  useEffect(() => {
    cargarClientesIniciales()
    cargarMediosDePago()
    // Corre una sola vez por instancia: `PantallaPos` se remonta entera por `key` ante cualquier
    // cambio de punto de venta/presupuesto.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Consumidor Final y primera página de clientes desde la instantánea local (solo escritorio),
  // mientras el servidor no los trajo: con un arranque sin red (o una red lenta) el cajero vende
  // igual. Corre cuando aparece la instantánea; si la carga del servidor ya fijó el Consumidor
  // Final, no hace nada.
  useEffect(() => {
    if (!datosLocalesDisponibles || !instantaneaDelPuntoVenta || consumidorFinalRef.current !== null) return
    const consumidorFinalLocal = instantaneaDelPuntoVenta.clientes.find((c) => c.esConsumidorFinal)
    if (!consumidorFinalLocal) return
    const consumidorFinal = aClienteListado(consumidorFinalLocal)
    consumidorFinalRef.current = consumidorFinal
    const opcionesLocales = buscarClientesOffline(instantaneaDelPuntoVenta, '').map(aClienteListado)
    setOpcionesClientes((prev) => (prev.length === 0 ? opcionesLocales : prev))
    if (!clienteRestauradoDelBorradorRef.current) {
      setClienteSeleccionado((prev) => prev ?? consumidorFinal)
    }
  }, [datosLocalesDisponibles, instantaneaDelPuntoVenta])

  // stage-pos-adjustments (JD-2): un cliente restaurado desde el borrador (navegación afuera y
  // vuelta) trae el `ClienteListado` cacheado en el momento en que se guardó el borrador — su
  // saldo puede haber cambiado desde entonces (ej. un pago a cuenta registrado desde otra
  // pantalla). Refetch único al montar, gateado igual que `refrescarClienteSeleccionado`; si
  // falla se conserva el cliente restaurado tal cual, nunca se lo resetea.
  useEffect(() => {
    if (!clienteRestauradoDelBorradorRef.current) return
    const idClienteRestaurado = borradorInicial?.clienteSeleccionado?.id
    if (idClienteRestaurado == null) return
    refrescarClienteSeleccionado(idClienteRestaurado)
    // `borradorInicial` proviene de `borradorInicialRef`, congelado en el primer render de esta
    // instancia — nunca cambia, así que este efecto corre una sola vez.
  }, [borradorInicial, refrescarClienteSeleccionado])

  // stage-pos-turno-y-foco: `medios` todavía no cargó cuando se arma el estado inicial de
  // `filasPago` (`filaPagoInicial(id, null)` más arriba nunca preselecciona nada) — apenas carga,
  // se preselecciona Efectivo SOLO si la fila sigue intacta (una sola fila, sin medio elegido)
  // para no pisar una elección que el cajero ya haya hecho mientras `medios` estaba en vuelo.
  useEffect(() => {
    if (medios === null) return
    const idEfectivo = idMedioEfectivo(medios)
    if (idEfectivo === '') return
    setFilasPago((prev) => (prev.length === 1 && prev[0].idMedioPago === '' ? [{ ...prev[0], idMedioPago: idEfectivo }] : prev))
  }, [medios])

  // stage-pos-adjustments: guarda el borrador del ticket en curso ante cualquier cambio de lo
  // que compone la venta libre — nunca bajo `?idPresupuesto=` (`claveBorrador` es `null` ahí) ni
  // sin `Provider` (`almacenBorradores` es `null` fuera de `ShellPos`/`Layout`). Deliberadamente
  // NO mira `entradaEscaneo`/`terminoCliente` (búfer de escaneo, término de búsqueda) — solo lo
  // que de verdad hace falta reconstruir.
  useEffect(() => {
    if (claveBorrador === null || !almacenBorradores) return
    almacenBorradores.guardar(claveBorrador, {
      lineas,
      precios,
      cantidadesEnEdicion,
      filasPago,
      proximoIdFilaPago: proximaFilaPagoIdRef.current,
      clienteSeleccionado,
      conEnvio: conEnvioElegido,
    })
  }, [claveBorrador, almacenBorradores, lineas, precios, cantidadesEnEdicion, filasPago, clienteSeleccionado, conEnvioElegido])

  // react-async-state regla 2: el importe del envío se pide solo con el envío tildado y con conexión (sin ella se usa el de la
  // instantánea); una respuesta desactualizada nunca pisa a la más reciente. Destildar y volver a tildar reintenta una
  // lectura fallida. Al empezar una lectura nueva (otro punto de venta, otra conexión, otro tildado) el importe anterior se
  // descarta: uno viejo no se muestra ni deja cobrar mientras llega el vigente.
  const hayConexion = sincronizacionOffline.enLinea
  useEffect(() => {
    if (!conEnvio || !hayConexion || !puntoVentaSeleccionada) return

    const generacion = (generacionImporteEnvioRef.current += 1)
    let vigente = true
    setImporteEnvio(null)
    setErrorImporteEnvio(false)

    api
      .get<ParametroResuelto>(
        `/parametros/importe_envio?idEmpresa=${puntoVentaSeleccionada.idEmpresa}&idPuntoVenta=${puntoVentaSeleccionada.id}`,
      )
      .then((parametro) => {
        if (!vigente || generacionImporteEnvioRef.current !== generacion) return
        const importe = Number(parametro.valor)
        if (!Number.isFinite(importe)) {
          setImporteEnvio(null)
          setErrorImporteEnvio(true)
          return
        }
        setImporteEnvio(importe)
      })
      .catch(() => {
        if (!vigente || generacionImporteEnvioRef.current !== generacion) return
        setImporteEnvio(null)
        setErrorImporteEnvio(true)
      })

    return () => {
      vigente = false
    }
  }, [conEnvio, hayConexion, puntoVentaSeleccionada])

  // react-async-state regla 2: cada cambio de punto de venta dispara la resolución de
  // tolerancia_pago (ADR-13: punto de venta > empresa > default) — una respuesta desactualizada
  // nunca puede pisar la más reciente. `vuelto_maximo` ya no se resuelve acá: dejó de gobernar
  // el vuelto de ventas en efectivo (decisión del dueño 2026-09-16, ver
  // Ways.Domain.Ventas.BilletesArgentinos) y mantener su fetch solo exponía el cobro a bloquearse
  // si ese endpoint fallaba, sin que el valor se usara para nada.
  useEffect(() => {
    if (!puntoVentaSeleccionada) {
      setParametros(null)
      setErrorParametros('')
      return
    }

    const generacion = (generacionParametrosRef.current += 1)
    let vigente = true

    api
      .get<ParametroResuelto>(
        `/parametros/tolerancia_pago?idEmpresa=${puntoVentaSeleccionada.idEmpresa}&idPuntoVenta=${puntoVentaSeleccionada.id}`,
      )
      .then((tolerancia) => {
        if (!vigente || generacionParametrosRef.current !== generacion) return
        setParametros({ toleranciaPago: Number(tolerancia.valor) })
        setErrorParametros('')
        setErrorParametrosEsDeRed(false)
      })
      .catch((e) => {
        if (!vigente || generacionParametrosRef.current !== generacion) return
        setParametros(null)
        setErrorParametros(
          e instanceof ErrorApi ? e.message : 'No se pudieron cargar los parámetros de pago. No se puede cobrar.',
        )
        setErrorParametrosEsDeRed(e instanceof ErrorDeRed)
      })

    return () => {
      vigente = false
    }
  }, [puntoVentaSeleccionada, recargaDeParametros])

  /**
   * Consulta el turno abierto del punto de venta — mismo endpoint y criterio de generación que
   * `PantallaCaja` en Caja.tsx (react-async-state regla 2). Extraída a función (en vez de vivir
   * solo dentro del efecto de abajo) para que "Reintentar" (judgment-day ronda 1, T1, CRITICAL)
   * pueda volver a dispararla con el mismo camino — sin esto, un `GET .../abierto` que falla deja
   * `bloqueadoPorTurno` en `true` para siempre (ni "Abrir turno" ni "Cerrar caja" se renderizan
   * porque el ternario del badge solo muestra el aviso de error) y el safety-net del 409 nunca es
   * alcanzable, sin ninguna forma de recuperarse salvo recargar la página entera — en el shell de
   * escritorio, sin selector de punto de venta, ni eso.
   */
  function consultarTurno(idPuntoVenta: number) {
    const miGeneracion = (generacionTurnoRef.current += 1)
    setCargandoTurno(true)
    setErrorTurno('')
    // Con red, el turno guardado en el dispositivo recién rige si el servidor no contestó dentro
    // del tope: mientras tanto se espera la respuesta, que es la que puede decir que ya se cerró.
    setEsperaDelTurnoVencida(false)
    const idTopeDeEspera = setTimeout(() => {
      if (montadoRef.current && generacionTurnoRef.current === miGeneracion) setEsperaDelTurnoVencida(true)
    }, LIMITE_DE_ESPERA_DEL_TURNO_MS)

    // Se retorna la promesa (en vez de tratarla como fire-and-forget) para que `reintentarTurno`
    // pueda liberar su guarda de reentrancia recién cuando esta corrida específica termina.
    return clienteDeCaja
      .obtenerAbierto(idPuntoVenta)
      .then((t) => {
        if (generacionTurnoRef.current !== miGeneracion) return
        aplicarTurnoConfirmado(t)
        setErrorTurnoEsDeRed(false)
        // Re-judgment (WARNING, ambos jueces): un `avisoCerrarCaja` viejo ("El turno ya fue
        // cerrado.") no debe sobrevivir a una consulta exitosa posterior — mount o "Reintentar" —
        // que confirma el turno abierto de nuevo; ese aviso es del click anterior, no de este.
        if (t) setAvisoCerrarCaja('')
      })
      .catch((e) => {
        if (generacionTurnoRef.current !== miGeneracion) return
        setTurno(null)
        setErrorTurno(
          e instanceof ErrorApi ? e.message : 'No se pudo consultar el turno abierto de este punto de venta.',
        )
        setErrorTurnoEsDeRed(e instanceof ErrorDeRed)
      })
      .finally(() => {
        clearTimeout(idTopeDeEspera)
        if (generacionTurnoRef.current !== miGeneracion) return
        setCargandoTurno(false)
        setTurnoConsultado(true)
      })
  }

  // Nunca corre bajo `?idPresupuesto=` (el banner/gate de turno es una noción del camino libre).
  useEffect(() => {
    if (modoPresupuesto || !puntoVentaSeleccionada) {
      setTurno(null)
      setCargandoTurno(false)
      setErrorTurno('')
      return
    }
    consultarTurno(puntoVentaSeleccionada.id)
    // `consultarTurno` no es estable entre renders (cierra sobre estado/props del componente),
    // pero su identidad no importa acá: el efecto solo necesita dispararse cuando cambian sus
    // precondiciones reales (`modoPresupuesto`/el punto de venta), nunca por una referencia nueva
    // de la propia función — mismo criterio que el resto de los efectos de esta pantalla que
    // llaman a un helper declarado en el cuerpo del componente.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [modoPresupuesto, puntoVentaSeleccionada])

  // Cuando la red vuelve (o cada vez que la sincronización vuelve a verificar la instantánea contra
  // el servidor), se piden de nuevo los datos que quedaron con la copia local por falta de red: lo
  // que responda el servidor reemplaza a la copia local. Un pedido que vuelve a fallar deja todo
  // como estaba y no se reintenta hasta la próxima verificación.
  useEffect(() => {
    if (!sincronizacionOffline.enLinea) return
    if (errorCargaClientesEsDeRed) cargarClientesIniciales()
    if (errorMediosEsDeRed) cargarMediosDePago()
    if (errorParametrosEsDeRed) setRecargaDeParametros((n) => n + 1)
    if (errorTurnoEsDeRed && !modoPresupuesto && puntoVentaSeleccionada) consultarTurno(puntoVentaSeleccionada.id)
    // Solo lo disparan la vuelta de la red y una verificación nueva; los indicadores de error se
    // leen al momento de correr.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sincronizacionOffline.enLinea, sincronizacionOffline.verificacionesConElServidor])

  /** "Reintentar" del aviso de error de turno — vuelve a consultar con el mismo camino que el
   * mount (mismo guard de generación: una respuesta vieja en vuelo desde antes nunca pisa la de
   * este reintento). El botón queda `disabled` mientras `cargandoTurno` es `true`, pero eso solo
   * cubre el render — react-async-state regla 11: la guarda de reentrancia real es un `ref`, no el
   * estado (`cargandoTurno` no se actualiza hasta el próximo render, así que dos clicks en el
   * mismo tick pasarían los dos sin esto), liberado en el `finally` SIN gatear por generación (se
   * libera siempre, la regla 11 lo pide explícito). */
  const reintentandoTurnoRef = useRef(false)
  function reintentarTurno() {
    if (reintentandoTurnoRef.current) return
    if (!puntoVentaSeleccionada) return
    reintentandoTurnoRef.current = true
    consultarTurno(puntoVentaSeleccionada.id).finally(() => {
      reintentandoTurnoRef.current = false
    })
  }

  /** El turno recién confirmado (apertura propia, autocuración del gate, o el 409 safety-net) es
   * la fuente más autoritativa posible: bumpear la generación invalida cualquier `GET …/abierto`
   * que siguiera en vuelo desde antes (mismo criterio que `turnoAbierto` en Caja.tsx). */
  function turnoConfirmadoAbierto(nuevoTurno: TurnoResumen) {
    generacionTurnoRef.current += 1
    setErrorTurno('')
    setErrorTurnoEsDeRed(false)
    setCargandoTurno(false)
    setTurnoConsultado(true)
    aplicarTurnoConfirmado(nuevoTurno)
    // Re-judgment (WARNING, ambos jueces): mismo criterio que en `consultarTurno` — "Abrir turno"
    // (gate propio o safety-net del 409) confirma un turno abierto nuevo, así que cualquier aviso
    // de "El turno ya fue cerrado." de un click anterior de "Cerrar caja" queda obsoleto.
    setAvisoCerrarCaja('')
  }

  /**
   * "Cerrar caja" de "Datos de la venta" — vuelve a consultar el turno abierto ANTES de hacer
   * cualquier otra cosa (judgment-day ronda 1, T2, WARNING: el `turno.id` que ya tiene el estado
   * pudo quedar viejo — otra pestaña/cajero cerró la caja mientras esta pantalla seguía mostrando
   * "Turno abierto"). Con `cajaDeEscritorio` (stage-pos-retiros-y-cierre-por-retiro, etapa 5):
   * abre el flujo de cierre por retiro EN ESTA MISMA pantalla (nunca navega). Sin él,
   * `alIrACerrarCaja` es el seam viejo del shell de escritorio; sin ninguno de los dos, navega con
   * `useNavigate` como siempre (camino web). Nunca actúa con un id potencialmente viejo: si la
   * consulta ya no encuentra turno, actualiza el badge a "cerrado" en vez de seguir a ciegas.
   */
  async function irACerrarCaja() {
    // regla 9/11: guarda de reentrancia de primera línea, liberada siempre en el `finally` sin
    // gate de generación (un doble click no debe disparar dos consultas).
    if (verificandoCierreRef.current) return
    // Excluyente con "Retirar": si ese flujo ya está abierto, gana él. Por `ref` y no por
    // `pasoRetiro` porque un click del mismo tick lee el snapshot del render anterior (regla 11).
    if (pasoRetiroRef.current !== null) return
    if (!puntoVentaSeleccionada) return

    // stage-pos-venta-offline-web (Parte D, regla dura: "no cerrar turno con el outbox no
    // vacío"): choke point único — cubre TANTO el camino web (`navigate` más abajo) como el
    // cierre por retiro del escritorio (`cajaDeEscritorio`), nunca hace falta duplicar el
    // chequeo en cada rama. Una venta que drena DESPUÉS de que el turno cerró la rechaza el
    // servidor — huérfana, con su ticket ya en la mano de un cliente (spec del dueño) — así que
    // se bloquea ANTES de tocar la red, ni siquiera hace falta consultar el turno primero.
    // judgment-day ronda 1 (CRITICAL): el bloqueo cubre TAMBIÉN `ventasConError` — una venta que
    // el servidor rechazó de forma permanente sigue siendo una venta real con ticket entregado,
    // nunca algo que un cierre pueda ignorar solo porque ya salió del outbox.
    function bloquearPorColaLocal(cantidadPendiente: number, cantidadConError: number): boolean {
      if (cantidadPendiente === 0 && cantidadConError === 0) return false
      const partes: string[] = []
      if (cantidadPendiente > 0) partes.push(`hay ${pluralizar(cantidadPendiente, 'venta')} sin sincronizar`)
      if (cantidadConError > 0) partes.push(fraseVentasNecesitanAtencion(cantidadConError))
      setAvisoCerrarCaja(`No se puede cerrar la caja: ${partes.join(' y ')}. Resolvé la situación antes de cerrar.`)
      return true
    }

    // Antes de consultar el turno se drena y se rinde la cola: con la venta local casi siempre hay
    // alguna venta recién cobrada esperando su envío, y la guarda de cierre del servidor exige un
    // reporte de rendición de menos de 5 minutos (`ReglaDeRendicionDeCola.VentanaDeFrescura`),
    // que con un intervalo de sincronización largo podría estar vencido. Una venta que necesita
    // atención no se resuelve drenando, así que sola bloquea de inmediato, sin tocar la red.
    if (sincronizacionOffline.outboxCount === 0 && bloquearPorColaLocal(0, sincronizacionOffline.ventasConError.length)) return

    verificandoCierreRef.current = true
    setVerificandoCierre(true)
    setAvisoCerrarCaja('')

    const miGeneracion = (generacionTurnoRef.current += 1)

    // Todo lo que espera vive dentro del `try`: el `finally` libera la guarda de reentrancia y el
    // `disabled` de "Cerrar caja"/"Retirar" pase lo que pase (regla 11).
    try {
      const cola = await sincronizacionOffline.drenarAhora()
      if (!montadoRef.current) return
      if (bloquearPorColaLocal(cola.pendientes, cola.conError)) return

      const turnoReal = await clienteDeCaja.obtenerAbierto(puntoVentaSeleccionada.id)
      // Re-judgment (WARNING, juez A): si la pantalla ya se desmontó mientras el `await` estaba en
      // vuelo, ni un `setState` ni, sobre todo, la navegación posterior deben dispararse — se
      // chequea ANTES que la generación (y antes de cualquier otra cosa) a propósito.
      if (!montadoRef.current) return
      if (generacionTurnoRef.current !== miGeneracion) return

      if (!turnoReal) {
        aplicarTurnoConfirmado(null)
        setAvisoCerrarCaja('El turno ya fue cerrado.')
        return
      }

      if (cajaDeEscritorio) {
        setIdTurnoACerrar(turnoReal.id)
        setPasoCierre('confirmar')
      } else if (alIrACerrarCaja) {
        alIrACerrarCaja(turnoReal.id)
      } else {
        navigate(`/caja/cierre?idTurno=${turnoReal.id}`)
      }
    } catch (e) {
      if (!montadoRef.current) return
      if (generacionTurnoRef.current !== miGeneracion) return
      setAvisoCerrarCaja(e instanceof ErrorApi ? e.message : 'No se pudo verificar el turno abierto.')
    } finally {
      verificandoCierreRef.current = false
      if (montadoRef.current) setVerificandoCierre(false)
    }
  }

  // --- stage-pos-retiros-y-cierre-por-retiro (etapa 5, desktop-only): flujo "Retirar" ---

  /** Abre el primer modal del flujo "Retirar" — botón propio de los controles de turno, visible
   * solo con `cajaDeEscritorio` y un turno abierto (ver `controlesTurno`). */
  function abrirRetiro() {
    // Excluyente con "Cerrar caja": mientras su consulta está en vuelo, `pasoCierre` sigue en
    // `null`, así que el `disabled` compartido no alcanza para un click del mismo tick (regla 11).
    // Esta guarda es además lo que hace imposible que `pasoRetiro` cambie entre ese `await` y el
    // `setPasoCierre` posterior: `verificandoCierreRef` recién se libera en el `finally`.
    if (verificandoCierreRef.current) return
    setErrorAperturaRetiro('')
    setAvisoRetiroOk('')
    cambiarPasoRetiro('confirmarApertura')
  }

  /** "No" del primer modal, o Escape/backdrop/"X" (`Modal.onCerrar`) — nada se auditó todavía. */
  function cancelarRetiro() {
    if (registrandoAperturaRetiroRef.current) return
    cambiarPasoRetiro(null)
  }

  /**
   * "Sí" — registra la apertura de cajón de auditoría (`AperturaCajon`, importe 0) y SOLO si esa
   * escritura confirma, encola el pulso de cajón. Si el POST de auditoría falla, el cajón NUNCA se
   * abre (spec del dueño): se muestra el error en el mismo modal y el cajero puede reintentar o
   * cancelar.
   */
  async function confirmarAperturaParaRetiro() {
    if (registrandoAperturaRetiroRef.current) return
    if (!turno || !cajaDeEscritorio) return

    registrandoAperturaRetiroRef.current = true
    setRegistrandoAperturaRetiro(true)
    setErrorAperturaRetiro('')

    try {
      await clienteDeCaja.registrarMovimiento(turno.id, {
        tipo: 'AperturaCajon',
        importe: 0,
        motivo: 'Apertura de cajón para retiro',
      })
      if (!montadoRef.current) return
      cajaDeEscritorio.encolarImpresion('la apertura de cajón', pulsoDeCajon())
      cambiarPasoRetiro('monto')
    } catch (e) {
      if (!montadoRef.current) return
      setErrorAperturaRetiro(e instanceof ErrorApi ? e.message : 'No se pudo registrar la apertura de cajón.')
    } finally {
      registrandoAperturaRetiroRef.current = false
      if (montadoRef.current) setRegistrandoAperturaRetiro(false)
    }
  }

  /** Confirmar el monto — registra el `Retiro` real y encola su ticket. NUNCA vuelve a pulsar el
   * cajón (ya se pulsó en `confirmarAperturaParaRetiro`). */
  async function confirmarMontoRetiro() {
    if (registrandoRetiroRef.current) return
    if (!turno || !cajaDeEscritorio) return
    if (montoRetiro === null || montoRetiro <= 0) return

    registrandoRetiroRef.current = true
    setRegistrandoRetiro(true)
    setErrorRetiro('')

    try {
      await clienteDeCaja.registrarMovimiento(turno.id, {
        tipo: 'Retiro',
        importe: montoRetiro,
        motivo: 'Retiro de efectivo',
      })
      if (!montadoRef.current) return
      cajaDeEscritorio.encolarImpresion(
        'el ticket de retiro',
        ticketRetiroDeEfectivo({ fecha: new Date().toISOString(), importe: montoRetiro }, cajaDeEscritorio.contexto),
      )
      cambiarPasoRetiro(null)
      setMontoRetiro(null)
      setAvisoRetiroOk('Retiro registrado.')
    } catch (e) {
      if (!montadoRef.current) return
      setErrorRetiro(e instanceof ErrorApi ? e.message : 'No se pudo registrar el retiro.')
    } finally {
      registrandoRetiroRef.current = false
      if (montadoRef.current) setRegistrandoRetiro(false)
    }
  }

  // El aviso de éxito es efímero — desaparece solo a los pocos segundos, o de inmediato si el
  // cajero arranca otro "Retirar" (`abrirRetiro` ya lo limpia). El timer se limpia en cada corrida
  // (incluido el desmontaje) para no setear estado después de que la pantalla ya no está.
  useEffect(() => {
    if (avisoRetiroOk === '') return
    const idTimeout = setTimeout(() => setAvisoRetiroOk(''), 4000)
    return () => clearTimeout(idTimeout)
  }, [avisoRetiroOk])

  // --- stage-pos-retiros-y-cierre-por-retiro (etapa 5, desktop-only): flujo "Cerrar caja" ---

  /** "No" del primer modal, o Escape/backdrop/"X" — el turno sigue abierto, nada cambió. */
  function cancelarConfirmarCierre() {
    if (registrandoAperturaCierreRef.current) return
    setPasoCierre(null)
    setIdTurnoACerrar(null)
  }

  /** "Sí" — mismo criterio que `confirmarAperturaParaRetiro`: solo encola el pulso de cajón si el
   * POST de auditoría confirma. */
  async function confirmarAperturaParaCierre() {
    if (registrandoAperturaCierreRef.current) return
    if (idTurnoACerrar === null || !cajaDeEscritorio) return

    registrandoAperturaCierreRef.current = true
    setRegistrandoAperturaCierre(true)
    setErrorAperturaCierre('')

    try {
      await clienteDeCaja.registrarMovimiento(idTurnoACerrar, {
        tipo: 'AperturaCajon',
        importe: 0,
        motivo: 'Apertura de cajón para cierre',
      })
      if (!montadoRef.current) return
      cajaDeEscritorio.encolarImpresion('la apertura de cajón', pulsoDeCajon())
      setPasoCierre('monto')
    } catch (e) {
      if (!montadoRef.current) return
      setErrorAperturaCierre(e instanceof ErrorApi ? e.message : 'No se pudo registrar la apertura de cajón.')
    } finally {
      registrandoAperturaCierreRef.current = false
      if (montadoRef.current) setRegistrandoAperturaCierre(false)
    }
  }

  /** Cancelar el monto — el cajón ya está abierto, pero el turno TODAVÍA no cerró (`cerrarPorRetiro`
   * nunca se llamó): cancelar acá simplemente deja el turno abierto, como pide la spec.
   *
   * judgment-day ronda 0 (JD-E5b-1, CRITICAL): NUNCA debe correr mientras `cierreIncierto` es
   * `true` — el propio `Modal` ya lo bloquea (`ocupado` incluye `cierreIncierto`, ver el render
   * más abajo), pero el guard de acá adentro es defensivo por si algún día algo más llega a
   * invocar esta función directamente. `setCierreIncierto(false)` es parte del reset normal
   * (no-op cuando ya es `false`, que es el 100% de las veces que este guard deja pasar) — así
   * ningún camino de cancelación puede dejar `cierreIncierto` pegado en `true` para el próximo
   * "Cerrar caja". */
  function cancelarMontoCierre() {
    if (cerrandoPorRetiroRef.current || recuperandoCierreRef.current || cierreIncierto) return
    setPasoCierre(null)
    setIdTurnoACerrar(null)
    setMontoCierre(null)
    setErrorCierrePorRetiro('')
    setCierreIncierto(false)
    reiniciarOverrideDeRendicion()
  }

  /** Reset del override supervisado de rendición: el rechazo que lo reveló es de ESTE intento, así
   * que el próximo "Cerrar caja" vuelve a arrancar sin el control a la vista. Lo llaman los dos
   * caminos de salida del paso "monto": cancelar y un cierre confirmado (`cierreConfirmado`). */
  function reiniciarOverrideDeRendicion() {
    setRendicionPendiente(false)
    setForzarSinRendicion(false)
    setMotivoSinRendicion('')
  }

  function cambiarForzarSinRendicion(valor: boolean) {
    if (cerrandoPorRetiroRef.current) return
    setForzarSinRendicion(valor)
  }

  function cambiarMotivoSinRendicion(valor: string) {
    if (cerrandoPorRetiroRef.current) return
    setMotivoSinRendicion(valor)
  }

  /** Refresca el estado del turno a "cerrado" tras un cierre por retiro confirmado (por la propia
   * respuesta 2xx o por la recuperación de un resultado incierto) — mismo criterio que
   * `turnoConfirmadoAbierto`: bumpear la generación invalida cualquier `GET …/abierto` en vuelo
   * desde antes. */
  function turnoConfirmadoCerrado() {
    generacionTurnoRef.current += 1
    setErrorTurno('')
    setCargandoTurno(false)
    aplicarTurnoConfirmado(null)
    setAvisoCerrarCaja('')
  }

  /** Cierra el flujo completo tras un cierre confirmado (2xx directo o recuperado tras un
   * resultado incierto) — encola el ticket, refresca el turno a "cerrado" y limpia todo el estado
   * del flujo. Nunca navega: el cajero se queda en la pantalla de venta, ahora bloqueada como
   * cualquier turno cerrado. */
  function cierreConfirmado(resumen: ResumenDeCierrePorRetiro) {
    if (!cajaDeEscritorio) return
    cajaDeEscritorio.encolarImpresion('el comprobante de cierre de turno', cierreDeTurno(resumen, cajaDeEscritorio.contexto))
    turnoConfirmadoCerrado()
    setPasoCierre(null)
    setIdTurnoACerrar(null)
    setMontoCierre(null)
    setCierreIncierto(false)
    setErrorCierrePorRetiro('')
    reiniciarOverrideDeRendicion()
    // stage-pos-sesion-offline: cierre de turno es uno de los tres disparadores de limpieza de la
    // sesión persistida (ver el doc-comment de `limpiarSesionDeCajeroPersistida`) — un turno es de
    // UN cajero, el próximo tiene que loguearse como sí mismo. Este es el único choke point de
    // "turno cerrado con éxito" del POS de escritorio (llega acá tanto desde el 2xx directo de
    // `confirmarCierrePorRetiro` como desde la recuperación de un cierre incierto en
    // `recuperarCierreIncierto`) — `CierreDeCaja.tsx` (camino web) nunca corre bajo Tauri, así que
    // no necesita este mismo llamado. No hace nada fuera de Tauri, nunca lanza.
    //
    // judgment-day ronda 1 (FIX 2a, BLOCKER): mismo motivo que `ShellPos.cerrarSesion` — sin
    // limpiar también el bearer EN MEMORIA, el próximo cajero heredaría la sesión de este (nunca
    // se le vuelve a pedir login mientras la pantalla no se recargue). `turnoConfirmadoCerrado()`
    // no dispara ningún nuevo `GET` (el gate de turno solo se re-consulta al montar, ver
    // `consultarTurno`), así que limpiar el bearer acá no bota al cajero de la pantalla bloqueada
    // de "turno cerrado" — recién hace efecto en la PRÓXIMA request real.
    establecerTokenDeSesionBearer(null)
    void terminarSesionLocalDelPos()
  }

  // El servidor rechaza `forzarSinRendicion` sin motivo (`400 motivo_requerido`), así que el motivo
  // se exige acá antes de intentar el cierre: `cierrePorRetiroBloqueado` es el ÚNICO enforcement
  // (el `disabled` de "Confirmar"), no hay un `if` espejo dentro del handler — con el botón
  // deshabilitado esa rama nunca se ejecutaría y una guarda inmatable no se shippea (PR #257).
  const motivoDeForzadoCompleto = motivoSinRendicion.trim() !== ''
  const esSupervisorOAdmin = usuario !== null && puedeForzarCierreSinRendicion(usuario.rolId)
  const cierrePorRetiroBloqueado = cerrandoPorRetiro || montoCierre === null || (forzarSinRendicion && !motivoDeForzadoCompleto)

  /**
   * Confirmar el monto — llama `cerrarPorRetiro` UNA sola vez por click. Un 503
   * `resultado_incierto` o una falla de red (no es `ErrorApi`: `fetch` la tira como excepción
   * cruda) entra en modo "cierre incierto" (`recuperarCierreIncierto`, abajo) — nunca vuelve a
   * postear el cierre desde acá adentro. Un `409 rendicion_de_dispositivo_pendiente` nunca es un
   * resultado incierto (el cierre definitivamente NO pasó): retorna antes de esa clasificación,
   * revelando el override supervisado.
   */
  async function confirmarCierrePorRetiro() {
    if (cerrandoPorRetiroRef.current) return
    if (idTurnoACerrar === null || montoCierre === null || !cajaDeEscritorio) return

    cerrandoPorRetiroRef.current = true
    setCerrandoPorRetiro(true)
    setErrorCierrePorRetiro('')

    // Los dos campos del override viajan juntos o no viajan: un motivo sin el flag es `400
    // motivo_sin_forzado` del lado del servidor.
    const base = { importeRetirado: montoCierre, observaciones: null }
    const solicitud: SolicitudDeCierrePorRetiro = forzarSinRendicion
      ? { ...base, forzarSinRendicion: true, motivoSinRendicion: motivoSinRendicion.trim() }
      : base

    try {
      const resumen = await clienteDeCaja.cerrarPorRetiro(idTurnoACerrar, solicitud)
      if (!montadoRef.current) return
      cierreConfirmado(resumen)
    } catch (e) {
      if (!montadoRef.current) return
      if (e instanceof ErrorApi && e.codigo === 'rendicion_de_dispositivo_pendiente') {
        // El servidor tiene su propia guarda de cola sin drenar, sobre TODOS los dispositivos del
        // punto de venta (no solo el de esta máquina, que `irACerrarCaja` ya chequeó): su mensaje
        // nombra cuál bloquea y por qué, así que se muestra tal cual. Las tres salidas reales son
        // las que nombra la copia de abajo — esta pantalla es el único cierre del POS de escritorio.
        setErrorCierrePorRetiro(
          `${e.message} Se destraba solo en cuanto ese dispositivo vuelva a rendir su cola; si no va a volver, un administrador puede revocarlo. Un supervisor también puede cerrar igual acá, con el motivo.`,
        )
        setRendicionPendiente(true)
        return
      }
      if (e instanceof ErrorApi && e.codigo === 'prohibido' && forzarSinRendicion) {
        // `ValidarOverrideDeRendicion` del servidor rechaza el forzado por rol con este código. Se
        // exige el CÓDIGO (no un 403 cualquiera): otro 403 mientras se fuerza —sesión degradada,
        // `OperacionDePos`, alcance de tenant— tiene otra causa y cae al mensaje real del servidor.
        // El rol del cliente nunca es fuente de verdad (`puedeForzarCierreSinRendicion` solo da
        // forma a la copia), así que este 403 es alcanzable aunque la pantalla creyera que se podía.
        setErrorCierrePorRetiro('Solo un supervisor o un administrador puede cerrar el turno sin la rendición del dispositivo.')
        return
      }
      const resultadoIncierto = e instanceof ErrorApi ? e.codigo === 'resultado_incierto' && e.estado === 503 : true
      if (resultadoIncierto) {
        setCierreIncierto(true)
        setErrorCierrePorRetiro(
          'No se pudo confirmar si el cierre se registró. Estamos verificando — no se va a reintentar el cierre solo.',
        )
      } else {
        setErrorCierrePorRetiro(e instanceof ErrorApi ? e.message : 'No se pudo cerrar el turno.')
      }
    } finally {
      cerrandoPorRetiroRef.current = false
      if (montadoRef.current) setCerrandoPorRetiro(false)
    }
  }

  /**
   * "Reintentar" bajo `cierreIncierto` — SOLO consulta `obtenerResumenDeCierre` (lectura
   * idempotente), nunca vuelve a postear `cerrarPorRetiro` (spec: "Never call cerrarPorRetiro
   * twice for one confirmation"). Tres desenlaces: (a) hay resumen — el cierre sí sucedió, se
   * completa igual que un 2xx directo; (b) `409 turno_no_cerrado` — el cierre NO sucedió, sale del
   * modo incierto y vuelve a habilitar "Confirmar" (un click ahí SÍ puede volver a postear, es una
   * confirmación nueva); (c) cualquier otra falla (incluida otra ambigua) — sigue incierto, el
   * cajero puede volver a apretar "Reintentar".
   */
  async function recuperarCierreIncierto() {
    if (recuperandoCierreRef.current) return
    if (idTurnoACerrar === null) return

    recuperandoCierreRef.current = true
    setRecuperandoCierre(true)

    try {
      const resumen = await clienteDeCaja.obtenerResumenDeCierre(idTurnoACerrar)
      if (!montadoRef.current) return
      cierreConfirmado(resumen)
    } catch (e) {
      if (!montadoRef.current) return
      if (e instanceof ErrorApi && e.codigo === 'turno_no_cerrado') {
        setCierreIncierto(false)
        setErrorCierrePorRetiro('El cierre no se confirmó — el turno sigue abierto. Podés reintentar.')
      } else {
        setErrorCierrePorRetiro('Todavía no se pudo confirmar el cierre. Reintentá en unos segundos.')
      }
    } finally {
      recuperandoCierreRef.current = false
      if (montadoRef.current) setRecuperandoCierre(false)
    }
  }

  // stage-pos-retiros-y-cierre-por-retiro (etapa 5): mientras cualquiera de los dos flujos de
  // arriba tiene un modal abierto (o una escritura propia en vuelo — ninguno de los dos limpia
  // `pasoRetiro`/`pasoCierre` hasta que la operación termina), la pantalla de venta entera queda
  // tan inerte como durante el propio checkout — se suma a `pantallaCobroInerte` más abajo.
  const cajaDeEscritorioOcupada = pasoRetiro !== null || pasoCierre !== null

  const ventaEnCurso =
    lineas.length > 0 || cobrando || confirmandoCobro || ventaFinalizada !== null || cajaDeEscritorioOcupada
  // Al desmontar solo sigue en curso lo que sobrevive a la navegación: un borrador con ítems. Los
  // estados transitorios (cobro, modales) mueren con esta pantalla.
  const borradorConItemsPersiste = claveBorrador !== null && almacenBorradores !== null && lineas.length > 0
  const alCambiarVentaEnCursoRef = useRef(alCambiarVentaEnCurso)
  const borradorConItemsPersisteRef = useRef(borradorConItemsPersiste)
  useEffect(() => {
    alCambiarVentaEnCursoRef.current = alCambiarVentaEnCurso
    borradorConItemsPersisteRef.current = borradorConItemsPersiste
  })
  useEffect(() => {
    alCambiarVentaEnCurso?.(ventaEnCurso)
  }, [ventaEnCurso, alCambiarVentaEnCurso])
  useEffect(() => () => alCambiarVentaEnCursoRef.current?.(borradorConItemsPersisteRef.current), [])

  // stage-pos-turno-y-foco: mientras no hay un turno CONFIRMADO abierto, la venta libre queda
  // bloqueada (solo búsqueda/consulta de precio) — `turno === null` cubre tanto "confirmado
  // cerrado" como "todavía no se sabe" (cargando o la consulta falló), fail-closed a propósito:
  // nunca se habilita vender sin una confirmación positiva. No aplica bajo `?idPresupuesto=` (esa
  // conversión tiene su propio gate de "Cobrar", el 409 turno_no_abierto la sigue cubriendo igual
  // como safety net). Tampoco aplica SIN punto de venta de sesión: ese caso ya tenía su propio
  // comportamiento preexistente (escanear sigue armando el carrito, "Cobrar" queda deshabilitado
  // por la precondición de punto de venta) — el turno ni siquiera se consulta sin un PV (efecto de
  // arriba), así que `turno` quedaría `null` para siempre y bloquearía sin necesidad. Declarado
  // ACÁ (antes del efecto de foco, que lo necesita en su guard) y no más abajo junto a
  // `precondicionesListas` — el orden importa, es una const de módulo del cuerpo del componente.
  const bloqueadoPorTurno = !modoPresupuesto && puntoVentaSeleccionada !== null && turno === null

  // judgment-day ronda 1 (T4): mensaje real para "Agregar" del buscador — `bloqueadoPorTurno`
  // cubre TANTO "todavía consultando" como "confirmado cerrado" (mismo comentario de arriba), pero
  // son avisos distintos para el cajero; acá se separan con `cargandoTurno`.
  const motivoSinAgregarEnBuscador = !bloqueadoPorTurno
    ? undefined
    : cargandoTurno
      ? 'Consultando turno…'
      : 'Caja cerrada: abrí la caja para vender.'

  // react-async-state regla 2/3/4: cada mutación del carrito (o un cambio de cliente/punto de
  // venta, de los que depende el lote) dispara una nueva resolución de precios; una respuesta
  // de una resolución anterior nunca puede pisar la de la más reciente (design: POS Screen
  // Composition, regla 2 — "generacionResolucionRef gates every /resolver response").
  //
  // La resolución depende solo de QUÉ líneas hay, de qué artículo y CUÁNTOS: fijar o quitar un
  // ajuste manual cambia `lineas` pero no esta clave, así que no vuelve a pegarle a
  // `/ofertas/resolver` ni enciende "Calculando…" (el ajuste se aplica en pantalla sobre los precios
  // ya resueltos). `idLinea` entra en la clave porque `precios` se indexa por línea: cualquier
  // cambio en la identidad de las líneas vuelve a resolver.
  const claveDeResolucion = lineas.map((l) => `${l.idLinea}:${l.idArticulo}:${l.cantidad}`).join('|')
  useEffect(() => {
    const generacion = (generacionResolucionRef.current += 1)

    // stage-17-presupuestos-y-remitos (Slice 7, design: Web composition — "skip the
    // price-resolution effect entirely"): el precio de una conversión sale congelado de
    // `items_presupuesto`, jamás de `ServicioDeOfertas.ResolverAsync` — este efecto no dispara
    // NINGÚN fetch bajo `?idPresupuesto=` (react-async-state regla 3: la generación igual se
    // bumpea arriba para huerfanar cualquier resolución en vuelo de una corrida anterior).
    if (modoPresupuesto) {
      setPrecios({})
      setCombosAplicados([])
      setAvisoPrecios('')
      setResolviendo(false)
      congelarInstantaneaDeLaPrevia(null)
      return
    }

    // Una edición de cantidad se debounce (el usuario suele seguir tipeando); un escaneo
    // dispara la resolución de inmediato, es una única mutación discreta. La bandera se
    // consume acá mismo, ANTES del guard de precondiciones, para que se resetee en cada
    // corrida del efecto sin importar si esta corrida llega a resolver o no — así una edición
    // hecha mientras cliente/punto de venta todavía cargan no queda pendiente y termina
    // heredada por una corrida posterior no relacionada.
    const demora = ultimaAccionEsEdicionRef.current ? 250 : 0
    ultimaAccionEsEdicionRef.current = false

    if (lineas.length === 0 || !clienteSeleccionado || !puntoVentaSeleccionada) {
      setPrecios({})
      setCombosAplicados([])
      setAvisoPrecios('')
      // Este efecto bumpea su propia generación en cada corrida (línea de arriba): eso huerfana
      // cualquier fetch en vuelo de una corrida anterior (su `finally` se salta porque la
      // generación ya no coincide). Sin este reset explícito, esta corrida temprana (ej. vaciar
      // el carrito, o que quede vacío tras cobrar) deja `resolviendo` en `true` para siempre.
      setResolviendo(false)
      congelarInstantaneaDeLaPrevia(null)
      return
    }

    // Venta local primero: si el cliente es el Consumidor Final o está en la instantánea de este
    // punto de venta, y esa instantánea tiene precio para TODAS las líneas en la lista del cliente,
    // la vista previa sale de ahí, sin red ni debounce. La instantánea queda congelada en
    // `instantaneaDeLaVistaPreviaRef`: es la que `cobrar()` usa para encolar, así se cobra
    // exactamente lo que el cajero vio. Un cliente que no está en la instantánea cobra por el
    // camino online, donde el servidor vuelve a resolver el precio: su vista previa también va
    // online, para que lo mostrado sea lo que se cobra.
    const instantaneaLocal = sincronizacionOffline.instantanea
    if (
      !modoPresupuesto &&
      instantaneaLocal !== null &&
      instantaneaLocal.idPuntoVenta === puntoVentaSeleccionada.id &&
      clienteAdmitidoOffline(clienteSeleccionado, instantaneaLocal) &&
      todasLasLineasTienenPrecioOffline(lineas, instantaneaLocal, clienteSeleccionado.idListaPrecio)
    ) {
      const previaDeLaInstantanea = resolverPreviaOffline(lineas, instantaneaLocal, clienteSeleccionado.idListaPrecio)
      setPrecios(previaDeLaInstantanea.precios)
      setCombosAplicados(previaDeLaInstantanea.combos)
      setAvisoPrecios('')
      setResolviendo(false)
      congelarInstantaneaDeLaPrevia(instantaneaLocal)
      return
    }

    let vigente = true
    setResolviendo(true)
    setAvisoPrecios('')

    const idTimeout = setTimeout(() => {
      clienteDeOfertas
        .resolverConCombos(aLineasDeResolucion(lineas, clienteSeleccionado.idListaPrecio, puntoVentaSeleccionada.idEmpresa))
        .then((resolucion) => {
          if (!vigente || generacionResolucionRef.current !== generacion) return
          setPrecios(indexarResolucionPorLinea(lineas, resolucion.lineas))
          setCombosAplicados(resolucion.combos)
          // Vista previa ONLINE: ninguna instantánea queda congelada, así que `cobrar()` va por
          // el camino online.
          congelarInstantaneaDeLaPrevia(null)
        })
        .catch((e) => {
          if (!vigente || generacionResolucionRef.current !== generacion) return

          // stage-pos-venta-offline-web (Parte B): la vista previa llegó acá por el camino online
          // (otro cliente, o alguna línea fuera de la instantánea) y no hubo servidor — se
          // resuelve contra la instantánea lo que se pueda. `idListaPrecio` es el que ya trae el
          // cliente elegido; el cobro local igual rechaza a un cliente que no esté en la
          // instantánea (`admisibilidadDeVentaOffline`).
          if (e instanceof ErrorDeRed && sincronizacionOffline.instantanea && clienteSeleccionado) {
            const previaSinRed = resolverPreviaOffline(lineas, sincronizacionOffline.instantanea, clienteSeleccionado.idListaPrecio)
            setPrecios(previaSinRed.precios)
            setCombosAplicados(previaSinRed.combos)
            setAvisoPrecios('')
            // judgment-day ronda 1 (WARNING): congela ACÁ la instantánea exacta que resolvió
            // esta vista previa — "Cobrar" (más abajo) y el ticket sintético tienen que usar
            // ESTA MISMA referencia, nunca una más fresca que `sincronizacionOffline.instantanea`
            // pueda tener para cuando el cajero haga click (el refresco en segundo plano de
            // `useSincronizacionOffline` no dispara este efecto a propósito, ver el comentario
            // de más abajo — así que sin este freeze, cobrar podía leer una instantánea distinta
            // de la que el cajero tiene en pantalla).
            congelarInstantaneaDeLaPrevia(sincronizacionOffline.instantanea)
            return
          }

          // Una resolución fallida invalida cualquier precio previo: dejar `precios` con datos
          // de una corrida anterior haría que el carrito quedara "parcialmente resuelto" (una
          // línea con precio viejo, otra en 0) en vez de entrar en modo vista previa fallida —
          // un subtotal a medias es peor que ninguno (judgment-day R3, falso negativo de Judge B).
          setPrecios({})
          setCombosAplicados([])
          setAvisoPrecios('No se pudo calcular la vista previa de precios. El total se confirma recién al cobrar.')
          congelarInstantaneaDeLaPrevia(null)
        })
        .finally(() => {
          if (!vigente || generacionResolucionRef.current !== generacion) return
          setResolviendo(false)
        })
    }, demora)

    return () => {
      vigente = false
      clearTimeout(idTimeout)
    }
    // `sincronizacionOffline.instantanea` NO es una dependencia a propósito: la vista previa local
    // y el fallback del catch la leen al MOMENTO en que corre esta resolución, nunca disparan una
    // corrida nueva por sí solos. Agregarla haría que cada refresco en segundo plano de la
    // instantánea cambiara los precios en pantalla (o volviera a pegarle a `/ofertas/resolver`)
    // sin que nada del carrito/cliente/punto de venta haya cambiado. judgment-day ronda 1
    // (WARNING): esto es EXACTAMENTE lo que `instantaneaDeLaVistaPreviaRef` explota — como el
    // refresco nunca reabre esta corrida, la instantánea que congela acá arriba es la misma que el
    // cajero sigue viendo en pantalla hasta el próximo cambio real de carrito/cliente/punto de
    // venta.
    //
    // `lineas` tampoco es dependencia: lo que cuenta es `claveDeResolucion` (ver arriba). El cuerpo
    // lee `lineas` del render en que cambió la clave, que tiene los mismos artículos y cantidades.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [claveDeResolucion, clienteSeleccionado, puntoVentaSeleccionada, reintentoPrecios, modoPresupuesto])

  /** Reintenta la vista previa de precios sin mutar el carrito (bumpea `reintentoPrecios` para
   * que el efecto de arriba vuelva a correr con las mismas líneas/cliente/punto de venta). */
  function reintentarPrecios() {
    if (cobrandoRef.current) return
    setReintentoPrecios((r) => r + 1)
  }

  // El Consumidor Final nunca puede pagar con cuenta corriente (spec: comprobantes-venta /
  // Cuenta Corriente Payment Gating) — si el cajero cambia de cliente a mitad de armar el pago
  // y ya había elegido un medio de cuenta corriente en alguna fila, esa fila queda sin medio en
  // vez de quedar en un estado que el servidor va a rechazar igual.
  useEffect(() => {
    if (!clienteSeleccionado?.esConsumidorFinal) return
    setFilasPago((prev) => {
      let cambio = false
      const siguiente = prev.map((f) => {
        if (f.idMedioPago === '') return f
        const medio = medioPorId[f.idMedioPago]
        if (medio?.comportamiento !== 'CuentaCorriente') return f
        cambio = true
        return { ...f, idMedioPago: '' as const }
      })
      return cambio ? siguiente : prev
    })
  }, [clienteSeleccionado, medioPorId])

  // stage-pos-borrador-persistente: una fila de pago restaurada de un borrador persistido puede
  // referenciar un medio de pago que ya no existe (dado de baja mientras la app estaba cerrada) —
  // se resetea a "sin elegir" en vez de dejar un id sin `<option>` que lo respalde en el `<select>`
  // (ver el render de "Pagos" más abajo). Gateado a `medios !== null` a propósito: `medioPorId`
  // arranca vacío mientras `medios` todavía carga, y sin este guard CUALQUIER fila con un medio
  // elegido (restaurada o no) se resetearía en ese instante inicial, antes de que el catálogo real
  // llegue a decidir si el id es válido.
  useEffect(() => {
    if (medios === null) return
    setFilasPago((prev) => filasConMedioInvalidoReseteado(prev, medioPorId))
  }, [medios, medioPorId])

  const mutarCarrito = useCallback((accion: AccionCarrito) => {
    if (cobrandoRef.current) return
    // El ajuste manual no cambia lo que se resuelve contra el servidor (ids y cantidades): no
    // arrastra ni consume la bandera del debounce de una edición de cantidad en curso.
    if (accion.tipo !== 'fijarAjusteManual' && accion.tipo !== 'quitarAjusteManual') {
      ultimaAccionEsEdicionRef.current = accion.tipo === 'editarCantidad'
    }
    setLineas((prev) => reducirCarrito(prev, accion))

    // El mapa de ediciones en curso es un override por fila: si la fila desaparece (quitar,
    // vaciar) o su cantidad se recalcula por fuera de la edición manual (un escaneo que suma
    // sobre la línea existente), el override queda desactualizado y debe limpiarse acá mismo
    // — no puede depender de que el blur del input dispare antes que la próxima mutación.
    const limpiarFila = (idLinea: string) =>
      setCantidadesEnEdicion((prev) => {
        if (!(idLinea in prev)) return prev
        const { [idLinea]: _omitido, ...resto } = prev
        return resto
      })

    const limpiarAjusteDeFila = (idLinea: string) =>
      setAjustesEnEdicion((prev) => {
        if (!(idLinea in prev)) return prev
        const { [idLinea]: _omitido, ...resto } = prev
        return resto
      })

    switch (accion.tipo) {
      case 'quitarLinea':
        limpiarFila(accion.idLinea)
        limpiarAjusteDeFila(accion.idLinea)
        break
      case 'escanear': {
        const destino = idLineaDestinoDeEscaneo(lineasRef.current, accion.linea)
        if (destino !== null) limpiarFila(destino)
        break
      }
      case 'vaciar':
        setCantidadesEnEdicion({})
        setAjustesEnEdicion({})
        break
      case 'editarCantidad':
      case 'fijarAjusteManual':
      case 'quitarAjusteManual':
        break
      default: {
        const _exhaustivo: never = accion
        void _exhaustivo
      }
    }
  }, [])

  /** Texto crudo del input de cantidad de una línea: mientras el usuario está editando (input
   * en `cantidadesEnEdicion`) se muestra tal cual se tipeó, incluso si todavía no es un número
   * completo (ej. "1." antes del dígito decimal) — spec: no perder el punto decimal a mitad de
   * tipeo. */
  function textoCantidad(l: LineaCarrito): string {
    return cantidadesEnEdicion[l.idLinea] ?? formatearNumeroDecimal(l.cantidad)
  }

  /** Marca el texto en edición que no se va a comprometer a la línea y también la cantidad ya
   * comprometida que no respeta la unidad del artículo (p.ej. una línea restaurada con 1,5 a la
   * que un reescaneo le completó la unidad "por unidad"). */
  function cantidadInvalida(l: LineaCarrito): boolean {
    const enEdicion = cantidadesEnEdicion[l.idLinea]
    const textoInvalido = enEdicion !== undefined && enEdicion.trim() !== '' && !esTextoDeCantidadValido(l.unidadVenta, enEdicion)
    return textoInvalido || !esCantidadValida(l.unidadVenta, l.cantidad)
  }

  function cambiarCantidad(idLinea: string, texto: string) {
    if (cobrandoRef.current) return
    setCantidadesEnEdicion((prev) => ({ ...prev, [idLinea]: texto }))
    const cantidad = parsearNumeroDecimal(texto)
    const lineaActual = lineas.find((l) => l.idLinea === idLinea)
    if (cantidad === null || !esTextoDeCantidadValido(lineaActual?.unidadVenta, texto)) return

    // Un estado intermedio del input (ej. "1," tipeando hacia "1,5") puede parsear al mismo
    // valor ya comprometido en la línea ("1," vale 1) — despachar en ese caso dispara
    // una resolución de precios redundante. Solo se despacha cuando el valor parseado difiere
    // de la cantidad comprometida.
    if (lineaActual && lineaActual.cantidad === cantidad) return

    mutarCarrito({ tipo: 'editarCantidad', idLinea, cantidad })
  }

  /** Suma al carrito lo que resolvió un escaneo. Una fracción de un artículo que se vende por
   * unidad no entra: se avisa donde se avisan los demás errores de escaneo. */
  function agregarEscaneado(articulo: ArticuloEscaneado): boolean {
    // Un artículo por importe sin el monto (se escaneó su código a secas) no tiene precio: se pide el importe.
    if (articulo.unidadVenta === 'Importe' && articulo.importe == null) {
      setErrorEscaneo(mensajeDeImporteRequerido(articulo.codigoInterno))
      return false
    }
    const { linea, cantidad } = aLineaDeCarritoDesdeEscaneo(articulo)
    if (fraccionaUnaUnidad(linea.unidadVenta, cantidad)) {
      setErrorEscaneo(MENSAJE_DE_CANTIDAD_ENTERA)
      return false
    }
    mutarCarrito({ tipo: 'escanear', linea, cantidad, idLinea: nuevoIdLinea() })
    return true
  }

  /**
   * Agrega las líneas de un combo por el mismo camino que un código escaneado (`agregarEscaneado`): `acumulaEnVenta`, las
   * reglas de cantidad y la resolución de precios se comportan igual que cargando los artículos de a uno, y es el carrito el
   * que después detecta el combo. Todo o nada: se comprueba todo antes de la primera línea, y las líneas se despachan juntas,
   * en el orden de los componentes.
   */
  function agregarLineasDeCombo(lineas: ArticuloEscaneado[]): boolean {
    if (cobrandoRef.current) {
      setErrorEscaneo(MENSAJE_DE_COMBO_CON_COBRO_EN_CURSO)
      return false
    }
    if (lineas.some((l) => fraccionaUnaUnidad(l.unidadVenta, l.cantidad))) {
      setErrorEscaneo(MENSAJE_DE_CANTIDAD_ENTERA)
      return false
    }
    for (const articulo of lineas) agregarEscaneado(articulo)
    return true
  }

  /** Entrega un combo ya leído (con sus candidatos) al carrito: lo agrega directo si no hay nada que elegir y, si una familia
   * tiene varios miembros, abre el selector. Devuelve `false` si no se pudo cargar, con el motivo en el aviso del escaneo. */
  function cargarCombo(combo: ComboParaCargar, desdeCodigo: boolean): boolean {
    const motivo = motivoDeComboNoCargable(combo)
    if (motivo !== null) {
      setErrorEscaneo(motivo)
      return false
    }
    if (componentesAElegir(combo).length > 0) {
      // El selector no se abre con un cobro en curso ni encima del buscador de combos.
      if (cobrandoRef.current) {
        setErrorEscaneo(MENSAJE_DE_COMBO_CON_COBRO_EN_CURSO)
        return false
      }
      setBuscadorDeCombosAbierto(false)
      setComboPorElegir({ combo, desdeCodigo })
      return true
    }
    const resolucion = resolverLineasDeCombo(combo)
    if (!resolucion.ok) {
      setErrorEscaneo(resolucion.motivo)
      return false
    }
    if (!agregarLineasDeCombo(resolucion.lineas)) return false
    if (desdeCodigo) setEntradaEscaneo('')
    return true
  }

  function cargarComboDesdeBuscador(combo: ComboParaCargar) {
    setBuscadorDeCombosAbierto(false)
    setErrorEscaneo('')
    cargarCombo(combo, false)
    focoPendienteRef.current = true
  }

  function cerrarBuscadorDeCombos() {
    setBuscadorDeCombosAbierto(false)
    focoPendienteRef.current = true
  }

  function confirmarMiembrosDeCombo(lineas: ArticuloEscaneado[]) {
    const pendiente = comboPorElegir
    setComboPorElegir(null)
    if (agregarLineasDeCombo(lineas) && pendiente?.desdeCodigo) setEntradaEscaneo('')
    focoPendienteRef.current = true
  }

  function cancelarMiembrosDeCombo() {
    setComboPorElegir(null)
    focoPendienteRef.current = true
  }

  /**
   * `C12` en el input de código: busca un combo activo con ese número en la misma fuente que el buscador. `'sin_combo'` ⇒ no
   * hay un combo con ese número y `'lectura_fallida'` ⇒ no se pudo averiguar: en los dos casos el llamador sigue con la
   * búsqueda de artículos con el texto original, porque el código interno de un artículo puede ser perfectamente `C12`.
   * Solo la búsqueda por código está en el `try`: un combo ENCONTRADO que no se puede leer ni cargar avisa el motivo y NO cae
   * a artículos, y `cargarCombo` corre fuera para que una excepción al agregar nunca desemboque en un escaneo de artículo.
   * Un detalle que responde 404 (el combo se dio de baja después de leerlo) es «no hay un combo con ese número».
   * Solo se llama para entradas con forma de código de combo, así que el escaneo común no paga nada.
   */
  async function intentarCargarComboPorCodigo(
    codigo: number,
    token: number,
    buscar: (codigo: number) => Promise<ComboDeLista | null>,
  ): Promise<'cargado' | 'rechazado' | 'sin_combo' | 'lectura_fallida' | 'obsoleto'> {
    let encontrado: ComboDeLista | null
    try {
      encontrado = await buscar(codigo)
    } catch {
      return tokenEscaneoRef.current === token ? 'lectura_fallida' : 'obsoleto'
    }
    if (tokenEscaneoRef.current !== token) return 'obsoleto'
    if (encontrado === null) return 'sin_combo'
    if (encontrado.motivoNoCargable !== null) {
      setErrorEscaneo(encontrado.motivoNoCargable)
      return 'rechazado'
    }

    let completo: ComboParaCargar
    try {
      completo = await fuenteDeCombos.cargar(encontrado.idCombo)
    } catch (e) {
      if (tokenEscaneoRef.current !== token) return 'obsoleto'
      if (e instanceof ErrorApi && e.estado === 404) return 'sin_combo'
      setErrorEscaneo(e instanceof ErrorApi ? e.message : `No se pudo leer el combo ${rotuloDeCodigoDeCombo(codigo)}.`)
      return 'rechazado'
    }
    if (tokenEscaneoRef.current !== token) return 'obsoleto'
    return cargarCombo(completo, true) ? 'cargado' : 'rechazado'
  }

  function confirmarCantidad(idLinea: string) {
    if (cobrandoRef.current) return
    setCantidadesEnEdicion((prev) => {
      const { [idLinea]: _omitido, ...resto } = prev
      return resto
    })
  }

  /** Texto del input "Ajuste %" de una línea: el borrador mientras se tipea y, si no, el porcentaje
   * comprometido con signo (`-10`, `12,5`) o vacío si la línea no tiene ajuste. */
  function textoDeAjuste(l: LineaCarrito): string {
    const borrador = ajustesEnEdicion[l.idLinea]
    if (borrador !== undefined) return borrador
    return l.ajusteManualPorcentaje == null ? '' : formatearPorcentajeDeAjusteConSigno(l.ajusteManualPorcentaje)
  }

  /** Motivo por el que el borrador de la línea no se puede comprometer; vacío si no hay borrador o es válido. */
  function mensajeDeAjusteInvalido(idLinea: string): string {
    const borrador = ajustesEnEdicion[idLinea]
    if (borrador === undefined) return ''
    const validacion = validarPorcentajeDeAjuste(borrador)
    return validacion.ok ? '' : validacion.mensaje
  }

  function cambiarAjuste(idLinea: string, texto: string) {
    if (cobrandoRef.current) return
    setAjustesEnEdicion((prev) => ({ ...prev, [idLinea]: texto }))
  }

  function descartarBorradorDeAjuste(idLinea: string) {
    setAjustesEnEdicion((prev) => {
      if (!(idLinea in prev)) return prev
      const { [idLinea]: _omitido, ...resto } = prev
      return resto
    })
  }

  /** Compromete el borrador con el carrito. Un texto inválido no muta nada y se conserva para que se
   * vea el error; vacío o cero quita el ajuste (solo si la línea tenía uno). */
  function confirmarAjuste(linea: LineaCarrito) {
    if (cobrandoRef.current) return
    const borrador = ajustesEnEdicion[linea.idLinea]
    if (borrador === undefined) return
    const validacion = validarPorcentajeDeAjuste(borrador)
    if (!validacion.ok) return
    descartarBorradorDeAjuste(linea.idLinea)
    const comprometido = linea.ajusteManualPorcentaje ?? null
    if (validacion.porcentaje === comprometido) return
    if (validacion.porcentaje === null) {
      mutarCarrito({ tipo: 'quitarAjusteManual', idLinea: linea.idLinea })
    } else {
      mutarCarrito({ tipo: 'fijarAjusteManual', idLinea: linea.idLinea, porcentaje: validacion.porcentaje })
    }
  }

  /** Enter confirma y devuelve el foco al input de código (lo que sigue es escanear); Escape descarta
   * el borrador. Sacar el foco del input dispara su blur, que es quien compromete el texto: con un
   * texto inválido Enter no mueve el foco. Si el input de código está inerte (escaneo en vuelo) el
   * foco simplemente se suelta. */
  function teclaDelAjuste(evento: React.KeyboardEvent<HTMLInputElement>, linea: LineaCarrito) {
    if (evento.key === 'Escape') {
      evento.preventDefault()
      evento.stopPropagation()
      descartarBorradorDeAjuste(linea.idLinea)
      return
    }
    if (evento.key !== 'Enter') return
    evento.preventDefault()
    if (cobrandoRef.current || mensajeDeAjusteInvalido(linea.idLinea) !== '') return
    const codigo = inputEscaneoRef.current
    if (codigo !== null && !codigo.disabled) codigo.focus()
    else evento.currentTarget.blur()
  }

  /** Agrega lo que la instantánea local resuelve para `entrada`. `noEncontrado` es que no la conoce y sigue la búsqueda en el
   * servidor; `rechazado` es que la conoce pero no se pudo agregar (el motivo ya quedó en el aviso del escaneo). */
  function agregarDesdeInstantanea(instantanea: InstantaneaDePos, entrada: string): 'agregado' | 'rechazado' | 'noEncontrado' {
    const resultado = resolverEscaneoOffline(instantanea, entrada)
    if (resultado.tipo === 'noEncontrado') return 'noEncontrado'
    if (resultado.tipo === 'importeInvalido') {
      setErrorEscaneo(MENSAJE_DE_IMPORTE_INVALIDO)
      return 'rechazado'
    }
    return agregarEscaneado(resultado.articulo) ? 'agregado' : 'rechazado'
  }

  /** F5 a F8 (o el botón de la tecla): agrega el artículo por importe de la tecla con el importe que hay en el campo de código,
   * con el mismo resultado que tipear `importe*código`. Sin datos, sin importe o con una tecla sin artículo avisa y no agrega. */
  function agregarPorAtajo(tecla: TeclaAtajo) {
    // Mismas compuertas que F2 y F4: nada se agrega detrás de un modal ni durante un cobro o una lectura en vuelo.
    if (modoPresupuesto || escaneando || cargaDeComboEnCursoRef.current || cobrandoRef.current || bloqueadoPorTurno) return
    if (buscadorAbierto || modalDeCombosAbierto || gateTurno || ventaFinalizada !== null || confirmandoCobro || descartandoVenta || cajaDeEscritorioOcupada) return

    const texto = entradaEscaneo.trim()
    if (!instantaneaDelPuntoVenta) {
      if (atajosEnLineaVigentes === null) {
        // Sin ninguna de las dos fuentes: si la lectura no está en vuelo (falló), esta tecla la vuelve a pedir.
        if (idPuntoVentaDeLosAtajosEnLinea !== null && !leyendoAtajosRef.current) leerAtajosEnLinea(idPuntoVentaDeLosAtajosEnLinea)
        setErrorEscaneo(MENSAJE_DE_ATAJO_SIN_DATOS)
        return
      }
      const atajo = atajosEnLineaVigentes.find((a) => a.teclaAtajo === tecla)
      if (!atajo) {
        setErrorEscaneo(mensajeDeAtajoSinArticulo(tecla))
        return
      }
      if (texto === '') {
        setErrorEscaneo(mensajeDeAtajoSinImporte(tecla))
        return
      }
      const enLinea = resolverAtajoEnLinea(atajo, texto)
      if (enLinea.tipo === 'importeInvalido') {
        setErrorEscaneo(MENSAJE_DE_IMPORTE_INVALIDO)
        return
      }
      if (enLinea.tipo === 'articulo' && agregarEscaneado(enLinea.articulo)) {
        setEntradaEscaneo('')
        setErrorEscaneo('')
      }
      focoPendienteRef.current = true
      return
    }
    const resultado = resolverAtajoOffline(instantaneaDelPuntoVenta, tecla, texto)
    if (resultado.tipo === 'sinArticulo') {
      setErrorEscaneo(mensajeDeAtajoSinArticulo(tecla))
      return
    }
    if (texto === '') {
      setErrorEscaneo(mensajeDeAtajoSinImporte(tecla))
      return
    }
    if (resultado.tipo === 'importeInvalido') {
      setErrorEscaneo(MENSAJE_DE_IMPORTE_INVALIDO)
      return
    }
    if (agregarEscaneado(resultado.articulo)) {
      setEntradaEscaneo('')
      setErrorEscaneo('')
    }
    focoPendienteRef.current = true
  }

  /** `codigoForzado` (stage-pos-modales-de-cobro): permite disparar el mismo camino de escaneo
   * con un código que nunca pasó por `entradaEscaneo` — el buffer de teclado del modal "Venta
   * finalizada" (ver `VentaFinalizada`). Sin argumento, se comporta exactamente como antes (lee
   * `entradaEscaneo`, el camino del input de código + Enter/"Agregar"). */
  async function escanear(codigoForzado?: string) {
    if (escaneando || cargaDeComboEnCursoRef.current || cobrandoRef.current || cajaDeEscritorioOcupada) return
    const entrada = (codigoForzado ?? entradaEscaneo).trim()
    if (!entrada) return

    // `C` + dígitos se prueba primero como combo; si no hay un combo con ese número sigue la búsqueda de artículos con el
    // texto original (D7). Solo las entradas con esa forma pasan por acá: el resto no paga ninguna lectura de más.
    const codigoDeCombo = modoPresupuesto ? null : parsearCodigoDeCombo(entrada)
    // Si la lectura de combos falló y el artículo tampoco aparece, el aviso tiene que decir también que no se pudo mirar combos.
    let combosSinLeer = false
    const conAvisoDeCombos = (mensaje: string) => (combosSinLeer ? `${mensaje} ${MENSAJE_DE_COMBOS_SIN_LEER}` : mensaje)
    if (codigoDeCombo !== null) {
      const tokenDeCombo = (tokenEscaneoRef.current += 1)
      cargaDeComboEnCursoRef.current = true
      setEscaneando(true)
      setErrorEscaneo('')
      let desenlace: Awaited<ReturnType<typeof intentarCargarComboPorCodigo>>
      try {
        const buscarSinRed = fuenteDeCombos.buscarSinRed
        const buscarEnLaApi = fuenteDeCombos.buscarEnLaApi
        if (buscarSinRed && buscarEnLaApi) {
          // Con instantánea y conexión la caja no espera a la red si la copia local puede responder: primero los combos de la
          // instantánea, después sus artículos, y solo si no conoce ninguno de los dos se le pregunta a la API.
          desenlace = await intentarCargarComboPorCodigo(codigoDeCombo, tokenDeCombo, buscarSinRed)
          if (desenlace === 'sin_combo' && !(instantaneaDelPuntoVenta && resolverEscaneoOffline(instantaneaDelPuntoVenta, entrada).tipo !== 'noEncontrado')) {
            desenlace = await intentarCargarComboPorCodigo(codigoDeCombo, tokenDeCombo, buscarEnLaApi)
          }
        } else {
          desenlace = await intentarCargarComboPorCodigo(codigoDeCombo, tokenDeCombo, fuenteDeCombos.buscarPorCodigo)
        }
      } catch {
        // Una excepción al cargar un combo encontrado termina acá: nunca sigue con el escaneo de artículos.
        if (tokenEscaneoRef.current === tokenDeCombo) {
          setEscaneando(false)
          setErrorEscaneo(`No se pudo cargar el combo ${rotuloDeCodigoDeCombo(codigoDeCombo)}.`)
          focoPendienteRef.current = true
        }
        return
      } finally {
        cargaDeComboEnCursoRef.current = false
      }
      if (desenlace === 'obsoleto') return
      setEscaneando(false)
      if (desenlace !== 'sin_combo' && desenlace !== 'lectura_fallida') {
        focoPendienteRef.current = true
        return
      }
      combosSinLeer = desenlace === 'lectura_fallida'
    }

    // Venta local primero: un código que está en la instantánea se agrega sin tocar la red. Solo
    // un código que no está (un artículo nuevo que todavía no sincronizó) va a buscarse online.
    if (instantaneaDelPuntoVenta) {
      const desenlace = agregarDesdeInstantanea(instantaneaDelPuntoVenta, entrada)
      if (desenlace !== 'noEncontrado') {
        if (desenlace === 'agregado') {
          setEntradaEscaneo('')
          setErrorEscaneo('')
        }
        focoPendienteRef.current = true
        return
      }
    }

    const token = (tokenEscaneoRef.current += 1)
    setEscaneando(true)
    setErrorEscaneo('')
    try {
      const articulo = await escanearEnElServidor(entrada)
      if (tokenEscaneoRef.current !== token) return
      if (agregarEscaneado(articulo)) setEntradaEscaneo('')
      focoPendienteRef.current = true
    } catch (e) {
      if (tokenEscaneoRef.current !== token) return

      // stage-pos-venta-offline-web (Parte B): solo cuando `fetch` mismo no encontró servidor
      // (`ErrorDeRed`, nunca un `ErrorApi` — un 404 real del servidor sigue siendo "no
      // encontrado"). Para el Consumidor Final la instantánea ya se buscó antes de ir a la red;
      // para otro cliente este es su único fallback. Mismo mapeo `ArticuloEscaneado` que la
      // respuesta online, así `aLineaDeCarritoDesdeEscaneo` no necesita saber de dónde vino.
      if (e instanceof ErrorDeRed && sincronizacionOffline.instantanea) {
        const desenlace = agregarDesdeInstantanea(sincronizacionOffline.instantanea, entrada)
        if (desenlace === 'agregado') setEntradaEscaneo('')
        if (desenlace !== 'noEncontrado') {
          focoPendienteRef.current = true
          return
        }
        setErrorEscaneo(conAvisoDeCombos('Sin conexión: no se encontró ese código en la última instantánea local.'))
        return
      }

      // El importe que tipeó el cajero no es válido: el texto del servidor habla de punto decimal y acá la coma también vale.
      if (e instanceof ErrorApi && e.codigo === 'importe_invalido') {
        setErrorEscaneo(MENSAJE_DE_IMPORTE_INVALIDO)
        return
      }

      setErrorEscaneo(conAvisoDeCombos(e instanceof ErrorApi ? e.message : 'No se pudo resolver el código escaneado.'))
    } finally {
      if (tokenEscaneoRef.current === token) setEscaneando(false)
    }
  }

  /** Consulta al servidor los clientes que la copia local no tiene (un cliente dado de alta después de la
   * última sincronización); para un mismo cliente el selector toma el registro del servidor. */
  async function buscarClientesEnElServidor(termino: string): Promise<OpcionDeCliente[]> {
    try {
      const pagina = await clienteDeClientes.listar(termino, false)
      return pagina.items.map(aOpcionDeCliente)
    } catch (e) {
      throw e instanceof ErrorApi ? e : new Error('No se pudieron buscar clientes.')
    }
  }

  /** "Agregar" de una fila del buscador — mismo camino que un código escaneado (`AccionCarrito`
   * tipo `escanear`): la resolución de precio/ofertas la sigue haciendo el efecto de `lineas` de
   * siempre, nunca este handler. */
  function agregarDesdeBusqueda(linea: ArticuloParaCarrito, cantidad: number) {
    // Un artículo por importe no tiene precio sin el monto, y el buscador no lo pide: es la misma regla del escaneo a secas.
    if (linea.unidadVenta === 'Importe' && linea.importe === undefined) {
      setErrorEscaneo(mensajeDeImporteRequerido(linea.codigoInterno))
      setBuscadorAbierto(false)
      focoPendienteRef.current = true
      return
    }
    mutarCarrito({ tipo: 'escanear', linea, cantidad, idLinea: nuevoIdLinea() })
    setBuscadorAbierto(false)
    focoPendienteRef.current = true
  }

  function cerrarBuscador() {
    setBuscadorAbierto(false)
    focoPendienteRef.current = true
  }

  function cambiarCliente(elegido: OpcionDeCliente) {
    if (cobrandoRef.current) return
    // Elegir al cliente que ya está elegido no pisa sus datos (que pueden ser más frescos que los de la lista).
    if (elegido.cliente.id !== clienteSeleccionado?.id) {
      setClienteSeleccionado(elegido.cliente)
      // El saldo y el límite de la lista pueden ser viejos (copia local, o una lista cargada hace rato) y
      // alimentan la validación del crédito: con señal se trae el registro del servidor en segundo plano.
      if (sincronizacionOffline.enLinea) refrescarClienteSeleccionado(elegido.cliente.id)
    }
    pedirFocoEnElCodigo()
  }

  function agregarFilaPago() {
    if (cobrandoRef.current) return
    const id = proximaFilaPagoIdRef.current++
    setFilasPago((prev) => [...prev, filaPagoVacia(id)])
  }

  function quitarFilaPago(id: number) {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.filter((f) => f.id !== id))
  }

  function cambiarMedioDeFila(id: number, idMedioPago: number | '') {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, idMedioPago } : f)))
  }

  function cambiarImporteDeFila(id: number, importe: number | null) {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, importe } : f)))
  }

  function cambiarReferenciaDeFila(id: number, referencia: string) {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, referencia } : f)))
  }

  /**
   * Cierra el modal "Venta finalizada" (clic en "Aceptar" o F9) — stage-pos-modales-de-cobro,
   * reemplaza a la vieja `nuevaVenta()`. El resto del estado de la venta anterior (carrito,
   * filas de pago, overrides de edición de cantidad, precios/aviso, error de escaneo) ya quedó
   * limpio desde el propio éxito de `cobrar()`, así que acá solo falta cerrar el modal.
   *
   * stage-17-presupuestos-y-remitos (Slice 7): bajo `?idPresupuesto=` el presupuesto que gobernó
   * esta venta ya quedó `convertido` — cerrar navega a la ruta libre en vez de limpiar el estado
   * local (react-async-state regla 8: el `key` de `Pos()` es el propio `idPresupuesto`, un reset
   * local acá dejaría el modo presupuesto pegado a una conversión ya consumida).
   */
  function cerrarVentaFinalizada() {
    if (modoPresupuesto) {
      navigate('/pos', { replace: true })
      return
    }
    setVentaFinalizada(null)
  }

  // F5 a F8 son los atajos de los artículos por importe. El comportamiento del navegador se evita SIEMPRE, mientras esta pantalla
  // esté montada y haya o no artículo, modal abierto o modificadores: F5 (también con Ctrl o Shift) recarga la página y con ella se
  // pierde el estado del POS, y F6/F7 mueven el foco o piden el modo cursor. Solo la tecla sola, sin repetición, agrega una línea.
  // El listener se registra una vez y llama a la versión del render actual por un ref, para no depender de las compuertas.
  const alAtajoDeImporteRef = useRef(agregarPorAtajo)
  useEffect(() => {
    alAtajoDeImporteRef.current = agregarPorAtajo
  })
  useEffect(() => {
    function alTeclado(evento: KeyboardEvent) {
      if (!esTeclaDeAtajo(evento.key)) return
      evento.preventDefault()
      if (evento.repeat || evento.altKey || evento.ctrlKey || evento.metaKey || evento.shiftKey) return
      alAtajoDeImporteRef.current(evento.key)
    }
    document.addEventListener('keydown', alTeclado)
    return () => document.removeEventListener('keydown', alTeclado)
  }, [])

  // F2 abre el buscador de artículos (spec pos-buscador-articulos) — solo con la venta libre
  // operable: nunca bajo `?idPresupuesto=` (carrito congelado, sin escaneo), nunca con el
  // checkout en vuelo, nunca con el gate de turno o el ticket ya emitido reemplazando la pantalla,
  // y nunca si el modal ya está abierto (no hay otro modal en esta pantalla que deba cerrarse
  // antes). Se re-suscribe con las dependencias en vez de leerlas por ref: son booleans que
  // cambian con poca frecuencia, el costo de resuscribir el listener es despreciable.
  useEffect(() => {
    function alTeclado(evento: KeyboardEvent) {
      if (evento.key !== 'F2') return
      if (modoPresupuesto || cobrando || buscadorAbierto || modalDeCombosAbierto || gateTurno || ventaFinalizada || confirmandoCobro || vaciandoCarrito || descartandoVenta) return
      // stage-pos-retiros-y-cierre-por-retiro (etapa 5): tampoco con un modal de "Retirar"/"Cerrar
      // caja" por retiro abierto.
      if (cajaDeEscritorioOcupada) return
      evento.preventDefault()
      setBuscadorAbierto(true)
    }
    document.addEventListener('keydown', alTeclado)
    return () => document.removeEventListener('keydown', alTeclado)
  }, [modoPresupuesto, cobrando, buscadorAbierto, modalDeCombosAbierto, gateTurno, ventaFinalizada, confirmandoCobro, vaciandoCarrito, descartandoVenta, cajaDeEscritorioOcupada])

  // F4 abre el buscador de combos, con las mismas compuertas que el botón (que se deshabilita con un escaneo en vuelo) y que
  // F2, y sin otro buscador abierto encima. F3 sigue siendo la "Dirección de entrega" del legacy (docs/10).
  useEffect(() => {
    function alTeclado(evento: KeyboardEvent) {
      // Con Alt, Ctrl o Meta la tecla es del sistema (Alt+F4 cierra la ventana): ni se evita ni abre el buscador.
      if (evento.key !== 'F4' || evento.altKey || evento.ctrlKey || evento.metaKey) return
      // Siempre se evita el comportamiento del navegador para esta tecla: dentro de la caja F4 es del POS.
      evento.preventDefault()
      if (modoPresupuesto || escaneando || cargaDeComboEnCursoRef.current || cobrando || buscadorAbierto || modalDeCombosAbierto) return
      if (gateTurno || ventaFinalizada || confirmandoCobro || vaciandoCarrito || descartandoVenta || cajaDeEscritorioOcupada) return
      setBuscadorDeCombosAbierto(true)
    }
    document.addEventListener('keydown', alTeclado)
    return () => document.removeEventListener('keydown', alTeclado)
  }, [modoPresupuesto, escaneando, cobrando, buscadorAbierto, modalDeCombosAbierto, gateTurno, ventaFinalizada, confirmandoCobro, vaciandoCarrito, descartandoVenta, cajaDeEscritorioOcupada])

  // Devuelve el foco al input de código recién cuando queda realmente habilitado (react-async-state
  // regla 9): un click en "Cobrar" mientras un escaneo o un agregado del buscador siguen en vuelo
  // no debe dejarlo enfocado ni operable durante el checkout. Gatillo EXPLÍCITO
  // (`focoPendienteRef`, seteado por `escanear()`/`agregarDesdeBusqueda()`/`cerrarBuscador()`):
  // siempre gana, nunca mira dónde está el foco actual — son reacciones directas a una acción del
  // cajero sobre este mismo input.
  //
  // `bloqueadoPorTurno` es parte del guard por la MISMA razón que `escaneando`/`cobrando`: el
  // input está `disabled` mientras es `true` (turno todavía cargando o confirmado cerrado) y un
  // elemento deshabilitado no puede recibir foco — sin este conjunct, un pedido explícito que
  // coincidiera con el turno todavía resolviendo se consumía contra un input ya deshabilitado (un
  // no-op silencioso, el pedido quedaba perdido para siempre). Con el conjunct acá, el efecto
  // vuelve a correr cuando `bloqueadoPorTurno` cambia y recién ahí consume el pedido.
  useEffect(() => {
    if (!focoPendienteRef.current) return
    if (escaneando || cobrando || buscadorAbierto || modalDeCombosAbierto || bloqueadoPorTurno || confirmandoCobro || ventaFinalizada || cajaDeEscritorioOcupada)
      return
    focoPendienteRef.current = false
    inputEscaneoRef.current?.focus()
  }, [pedidosDeFoco, escaneando, cobrando, buscadorAbierto, modalDeCombosAbierto, bloqueadoPorTurno, confirmandoCobro, ventaFinalizada, cajaDeEscritorioOcupada])

  /**
   * Foco NEUTRAL — mount inicial (reemplaza el `autoFocus` nativo, no confiable en un mount real:
   * ver abajo) y desbloqueo del turno (judgment-day ronda 1, T3, WARNING). A diferencia del efecto
   * de arriba, este NUNCA le saca el foco a un control que el cajero eligió a propósito mientras
   * el turno todavía cargaba (ej. clickear "Buscar artículo") — solo enfoca el input de código si
   * el foco actual es neutral (`body`/`null`) o ya es el propio input. No consume ningún "pedido":
   * se reevalúa en cada corrida relevante, siempre de forma idempotente (enfocar un elemento ya
   * enfocado no hace nada).
   *
   * Causa real del foco inicial roto, encontrada reproduciendo en un navegador real (no en un
   * test): el `autoFocus` nativo (HTML/React) no hace nada cuando el documento todavía no tiene
   * foco de ventana/SO en el momento del mount — confirmado a mano en Chrome real:
   * `document.hasFocus()` da `false` justo después de una navegación dura, y en ese estado un
   * `<input autoFocus>` NUNCA queda como `document.activeElement` (se queda en `body`
   * indefinidamente) — pero un `elemento.focus()` imperativo, llamado en el mismo momento, SÍ lo
   * enfoca igual. jsdom no reproduce esta restricción, así que un test en jsdom en verde nunca fue
   * evidencia de que el `autoFocus` nativo funcionara en la app real (react-async-state regla 12,
   * extendida de "restaurar foco" a "adquirir foco por primera vez").
   */
  useEffect(() => {
    if (escaneando || cobrando || buscadorAbierto || modalDeCombosAbierto || bloqueadoPorTurno || confirmandoCobro || ventaFinalizada || cajaDeEscritorioOcupada)
      return
    const activo = document.activeElement
    if (activo !== document.body && activo !== null && activo !== inputEscaneoRef.current) return
    inputEscaneoRef.current?.focus()
  }, [escaneando, cobrando, buscadorAbierto, modalDeCombosAbierto, bloqueadoPorTurno, confirmandoCobro, ventaFinalizada, cajaDeEscritorioOcupada])

  // `subtotalPrevia` es lo que hay que cobrar, ya con los ajustes manuales de cada línea; de ahí
  // salen `totalActual`, `validarPagosLocal` y el vuelto. `ajustesManuales` solo los separa para
  // mostrarlos en "Datos de la venta".
  /** Orden de pantalla: la línea agregada más recientemente va primera. El estado, el borrador, el pedido al
   * servidor y el ticket conservan el orden de agregado. */
  const lineasEnPantalla = [...lineas].reverse()

  const subtotalDeLineas = calcularSubtotalPrevia(lineas, precios)
  // La línea Envío entra al total como una línea más. Con conexión el importe es el leído del servidor; sin ella o si esa
  // lectura falló, el de la instantánea. Online el servidor lo vuelve a calcular al cobrar; en una venta local es el que se
  // manda y se cobra.
  const importeVigenteDelEnvio = importeDeEnvioVigente({
    hayConexion,
    importeEnLinea: importeEnvio,
    errorAlLeerEnLinea: errorImporteEnvio,
    instantanea: instantaneaDelPuntoVenta,
  })
  const importeDelEnvio = importeDeEnvioAplicado({ conEnvio, importe: importeVigenteDelEnvio, hayLineas: lineas.length > 0 })
  const subtotalPrevia = subtotalDeLineas === null ? null : subtotalDeLineas + importeDelEnvio
  // Por qué no se puede cerrar la venta con envío (cliente o importe); `null` sin envío o con todo en orden.
  const motivoDeEnvio = motivoDeEnvioPendiente({
    conEnvio,
    cliente: clienteSeleccionado,
    hayConexion,
    importeVigente: importeVigenteDelEnvio,
    errorAlLeerElImporte: errorImporteEnvio,
  })
  const ajustesManuales = calcularAjustesManualesPrevia(lineas, precios)
  // stage-17-presupuestos-y-remitos (Slice 7): bajo `?idPresupuesto=` el total nunca sale de la
  // resolución de precios (que ni siquiera corre) — sale del propio presupuesto congelado.
  const totalActual = modoPresupuesto ? (presupuesto?.total ?? 0) : (subtotalPrevia ?? 0)

  // Una vista previa fallida (`avisoPrecios` seteado, sin estar resolviendo) no cuenta como
  // precondición incumplida (decisión de diseño 3: el servidor es la autoridad final del total)
  // — solo la resolución en vuelo bloquea. `subtotalPrevia === null` sin aviso es el estado de
  // carga inicial (todavía no hay nada que mostrar), ese sí sigue bloqueando. Bajo
  // `?idPresupuesto=` esta noción no aplica — el total sale ya congelado, nunca de una
  // resolución que pudo fallar.
  const previaFallida = !modoPresupuesto && subtotalPrevia === null && avisoPrecios !== ''

  // Con vista previa fallida, el total no es confiable — calcular vuelto/falta contra un total
  // sintético de 0 sugeriría como vuelto el importe tendido completo (judgment-day R3, CRITICAL).
  // En su lugar, el total usado para la sugerencia de vuelto es la propia suma de los importes
  // cargados: el excedente contra ese total siempre da 0, así que el vuelto sugerido queda en 0.
  const totalParaVuelto = previaFallida ? sumarImportes(filasAPagosParaCalculo(filasPago, medioPorId)) : totalActual
  const pagosConVuelto = filasAPagosConVuelto(filasPago, medioPorId, totalParaVuelto)
  const faltante = previaFallida ? 0 : calcularFaltante(totalActual, pagosConVuelto)
  const excedente = previaFallida ? 0 : calcularExcedente(totalActual, pagosConVuelto)

  // stage-pos-atajos-cobro: "cubre el total" para F9 espeja EXACTO el rechazo por tolerancia de
  // `validarPagosLocal` (`sumaImportes + toleranciaPago < total`, acá negado) — nunca un `>=`
  // crudo sin tolerancia, para no exigirle al cajero un peso más de lo que el propio botón
  // "Cobrar" ya aceptaría. `faltante` ya vale `0` bajo vista previa fallida (arriba), así que ese
  // caso queda cubierto solo por ser `<=` cualquier tolerancia no negativa, sin caso especial.
  const toleranciaPago = parametros?.toleranciaPago ?? 0
  const pagosCubrenElTotal = faltante <= toleranciaPago

  const rechazoLocal =
    (!modoPresupuesto && subtotalPrevia === null) || !clienteSeleccionado || !parametros
      ? null
      : validarPagosLocal({
          total: totalActual,
          pagos: pagosConVuelto,
          toleranciaPago: parametros.toleranciaPago,
          esConsumidorFinal: clienteSeleccionado.esConsumidorFinal,
          saldoCliente: clienteSeleccionado.saldo,
          limiteCredito: clienteSeleccionado.limiteCredito,
          creditoIlimitado: clienteSeleccionado.creditoIlimitado,
          exigirLimiteDeCredito: !previaLocal,
        })

  // Aviso, nunca bloqueo: con la vista previa local el límite lo decide el servidor al cobrar (o,
  // sin respuesta, la venta se registra igual). Los datos de la instantánea solo anticipan lo que
  // probablemente conteste.
  const clienteEnInstantanea =
    previaLocal && clienteSeleccionado !== null && !clienteSeleccionado.esConsumidorFinal
      ? (instantaneaDelPuntoVenta?.clientes.find((c) => c.idCliente === clienteSeleccionado.id) ?? null)
      : null
  const avisoLimiteSegunInstantanea =
    clienteEnInstantanea !== null && instantaneaDelPuntoVenta !== null && superaLimiteDeCredito(clienteEnInstantanea, consumoCuentaCorriente(pagosConVuelto))
      ? `Según los datos de ${formatearVejezDeInstantanea(sincronizacionOffline.verificadaEn ?? instantaneaDelPuntoVenta.momento, new Date())}, este pago supera el límite de crédito del cliente. Se consulta al servidor al cobrar.`
      : ''

  // react-async-state regla 7: si medios de pago o parámetros no cargaron, "Cobrar" queda
  // efectivamente deshabilitado — no solo un aviso decorativo. Bajo `?idPresupuesto=` la
  // precondición de "hay líneas" es "el presupuesto cargó Y es `Convertible`" (la misma fuente de
  // verdad server-side que oculta el botón "Convertir en venta" en Presupuesto.tsx).
  const hayAjusteSinConfirmar = Object.keys(ajustesEnEdicion).length > 0
  const lineasPorImporteObsoletas = instantaneaDelPuntoVenta ? lineasConImporteDeArticuloOrdinario(lineas, instantaneaDelPuntoVenta) : []
  const precondicionesListas = modoPresupuesto
    ? presupuesto !== null &&
      presupuesto.convertible &&
      clienteSeleccionado !== null &&
      puntoVentaSeleccionada !== null &&
      medios !== null &&
      errorMedios === '' &&
      parametros !== null &&
      errorParametros === ''
    : !bloqueadoPorTurno &&
      !sincronizacionOffline.sincronizacionInicialPendiente &&
      // Un ajuste manual tipeado y sin confirmar todavía no está en el carrito: cobrar ahí cobraría
      // sin él. Como `puedeCobrar` y el F9 cuelgan de esta condición, el botón "Cobrar" y el atajo
      // quedan inertes hasta que se confirma o se descarta.
      !hayAjusteSinConfirmar &&
      lineasPorImporteObsoletas.length === 0 &&
      lineas.length > 0 &&
      lineas.every((l) => esCantidadValida(l.unidadVenta, l.cantidad)) &&
      motivoDeEnvio === null &&
      clienteSeleccionado !== null &&
      puntoVentaSeleccionada !== null &&
      medios !== null &&
      errorMedios === '' &&
      parametros !== null &&
      errorParametros === '' &&
      !resolviendo &&
      (subtotalPrevia !== null || previaFallida)

  // Con vista previa disponible (o bajo `?idPresupuesto=`, el total congelado), se exige la
  // validación local completa (`rechazoLocal`). Con vista previa fallida en el camino libre,
  // alcanza un chequeo mínimo de sanidad (al menos una fila de pago con importe > 0): el
  // servidor recalcula el total real y su rechazo se muestra igual de legible.
  const puedeCobrar = modoPresupuesto
    ? precondicionesListas && !cobrando && rechazoLocal === null
    : precondicionesListas && !cobrando && (subtotalPrevia !== null ? rechazoLocal === null : pagosConVuelto.length > 0)

  // El diálogo "¿Finalizar venta?" (F9) es una puerta de confirmación real (react-async-state
  // regla 13: "una compuerta de confirmación debe declarar el nivel en el que es inerte") —
  // mientras está abierta, el resto de los controles de venta libre quedan tan inertes como
  // durante el propio `cobrando`. Sin esto, el cajero podría editar el carrito o los pagos con el
  // diálogo abierto y terminar cobrando un total/vuelto distinto del que el diálogo mostró.
  //
  // stage-pos-modales-de-cobro: el modal "Venta finalizada" declara el mismo nivel de inercia —
  // sigue siendo la MISMA pantalla detrás (ya no una pantalla de resumen aparte que la
  // reemplazaba), así que sin este conjunct el cajero podría escanear o volver a cobrar con el
  // modal todavía abierto.
  //
  // stage-pos-retiros-y-cierre-por-retiro (etapa 5): mismo criterio para "Retirar"/"Cerrar caja"
  // por retiro — cualquiera de los dos con un modal abierto declara la pantalla entera inerte.
  const pantallaCobroInerte = cobrando || confirmandoCobro || ventaFinalizada !== null || cajaDeEscritorioOcupada
  const altaDeCliente = altaRapidaDeClienteDisponible({
    enLinea: sincronizacionOffline.enLinea,
    inerte: pantallaCobroInerte || bloqueadoPorTurno,
  })

  // "Cerrar caja" y "Retirar" son excluyentes y comparten UN solo derivado, para que los dos
  // hermanos no puedan volver a divergir: mientras `verificandoCierre` está en vuelo todavía no
  // hay ningún modal abierto, así que `pantallaCobroInerte` por sí solo dejaría "Retirar" vivo.
  const controlesDeCajaInertes = verificandoCierre || pantallaCobroInerte

  const enHeader = nodoRanuraHeader !== null

  // Lo que el header del shell necesita para ofrecer las acciones de caja y de sincronización. El
  // turno cuenta como resuelto cuando el servidor ya contestó o cuando rige el turno local, para
  // coincidir con lo que muestra el chip "Caja abierta"/"Caja cerrada".
  const turnoResuelto =
    !modoPresupuesto && puntoVentaSeleccionada !== null && (turnoConsultado || usarTurnoLocal) && !cargandoTurno && !errorTurno
  const puedeSincronizar = !modoPresupuesto && puntoVentaSeleccionada !== null && sincronizacionOffline.instantanea !== null
  const estadoDeAcciones: EstadoDeAccionesDePos = {
    listo: turnoResuelto,
    turnoAbierto: turnoResuelto ? turno !== null : null,
    imposible: modoPresupuesto || puntoVentaSeleccionada === null || errorTurno !== '',
    verificandoCierre,
    retiroOfrecido: cajaDeEscritorio !== undefined,
    habilitadas: {
      abrirTurno: turnoResuelto && turno === null,
      cerrarTurno: turnoResuelto && turno !== null && !controlesDeCajaInertes,
      retirar: turnoResuelto && turno !== null && cajaDeEscritorio !== undefined && !controlesDeCajaInertes,
      sincronizar: puedeSincronizar && !sincronizacionOffline.sincronizando,
    },
    puedeSincronizar,
    sincronizando: sincronizacionOffline.sincronizando,
  }
  useRegistroDeAccionesDePos(estadoDeAcciones, {
    abrirTurno: () => setGateTurno(true),
    cerrarTurno: () => void irACerrarCaja(),
    retirar: abrirRetiro,
    sincronizar: () => void sincronizacionOffline.sincronizarAhora(),
    enfocarEscaneo: () => {
      focoPendienteRef.current = true
      setPedidosDeFoco((n) => n + 1)
    },
  })

  /**
   * stage-pos-caja-en-cabecera: acción primaria de caja (badge + "Abrir caja"/"Cerrar caja",
   * antes en "Datos de la venta") — decisión del dueño: se mueve a una posición prominente,
   * portaleada al header de `ShellPos` cuando hay una ranura (`nodoRanuraHeader`, ver el contexto)
   * o renderizada en una franja propia arriba del carrito en la app web. Un único JSX para ambos
   * destinos: la lógica de turno hardened por varias rondas de judgment-day (`consultarTurno`,
   * `reintentarTurno`, `irACerrarCaja`, `gateTurno`) no se toca, solo cambia dónde se monta.
   * Mismas guardas que antes: nunca bajo `?idPresupuesto=`, "Verificando…" mientras
   * `verificandoCierre`, inerte durante `pantallaCobroInerte`.
   */
  const controlesTurno = (
    <>
      {!puntoVentaSeleccionada && (
        <span className="badge bg-warning text-dark">Sin puntos de venta disponibles</span>
      )}
      {!modoPresupuesto && puntoVentaSeleccionada && (
        <div className="d-flex align-items-center flex-wrap gap-2">
          {/* stage-pos-venta-offline-web (Parte C, "el cajero siempre tiene que ver cuántas
              ventas quedan sin sincronizar"): SIEMPRE visible, incluso en 0 — nunca solo cuando
              hay algo pendiente, así el cajero nunca tiene que preguntarse si el indicador
              existe. */}
          <span
            className={`badge text-nowrap ${sincronizacionOffline.outboxCount > 0 ? 'bg-warning text-dark' : 'bg-body-secondary text-body border'}`}
            title="Ventas registradas sin conexión que todavía no se sincronizaron con el servidor"
          >
            Sin sincronizar: {sincronizacionOffline.outboxCount}
          </span>
          {sincronizacionOffline.sincronizacionInicialPendiente && (
            <span className="text-muted small">Sincronizando con el servidor antes de cobrar…</span>
          )}
          {/* Controles de sincronización: solo en un dispositivo que ya tiene instantánea local
              (el POS de escritorio) — la app web no tiene nada que sincronizar. */}
          {sincronizacionOffline.instantanea && (
            <span className="d-flex align-items-center gap-2 small">
              {/* En el header del shell, "Sincronizar" vive en el menú Sistema. */}
              {!enHeader && (
                <button
                  type="button"
                  className="btn btn-outline-secondary btn-sm"
                  disabled={sincronizacionOffline.sincronizando}
                  onClick={() => void sincronizacionOffline.sincronizarAhora()}
                >
                  {sincronizacionOffline.sincronizando ? 'Sincronizando…' : 'Sincronizar ahora'}
                </button>
              )}
              <span className="text-muted text-nowrap">Precios de {formatearVejezDeInstantanea(sincronizacionOffline.verificadaEn ?? sincronizacionOffline.instantanea.momento, new Date())}</span>
            </span>
          )}
          {/* judgment-day ronda 1 (CRITICAL): reemplaza al viejo `errorDeDrenado` (un string único
              para el ítem más viejo, que además frenaba TODO el drenado) — ahora cada venta
              rechazada de forma permanente queda visible con su propio error real, sin bloquear
              el resto de la cola.
              judgment-day ronda 2 (WARNING): cada venta ahora trae una salida real — "Reintentar"
              (seguro, idempotente por número preasignado + contenido) y "Descartar" (con
              confirmación explícita que nombra la venta, nunca silenciosa) — antes esta lista no
              tenía ninguna acción y la copia prometía una resolución que no existía
              (`claims-match-code`). */}
          {sincronizacionOffline.ventasConError.length > 0 && (
            <div className="d-flex flex-column gap-1">
              <span className="badge bg-danger">{fraseVentasNecesitanAtencion(sincronizacionOffline.ventasConError.length)}</span>
              {sincronizacionOffline.ventasConError.map((venta) => {
                const enAccion = idLocalEnAccion === venta.idLocal
                return (
                  <span
                    key={venta.idLocal}
                    className="alert alert-danger py-1 px-2 small d-flex flex-wrap align-items-center gap-2 mb-0"
                    role="alert"
                  >
                    <span>{venta.mensaje}</span>
                    <button
                      type="button"
                      className="btn btn-sm btn-outline-danger"
                      disabled={enAccion}
                      onClick={() => onReintentarVentaConError(venta.idLocal)}
                    >
                      {enAccion ? 'Reintentando…' : 'Reintentar'}
                    </button>
                    <button
                      type="button"
                      className="btn btn-sm btn-outline-secondary"
                      disabled={enAccion}
                      onClick={(evento) => onPedirDescarteDeVenta(venta.idLocal, evento)}
                    >
                      Descartar
                    </button>
                  </span>
                )
              })}
            </div>
          )}
          {cargandoTurno ? (
            <span className="text-muted small">Consultando turno…</span>
          ) : errorTurno ? (
            <div className="alert alert-danger py-1 px-2 small d-flex justify-content-between align-items-center gap-2 mb-0">
              <span>{errorTurno}</span>
              <button
                type="button"
                className="btn btn-sm btn-outline-danger"
                disabled={cargandoTurno}
                onClick={reintentarTurno}
              >
                Reintentar
              </button>
            </div>
          ) : (
            <>
              {turno ? (
                <>
                  <span className="badge bg-success text-nowrap">Caja abierta</span>
                  {!enHeader && (
                    <button
                      type="button"
                      className="btn btn-danger btn-sm"
                      disabled={controlesDeCajaInertes}
                      onClick={() => void irACerrarCaja()}
                    >
                      {verificandoCierre ? 'Verificando…' : 'Cerrar caja'}
                    </button>
                  )}
                  {/* stage-pos-retiros-y-cierre-por-retiro (etapa 5): solo el POS de escritorio
                      (`cajaDeEscritorio` presente) — la app web no tiene cajón ni impresora. */}
                  {!enHeader && cajaDeEscritorio && (
                    <button
                      type="button"
                      className="btn btn-outline-warning btn-sm"
                      disabled={controlesDeCajaInertes}
                      onClick={abrirRetiro}
                    >
                      Retirar
                    </button>
                  )}
                </>
              ) : (
                <>
                  <span className="badge bg-secondary text-nowrap">Caja cerrada</span>
                  {!enHeader && (
                    <button type="button" className="btn btn-warning btn-sm" onClick={() => setGateTurno(true)}>
                      Abrir caja
                    </button>
                  )}
                </>
              )}
              {avisoCerrarCaja && <span className="alert alert-warning py-1 px-2 small mb-0">{avisoCerrarCaja}</span>}
              {avisoRetiroOk && <span className="alert alert-success py-1 px-2 small mb-0">{avisoRetiroOk}</span>}
            </>
          )}
        </div>
      )}
    </>
  )

  // Portalea `controlesTurno` al header de `ShellPos` cuando hay ranura, o los muestra en una
  // franja propia arriba del carrito en la app web (sin `ShellPos`, `nodoRanuraHeader` es `null`
  // por default del contexto). Una sola vez acá — se reutiliza en cada `return` de pantalla que
  // deba mostrarla (gate de turno y venta en curso). El cobro exitoso ya no reemplaza la pantalla
  // (stage-pos-modales-de-cobro: el modal "Venta finalizada" se renderiza DENTRO del mismo
  // `return` de la venta en curso), así que la franja sigue montada mientras ese modal está
  // abierto — inerte igual que el resto de la pantalla vía `pantallaCobroInerte`.
  const franjaDeCaja = nodoRanuraHeader ? (
    createPortal(controlesTurno, nodoRanuraHeader)
  ) : (
    <div className="container-fluid pt-3">
      <div className="row">
        <div className="col-12 d-flex align-items-center flex-wrap gap-2 mb-2">{controlesTurno}</div>
      </div>
    </div>
  )

  async function cobrar() {
    // react-async-state regla 9: guard de reentrancia de primera línea — un doble click en el
    // mismo tick le gana al re-render que deshabilita el botón.
    if (cobrandoRef.current) return
    if (!puedeCobrar || !clienteSeleccionado || !puntoVentaSeleccionada) return

    const miGeneracion = (generacionCobroRef.current += 1)
    cobrandoRef.current = true
    setCobrando(true)
    setErrorCobro('')
    setCobroAApartarAhora(null)
    const inicioDelCobro = Date.now()
    const forzarFactura = !modoPresupuesto && facturar
    // Se declara afuera para que el `catch` también sepa que hay un cobro aprobado que se conserva.
    let cobroDeMercadoPago: { idFila: number; idCobro: number; numero: number; importe: number; apartable: CobroAAPartar } | null = null

    try {
      // stage-17-presupuestos-y-remitos (Slice 7, design: Web composition — "post {
      // idPuntoVenta, codigoTipoComprobante: 'TX', idPresupuestoOrigen, lineas: undefined,
      // pagos }"): bajo `?idPresupuesto=` NUNCA se manda `lineas` ni `idCliente` — el precio y el
      // cliente salen congelados server-side del presupuesto, jamás de lo que esta pantalla
      // pudiera mostrar.
      // Cobro con QR de Mercado Pago: se resuelve ANTES de armar la venta. La pantalla sigue inerte (`cobrando`) mientras el
      // modal espera, y la venta se encola recién con el cobro aprobado y el número que ya se reservó para él.
      const planDeMercadoPago = planDeCobroMercadoPago(pagosConVuelto, medioPorId)
      if (planDeMercadoPago.tipo === 'varios') {
        setErrorCobro(MENSAJE_DE_VARIOS_COBROS)
        return
      }
      if (planDeMercadoPago.tipo === 'uno') {
        if (modoPresupuesto) {
          setErrorCobro('Un presupuesto no se puede cobrar con Mercado Pago desde esta pantalla: convertilo en venta y cobrala con otro medio.')
          return
        }
        const resultadoDelCobro = await cobrarConMercadoPago(
          {
            enLinea: sincronizacionOffline.enLinea,
            idPuntoVenta: puntoVentaSeleccionada.id,
            reserva: reservaConAviso,
            esperarCobro: modalDeCobroMercadoPago.esperarCobro,
            confirmarReuso: confirmacionDeReuso.confirmar,
          },
          { idMedioPago: planDeMercadoPago.pago.idMedioPago, importe: planDeMercadoPago.pago.importe },
        )
        if (generacionCobroRef.current !== miGeneracion) return
        if (resultadoDelCobro.tipo === 'detenido') {
          setErrorCobro(resultadoDelCobro.mensaje)
          setCobroAApartarAhora(resultadoDelCobro.apartable ?? null)
          return
        }
        cobroDeMercadoPago = {
          idFila: planDeMercadoPago.pago.idFila,
          idCobro: resultadoDelCobro.idCobro,
          numero: resultadoDelCobro.numero,
          importe: resultadoDelCobro.importe,
          apartable: cobroAAPartar(
            { idCobro: resultadoDelCobro.idCobro, importe: resultadoDelCobro.importe, importePagado: resultadoDelCobro.importe },
            resultadoDelCobro.numero,
            { idPuntoVenta: puntoVentaSeleccionada.id, dueno: resultadoDelCobro.dueno, idMedioPago: planDeMercadoPago.pago.idMedioPago },
          ),
        }
      }
      const pagosDeLaVenta = aPagosDeVenta(pagosConVuelto).map((pago, indice) =>
        cobroDeMercadoPago !== null && pagosConVuelto[indice].idFila === cobroDeMercadoPago.idFila
          ? { ...pago, referencia: null, idCobroMercadoPago: cobroDeMercadoPago.idCobro }
          : pago,
      )
      const seguroSeFactura = ventaSeguroSeFactura({ forzarFactura, pagos: pagosDeLaVenta, medioPorId })
      const solicitud =
        modoPresupuesto && idPresupuesto !== null
          ? aSolicitudDeVentaDesdePresupuesto(puntoVentaSeleccionada.id, idPresupuesto, aPagosDeVenta(pagosConVuelto))
          : aSolicitudDeVenta({
              idPuntoVenta: puntoVentaSeleccionada.id,
              idCliente: clienteSeleccionado.id,
              codigoTipoComprobante: 'TX',
              idComprobanteAsociado: null,
              lineas,
              // spec pos-buscador-articulos: sin selector de lote inline en el carrito (nunca
              // requerido para una venta TX, signo +1 — `ServicioDeVentas` solo exige `idLote`
              // explícito en una línea de devolución NCX, signo -1, que esta pantalla jamás emite).
              // Toda línea viaja sin lote elegido: el servidor resuelve FEFO solo (camino feliz de
              // cero tecleo, design decisión 19 de stage-12-lotes-vencimientos).
              lotesSeleccionados: {},
              pagos: pagosDeLaVenta,
              direccionEntrega: null,
              observaciones: null,
              forzarFactura,
              conEnvio,
            })

      // Solo una venta que seguro se factura y tiene conexión espera la respuesta fiscal; el resto imprime el ticket al instante.
      const esperarLaFactura = seguroSeFactura && sincronizacionOffline.enLinea

      /** Texto del aviso de una venta con cuenta corriente encolada sin respuesta del servidor
       * sobre el límite. Los datos de la instantánea solo agregan una pista, nunca bloquean. */
      const avisoDeLimiteNoValidado = (instantanea: InstantaneaDePos): string => {
        const base = 'No se pudo validar el límite de crédito con el servidor: la venta se registró igual.'
        const local = instantanea.clientes.find((c) => c.idCliente === clienteSeleccionado.id)
        if (!local || !superaLimiteDeCredito(local, consumoCuentaCorriente(pagosConVuelto))) return base
        const vejez = formatearVejezDeInstantanea(sincronizacionOffline.verificadaEn ?? instantanea.momento, new Date())
        return `${base} Según los datos de ${vejez}, el cliente supera su límite.`
      }

      /** Encola la venta en el outbox con los precios de `instantanea` y arma el comprobante
       * sintético con esa MISMA instantánea — el ticket tiene que coincidir con lo encolado, y
       * ambos con lo que el cajero vio. Con `encolada` o `sinComprobante` la venta ya quedó
       * guardada de forma durable: `encolarVentaOffline` solo devuelve `ok: true` después de
       * releer el outbox y confirmar que la venta está adentro. */
      const encolarLocal = async (
        instantanea: InstantaneaDePos,
      ): Promise<
        | { tipo: 'encolada'; idLocal: string; comprobante: ComprobanteEmitido; avisoLimiteDeCredito: string | null }
        | { tipo: 'sinComprobante'; idLocal: string; numeroVisible: string; avisoLimiteDeCredito: string | null }
        | { tipo: 'rechazada'; motivo: MotivoRechazoOffline }
      > => {
        // El importe que se muestra en el carrito es el que cobra la caja: viaja en la solicitud de la cola y el servidor lo
        // graba (solo una venta con número pre-asignado puede mandarlo).
        const resultado = await sincronizacionOffline.encolarVentaOffline({
          solicitudBase: conEnvio && importeVigenteDelEnvio !== null ? { ...solicitud, importeEnvio: importeVigenteDelEnvio } : solicitud,
          esConsumidorFinal: clienteSeleccionado.esConsumidorFinal,
          idListaPrecio: clienteSeleccionado.idListaPrecio,
          pagos: pagosConVuelto,
          instantaneaCongelada: instantanea,
          interesadoEnLaRespuesta: esperarLaFactura,
          ...(cobroDeMercadoPago !== null ? { numeroReservado: cobroDeMercadoPago.numero } : {}),
        })
        if (!resultado.ok) return { tipo: 'rechazada', motivo: resultado.motivo }
        const avisoLimiteDeCredito = resultado.limiteDeCreditoNoValidado ? avisoDeLimiteNoValidado(instantanea) : null
        const comprobante = construirComprobanteOfflineSintetico({
          numero: resultado.numero,
          numeroVisible: resultado.numeroVisible,
          idPuntoVenta: puntoVentaSeleccionada.id,
          idCliente: clienteSeleccionado.id,
          lineas,
          instantanea,
          idListaPrecio: clienteSeleccionado.idListaPrecio,
          pagos: aPagosDeVenta(pagosConVuelto),
          ahora: new Date(),
          ...(conEnvio && importeVigenteDelEnvio !== null
            ? { envio: { importe: importeVigenteDelEnvio, direccionEntrega: clienteSeleccionado.domicilio } }
            : {}),
        })
        // Defensa en profundidad: `ok: true` ya exigió que todas las líneas tengan precio en esta
        // misma instantánea, que es lo único que puede dejar al comprobante sin armar.
        if (!comprobante) return { tipo: 'sinComprobante', idLocal: resultado.idLocal, numeroVisible: resultado.numeroVisible, avisoLimiteDeCredito }
        return { tipo: 'encolada', idLocal: resultado.idLocal, comprobante, avisoLimiteDeCredito }
      }

      /** Un cobro aprobado nunca se pierde por una venta que no se cerró: se le dice al cajero cómo seguir. */
      const conGuiaDeCobroAprobado = (mensaje: string) => {
        if (cobroDeMercadoPago === null) return mensaje
        setCobroAApartarAhora(cobroDeMercadoPago.apartable)
        return `${mensaje} ${indicacionDeCobroAprobado(cobroDeMercadoPago.importe, (valor) => formatearMoneda(valor))}`
      }

      /** Comprobante a mostrar (del servidor o sintético) o `null` si ya se informó por qué no
       * hay venta (o la respuesta quedó vieja). */
      const emitirOEncolar = async (): Promise<{
        emitido: ComprobanteEmitido
        pendienteDeSincronizar: boolean
        avisoLimiteDeCredito: string | null
      } | null> => {
        // Una venta encolada (con o sin comprobante para mostrar) dispara el envío en segundo
        // plano — el cajero nunca lo espera.
        const resolverEncolado = async (local: Awaited<ReturnType<typeof encolarLocal>>) => {
          if (local.tipo === 'rechazada') {
            setErrorCobro(conGuiaDeCobroAprobado(mensajeDeRechazoOffline(local.motivo)))
            return null
          }
          void sincronizacionOffline.drenarAhora().catch(() => undefined)
          if (local.tipo === 'sinComprobante') {
            // Nadie va a esperar la respuesta: con tope 0 el hook borra el interés declarado al encolar.
            void sincronizacionOffline.esperarRespuesta(local.idLocal, 0)
            // react-async-state regla 6: la venta ya se guardó, nunca se reporta como un fallo.
            setErrorCobro(
              [
                `La venta ${local.numeroVisible} se guardó en este dispositivo, pero no se pudo armar el comprobante para mostrar/imprimir — va a aparecer cuando sincronice.`,
                local.avisoLimiteDeCredito,
              ]
                .filter(Boolean)
                .join(' '),
            )
            return null
          }
          if (!esperarLaFactura) return { emitido: local.comprobante, pendienteDeSincronizar: true, avisoLimiteDeCredito: local.avisoLimiteDeCredito }
          // Espera acotada de la respuesta de ESTA venta: sin ella (o sin factura aprobada) se imprime el ticket y la
          // factura queda para después. Nunca falla la venta, que ya está guardada.
          setFacturando(true)
          let respuesta: ComprobanteEmitido | null
          try {
            respuesta = await sincronizacionOffline.esperarRespuesta(local.idLocal, Math.max(0, ESPERA_DE_LA_FACTURA_MS - (Date.now() - inicioDelCobro)))
          } finally {
            setFacturando(false)
          }
          // Con la factura aprobada se imprime el comprobante del servidor entero (ítems, totales, pagos y fiscal); sin ella, el sintético.
          const delServidor = respuesta ? conFiscalSoloSiSeguroSeFactura(respuesta, seguroSeFactura) : null
          return {
            emitido: delServidor?.fiscal ? delServidor : { ...local.comprobante, fiscal: undefined },
            pendienteDeSincronizar: respuesta === null,
            avisoLimiteDeCredito: local.avisoLimiteDeCredito,
          }
        }

        // Venta local primero: si la vista previa salió de la instantánea (congelada en
        // `instantaneaDeLaVistaPreviaRef`), se intenta encolar sin esperar al servidor. El único
        // gate es `admisibilidadDeVentaOffline` (cliente de la instantánea, medios admitidos, todas
        // las líneas con precio, números reservados): si rechaza, sigue el camino online de
        // siempre. Salvo un rechazo que el servidor ya contestó (límite superado o consulta
        // rechazada): ese se informa y la venta no se manda online sin número.
        const instantaneaDeLaPrevia = instantaneaDeLaVistaPreviaRef.current
        if (instantaneaDeLaPrevia !== null) {
          const local = await encolarLocal(instantaneaDeLaPrevia)
          if (generacionCobroRef.current !== miGeneracion) return null
          // Sin conexión la copia local vieja (sin `proveedor`) no deja vender con medios electrónicos y el camino online tampoco existe.
          // Un cobro de Mercado Pago apartado solo frena a la venta local (tomaría su número): una venta online lleva el número del servidor.
          const sinSalidaOnline =
            local.tipo === 'rechazada' && (local.motivo === 'instantanea_sin_integracion' || local.motivo === 'cobro_en_curso') && !sincronizacionOffline.enLinea
          if (local.tipo !== 'rechazada' || rechazoOfflineEsDefinitivo(local.motivo) || sinSalidaOnline) return await resolverEncolado(local)
        }

        try {
          // Una venta cobrada con Mercado Pago lleva siempre el número que ya se reservó: el servidor lo liga a su cobro.
          const emitidoOnline = await clienteDeVentas.emitir(
            cobroDeMercadoPago !== null ? { ...solicitud, numeroPreasignado: cobroDeMercadoPago.numero } : solicitud,
          )
          // El bloque tiene que dejar atrás el número del cobro antes de seguir: una baja que se pierde dejaría a otra venta tomarlo.
          if (cobroDeMercadoPago !== null) {
            await sincronizacionOffline.consumirReservaDeCobro(cobroDeMercadoPago.numero, cobroDeMercadoPago.apartable.dueno).catch(() => false)
          }
          // Esta venta no pasó por el outbox, así que ninguna pasada de drenado va a pedir su facturación; solo se pide si la respuesta la dejó pendiente.
          sincronizacionOffline.procesarFacturaciones(emitidoOnline.fiscal)
          return { emitido: conFiscalSoloSiSeguroSeFactura(emitidoOnline, seguroSeFactura), pendienteDeSincronizar: false, avisoLimiteDeCredito: null }
        } catch (e) {
          if (generacionCobroRef.current !== miGeneracion) return null

          // Sin respuesta, el pedido online pudo haberse registrado igual (respuesta perdida):
          // encolarlo ahora con otro número pre-asignado duplicaría la venta. Se informa como
          // resultado incierto, igual que un 503 `resultado_incierto`.
          if (e instanceof ErrorDeRed) {
            // La venta pudo registrarse: el saldo de pantalla ya no es el del servidor.
            saldoConfirmadoRef.current = null
            setErrorCobro(MENSAJE_DE_COBRO_INCIERTO)
            return null
          }
          throw e
        }
      }

      // La lectura de la cola se pide antes de emitir: la cola atiende en orden, así que no incluye la venta que
      // se está encolando ahora (ni se la cuenta dos veces si es online).
      const marcaDeLaCola = descensosDeLaColaRef.current
      const lecturaDeLaCola = clienteSeleccionado.esConsumidorFinal
        ? null
        : conTiempoLimite(() => sincronizacionOffline.consumoPendienteDeCuentaCorriente(clienteSeleccionado.id), ESPERA_DE_LA_COLA_PARA_EL_TICKET_MS).catch(() => null)
      const resultado = await emitirOEncolar()
      if (resultado === null) return
      const { emitido, pendienteDeSincronizar, avisoLimiteDeCredito } = resultado

      // Éxito compartido — servidor o comprobante sintético de la venta encolada: el modal "Venta
      // finalizada" solo necesita lo que muestra, número/total/medios/vuelto (nunca recalculado de
      // `pagosConVuelto` de nuevo acá).
      setVentaFinalizada({
        numeroVisible: emitido.numeroVisible,
        total: emitido.total,
        medios: emitido.pagos.map((pago) => ({
          nombre: medioPorId[pago.idMedioPago]?.nombre ?? `Medio #${pago.idMedioPago}`,
          importe: pago.importe,
        })),
        vuelto: sumarVueltos(emitido.pagos),
        itemsVencidos: emitido.items
          .filter((item) => item.loteVencido)
          .map((item) => ({ descripcion: item.descripcion, codigoLote: item.codigoLote })),
        pendienteDeSincronizar,
        avisoLimiteDeCredito,
      })
      // stage-desktop-pos: `puedeCobrar`/`precondicionesListas` ya exigieron `medios !== null`
      // para llegar hasta acá — el seam nunca dispara con la lista todavía sin cargar.
      // Confirmado solo si la lectura del servidor es la que está en pantalla y ninguna venta salió de la cola desde que se empezó a cobrar.
      const saldoConfirmadoAlCobrar = saldoConfirmadoRef.current === clienteSeleccionado && descensosDeLaColaRef.current === marcaDeLaCola
      saldoConfirmadoRef.current = null
      if (lecturaDeLaCola === null) {
        alEmitir?.(emitido, clienteSeleccionado, medios ?? [])
      } else {
        const clienteDeLaVenta = clienteSeleccionado
        void lecturaDeLaCola.then((consumoPendiente) =>
          alEmitir?.(emitido, clienteDeLaVenta, medios ?? [], { consumoPendiente, saldoConfirmado: saldoConfirmadoAlCobrar }),
        )
      }
      setLineas([])
      setPrecios({})
      setCombosAplicados([])
      setCantidadesEnEdicion({})
      setAjustesEnEdicion({})
      setFilasPago([filaPagoInicial(proximaFilaPagoIdRef.current++, medios)])
      setEntradaEscaneo('')
      setFacturar(false)
      setConEnvioElegido(null)
      // El modal ya no depende de un click en "Nueva venta" para limpiar lo que queda de la
      // venta anterior — el reset es parte del propio éxito de `cobrar()`.
      setErrorEscaneo('')
      // stage-pos-adjustments: en venta libre, el próximo ticket vuelve a arrancar en Consumidor
      // Final — nunca bajo `?idPresupuesto=` (esa conversión mantiene el cliente que trae el
      // presupuesto). Si Consumidor Final no llegó a cargar, se deja el cliente como está: una
      // falla de carga no puede vaciar una selección que sí funciona.
      if (!modoPresupuesto && consumidorFinalRef.current) {
        setClienteSeleccionado(consumidorFinalRef.current)
      }
      // stage-pos-adjustments: una venta recién cobrada nunca resucita al volver de otra
      // pantalla — el borrador de ESTE ticket se descarta (el efecto de guardado de arriba va a
      // volver a escribir uno equivalente-a-vacío enseguida, pero un borrador vacío restaura
      // exactamente el mismo estado por defecto, así que da lo mismo).
      if (claveBorrador !== null) {
        almacenBorradores?.limpiar(claveBorrador)
      }
    } catch (e) {
      if (generacionCobroRef.current !== miGeneracion) return
      // Un cobro que falló sin respuesta definitiva (por ejemplo un 503 `resultado_incierto`) pudo registrarse igual.
      saldoConfirmadoRef.current = null
      // stage-6-turnos-caja (Slice 7): el gate seam reemplaza el panel entero, no un aviso más
      // — reintentar el checkout sin turno abierto solo repetiría el mismo 409.
      if (e instanceof ErrorApi && e.codigo === 'turno_no_abierto') {
        // judgment-day JD-E2-1 (CRITICAL): el 409 es la confirmación más autoritativa posible de
        // que el turno está cerrado — sin este reset, `turno` seguía en su último valor
        // confirmado ("abierto"), así que `controlesTurno`/`franjaDeCaja` (header/franja)
        // contradecía al propio gate que se abre acá abajo: "Caja abierta" y un "Cerrar caja"
        // habilitado a la vista mientras `PanelGateTurno` decía "No hay un turno abierto". Mismo
        // criterio que `turnoConfirmadoAbierto`: bumpear la generación invalida cualquier `GET
        // …/abierto` en vuelo desde antes (ej. un "Cerrar caja" concurrente) que pudiera pisar
        // este reset con un "abierto" stale.
        generacionTurnoRef.current += 1
        aplicarTurnoConfirmado(null)
        setGateTurno(true)
      } else {
        const mensaje =
          copiaDeArticuloNoVendible(e) ??
          (e instanceof ErrorApi
            ? (mensajeDeRechazoDeAjusteManual(e.codigo) ?? mensajeDeRechazoDeEnvio(e.codigo) ?? e.message)
            : 'No se pudo registrar la venta.')
        if (cobroDeMercadoPago !== null) setCobroAApartarAhora(cobroDeMercadoPago.apartable)
        setErrorCobro(
          cobroDeMercadoPago === null ? mensaje : `${mensaje} ${indicacionDeCobroAprobado(cobroDeMercadoPago.importe, (valor) => formatearMoneda(valor))}`,
        )
      }
    } finally {
      if (generacionCobroRef.current === miGeneracion) {
        cobrandoRef.current = false
        setCobrando(false)
        void sincronizarConLaReservaDeCobro()
      }
    }
  }

  /** La reserva del hook, avisando a la pantalla cada vez que la suelta o la consume para que el aviso de "retomar" no sobreviva. */
  const reservaConAviso = {
    ...sincronizacionOffline,
    soltarReservaDeCobro: async (numero: number, dueno?: string) => {
      const soltada = await sincronizacionOffline.soltarReservaDeCobro(numero, dueno)
      void sincronizarConLaReservaDeCobro()
      return soltada
    },
    consumirReservaDeCobro: async (numero: number, dueno?: string) => {
      const consumida = await sincronizacionOffline.consumirReservaDeCobro(numero, dueno)
      void sincronizarConLaReservaDeCobro()
      return consumida
    },
  }

  /** Relee la reserva del almacén: el bloqueo de otras ventas y el aviso de "cobro retomable" solo valen mientras su reserva siga
   * viva. Si la venta la consumió, se canceló o se resolvió por cualquier camino (incluido `cobrar()`), el aviso desaparece. */
  async function sincronizarConLaReservaDeCobro() {
    try {
      const vigente = await sincronizacionOffline.leerReservaDeCobro()
      setCobroDeRecarga((aviso) => (aviso !== null && (vigente === null || vigente.numero !== aviso.numero) ? null : aviso))
    } catch {
      // Sin poder leer el almacén todo se queda como estaba: afirmar que el cobro se resolvió sin verificarlo sería peor.
    }
  }

  /** F9 sin cobertura (spec pos-atajos-cobro, F9 rama a): foco a la primera fila de pago vacía, o
   * a la primera fila si todas tienen importe cargado pero la suma no alcanza — nunca abre el
   * diálogo. El input se ubica por `id` (armado con el mismo `fila.id` que ya identifica cada
   * fila unívocamente, ver `etiquetaDeCampoFila`) en vez de mantener un mapa de refs aparte. */
  function enfocarPrimeraFilaDePagoPendiente() {
    const filaVacia = filasPago.find((f) => f.importe === null)
    const idFila = (filaVacia ?? filasPago[0])?.id
    if (idFila === undefined) return
    document.getElementById(`pos-fila-pago-importe-${idFila}`)?.focus()
  }

  /**
   * judgment-day ronda 1 (K2, CRITICAL): `cobrandoRef` de `cobrar()` alcanza para que nunca se
   * dispare un SEGUNDO POST, pero no alcanza para decidir QUIÉN cierra el diálogo — un F9/click
   * repetido mientras el `cobrar()` real sigue en vuelo entra igual a esta función, su propio
   * `await cobrar()` resuelve CASI enseguida (el guard de `cobrar()` lo corta antes de la red) y
   * ese llamado redundante alcanzaba a correr `setConfirmandoCobro(false)` ANTES de que la venta
   * real terminara — el diálogo desaparecía de en medio del cobro. `finalizandoDesdeDialogoRef` es
   * la guarda DEDICADA a esa propiedad distinta: solo la invocación que de verdad es dueña del
   * `cobrar()` en curso puede cerrar el diálogo: cualquier invocación redundante (incluido un F9
   * mantenido apretado, `evento.repeat`) no hace nada.
   */
  const finalizandoDesdeDialogoRef = useRef(false)
  async function confirmarYcobrar() {
    if (finalizandoDesdeDialogoRef.current) return
    finalizandoDesdeDialogoRef.current = true
    try {
      await cobrar()
    } finally {
      finalizandoDesdeDialogoRef.current = false
    }
    setConfirmandoCobro(false)
  }

  /** Cierra la espera de un cobro retomado tras una recarga. No hay carrito al que cerrarle la venta: un cobro aprobado queda vivo y
   * la próxima venta cobrada con Mercado Pago por el mismo importe lo usa (`cobrarConMercadoPago`), o se aparta para liberar la caja.
   * `resolver` devuelve `null` si el servidor dice que la venta de ese cobro ya se registró: la reserva era un resto y se da de baja. */
  async function terminarCobroDeRecarga(
    reserva: ReservaDeCobro,
    miGeneracion: number,
    resolver: () => Promise<Parameters<typeof resolverResultadoDelCobro>[0] | null>,
  ) {
    cobrandoRef.current = true
    setCobrando(true)
    setErrorCobro('')
    setCobroAApartarAhora(null)
    try {
      const resultado = await resolver()
      if (generacionCobroRef.current !== miGeneracion) return
      if (resultado === null) {
        await sincronizacionOffline.consumirReservaDeCobro(reserva.numero, reserva.dueno)
        setCobroDeRecarga(null)
        return
      }
      const contexto = { idPuntoVenta: reserva.idPuntoVenta, dueno: reserva.dueno, idMedioPago: reserva.idMedioPago }
      const final = await resolverResultadoDelCobro(resultado, reserva.numero, sincronizacionOffline, contexto)
      setCobroDeRecarga(null)
      if (resultado.tipo !== 'sin_aprobar') setCobroAApartarAhora(cobroAAPartar(resultado.cobro, reserva.numero, contexto))
      setErrorCobro(
        final.tipo === 'aprobado'
          ? `El cobro de Mercado Pago de ${formatearMoneda(final.importe)} quedó aprobado. Cargá la venta y cobrala con Mercado Pago por ese importe: se usa este cobro. O apartalo para seguir vendiendo.`
          : final.mensaje,
      )
    } catch (e) {
      if (generacionCobroRef.current === miGeneracion) setErrorCobro(mensajeDeErrorDeCobroMercadoPago(e))
    } finally {
      if (generacionCobroRef.current === miGeneracion) {
        cobrandoRef.current = false
        setCobrando(false)
        void sincronizarConLaReservaDeCobro()
      }
    }
  }

  async function retomarCobroDeRecarga() {
    if (cobrandoRef.current || cobroDeRecarga === null || cobroDeRecarga.idCobro === undefined) return
    const reserva = cobroDeRecarga
    const idCobro = reserva.idCobro as number
    const miGeneracion = (generacionCobroRef.current += 1)
    await terminarCobroDeRecarga(reserva, miGeneracion, async () => {
      const actual = await clienteDeCobrosMercadoPago.consultar(idCobro)
      if (actual.usadoEnVenta) return null
      return modalDeCobroMercadoPago.esperarCobro(actual, construirNumeroVisible(reserva.idPuntoVenta, reserva.numero))
    })
  }

  /** Aparta un cobro aprobado sin venta en la lista durable del dispositivo y libera la caja: el número nunca se consumió, así que no
   * queda ningún hueco. Pide confirmación; la lista no se borra sola. */
  async function apartarElCobroAprobado(aparta: CobroAAPartar, miGeneracion: number): Promise<boolean> {
    const numeroVisible = construirNumeroVisible(aparta.cobro.idPuntoVenta, aparta.cobro.numero)
    const confirmado = await confirmacionDeReuso.confirmarApartado(
      { idCobro: aparta.cobro.idCobro, importe: aparta.cobro.importePagado ?? aparta.cobro.importe },
      numeroVisible,
    )
    if (!confirmado || generacionCobroRef.current !== miGeneracion) return false
    const apartado = await sincronizacionOffline.apartarCobroSinVenta({ numero: aparta.cobro.numero, dueno: aparta.dueno, cobro: aparta.cobro })
    if (generacionCobroRef.current !== miGeneracion) return false
    if (!apartado) {
      setErrorCobro('No se pudo apartar el cobro en este dispositivo. Probá de nuevo.')
      return false
    }
    setCobroAApartarAhora(null)
    setCobroDeRecarga(null)
    setCobrosSinVenta(await sincronizacionOffline.leerCobrosSinVenta().catch(() => []))
    setErrorCobro(
      `El cobro ${aparta.cobro.idCobro} por ${formatearMoneda(aparta.cobro.importePagado ?? aparta.cobro.importe)} quedó apartado y la caja puede vender. Un supervisor lo resuelve desde la conciliación de Mercado Pago.`,
    )
    return true
  }

  /** "Apartar cobro y liberar la caja" tras un mensaje de cobro aprobado sin venta. */
  async function apartarCobroAprobadoDetenido() {
    if (cobrandoRef.current || cobroAApartarAhora === null) return
    const aparta = cobroAApartarAhora
    const miGeneracion = (generacionCobroRef.current += 1)
    cobrandoRef.current = true
    setCobrando(true)
    try {
      await apartarElCobroAprobado(aparta, miGeneracion)
    } catch (e) {
      if (generacionCobroRef.current === miGeneracion) setErrorCobro(mensajeDeErrorDeCobroMercadoPago(e))
    } finally {
      if (generacionCobroRef.current === miGeneracion) {
        cobrandoRef.current = false
        setCobrando(false)
        void sincronizarConLaReservaDeCobro()
      }
    }
  }

  /** "Apartar cobro y liberar la caja" desde el aviso de recarga: consulta el cobro; solo uno aprobado se aparta (uno pendiente se
   * retoma o se cancela; uno ya cerrado o ya vendido simplemente libera la reserva). */
  async function apartarCobroDeRecarga() {
    if (cobrandoRef.current || cobroDeRecarga === null || cobroDeRecarga.idCobro === undefined) return
    const reserva = cobroDeRecarga
    const idCobro = reserva.idCobro as number
    const miGeneracion = (generacionCobroRef.current += 1)
    cobrandoRef.current = true
    setCobrando(true)
    setErrorCobro('')
    setCobroAApartarAhora(null)
    try {
      const actual = await clienteDeCobrosMercadoPago.consultar(idCobro)
      if (generacionCobroRef.current !== miGeneracion) return
      if (actual.usadoEnVenta) {
        await sincronizacionOffline.consumirReservaDeCobro(reserva.numero, reserva.dueno)
        setCobroDeRecarga(null)
        return
      }
      const vista = vistaDelCobro(actual)
      if (vista.tipo === 'sin_aprobar') {
        await sincronizacionOffline.soltarReservaDeCobro(reserva.numero, reserva.dueno)
        setCobroDeRecarga(null)
        setErrorCobro(TEXTO_POR_CIERRE_SIN_APROBAR[vista.estado])
        return
      }
      if (vista.tipo === 'aprobado' || vista.tipo === 'importe_distinto') {
        await apartarElCobroAprobado(
          cobroAAPartar(actual, reserva.numero, { idPuntoVenta: reserva.idPuntoVenta, dueno: reserva.dueno, idMedioPago: reserva.idMedioPago }),
          miGeneracion,
        )
        return
      }
      setErrorCobro('El cobro sigue pendiente: retomalo o cancelalo. Solo un cobro aprobado se aparta.')
    } catch (e) {
      if (generacionCobroRef.current === miGeneracion) setErrorCobro(mensajeDeErrorDeCobroMercadoPago(e))
    } finally {
      if (generacionCobroRef.current === miGeneracion) {
        cobrandoRef.current = false
        setCobrando(false)
        void sincronizarConLaReservaDeCobro()
      }
    }
  }

  async function cancelarCobroDeRecarga() {
    if (cobrandoRef.current || cobroDeRecarga === null || cobroDeRecarga.idCobro === undefined) return
    const reserva = cobroDeRecarga
    const idCobro = reserva.idCobro as number
    const miGeneracion = (generacionCobroRef.current += 1)
    await terminarCobroDeRecarga(reserva, miGeneracion, async () => {
      const cancelado = await clienteDeCobrosMercadoPago.cancelar(idCobro)
      return modalDeCobroMercadoPago.esperarCobro(cancelado, construirNumeroVisible(reserva.idPuntoVenta, reserva.numero))
    })
  }

  /** F10, Escape o clic en "Cancelar" del diálogo (spec pos-atajos-cobro, F9 rama b): nunca
   * cancela nada mientras el cobro sigue en vuelo (regla 13 — "un cancelar no debe suceder a
   * nada"). El foco se devuelve en el efecto de abajo, NUNCA acá mismo: mientras el diálogo está
   * abierto, `pantallaCobroInerte` deja el control original (`disparadorConfirmacionRef`)
   * `disabled` — un `.focus()` síncrono contra un control todavía deshabilitado (React recién va
   * a aplicar el cierre del diálogo) es un no-op silencioso. */
  function cancelarConfirmacionDeCobro() {
    if (cobrandoRef.current) return
    setConfirmandoCobro(false)
  }

  /** Devuelve el foco recién cuando el diálogo "¿Finalizar venta?" TERMINA de cerrarse (cancelado,
   * finalizado con éxito o con error) — nunca antes: en un `useEffect` (no en el propio handler de
   * cancelar/finalizar) porque necesita que React ya haya confirmado el commit que reactiva el
   * control original (`pantallaCobroInerte` deja de deshabilitarlo) o desmontado la pantalla
   * entera (venta emitida). `esAlcanzable` descarta un control que ya no está en el documento o
   * que sigue deshabilitado (ej. la venta emitida reemplazó toda la pantalla); en ese caso cae al
   * input de código, que tampoco puede recibir foco si a su vez ya no está montado (`?.focus()`
   * sobre `null` es un no-op). */
  const confirmandoCobroPrevioRef = useRef(false)
  useEffect(() => {
    if (confirmandoCobroPrevioRef.current && !confirmandoCobro) {
      const foco = disparadorConfirmacionRef.current
      disparadorConfirmacionRef.current = null
      const alcanzable = foco !== null && foco.isConnected && !foco.matches(':disabled') && foco !== document.body
      if (alcanzable) {
        foco.focus()
      } else {
        inputEscaneoRef.current?.focus()
      }
    }
    confirmandoCobroPrevioRef.current = confirmandoCobro
  }, [confirmandoCobro])

  /** Quitar o vaciar saca del documento el control que tenía el foco: lo pide para el input de
   * código por el mismo camino que el resto de las acciones del cajero (queda pendiente si el
   * input está momentáneamente deshabilitado). */
  function pedirFocoEnElCodigo() {
    focoPendienteRef.current = true
    setPedidosDeFoco((n) => n + 1)
  }

  function quitarLinea(idLinea: string) {
    mutarCarrito({ tipo: 'quitarLinea', idLinea })
    pedirFocoEnElCodigo()
  }

  const puedeVaciar = !modoPresupuesto && lineas.length > 0 && !escaneando && !pantallaCobroInerte && !bloqueadoPorTurno

  function pedirConfirmacionDeVaciado() {
    setVaciandoCarrito(true)
  }

  /** Recibo del pago a cuenta por la misma cola que el resto de los tickets. El pago ya está
   * registrado: si la impresión falla, lo informa el aviso con "Reimprimir" de la cola. */
  async function imprimirReciboDePago(pago: PagoACuentaRegistrado) {
    if (!cajaDeEscritorio) return
    const consumoPendiente = await conTiempoLimite(
      () => sincronizacionOffline.consumoPendienteDeCuentaCorriente(pago.cliente.id),
      ESPERA_DE_LA_COLA_PARA_EL_TICKET_MS,
    ).catch(() => null)
    const saldos = saldosDeUnPagoACuenta({
      pagos: pago.comprobante.pagos,
      saldoEnPantalla: pago.saldoEnPantalla,
      saldoPosterior: pago.saldoPosterior,
      consumoPendiente: consumoPendiente ?? 0,
    })
    const colaSinCambios = descensosDeLaColaRef.current === descensosAlPagarRef.current
    cajaDeEscritorio.encolarImpresion(
      'el recibo del pago a cuenta',
      ticketDePagoACuenta(
        {
          comprobante: pago.comprobante,
          cliente: { numero: pago.cliente.numero, nombre: nombreCompletoDeCliente(pago.cliente) },
          saldoAnterior: saldos.anterior,
          saldoNuevo: saldos.nuevo,
          estimado: esSaldoEstimado({ confirmado: pago.saldoPosterior !== null && colaSinCambios, consumoPendiente }),
        },
        cajaDeEscritorio.contexto,
        medios ?? [],
      ),
    )
  }

  function cancelarVaciado() {
    setVaciandoCarrito(false)
    pedirFocoEnElCodigo()
  }

  // Si el carrito deja de poder vaciarse con la confirmación abierta (turno cerrado, cobro en curso),
  // el diálogo no puede seguir prometiendo que lo vacía.
  useEffect(() => {
    if (vaciandoCarrito && !puedeVaciar) {
      setVaciandoCarrito(false)
      focoPendienteRef.current = true
      setPedidosDeFoco((n) => n + 1)
    }
  }, [vaciandoCarrito, puedeVaciar])

  function confirmarVaciado() {
    setVaciandoCarrito(false)
    setConEnvioElegido(null)
    mutarCarrito({ tipo: 'vaciar' })
    pedirFocoEnElCodigo()
  }

  /** Devuelve el foco al input de código cuando el modal "Venta finalizada" TERMINA de cerrarse
   * (clic en "Aceptar" o F9) — mismo criterio que `confirmandoCobroPrevioRef` de arriba, pero acá
   * siempre va al input de código (nunca hay un control disparador que restaurar: el modal
   * aparece solo del lado del servidor respondiendo, nunca de un control que el cajero eligió a
   * propósito). Bajo `?idPresupuesto=`, `cerrarVentaFinalizada` navega afuera de esta pantalla en
   * vez de volver `ventaFinalizada` a `null` — este efecto nunca llega a correr con esa
   * transición bajo ese modo (la pantalla ya se desmontó). */
  const ventaFinalizadaPrevioRef = useRef(false)
  useEffect(() => {
    if (ventaFinalizadaPrevioRef.current && !ventaFinalizada) {
      inputEscaneoRef.current?.focus()
    }
    ventaFinalizadaPrevioRef.current = ventaFinalizada !== null
  }, [ventaFinalizada])

  /**
   * judgment-day ronda 1 (K1, CRITICAL): el listener de F9 vivía en un efecto re-suscripto por
   * dependencias (`[confirmandoCobro, buscadorAbierto, ..., puedeCobrar]`) que NO incluía
   * `filasPago` — ni podía, honestamente: `filasPago` es un array que cambia de referencia en
   * cada tecleo de importe, listarlo ahí haría re-suscribir el listener en cada tecla, y encima
   * no alcanzaría (`cobrar()`, `pagosConVuelto`, `clienteSeleccionado`, etc. tienen el mismo
   * problema un nivel más abajo). Mientras el efecto no se re-suscribe, sigue closureando la
   * versión VIEJA de `enfocarPrimeraFilaDePagoPendiente`/`confirmarYcobrar`/etc. — agregar o
   * quitar una fila de pago sin que ESO ADEMÁS cambiara `pagosCubrenElTotal`/`puedeCobrar` dejaba
   * un F9 enfocando una fila que ya no existe, o cobrando con líneas/pagos de un render anterior.
   *
   * Arreglo (sin `eslint-disable`, patrón "latest ref"): el propio cuerpo del componente escribe
   * el snapshot más fresco acá en CADA render (vía el efecto sin dependencias de abajo); el
   * listener de teclado se suscribe UNA sola vez (`[]`) y lee siempre `f9Ref.current` — nunca
   * puede quedar viejo porque nunca "cierra" sobre nada reactivo. `useLayoutEffect`, no
   * `useEffect` (re-judgment, WARNING): corre SÍNCRONO dentro del commit, así que un keydown
   * nativo nunca puede intercalarse entre el commit y el flush (pasivo) del snapshot.
   */
  type SnapshotF9 = {
    confirmandoCobro: boolean
    ventaFinalizada: boolean
    buscadorAbierto: boolean
    gateTurno: boolean
    cajaDeEscritorioOcupada: boolean
    modoPresupuesto: boolean
    presupuestoCargado: boolean
    precondicionesListas: boolean
    pagosCubrenElTotal: boolean
    puedeCobrar: boolean
    vaciandoCarrito: boolean
    descartandoVenta: boolean
    puedeVaciar: boolean
    pedirConfirmacionDeVaciado: () => void
    cancelarVaciado: () => void
    confirmarYcobrar: () => void
    cancelarConfirmacionDeCobro: () => void
    enfocarPrimeraFilaDePagoPendiente: () => void
    cerrarVentaFinalizada: () => void
  }
  const f9Ref = useRef<SnapshotF9>(null)
  useLayoutEffect(() => {
    f9Ref.current = {
      confirmandoCobro,
      ventaFinalizada: ventaFinalizada !== null,
      buscadorAbierto: buscadorAbierto || modalDeCombosAbierto,
      gateTurno,
      cajaDeEscritorioOcupada,
      modoPresupuesto,
      presupuestoCargado: presupuesto !== null,
      precondicionesListas,
      pagosCubrenElTotal,
      puedeCobrar,
      vaciandoCarrito,
      descartandoVenta,
      puedeVaciar,
      pedirConfirmacionDeVaciado,
      cancelarVaciado,
      confirmarYcobrar,
      cancelarConfirmacionDeCobro,
      enfocarPrimeraFilaDePagoPendiente,
      cerrarVentaFinalizada,
    }
  })

  useEffect(() => {
    function alTeclado(evento: KeyboardEvent) {
      const m = f9Ref.current
      if (!m) return

      // F10 abre la barra de menú del navegador: en esta pantalla nunca debe llegar a hacerlo.
      if (evento.key === 'F10') evento.preventDefault()

      // stage-pos-modales-de-cobro: respaldo del cierre por F9 del modal "Venta finalizada" — el
      // dueño real es el propio `manejarTeclado` de `VentaFinalizada` (con su buffer de escaneo y
      // su propio `evento.stopPropagation()`, judgment-day ronda 0), que intercepta la tecla ANTES
      // de que llegue a burbujear hasta acá mientras el foco está adentro del modal. Esta rama
      // solo se alcanza si esa condición no se cumplió (ej. un test que dispara el evento
      // directo sobre `document`) — sin buffer que devolver, cierra sin más.
      if (m.ventaFinalizada) {
        if (evento.key !== 'F9') return
        if (evento.repeat) return
        evento.preventDefault()
        m.cerrarVentaFinalizada()
        return
      }

      if (m.confirmandoCobro) {
        if (evento.key === 'F9') {
          // K2: un F9 mantenido apretado dispara keydown repetidos (`repeat: true`) — ni siquiera
          // vale la pena invocar `confirmarYcobrar` (su propia guarda los cortaría igual).
          if (evento.repeat) {
            evento.preventDefault()
            return
          }
          evento.preventDefault()
          void m.confirmarYcobrar()
        } else if (evento.key === 'F10') {
          // WebView2/algunos navegadores: F10 sin `preventDefault` activa la barra de menú.
          evento.preventDefault()
          m.cancelarConfirmacionDeCobro()
        } else if (evento.key === 'Escape') {
          evento.preventDefault()
          m.cancelarConfirmacionDeCobro()
        }
        return
      }

      if (m.vaciandoCarrito) {
        if (evento.key === 'F10' && evento.repeat) return
        if (evento.key === 'F10') {
          evento.preventDefault()
          m.cancelarVaciado()
        } else if (evento.key === 'F9') {
          evento.preventDefault()
        }
        return
      }

      if (evento.key === 'F10') {
        if (evento.repeat) return
        if (!m.puedeVaciar || m.buscadorAbierto || m.gateTurno) return
        if (document.querySelector('[role="dialog"], [role="alertdialog"]')) return
        m.pedirConfirmacionDeVaciado()
        return
      }

      if (evento.key !== 'F9') return
      if (evento.repeat) return
      if (m.buscadorAbierto || m.gateTurno || m.cajaDeEscritorioOcupada || m.descartandoVenta) return
      if (m.modoPresupuesto && !m.presupuestoCargado) return
      if (cobrandoRef.current) return
      evento.preventDefault()

      // "Cobrar disabled por otro motivo" (turno cerrado, carrito vacío, cliente/punto de venta
      // sin elegir, medios/parámetros sin cargar): F9 no hace nada — nunca bypasea validaciones.
      if (!m.precondicionesListas) return

      if (!m.pagosCubrenElTotal) {
        m.enfocarPrimeraFilaDePagoPendiente()
        return
      }

      // Cubre el total pero `puedeCobrar` sigue en `false` por otra razón local (ej. falta la
      // referencia de un medio que la requiere, el vuelto no se justifica con billetes): tampoco
      // abre el diálogo — el propio botón "Cobrar" seguiría deshabilitado.
      if (!m.puedeCobrar) return

      disparadorConfirmacionRef.current = document.activeElement as HTMLElement | null
      setConfirmandoCobro(true)
    }

    document.addEventListener('keydown', alTeclado)
    return () => document.removeEventListener('keydown', alTeclado)
  }, [])

  // stage-17-presupuestos-y-remitos (Slice 7): bajo `?idPresupuesto=`, la pantalla entera espera
  // el presupuesto congelado antes de mostrar nada operable — mismo criterio de carga/error
  // bloqueante que `OrdenDeCompra.tsx`.
  if (modoPresupuesto && cargandoPresupuesto && presupuesto === null) {
    return (
      <div className="container-fluid py-4">
        <Cargando />
      </div>
    )
  }

  if (modoPresupuesto && errorPresupuesto && presupuesto === null) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Presupuesto" variante="danger">
          <p className="text-muted">{errorPresupuesto}</p>
          <Link className="btn btn-outline-secondary" to="/presupuestos">
            Volver a presupuestos
          </Link>
        </Box>
      </div>
    )
  }

  // stage-pos-caja-en-cabecera: el gate de turno también muestra `franjaDeCaja` (era uno de los
  // "early returns" que la dejaban afuera antes de esta etapa) — clickear "Abrir caja" acá arriba
  // mientras este panel ya está abierto es un no-op (`gateTurno` ya es `true`).
  if (gateTurno && puntoVentaSeleccionada) {
    return (
      <>
        {franjaDeCaja}
        <PanelGateTurno
          idPuntoVenta={puntoVentaSeleccionada.id}
          onAbierto={(turnoAbierto) => {
            setGateTurno(false)
            setErrorCobro('')
            turnoConfirmadoAbierto(turnoAbierto)
          }}
        />
      </>
    )
  }

  return (
    <>
      {franjaDeCaja}
      <div className="container-fluid py-4" key="venta-en-curso">
      {/* stage-17-presupuestos-y-remitos (Slice 7, design: Web composition — "Esta venta viene
          del presupuesto N° … (vence el …)"): el banner reemplaza cualquier duda sobre por qué
          el carrito está congelado, en vez de dejar que el operador lo descubra tocando algo
          deshabilitado. */}
      {modoPresupuesto && presupuesto && (
        <div className="row g-3 mb-3">
          <div className="col-12">
            <Box titulo="Venta desde presupuesto" variante="warning">
              <p className="mb-0">
                Esta venta viene del presupuesto N° {presupuesto.numero ?? presupuesto.idPresupuesto}
                {presupuesto.vencimiento &&
                  ` (vence el ${new Date(`${presupuesto.vencimiento}T00:00:00`).toLocaleDateString('es-AR')})`}
                . El carrito quedó congelado con el precio ofrecido — no se puede escanear, editar cantidades ni quitar
                líneas.
              </p>
            </Box>
          </div>
        </div>
      )}

      <div className="row g-3">
        <div className="col-lg-8">
          <Box
            etiqueta="Carrito"
            encabezado={
              <>
                <div className="flex-grow-1" style={{ minWidth: 0 }}>
                  <SelectorConBusqueda<OpcionDeCliente>
                    id="pos-cliente"
                    nombreAccesible="Cliente"
                    className="form-select-sm"
                    // Sin conexión se puede elegir cualquier cliente de la instantánea: la vista previa
                    // y el cobro local salen de su propia lista (`admisibilidadDeVentaOffline`).
                    opciones={opcionesDeCliente}
                    valor={clienteSeleccionado?.id ?? null}
                    disabled={pantallaCobroInerte || modoPresupuesto || bloqueadoPorTurno}
                    buscarLocal={buscarClientesLocales}
                    alBuscar={sincronizacionOffline.enLinea ? buscarClientesEnElServidor : undefined}
                    textoSinResultados={
                      !sincronizacionOffline.enLinea && instantaneaDelPuntoVenta
                        ? 'Sin conexión: no se encontró ningún cliente en la copia local.'
                        : 'Sin resultados.'
                    }
                    onChange={cambiarCliente}
                    alCancelar={pedirFocoEnElCodigo}
                  />
                </div>
                {!modoPresupuesto && (
                  <button
                    type="button"
                    className="btn btn-sm btn-outline-primary flex-shrink-0 text-nowrap"
                    title={altaDeCliente.motivo}
                    disabled={!altaDeCliente.habilitado}
                    onClick={() => setAltaDeClienteAbierta(true)}
                  >
                    Nuevo cliente
                  </button>
                )}
                {!modoPresupuesto && (
                  <BotonIcono
                    icono="detalle"
                    etiqueta="Ver detalle"
                    title="Ver detalle"
                    className="flex-shrink-0"
                    disabled={pantallaCobroInerte || bloqueadoPorTurno || !puntoVentaSeleccionada || medios === null}
                    onClick={() => setModalCuentaCorrienteAbierto(true)}
                  />
                )}
                {clienteSeleccionado && !clienteSeleccionado.esConsumidorFinal && <ChipDeSaldo cliente={clienteSeleccionado} />}
                {!modoPresupuesto && (
                  <div className="form-check mb-0 flex-shrink-0 text-nowrap">
                    <input
                      id="pos-con-envio"
                      type="checkbox"
                      className="form-check-input"
                      checked={conEnvio}
                      disabled={pantallaCobroInerte || bloqueadoPorTurno}
                      onChange={(e) => setConEnvioElegido(e.target.checked)}
                    />
                    <label className="form-check-label" htmlFor="pos-con-envio">
                      Con envío
                    </label>
                  </div>
                )}
              </>
            }
          >
            {/* stage-pos-turno-y-foco: aviso claro de por qué escaneo/carrito/cliente/pagos están
                deshabilitados — solo cuando el turno está CONFIRMADO cerrado (nunca mientras
                todavía se está consultando, ni si la consulta falló: `errorTurno` tiene su propio
                aviso en "Datos de la venta"). */}
            {bloqueadoPorTurno && !cargandoTurno && errorTurno === '' && (
              <div className="alert alert-warning py-1 px-2 small">Caja cerrada: abrí la caja para vender.</div>
            )}
            {/* Solo mientras la pantalla se apoya en la copia local por falta de red: la
                sincronización no alcanza al servidor, o alguna carga del arranque (clientes,
                medios, tolerancia, turno) falló sin red. La vejez es la de la última verificación
                de la instantánea contra el servidor. */}
            {!modoPresupuesto && instantaneaDelPuntoVenta && (!sincronizacionOffline.enLinea || datosLocalesPorFaltaDeRed) && (
              <div className="alert alert-secondary py-1 px-2 small">
                Sin conexión — datos de{' '}
                {formatearVejezDeInstantanea(sincronizacionOffline.verificadaEn ?? instantaneaDelPuntoVenta.momento, new Date())}. Se vende con la
                copia local de este dispositivo.
              </div>
            )}
            {!sincronizacionOffline.enLinea && !modoPresupuesto && (
              <div className="form-text mb-2">Sin conexión: se vende al Consumidor Final o a un cliente de la copia local.</div>
            )}
            {errorEscaneo && !modoPresupuesto && <div className="alert alert-danger py-1 px-2 small">{errorEscaneo}</div>}
            {avisoPrecios && !modoPresupuesto && (
              <div className="alert alert-warning py-1 px-2 small d-flex justify-content-between align-items-center gap-2">
                <span>{avisoPrecios}</span>
                <button
                  type="button"
                  className="btn btn-sm btn-outline-warning"
                  disabled={pantallaCobroInerte || resolviendo}
                  onClick={reintentarPrecios}
                >
                  Reintentar
                </button>
              </div>
            )}

            {!modoPresupuesto && (
              <div className={`input-group ${atajosDeImporte.length > 0 ? 'mb-2' : 'mb-3'}`}>
                <input
                  ref={inputEscaneoRef}
                  type="text"
                  className="form-control"
                  placeholder="Escanear o tipear un código (ej. 3*7790001234567)"
                  aria-label="Código escaneado"
                  value={entradaEscaneo}
                  disabled={escaneando || pantallaCobroInerte || bloqueadoPorTurno}
                  onChange={(e) => setEntradaEscaneo(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), escanear())}
                />
                <button
                  type="button"
                  className="btn btn-primary"
                  disabled={escaneando || pantallaCobroInerte || bloqueadoPorTurno}
                  onClick={() => void escanear()}
                >
                  {escaneando ? 'Buscando…' : 'Agregar'}
                </button>
                {/* "Buscar artículo" en vez de "Buscar" a secas: así el nombre dice qué se busca y no se
                    confunde con la búsqueda de clientes del selector de la cabecera. El `(F2)` es
                    `aria-hidden`: solo decoración visual, el nombre accesible sigue
                    siendo "Buscar artículo" a secas — `aria-keyshortcuts` es la forma correcta de
                    exponer el atajo a tecnología asistiva. */}
                <button
                  type="button"
                  className="btn btn-outline-primary"
                  disabled={escaneando || pantallaCobroInerte}
                  aria-keyshortcuts="F2"
                  onClick={() => setBuscadorAbierto(true)}
                >
                  Buscar artículo <sup aria-hidden="true">(F2)</sup>
                </button>
                {/* F4: F1, F2, F3 (dirección de entrega), F9, F10 y F12 son atajos del legacy a preservar (docs/05); F4 es la
                    primera tecla de función que no usa el legacy ni esta pantalla. */}
                <button
                  type="button"
                  className="btn btn-outline-primary"
                  disabled={escaneando || pantallaCobroInerte}
                  aria-keyshortcuts="F4"
                  onClick={() => setBuscadorDeCombosAbierto(true)}
                >
                  Buscar combo <sup aria-hidden="true">(F4)</sup>
                </button>
              </div>
            )}
            {/* Atajos de los artículos por importe: la tecla toma el importe tipeado en el campo de código (F5 a F8). */}
            {!modoPresupuesto && atajosDeImporte.length > 0 && (
              <div className="d-flex flex-wrap gap-2 mb-3">
                {atajosDeImporte.map((atajo) => (
                  <button
                    key={atajo.teclaAtajo}
                    type="button"
                    className="btn btn-outline-primary btn-sm"
                    disabled={escaneando || pantallaCobroInerte || bloqueadoPorTurno}
                    aria-keyshortcuts={atajo.teclaAtajo}
                    onClick={() => agregarPorAtajo(atajo.teclaAtajo)}
                  >
                    {atajo.nombre} <sup aria-hidden="true">({atajo.teclaAtajo})</sup>
                  </button>
                ))}
              </div>
            )}

            <div className="table-responsive">
              <table className="table table-striped table-hover table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Código</th>
                    <th>Artículo</th>
                    <th style={{ width: 110 }}>Cantidad</th>
                    <th className="text-end">Precio unit.</th>
                    <th className="text-end" style={{ width: 110 }}>
                      Ajuste %
                    </th>
                    <th className="text-end">Total</th>
                    <th className="text-end">Acciones</th>
                  </tr>
                </thead>
                <tbody>
                  {modoPresupuesto
                    ? (presupuesto?.items ?? []).map((item) => (
                        // stage-17-presupuestos-y-remitos (Slice 7): fila 100% de solo lectura,
                        // sourced del propio presupuesto congelado — sin `precios`/`previaDeLinea`
                        // (esos dependen de una resolución que este modo nunca dispara).
                        <tr key={item.orden}>
                          <td>—</td>
                          <td>{item.descripcion}</td>
                          <td>{item.cantidad}</td>
                          <td className="text-end">{formatearMoneda(item.precioUnitario)}</td>
                          <td className="text-end">—</td>
                          <td className="text-end">{formatearMoneda(item.total)}</td>
                          <td className="text-end">—</td>
                        </tr>
                      ))
                    : lineasEnPantalla.map((l) => {
                        const resultado = precios[l.idLinea]
                        const previa = previaDeLinea(l, resultado)
                        const tieneDescuento = previa.descuentoUnitario > 0 && resultado?.precioOriginal != null
                        const comboDeLinea = comboVigenteDeLinea(l.cantidad, resultado?.combo)
                        // La línea solo conoce el combo de menor id entre los que la consumieron, pero su ahorro suma
                        // todos: el nombre solo se muestra cuando el carrito aplicó UN combo (no puede ser otro).
                        const nombreDelCombo =
                          comboDeLinea && combosAplicados.length === 1 && combosAplicados[0].idCombo === comboDeLinea.idCombo
                            ? combosAplicados[0].nombre
                            : 'Combos'
                        const ajusteDeLinea = l.ajusteManualPorcentaje ?? null
                        const mensajeDeAjuste = mensajeDeAjusteInvalido(l.idLinea)
                        const lineaInerte = pantallaCobroInerte || bloqueadoPorTurno
                        const textoDelAjuste =
                          ajusteDeLinea === null
                            ? ''
                            : [
                                `${rotuloDeAjusteManual(ajusteDeLinea)} ${formatearPorcentajeDeAjuste(ajusteDeLinea)}%`,
                                previa.total === null ? '' : formatearImporteConSigno(previa.ajusteManual, { simbolo: true }),
                              ]
                                .filter(Boolean)
                                .join(' ')
                        return (
                          <tr key={l.idLinea}>
                            <td>{l.codigoBarra ?? l.codigoInterno}</td>
                            <td>
                              {l.nombre}
                              {comboDeLinea && (
                                <div>
                                  <span className="badge text-bg-success" title="Combo aplicado a esta línea">
                                    {[nombreDelCombo, formatearImporteConSigno(-comboDeLinea.ahorro, { simbolo: true })].join(' ')}
                                  </span>
                                </div>
                              )}
                              {ajusteDeLinea !== null && (
                                <div>
                                  <span className={`badge ${ajusteDeLinea < 0 ? 'text-bg-warning' : 'text-bg-info'}`}>{textoDelAjuste}</span>
                                </div>
                              )}
                            </td>
                            <td>
                              <input
                                {...atributosDeCampoDeCantidad(restriccionDeCantidad(l.unidadVenta))}
                                className={`form-control form-control-sm${cantidadInvalida(l) ? ' is-invalid' : ''}`}
                                aria-label={`Cantidad de ${l.nombre}`}
                                aria-invalid={cantidadInvalida(l) || undefined}
                                title={
                                  l.importe !== undefined
                                    ? MENSAJE_DE_CANTIDAD_DE_IMPORTE
                                    : esFraccionDeArticuloPorUnidad(l.unidadVenta, textoCantidad(l))
                                      ? MENSAJE_DE_CANTIDAD_ENTERA
                                      : undefined
                                }
                                value={textoCantidad(l)}
                                disabled={pantallaCobroInerte || bloqueadoPorTurno || l.importe !== undefined}
                                onChange={(e) => cambiarCantidad(l.idLinea, e.target.value)}
                                onBlur={() => confirmarCantidad(l.idLinea)}
                              />
                            </td>
                            <td className="text-end">
                              {previa.precioUnitario === null ? (
                                '—'
                              ) : (
                                <PrecioUnitarioDeLinea
                                  cantidad={l.cantidad}
                                  resultado={resultado}
                                  precioUnitario={previa.precioUnitario}
                                  tieneDescuento={tieneDescuento}
                                />
                              )}
                            </td>
                            <td>
                              <input
                                type="text"
                                inputMode="decimal"
                                autoComplete="off"
                                className={`form-control form-control-sm text-end${mensajeDeAjuste ? ' is-invalid' : ''}`}
                                style={{ width: 90 }}
                                aria-label={`Ajuste % de ${l.nombre}`}
                                aria-invalid={mensajeDeAjuste !== '' || undefined}
                                title={mensajeDeAjuste || undefined}
                                value={textoDeAjuste(l)}
                                disabled={lineaInerte}
                                onChange={(e) => cambiarAjuste(l.idLinea, e.target.value)}
                                onBlur={() => confirmarAjuste(l)}
                                onKeyDown={(e) => teclaDelAjuste(e, l)}
                              />
                            </td>
                            <td className="text-end">{previa.total === null ? '—' : formatearMoneda(previa.total)}</td>
                            <td className="text-end text-nowrap">
                              <BotonIcono
                                icono="eliminar"
                                etiqueta="Quitar"
                                title="Quitar"
                                disabled={lineaInerte}
                                onClick={() => quitarLinea(l.idLinea)}
                              />
                            </td>
                          </tr>
                        )
                      })}
                  {!modoPresupuesto && importeDelEnvio > 0 && (
                    <tr>
                      <td>—</td>
                      <td>Envío</td>
                      <td>1</td>
                      <td className="text-end">{formatearMoneda(importeDelEnvio)}</td>
                      <td className="text-end">—</td>
                      <td className="text-end">{formatearMoneda(importeDelEnvio)}</td>
                      <td className="text-end">—</td>
                    </tr>
                  )}
                  {(modoPresupuesto ? (presupuesto?.items.length ?? 0) === 0 : lineas.length === 0) && (
                    <tr>
                      <td colSpan={7} className="text-center text-muted py-4">
                        {modoPresupuesto ? 'Este presupuesto no tiene items.' : 'Escaneá o tipeá un código para empezar la venta.'}
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            {!modoPresupuesto && lineas.length > 0 && (
              <button
                type="button"
                className="btn btn-outline-danger btn-sm"
                disabled={!puedeVaciar}
                aria-keyshortcuts="F10"
                onClick={pedirConfirmacionDeVaciado}
              >
                Vaciar carrito <sup aria-hidden="true">(F10)</sup>
              </button>
            )}
          </Box>
        </div>

        <div className="col-lg-4">
          <Box titulo="Datos de la venta">

            <div className="text-center mb-3">
              <span className="d-block text-muted small">Total previo</span>
              <strong className="d-block display-5 lh-1">
                {modoPresupuesto
                  ? cargandoPresupuesto
                    ? 'Cargando…'
                    : formatearMoneda(totalActual)
                  : resolviendo
                    ? 'Calculando…'
                    : subtotalPrevia === null
                      ? '—'
                      : formatearMoneda(subtotalPrevia)}
              </strong>
            </div>

            {(combosAplicados.length > 0 || ajustesManuales.descuentoManualTotal !== 0 || ajustesManuales.recargoManualTotal !== 0) && (
              <div className="small mb-3">
                {combosAplicados.map((c) => (
                  <div key={c.idCombo} className="d-flex justify-content-between mb-1 text-success-emphasis">
                    <span>Combo {rotuloDeComboAplicado(c)}</span>
                    <span>{formatearMoneda(-c.ahorro)}</span>
                  </div>
                ))}
                {ajustesManuales.descuentoManualTotal !== 0 && (
                  <div className="d-flex justify-content-between mb-1 text-warning-emphasis">
                    <span>Desc. manual</span>
                    <span>{formatearMoneda(-ajustesManuales.descuentoManualTotal)}</span>
                  </div>
                )}
                {ajustesManuales.recargoManualTotal !== 0 && (
                  <div className="d-flex justify-content-between mb-1 text-info-emphasis">
                    <span>Recargo</span>
                    <span>{formatearImporteConSigno(ajustesManuales.recargoManualTotal, { simbolo: true })}</span>
                  </div>
                )}
              </div>
            )}

            {errorCargaClientes && !(errorCargaClientesEsDeRed && datosLocalesDisponibles && instantaneaDelPuntoVenta?.clientes.some((c) => c.esConsumidorFinal)) && (
              <div className="alert alert-danger py-1 px-2 small">{errorCargaClientes}</div>
            )}
            {errorClientes && <div className="alert alert-danger py-1 px-2 small">{errorClientes}</div>}
            {errorMedios && <div className="alert alert-danger py-1 px-2 small">{errorMedios}</div>}
            {errorParametros && <div className="alert alert-danger py-1 px-2 small">{errorParametros}</div>}
            {errorCobro && <div className="alert alert-danger py-1 px-2 small">{errorCobro}</div>}
            {cobroAApartarAhora !== null && !modoPresupuesto && (
              <div className="alert alert-warning py-1 px-2 small" role="alert">
                <p className="mb-1">Mientras el cobro aprobado siga sin venta, esta caja no puede iniciar otro cobro con Mercado Pago.</p>
                <button type="button" className="btn btn-sm btn-outline-danger" disabled={pantallaCobroInerte} onClick={() => void apartarCobroAprobadoDetenido()}>
                  Apartar cobro y liberar la caja
                </button>
              </div>
            )}
            {cobrosSinVenta.length > 0 && (
              <details className="alert alert-info py-1 px-2 small" aria-label="Cobros de Mercado Pago sin venta">
                <summary>
                  <span className="badge bg-danger me-1">{cobrosSinVenta.length}</span>
                  Cobros de Mercado Pago sin venta
                </summary>
                <p className="mb-1">Están aprobados y sin venta registrada: necesitan a un supervisor y se resuelven en la conciliación de Mercado Pago.</p>
                <ul className="mb-0">
                  {cobrosSinVenta.map((c) => (
                    <li key={c.idCobro}>
                      {formatearMoneda(c.importePagado ?? c.importe)} — cobro {c.idCobro} — {new Date(c.apartadoEn).toLocaleString('es-AR')}
                    </li>
                  ))}
                </ul>
              </details>
            )}
            {cobroDeRecarga !== null && !modoPresupuesto && (
              <div className="alert alert-warning py-1 px-2 small" role="alert">
                <p className="mb-1">
                  Quedó un cobro de Mercado Pago sin cerrar por {formatearMoneda(cobroDeRecarga.importe ?? 0)} (venta{' '}
                  {construirNumeroVisible(cobroDeRecarga.idPuntoVenta, cobroDeRecarga.numero)}).
                </p>
                <div className="d-flex gap-2">
                  <button type="button" className="btn btn-sm btn-primary" disabled={pantallaCobroInerte} onClick={() => void retomarCobroDeRecarga()}>
                    Retomar cobro
                  </button>
                  <button type="button" className="btn btn-sm btn-outline-danger" disabled={pantallaCobroInerte} onClick={() => void cancelarCobroDeRecarga()}>
                    Cancelar cobro
                  </button>
                  <button type="button" className="btn btn-sm btn-outline-secondary" disabled={pantallaCobroInerte} onClick={() => void apartarCobroDeRecarga()}>
                    Apartar cobro y liberar la caja
                  </button>
                </div>
              </div>
            )}
            {avisoLimiteSegunInstantanea && !modoPresupuesto && (
              <div className="alert alert-warning py-1 px-2 small">{avisoLimiteSegunInstantanea}</div>
            )}

            <h6>Pagos</h6>
            {filasPago.map((fila) => {
              const medioDeFila = fila.idMedioPago === '' ? null : (medioPorId[fila.idMedioPago] ?? null)

              return (
                <div className="row g-2 mb-2 align-items-center" key={fila.id}>
                  <div className="col-5">
                    <select
                      className="form-select form-select-sm"
                      aria-label="Medio de pago"
                      value={fila.idMedioPago}
                      disabled={pantallaCobroInerte || medios === null || bloqueadoPorTurno}
                      onChange={(e) => cambiarMedioDeFila(fila.id, e.target.value === '' ? '' : Number(e.target.value))}
                    >
                      <option value="">Elegir medio…</option>
                      {(medios ?? [])
                        .filter((m) => medioDisponibleParaCliente(m, clienteSeleccionado?.esConsumidorFinal ?? false))
                        // stage-pos-venta-offline-web (Parte D): proactivo — sin señal solo se
                        // ofrecen los medios que la venta local admite (`medioAdmitidoOffline`:
                        // efectivo y electrónico, y cuenta corriente solo para un cliente
                        // identificado). Solo gatea qué opciones se OFRECEN; el rechazo real vive
                        // en `admisibilidadDeVentaOffline` (mensajeDeRechazoOffline), que corre
                        // igual aunque esta pantalla pensara (erróneamente) que hay señal.
                        .filter(
                          (m) => sincronizacionOffline.enLinea || medioAdmitidoOffline(m.comportamiento, clienteSeleccionado?.esConsumidorFinal ?? true),
                        )
                        .map((m) => (
                          <option key={m.id} value={m.id}>
                            {m.nombre}
                          </option>
                        ))}
                    </select>
                  </div>
                  <div className="col-5">
                    <CampoImporte
                      id={`pos-fila-pago-importe-${fila.id}`}
                      className="form-control form-control-sm"
                      aria-label={etiquetaDeCampoFila('Importe', medioDeFila, fila.id)}
                      valor={fila.importe}
                      disabled={pantallaCobroInerte || bloqueadoPorTurno}
                      onChange={(v) => cambiarImporteDeFila(fila.id, v)}
                    />
                  </div>
                  <div className="col-2 d-flex align-items-center justify-content-end">
                    {filasPago.length > 1 && (
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger"
                        disabled={pantallaCobroInerte || bloqueadoPorTurno}
                        aria-label="Quitar medio de pago"
                        onClick={() => quitarFilaPago(fila.id)}
                      >
                        ×
                      </button>
                    )}
                  </div>
                  {/* stage-pos-caja-en-cabecera: la referencia solo se pide para el medio que la
                      exige — el servidor rechaza la venta sin ella (`requiereReferencia`), pero
                      mostrarla siempre para medios que no la usan (ej. Efectivo) era ruido puro. */}
                  {medioDeFila?.requiereReferencia && !medioDeFila.proveedor && (
                    <div className="col-12">
                      <input
                        type="text"
                        className="form-control form-control-sm"
                        aria-label={etiquetaDeCampoFila('Referencia', medioDeFila, fila.id)}
                        placeholder="Referencia (requerida)"
                        value={fila.referencia}
                        disabled={pantallaCobroInerte || bloqueadoPorTurno}
                        onChange={(e) => cambiarReferenciaDeFila(fila.id, e.target.value)}
                      />
                    </div>
                  )}
                </div>
              )
            })}

            <button
              type="button"
              className="btn btn-outline-secondary btn-sm mb-3"
              disabled={pantallaCobroInerte || bloqueadoPorTurno}
              onClick={agregarFilaPago}
            >
              + Agregar medio de pago
            </button>

            <div className="d-flex justify-content-between align-items-baseline mb-2">
              <span>{pagosCubrenElTotal ? 'Vuelto' : 'Falta'}</span>
              <span className={`fw-semibold ${previaFallida ? 'text-muted' : pagosCubrenElTotal ? 'text-success' : 'text-danger'}`}>
                {previaFallida ? 'se confirma al cobrar' : formatearMoneda(pagosCubrenElTotal ? excedente : faltante)}
              </span>
            </div>

            {rechazoLocal && <div className="alert alert-warning py-1 px-2 small">{rechazoLocal.mensaje}</div>}

            {motivoDeEnvio && (
              <div className="alert alert-warning py-1 px-2 small" role="status">
                {motivoDeEnvio}
              </div>
            )}

            {lineasPorImporteObsoletas.length > 0 && (
              <div className="alert alert-warning py-1 px-2 small" role="status">
                {mensajeDeLineaPorImporteObsoleta(lineasPorImporteObsoletas[0].nombre)}
              </div>
            )}

            {hayAjusteSinConfirmar && (
              <div className="alert alert-warning py-1 px-2 small" role="status">
                Confirmá (Enter) o descartá (Esc) el ajuste antes de cobrar.
              </div>
            )}

            {!modoPresupuesto && (
              <div className="form-check mb-2">
                <input
                  id="pos-facturar"
                  type="checkbox"
                  className="form-check-input"
                  checked={facturar}
                  disabled={pantallaCobroInerte}
                  onChange={(e) => setFacturar(e.target.checked)}
                />
                <label className="form-check-label" htmlFor="pos-facturar">
                  Facturar
                </label>
              </div>
            )}

            <button
              type="button"
              className="btn btn-success w-100"
              disabled={!puedeCobrar || confirmandoCobro || ventaFinalizada !== null}
              aria-keyshortcuts="F9"
              onClick={cobrar}
            >
              {facturando ? (
                'Facturando…'
              ) : cobrando ? (
                'Cobrando…'
              ) : (
                <>
                  Cobrar <sup aria-hidden="true">(F9)</sup>
                </>
              )}
            </button>
          </Box>
        </div>
      </div>

      {/* stage-pos-atajos-cobro: SOLO F9 pasa por acá — clickear "Cobrar" con el mouse sigue
          cobrando de inmediato sin pedir confirmación, comportamiento preexistente. Se renderiza
          acá, fuera del panel "Datos de la venta" (stage-pos-modales-de-cobro): es un modal
          centrado con su propio backdrop, nunca un bloque que agrande el panel.
          `!ventaFinalizada` (judgment-day ronda 0): entre que `cobrar()` (llamado desde
          `confirmarYcobrar`) setea `ventaFinalizada` y que ese mismo `confirmarYcobrar` recién
          después vuelve `confirmandoCobro` a `false`, hay un render con los dos estados en
          `true` a la vez — sin este conjunct, ambos modales se dibujaban superpuestos en ese
          instante. */}
      {modalDeCobroMercadoPago.modal}
      {confirmacionDeReuso.modal}

      {confirmandoCobro && !ventaFinalizada && (
        <ConfirmacionDeCobro
          total={totalActual}
          pagado={sumarImportes(pagosConVuelto)}
          vuelto={excedente}
          previaFallida={previaFallida}
          ocupado={cobrando}
          onFinalizar={confirmarYcobrar}
          onCancelar={cancelarConfirmacionDeCobro}
        />
      )}

      {vaciandoCarrito && (
        <Modal
          titulo="Vaciar carrito"
          restaurarFoco={false}
          onCerrar={cancelarVaciado}
          pie={
            <>
              <button type="button" className="btn btn-outline-secondary" autoFocus onClick={cancelarVaciado}>
                No
              </button>
              <button type="button" className="btn btn-danger" onClick={confirmarVaciado}>
                Sí
              </button>
            </>
          }
        >
          <p className="mb-0">¿Está seguro que quiere vaciar el carrito?</p>
        </Modal>
      )}

      {ventaPorDescartar && (
        <ConfirmacionDeBaja
          titulo={`la venta ${construirNumeroVisible(ventaPorDescartar.idPuntoVenta, ventaPorDescartar.numeroPreasignado)}`}
          pregunta="Descartar"
          nota="Nunca se va a sincronizar con el servidor."
          etiquetaConfirmar="Confirmar descarte"
          etiquetaEnCurso="Descartando…"
          ocupado={idLocalEnAccion === ventaPorDescartar.idLocal}
          disparador={disparadorDeDescarte}
          error={errorDescarte}
          onConfirmar={() => void onConfirmarDescarteDeVenta(ventaPorDescartar.idLocal)}
          onCancelar={onCancelarDescarteDeVenta}
        />
      )}

      {ventaFinalizada && (
        <VentaFinalizada
          numeroVisible={ventaFinalizada.numeroVisible}
          total={ventaFinalizada.total}
          medios={ventaFinalizada.medios}
          vuelto={ventaFinalizada.vuelto}
          itemsVencidos={ventaFinalizada.itemsVencidos}
          pendienteDeSincronizar={ventaFinalizada.pendienteDeSincronizar}
          avisoLimiteDeCredito={ventaFinalizada.avisoLimiteDeCredito}
          modoPresupuesto={modoPresupuesto}
          onCerrar={cerrarVentaFinalizada}
          onEscanearCodigo={(codigo) => void escanear(codigo)}
          onDejarCodigoEnInput={setEntradaEscaneo}
        />
      )}

      {buscadorAbierto && !modoPresupuesto && (
        <ModalDeBusquedaDeArticulos
          idListaPrecio={clienteSeleccionado?.idListaPrecio ?? null}
          idEmpresa={puntoVentaSeleccionada?.idEmpresa ?? null}
          motivoSinAgregar={motivoSinAgregarEnBuscador}
          onAgregar={agregarDesdeBusqueda}
          onCerrar={cerrarBuscador}
        />
      )}

      {buscadorDeCombosAbierto && !modoPresupuesto && (
        <ModalDeBusquedaDeCombos
          fuente={fuenteDeCombos}
          motivoSinAgregar={motivoSinAgregarEnBuscador}
          onCargar={cargarComboDesdeBuscador}
          onCerrar={cerrarBuscadorDeCombos}
        />
      )}

      {comboPorElegir && !modoPresupuesto && (
        <SelectorDeMiembrosDeCombo combo={comboPorElegir.combo} onConfirmar={confirmarMiembrosDeCombo} onCancelar={cancelarMiembrosDeCombo} />
      )}

      {altaDeClienteAbierta && !modoPresupuesto && (
        <ModalAltaRapidaDeCliente
          alCerrar={() => {
            setAltaDeClienteAbierta(false)
            pedirFocoEnElCodigo()
          }}
          alCreado={(creado) => {
            setAltaDeClienteAbierta(false)
            void sincronizacionOffline.registrarClienteEnLaCopiaLocal(creado).catch(() => undefined)
            cambiarCliente(aOpcionDeCliente(creado))
          }}
        />
      )}

      {modalCuentaCorrienteAbierto && !modoPresupuesto && puntoVentaSeleccionada && medios && (
        <ModalCuentaCorrientePos
          clienteInicial={clienteSeleccionado}
          puntoVenta={puntoVentaSeleccionada}
          medios={medios}
          onCerrar={() => {
            setModalCuentaCorrienteAbierto(false)
            pedirFocoEnElCodigo()
          }}
          onPagoRegistrado={(idCliente) => {
            // El saldo en pantalla quedó viejo: deja de ser confirmado hasta que la lectura nueva lo reemplace.
            saldoConfirmadoRef.current = null
            descensosAlPagarRef.current = descensosDeLaColaRef.current
            refrescarClienteSeleccionado(idCliente)
          }}
          onReciboListo={cajaDeEscritorio ? imprimirReciboDePago : undefined}
        />
      )}

      {/* stage-pos-retiros-y-cierre-por-retiro (etapa 5, desktop-only): flujo "Retirar" — dos
          modales apilables en secuencia, nunca los dos a la vez (`pasoRetiro` es el único dueño). */}
      {pasoRetiro === 'confirmarApertura' && (
        <Modal
          titulo="¿Querés retirar efectivo?"
          ocupado={registrandoAperturaRetiro}
          onCerrar={cancelarRetiro}
          pie={
            <>
              <button type="button" className="btn btn-outline-secondary" disabled={registrandoAperturaRetiro} onClick={cancelarRetiro}>
                No
              </button>
              <button
                type="button"
                className="btn btn-primary"
                disabled={registrandoAperturaRetiro}
                onClick={() => void confirmarAperturaParaRetiro()}
              >
                {registrandoAperturaRetiro ? 'Abriendo…' : 'Sí'}
              </button>
            </>
          }
        >
          {errorAperturaRetiro && <div className="alert alert-danger py-1 px-2 small">{errorAperturaRetiro}</div>}
          <p className="mb-0">Se va a abrir el cajón para contar el retiro.</p>
        </Modal>
      )}

      {pasoRetiro === 'monto' && (
        <Modal
          titulo="Monto retirado"
          ocupado={registrandoRetiro}
          noDescartable
          pie={
            <>
              <button
                type="button"
                className="btn btn-primary"
                disabled={registrandoRetiro || montoRetiro === null || montoRetiro <= 0}
                onClick={() => void confirmarMontoRetiro()}
              >
                {registrandoRetiro ? 'Registrando…' : 'Confirmar'}
              </button>
            </>
          }
        >
          {errorRetiro && <div className="alert alert-danger py-1 px-2 small">{errorRetiro}</div>}
          <label className="form-label" htmlFor="pos-retiro-monto">
            Monto
          </label>
          <CampoImporte
            id="pos-retiro-monto"
            className="form-control"
            valor={montoRetiro}
            disabled={registrandoRetiro}
            onChange={setMontoRetiro}
          />
        </Modal>
      )}

      {/* stage-pos-retiros-y-cierre-por-retiro (etapa 5, desktop-only): flujo "Cerrar caja" por
          retiro — mismo criterio de dos modales apilables que "Retirar". */}
      {pasoCierre === 'confirmar' && (
        <Modal
          titulo="¿Querés cerrar el turno?"
          ocupado={registrandoAperturaCierre}
          onCerrar={cancelarConfirmarCierre}
          pie={
            <>
              <button
                type="button"
                className="btn btn-outline-secondary"
                disabled={registrandoAperturaCierre}
                onClick={cancelarConfirmarCierre}
              >
                No
              </button>
              <button
                type="button"
                className="btn btn-primary"
                disabled={registrandoAperturaCierre}
                onClick={() => void confirmarAperturaParaCierre()}
              >
                {registrandoAperturaCierre ? 'Abriendo…' : 'Sí'}
              </button>
            </>
          }
        >
          {errorAperturaCierre && <div className="alert alert-danger py-1 px-2 small">{errorAperturaCierre}</div>}
          <p className="mb-0">Se va a abrir el cajón para contar el efectivo a retirar.</p>
        </Modal>
      )}

      {pasoCierre === 'monto' && (
        <Modal
          titulo="Efectivo a retirar"
          // judgment-day ronda 0 (JD-E5b-1, CRITICAL): mientras `cierreIncierto` es `true`, el
          // resultado real del cierre todavía no se sabe — la ÚNICA salida es "Reintentar"
          // (`recuperarCierreIncierto`). `ocupado` acá bloquea la ×, Escape y el click en el
          // fondo (ver `Modal.tsx`: los tres chequean `ocupado` antes de llamar `onCerrar`), así
          // que ningún cierre "silencioso" del modal puede saltear la reconciliación — si el
          // cierre ya sucedió del lado del servidor, descartar el modal sin pasar por
          // `recuperarCierreIncierto` dejaría el comprobante sin imprimir y el header mintiendo
          // "Caja abierta".
          ocupado={cerrandoPorRetiro || recuperandoCierre || cierreIncierto}
          onCerrar={cancelarMontoCierre}
          pie={
            cierreIncierto ? (
              <button
                type="button"
                className="btn btn-primary"
                disabled={recuperandoCierre}
                onClick={() => void recuperarCierreIncierto()}
              >
                {recuperandoCierre ? 'Verificando…' : 'Reintentar'}
              </button>
            ) : (
              <>
                <button type="button" className="btn btn-outline-secondary" disabled={cerrandoPorRetiro} onClick={cancelarMontoCierre}>
                  Cancelar
                </button>
                <button
                  type="button"
                  className="btn btn-danger"
                  disabled={cierrePorRetiroBloqueado}
                  onClick={() => void confirmarCierrePorRetiro()}
                >
                  {cerrandoPorRetiro ? 'Cerrando…' : 'Confirmar'}
                </button>
              </>
            )
          }
        >
          {errorCierrePorRetiro && <div className="alert alert-danger py-1 px-2 small">{errorCierrePorRetiro}</div>}
          {!cierreIncierto && (
            <>
              <label className="form-label" htmlFor="pos-cierre-monto">
                Monto
              </label>
              <CampoImporte
                id="pos-cierre-monto"
                className="form-control"
                valor={montoCierre}
                disabled={cerrandoPorRetiro}
                onChange={setMontoCierre}
              />

              {/* Override supervisado de la guarda de rendición — visible SOLO después de un
                  `409 rendicion_de_dispositivo_pendiente`, nunca por defecto. Mismo opt-in
                  explícito + motivo obligatorio que el bloque equivalente de `CierreDeCaja.tsx`. */}
              {rendicionPendiente && (
                <div className="border border-danger p-2 mt-3">
                  <div className="form-check">
                    <input
                      id="pos-cierre-forzar-sin-rendicion"
                      type="checkbox"
                      className="form-check-input"
                      checked={forzarSinRendicion}
                      disabled={cerrandoPorRetiro}
                      onChange={(e) => cambiarForzarSinRendicion(e.target.checked)}
                    />
                    <label className="form-check-label" htmlFor="pos-cierre-forzar-sin-rendicion">
                      Cerrar igual, sin la rendición del dispositivo. Las ventas que ese dispositivo sincronice
                      después de este cierre no van a estar en el arqueo de este turno: van a caer en el turno
                      siguiente, y los dos arqueos van a quedar mal.
                    </label>
                  </div>

                  <div className="mt-2">
                    <label className="form-label" htmlFor="pos-cierre-motivo-sin-rendicion">
                      Motivo del cierre forzado (obligatorio)
                    </label>
                    <input
                      id="pos-cierre-motivo-sin-rendicion"
                      type="text"
                      className="form-control"
                      value={motivoSinRendicion}
                      disabled={cerrandoPorRetiro || !forzarSinRendicion}
                      onChange={(e) => cambiarMotivoSinRendicion(e.target.value)}
                    />
                    <div className="form-text">Queda registrado en la auditoría del cierre.</div>
                  </div>

                  {!esSupervisorOAdmin && (
                    <div className="alert alert-warning py-1 px-2 small mt-2 mb-0">
                      Con tu rol el servidor va a rechazar el cierre forzado: pedile a un supervisor o a un
                      administrador que lo haga.
                    </div>
                  )}
                </div>
              )}
            </>
          )}
        </Modal>
      )}
      </div>
    </>
  )
}

/**
 * `/pos` (stage-17-presupuestos-y-remitos, Slice 7, design: Web composition — `react-async-state`
 * regla 8): lee `?idPresupuesto=` de la URL y el punto de venta de la sesión, y remonta
 * `PantallaPos` entera por `key` cuando cualquiera de los dos cambia — un `idPresupuesto` inválido
 * o ausente se trata como el camino libre, nunca un error bloqueante (la ruta sin query sigue
 * siendo el POS de siempre).
 */
type PropsPos = {
  alEmitir?: AlEmitirVenta
  alIrACerrarCaja?: (idTurno: number) => void
  cajaDeEscritorio?: CajaDeEscritorio
  alCambiarVentaEnCurso?: (enCurso: boolean) => void
}

export function Pos({ alEmitir, alIrACerrarCaja, cajaDeEscritorio, alCambiarVentaEnCurso }: PropsPos = {}) {
  const [searchParams] = useSearchParams()
  const { puntoVenta } = usePuntoVenta()
  const almacenBorradores = useContext(BorradorDeTicketContext)
  const crudo = searchParams.get('idPresupuesto')
  const idPresupuesto = crudo !== null && Number.isFinite(Number(crudo)) ? Number(crudo) : null

  // JD-2: `PantallaPos` lee su borrador inicial de forma síncrona en el primer render de su
  // propio `useRef` — bajo un `Provider` (`Layout`/`ShellPos`) hay que esperar acá, ANTES de
  // montarla, a que la hidratación desde IndexedDB termine (o venza su propio timeout). Sin
  // `Provider` (la mayoría de los tests, rutas que no cuelgan de `Layout`) `almacenBorradores` es
  // `null` y el comportamiento es el de siempre: listo desde el primer render.
  if (almacenBorradores && !almacenBorradores.listo) {
    return (
      <div className="container-fluid py-4">
        <Cargando />
      </div>
    )
  }

  return (
    <PantallaPos
      key={`${idPresupuesto ?? 'libre'}:${puntoVenta?.id ?? 'sin-pv'}`}
      idPresupuesto={idPresupuesto}
      alEmitir={alEmitir}
      alIrACerrarCaja={alIrACerrarCaja}
      cajaDeEscritorio={cajaDeEscritorio}
      alCambiarVentaEnCurso={alCambiarVentaEnCurso}
    />
  )
}
