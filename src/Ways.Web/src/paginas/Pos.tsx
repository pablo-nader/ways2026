import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { clienteDeArticulos } from '../api/articulos'
import { clienteDeCaja } from '../api/caja'
import { reducirCarrito, type AccionCarrito, type LineaCarrito } from '../api/carrito'
import { clienteDeCatalogo } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import { clienteDeOfertas } from '../api/ofertas'
import {
  aPagosDeVenta,
  calcularExcedente,
  calcularFaltante,
  filaPagoInicial,
  filaPagoVacia,
  filasAPagosConVuelto,
  filasAPagosParaCalculo,
  idMedioEfectivo,
  medioDisponibleParaCliente,
  sumarImportes,
  sumarVueltos,
  validarPagosLocal,
  type FilaPago,
} from '../api/pagos'
import { aSolicitudDeVentaDesdePresupuesto, clienteDePresupuestos } from '../api/presupuestos'
import type {
  ClienteListado,
  ComprobanteEmitido,
  MedioPagoAlta,
  MedioPagoListado,
  ParametroResuelto,
  PresupuestoParaVenta,
  ResultadoDeResolucion,
  TurnoResumen,
} from '../api/tipos'
import {
  aLineaDeCarritoDesdeEscaneo,
  aLineasDeResolucion,
  aSolicitudDeVenta,
  calcularSubtotalPrevia,
  clienteDeVentas,
  indexarResolucionPorArticulo,
  previaDeLinea,
} from '../api/ventas'
import { Box } from '../componentes/Box'
import { CampoImporte } from '../componentes/CampoImporte'
import { Cargando } from '../componentes/Cargando'
import { ModalDeBusquedaDeArticulos } from '../componentes/ModalDeBusquedaDeArticulos'
import { formatearImporte } from '../formato/importes'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'

/** Piso de cantidad por línea, compartido entre el guard de edición y los atributos
 * `min`/`step` del input — evita que ambos se desincronicen (ej. el guard aceptando
 * cantidades que el input ya no permite tipear). */
const CANTIDAD_MINIMA = 0.001

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

function fusionarOpcionesCliente(opciones: ClienteListado[], seleccionado: ClienteListado | null): ClienteListado[] {
  if (!seleccionado) return opciones
  return opciones.some((c) => c.id === seleccionado.id) ? opciones : [seleccionado, ...opciones]
}

function etiquetaDeCliente(c: ClienteListado): string {
  const nombreCompleto = c.razonSocial ?? [c.nombre, c.apellido].filter(Boolean).join(' ')
  return `#${c.numero} — ${nombreCompleto}`
}

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
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
            {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}

            <div className="row g-2 align-items-end" style={{ maxWidth: 640 }}>
              <div className="col-md-4">
                <label className="form-label" htmlFor="pos-gate-fondo-inicial">
                  Fondo inicial
                </label>
                <CampoImporte
                  id="pos-gate-fondo-inicial"
                  className="form-control rounded-0"
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
                  className="form-control rounded-0"
                  value={observaciones}
                  disabled={abriendo}
                  onChange={(e) => setObservaciones(e.target.value)}
                />
              </div>
              <div className="col-md-3">
                <button type="button" className="btn btn-primary rounded-0 w-100" disabled={abriendo} onClick={abrir}>
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
          <div className="modal-content rounded-0">
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
                  className="btn btn-success rounded-0"
                  disabled={ocupado}
                  aria-keyshortcuts="F9"
                  onClick={onFinalizar}
                >
                  {ocupado ? 'Finalizando…' : 'Finalizar (F9)'}
                </button>
                <button
                  ref={cancelarRef}
                  type="button"
                  className="btn btn-outline-secondary rounded-0"
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
}

type PropsVentaFinalizada = ResumenVentaFinalizada & { onCerrar: () => void }

/**
 * Modal "Venta finalizada" (stage-pos-modales-de-cobro): reemplaza a la pantalla de resumen de
 * ticket completa que reemplazaba toda la pantalla hasta que el cajero apretaba "Nueva venta" —
 * el carrito ya se reseteó para cuando este modal aparece (`cobrar()`), así que cerrarlo nunca
 * requiere ese paso extra. Mismo criterio de foco que `ConfirmacionDeCobro`: un solo control
 * focusable ("Aceptar"), autofocado al montar, Tab/Shift+Tab lo mantienen ahí (la pantalla de
 * atrás sigue con su navbar montada, así que sin esta trampa `aria-modal="true"` sería mentira
 * para tecnología asistiva — regla 13 de react-async-state). F9 también cierra: cableado en el
 * listener global de `PantallaPos` (`f9Ref`), que ignora `repeat` a propósito — un F9 todavía
 * sostenido desde la propia confirmación de cobro no debe cerrar este modal por accidente apenas
 * aparece.
 */
function VentaFinalizada({ numeroVisible, total, medios, vuelto, itemsVencidos, onCerrar }: PropsVentaFinalizada) {
  const aceptarRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    aceptarRef.current?.focus()
  }, [])

  function atraparTab(evento: React.KeyboardEvent<HTMLDivElement>) {
    if (evento.key !== 'Tab') return
    evento.preventDefault()
    aceptarRef.current?.focus()
  }

  return (
    <>
      <div className="modal d-block" tabIndex={-1} role="dialog" aria-modal="true" aria-label="Venta finalizada" onKeyDown={atraparTab}>
        <div className="modal-dialog modal-dialog-centered" role="document">
          <div className="modal-content rounded-0">
            <div className="modal-header">
              <h5 className="modal-title">Venta finalizada</h5>
            </div>
            <div className="modal-body text-center">
              {/* design decisión 12 ("Expired Lot Sale Warns, Never Blocks"): nunca bloquea la
                  venta — solo la última chance de que el operador se entere de que salió un lote
                  vencido, ahora que la vieja pantalla de resumen con el detalle de items ya no
                  existe. */}
              {itemsVencidos.length > 0 && (
                <div className="alert alert-danger rounded-0 text-start mb-3">
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
              <button ref={aceptarRef} type="button" className="btn btn-primary rounded-0" aria-keyshortcuts="F9" onClick={onCerrar}>
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
type PropsPantallaPos = {
  idPresupuesto: number | null
  alEmitir?: (comprobante: ComprobanteEmitido, cliente: ClienteListado, medios: MedioPagoListado[]) => void
  /** stage-pos-turno-y-foco: seam de navegación de "Cerrar caja" — la app web y el shell de
   * escritorio tienen rutas distintas para `CierreDeCaja` (`/caja/cierre` vs. `/cerrar-caja`).
   * `undefined` (app web normal) navega a la ruta libre con `useNavigate`; el shell pasa su
   * propia función para navegar a la suya. */
  alIrACerrarCaja?: (idTurno: number) => void
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
function PantallaPos({ idPresupuesto, alEmitir, alIrACerrarCaja }: PropsPantallaPos) {
  const modoPresupuesto = idPresupuesto !== null
  const navigate = useNavigate()
  const { puntoVenta: puntoVentaDeSesion, puntosVenta } = usePuntoVenta()

  const [opcionesClientes, setOpcionesClientes] = useState<ClienteListado[]>([])
  const [clienteSeleccionado, setClienteSeleccionado] = useState<ClienteListado | null>(null)
  const [terminoCliente, setTerminoCliente] = useState('')
  const [buscandoClientes, setBuscandoClientes] = useState(false)
  const [errorClientes, setErrorClientes] = useState('')
  const generacionClientesRef = useRef(0)

  const [lineas, setLineas] = useState<LineaCarrito[]>([])
  const [precios, setPrecios] = useState<Record<number, ResultadoDeResolucion>>({})
  const [resolviendo, setResolviendo] = useState(false)
  const [avisoPrecios, setAvisoPrecios] = useState('')
  const [reintentoPrecios, setReintentoPrecios] = useState(0)
  const generacionResolucionRef = useRef(0)
  const ultimaAccionEsEdicionRef = useRef(false)
  const [cantidadesEnEdicion, setCantidadesEnEdicion] = useState<Record<number, string>>({})

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

  // stage-pos-buscador-articulos: modal de búsqueda por nombre (F2, botón "Buscar" junto al de
  // código) — se abre solo con la venta libre operable (nunca bajo `?idPresupuesto=`, ni con el
  // checkout en vuelo, ni con el gate de turno o el ticket ya emitido en pantalla).
  const [buscadorAbierto, setBuscadorAbierto] = useState(false)

  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [errorMedios, setErrorMedios] = useState('')

  const [parametros, setParametros] = useState<{ toleranciaPago: number } | null>(null)
  const [errorParametros, setErrorParametros] = useState('')
  const generacionParametrosRef = useRef(0)

  const proximaFilaPagoIdRef = useRef(1)
  const [filasPago, setFilasPago] = useState<FilaPago[]>(() => [filaPagoInicial(proximaFilaPagoIdRef.current++, null)])

  // react-async-state regla 9: mientras el checkout está en vuelo, TODO lo que podría
  // superponerse (escaneo, edición de carrito, cliente, filas de pago) queda
  // inerte — `cobrandoRef` es el guard de reentrancia de primera línea (un doble click en el
  // mismo tick le gana al re-render que deshabilita el botón), `cobrando` es lo que deshabilita
  // los controles en pantalla.
  const [cobrando, setCobrando] = useState(false)
  const cobrandoRef = useRef(false)
  const generacionCobroRef = useRef(0)
  const [errorCobro, setErrorCobro] = useState('')

  // stage-pos-atajos-cobro: F9 con el pago ya cubierto abre esta puerta antes de cobrar de
  // verdad ("¿Finalizar venta?") — clickear "Cobrar" directamente NUNCA pasa por acá (sigue
  // cobrando de inmediato, comportamiento preexistente). `disparadorConfirmacionRef` captura
  // SÍNCRONAMENTE (react-async-state regla 12) el control enfocado en el momento del F9 que
  // abrió la puerta, para poder devolverle el foco si el cajero cancela.
  const [confirmandoCobro, setConfirmandoCobro] = useState(false)
  const disparadorConfirmacionRef = useRef<HTMLElement | null>(null)

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
  const [turno, setTurno] = useState<TurnoResumen | null>(null)
  const [cargandoTurno, setCargandoTurno] = useState(false)
  const [errorTurno, setErrorTurno] = useState('')
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

  const medioPorId = useMemo(() => {
    const indice: Record<number, MedioPagoListado> = {}
    for (const m of medios ?? []) indice[m.id] = m
    return indice
  }, [medios])

  // Carga inicial: clientes (para encontrar el Consumidor Final por defecto, spec: "Omitted
  // idCliente defaults to Consumidor Final") y medios de pago (panel de pagos, Slice 7). Cada uno
  // con su propio try/catch: que uno falle no bloquea al otro.
  useEffect(() => {
    let vigente = true

    const generacionClientes = (generacionClientesRef.current += 1)

    clienteDeClientes
      .listar('', false)
      .then((pagina) => {
        if (!vigente || generacionClientesRef.current !== generacionClientes) return
        setOpcionesClientes(pagina.items)
        // stage-17-presupuestos-y-remitos (Slice 7): bajo `?idPresupuesto=` el cliente lo trae el
        // presupuesto (efecto dedicado más abajo, hidrata el registro completo por id) — el
        // default de Consumidor Final NUNCA escribe acá bajo este modo, mismo criterio que el
        // punto de venta (que bajo este modo sale del presupuesto y no de la sesión).
        if (!modoPresupuesto) {
          const consumidorFinal = pagina.items.find((c) => c.esConsumidorFinal) ?? null
          setClienteSeleccionado(consumidorFinal)
        }
      })
      .catch((e) => {
        if (!vigente || generacionClientesRef.current !== generacionClientes) return
        setErrorClientes(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los clientes.')
      })

    clienteMediosPago
      .listar(false)
      .then((lista) => {
        if (!vigente) return
        setMedios(lista)
      })
      .catch((e) => {
        if (!vigente) return
        setMedios([])
        setErrorMedios(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los medios de pago. No se puede cobrar.')
      })

    return () => {
      vigente = false
    }
    // `modoPresupuesto` es estable durante toda la vida de esta instancia (deriva de la prop
    // `idPresupuesto`, y `PantallaPos` se remonta entera por `key` cuando cambia) — se declara
    // igual para dejar el efecto exhaustivo, nunca dispara una segunda corrida.
  }, [modoPresupuesto])

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
      })
      .catch((e) => {
        if (!vigente || generacionParametrosRef.current !== generacion) return
        setParametros(null)
        setErrorParametros(
          e instanceof ErrorApi ? e.message : 'No se pudieron cargar los parámetros de pago. No se puede cobrar.',
        )
      })

    return () => {
      vigente = false
    }
  }, [puntoVentaSeleccionada])

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

    // Se retorna la promesa (en vez de tratarla como fire-and-forget) para que `reintentarTurno`
    // pueda liberar su guarda de reentrancia recién cuando esta corrida específica termina.
    return clienteDeCaja
      .obtenerAbierto(idPuntoVenta)
      .then((t) => {
        if (generacionTurnoRef.current !== miGeneracion) return
        setTurno(t)
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
      })
      .finally(() => {
        if (generacionTurnoRef.current !== miGeneracion) return
        setCargandoTurno(false)
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
    setCargandoTurno(false)
    setTurno(nuevoTurno)
    // Re-judgment (WARNING, ambos jueces): mismo criterio que en `consultarTurno` — "Abrir turno"
    // (gate propio o safety-net del 409) confirma un turno abierto nuevo, así que cualquier aviso
    // de "El turno ya fue cerrado." de un click anterior de "Cerrar caja" queda obsoleto.
    setAvisoCerrarCaja('')
  }

  /**
   * "Cerrar caja" de "Datos de la venta" — vuelve a consultar el turno abierto ANTES de navegar
   * (judgment-day ronda 1, T2, WARNING: el `turno.id` que ya tiene el estado pudo quedar viejo —
   * otra pestaña/cajero cerró la caja mientras esta pantalla seguía mostrando "Turno abierto" — el
   * shell anterior hacía esta misma consulta fresca en el click, este reemplazo no debía perder
   * esa garantía). `alIrACerrarCaja` es el seam del shell de escritorio (ruta `/cerrar-caja`,
   * distinta de la ruta libre `/caja/cierre` de la app web); sin el prop, navega con `useNavigate`
   * como siempre. Nunca navega con un id potencialmente viejo: si la consulta ya no encuentra
   * turno, actualiza el badge a "cerrado" en vez de navegar a ciegas.
   */
  async function irACerrarCaja() {
    // regla 9/11: guarda de reentrancia de primera línea, liberada siempre en el `finally` sin
    // gate de generación (un doble click no debe disparar dos consultas).
    if (verificandoCierreRef.current) return
    if (!puntoVentaSeleccionada) return

    verificandoCierreRef.current = true
    setVerificandoCierre(true)
    setAvisoCerrarCaja('')
    const miGeneracion = (generacionTurnoRef.current += 1)

    try {
      const turnoReal = await clienteDeCaja.obtenerAbierto(puntoVentaSeleccionada.id)
      // Re-judgment (WARNING, juez A): si la pantalla ya se desmontó mientras el `await` estaba en
      // vuelo, ni un `setState` ni, sobre todo, la navegación posterior deben dispararse — se
      // chequea ANTES que la generación (y antes de cualquier otra cosa) a propósito.
      if (!montadoRef.current) return
      if (generacionTurnoRef.current !== miGeneracion) return

      if (!turnoReal) {
        setTurno(null)
        setAvisoCerrarCaja('El turno ya fue cerrado.')
        return
      }

      if (alIrACerrarCaja) {
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
      : 'Turno cerrado: abrí un turno para vender.'

  // react-async-state regla 2/3/4: cada mutación del carrito (o un cambio de cliente/punto de
  // venta, de los que depende el lote) dispara una nueva resolución de precios; una respuesta
  // de una resolución anterior nunca puede pisar la de la más reciente (design: POS Screen
  // Composition, regla 2 — "generacionResolucionRef gates every /resolver response").
  useEffect(() => {
    const generacion = (generacionResolucionRef.current += 1)

    // stage-17-presupuestos-y-remitos (Slice 7, design: Web composition — "skip the
    // price-resolution effect entirely"): el precio de una conversión sale congelado de
    // `items_presupuesto`, jamás de `ServicioDeOfertas.ResolverAsync` — este efecto no dispara
    // NINGÚN fetch bajo `?idPresupuesto=` (react-async-state regla 3: la generación igual se
    // bumpea arriba para huerfanar cualquier resolución en vuelo de una corrida anterior).
    if (modoPresupuesto) {
      setPrecios({})
      setAvisoPrecios('')
      setResolviendo(false)
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
      setAvisoPrecios('')
      // Este efecto bumpea su propia generación en cada corrida (línea de arriba): eso huerfana
      // cualquier fetch en vuelo de una corrida anterior (su `finally` se salta porque la
      // generación ya no coincide). Sin este reset explícito, esta corrida temprana (ej. vaciar
      // el carrito, o que quede vacío tras cobrar) deja `resolviendo` en `true` para siempre.
      setResolviendo(false)
      return
    }

    let vigente = true
    setResolviendo(true)
    setAvisoPrecios('')

    const idTimeout = setTimeout(() => {
      clienteDeOfertas
        .resolver(aLineasDeResolucion(lineas, clienteSeleccionado.idListaPrecio, puntoVentaSeleccionada.idEmpresa))
        .then((resultados) => {
          if (!vigente || generacionResolucionRef.current !== generacion) return
          setPrecios(indexarResolucionPorArticulo(resultados))
        })
        .catch(() => {
          if (!vigente || generacionResolucionRef.current !== generacion) return
          // Una resolución fallida invalida cualquier precio previo: dejar `precios` con datos
          // de una corrida anterior haría que el carrito quedara "parcialmente resuelto" (una
          // línea con precio viejo, otra en 0) en vez de entrar en modo vista previa fallida —
          // un subtotal a medias es peor que ninguno (judgment-day R3, falso negativo de Judge B).
          setPrecios({})
          setAvisoPrecios('No se pudo calcular la vista previa de precios. El total se confirma recién al cobrar.')
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
  }, [lineas, clienteSeleccionado, puntoVentaSeleccionada, reintentoPrecios, modoPresupuesto])

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
        return { ...f, idMedioPago: '' as const, vueltoManual: null }
      })
      return cambio ? siguiente : prev
    })
  }, [clienteSeleccionado, medioPorId])

  const mutarCarrito = useCallback((accion: AccionCarrito) => {
    if (cobrandoRef.current) return
    ultimaAccionEsEdicionRef.current = accion.tipo === 'editarCantidad'
    setLineas((prev) => reducirCarrito(prev, accion))

    // El mapa de ediciones en curso es un override por fila: si la fila desaparece (quitar,
    // vaciar) o su cantidad se recalcula por fuera de la edición manual (un escaneo que suma
    // sobre la línea existente), el override queda desactualizado y debe limpiarse acá mismo
    // — no puede depender de que el blur del input dispare antes que la próxima mutación.
    const limpiarFila = (idArticulo: number) =>
      setCantidadesEnEdicion((prev) => {
        if (!(idArticulo in prev)) return prev
        const { [idArticulo]: _omitido, ...resto } = prev
        return resto
      })

    switch (accion.tipo) {
      case 'quitarLinea':
        limpiarFila(accion.idArticulo)
        break
      case 'escanear':
        limpiarFila(accion.linea.idArticulo)
        break
      case 'vaciar':
        setCantidadesEnEdicion({})
        break
      case 'editarCantidad':
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
    return cantidadesEnEdicion[l.idArticulo] ?? String(l.cantidad)
  }

  function cambiarCantidad(idArticulo: number, texto: string) {
    if (cobrandoRef.current) return
    setCantidadesEnEdicion((prev) => ({ ...prev, [idArticulo]: texto }))
    const cantidad = Number(texto)
    if (texto.trim() === '' || !Number.isFinite(cantidad) || cantidad < CANTIDAD_MINIMA) return

    // Un estado intermedio del input (ej. "1." tipeando hacia "1.5") puede parsear al mismo
    // valor ya comprometido en la línea (Number("1.") === 1) — despachar en ese caso dispara
    // una resolución de precios redundante. Solo se despacha cuando el valor parseado difiere
    // de la cantidad comprometida.
    const lineaActual = lineas.find((l) => l.idArticulo === idArticulo)
    if (lineaActual && lineaActual.cantidad === cantidad) return

    mutarCarrito({ tipo: 'editarCantidad', idArticulo, cantidad })
  }

  function confirmarCantidad(idArticulo: number) {
    if (cobrandoRef.current) return
    setCantidadesEnEdicion((prev) => {
      const { [idArticulo]: _omitido, ...resto } = prev
      return resto
    })
  }

  async function escanear() {
    if (escaneando || cobrandoRef.current) return
    const entrada = entradaEscaneo.trim()
    if (!entrada) return

    const token = (tokenEscaneoRef.current += 1)
    setEscaneando(true)
    setErrorEscaneo('')
    try {
      const articulo = await clienteDeArticulos.escanear(entrada)
      if (tokenEscaneoRef.current !== token) return
      const { linea, cantidad } = aLineaDeCarritoDesdeEscaneo(articulo)
      mutarCarrito({ tipo: 'escanear', linea, cantidad })
      setEntradaEscaneo('')
      focoPendienteRef.current = true
    } catch (e) {
      if (tokenEscaneoRef.current !== token) return
      setErrorEscaneo(e instanceof ErrorApi ? e.message : 'No se pudo resolver el código escaneado.')
    } finally {
      if (tokenEscaneoRef.current === token) setEscaneando(false)
    }
  }

  async function buscarClientes() {
    if (buscandoClientes || cobrandoRef.current) return
    const generacion = (generacionClientesRef.current += 1)
    setBuscandoClientes(true)
    setErrorClientes('')
    try {
      const pagina = await clienteDeClientes.listar(terminoCliente, false)
      if (generacionClientesRef.current !== generacion) return
      setOpcionesClientes(pagina.items)
    } catch (e) {
      if (generacionClientesRef.current === generacion) {
        setErrorClientes(e instanceof ErrorApi ? e.message : 'No se pudieron buscar clientes.')
      }
    } finally {
      if (generacionClientesRef.current === generacion) setBuscandoClientes(false)
    }
  }

  /** "Agregar" de una fila del buscador — mismo camino que un código escaneado (`AccionCarrito`
   * tipo `escanear`): la resolución de precio/ofertas la sigue haciendo el efecto de `lineas` de
   * siempre, nunca este handler. */
  function agregarDesdeBusqueda(linea: Omit<LineaCarrito, 'cantidad'>, cantidad: number) {
    mutarCarrito({ tipo: 'escanear', linea, cantidad })
    setBuscadorAbierto(false)
    focoPendienteRef.current = true
  }

  function cerrarBuscador() {
    setBuscadorAbierto(false)
    focoPendienteRef.current = true
  }

  function cambiarCliente(id: number) {
    if (cobrandoRef.current) return
    const encontrado = fusionarOpcionesCliente(opcionesClientes, clienteSeleccionado).find((c) => c.id === id)
    setClienteSeleccionado(encontrado ?? null)
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
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, idMedioPago, vueltoManual: null } : f)))
  }

  function cambiarImporteDeFila(id: number, importe: number | null) {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, importe } : f)))
  }

  function cambiarReferenciaDeFila(id: number, referencia: string) {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, referencia } : f)))
  }

  function cambiarVueltoDeFila(id: number, vueltoManual: number | null) {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, vueltoManual } : f)))
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

  // F2 abre el buscador de artículos (spec pos-buscador-articulos) — solo con la venta libre
  // operable: nunca bajo `?idPresupuesto=` (carrito congelado, sin escaneo), nunca con el
  // checkout en vuelo, nunca con el gate de turno o el ticket ya emitido reemplazando la pantalla,
  // y nunca si el modal ya está abierto (no hay otro modal en esta pantalla que deba cerrarse
  // antes). Se re-suscribe con las dependencias en vez de leerlas por ref: son booleans que
  // cambian con poca frecuencia, el costo de resuscribir el listener es despreciable.
  useEffect(() => {
    function alTeclado(evento: KeyboardEvent) {
      if (evento.key !== 'F2') return
      if (modoPresupuesto || cobrando || buscadorAbierto || gateTurno || ventaFinalizada || confirmandoCobro) return
      evento.preventDefault()
      setBuscadorAbierto(true)
    }
    document.addEventListener('keydown', alTeclado)
    return () => document.removeEventListener('keydown', alTeclado)
  }, [modoPresupuesto, cobrando, buscadorAbierto, gateTurno, ventaFinalizada, confirmandoCobro])

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
    if (escaneando || cobrando || buscadorAbierto || bloqueadoPorTurno || confirmandoCobro || ventaFinalizada) return
    focoPendienteRef.current = false
    inputEscaneoRef.current?.focus()
  }, [escaneando, cobrando, buscadorAbierto, bloqueadoPorTurno, confirmandoCobro, ventaFinalizada])

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
    if (escaneando || cobrando || buscadorAbierto || bloqueadoPorTurno || confirmandoCobro || ventaFinalizada) return
    const activo = document.activeElement
    if (activo !== document.body && activo !== null && activo !== inputEscaneoRef.current) return
    inputEscaneoRef.current?.focus()
  }, [escaneando, cobrando, buscadorAbierto, bloqueadoPorTurno, confirmandoCobro, ventaFinalizada])

  const subtotalPrevia = calcularSubtotalPrevia(lineas, precios)
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
  // cargados: el excedente contra ese total siempre da 0, así que el vuelto sugerido queda en 0
  // — el cajero puede tipear uno manualmente si lo sabe (`vueltoDeFila` sigue respetando el
  // override manual).
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
        })

  // react-async-state regla 7: si medios de pago o parámetros no cargaron, "Cobrar" queda
  // efectivamente deshabilitado — no solo un aviso decorativo. Bajo `?idPresupuesto=` la
  // precondición de "hay líneas" es "el presupuesto cargó Y es `Convertible`" (la misma fuente de
  // verdad server-side que oculta el botón "Convertir en venta" en Presupuesto.tsx).
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
      lineas.length > 0 &&
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
  const pantallaCobroInerte = cobrando || confirmandoCobro || ventaFinalizada !== null

  async function cobrar() {
    // react-async-state regla 9: guard de reentrancia de primera línea — un doble click en el
    // mismo tick le gana al re-render que deshabilita el botón.
    if (cobrandoRef.current) return
    if (!puedeCobrar || !clienteSeleccionado || !puntoVentaSeleccionada) return

    const miGeneracion = (generacionCobroRef.current += 1)
    cobrandoRef.current = true
    setCobrando(true)
    setErrorCobro('')

    try {
      // stage-17-presupuestos-y-remitos (Slice 7, design: Web composition — "post {
      // idPuntoVenta, codigoTipoComprobante: 'TX', idPresupuestoOrigen, lineas: undefined,
      // pagos }"): bajo `?idPresupuesto=` NUNCA se manda `lineas` ni `idCliente` — el precio y el
      // cliente salen congelados server-side del presupuesto, jamás de lo que esta pantalla
      // pudiera mostrar.
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
              pagos: aPagosDeVenta(pagosConVuelto),
              direccionEntrega: null,
              observaciones: null,
            })

      const emitido = await clienteDeVentas.emitir(solicitud)
      if (generacionCobroRef.current !== miGeneracion) return

      // stage-pos-modales-de-cobro: el modal "Venta finalizada" solo necesita lo que muestra —
      // número, total, medios aplicados con su importe y el vuelto a entregar, todo desde la
      // propia respuesta del servidor (nunca recalculado de `pagosConVuelto` local).
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
      })
      // stage-desktop-pos: `puedeCobrar`/`precondicionesListas` ya exigieron `medios !== null`
      // para llegar hasta acá — el seam nunca dispara con la lista todavía sin cargar.
      alEmitir?.(emitido, clienteSeleccionado, medios ?? [])
      setLineas([])
      setPrecios({})
      setCantidadesEnEdicion({})
      setFilasPago([filaPagoInicial(proximaFilaPagoIdRef.current++, medios)])
      setEntradaEscaneo('')
      setTerminoCliente('')
      // El modal ya no depende de un click en "Nueva venta" para limpiar lo que queda de la
      // venta anterior — el reset es parte del propio éxito de `cobrar()`.
      setErrorEscaneo('')
    } catch (e) {
      if (generacionCobroRef.current !== miGeneracion) return
      // stage-6-turnos-caja (Slice 7): el gate seam reemplaza el panel entero, no un aviso más
      // — reintentar el checkout sin turno abierto solo repetiría el mismo 409.
      if (e instanceof ErrorApi && e.codigo === 'turno_no_abierto') {
        setGateTurno(true)
      } else {
        setErrorCobro(e instanceof ErrorApi ? e.message : 'No se pudo registrar la venta.')
      }
    } finally {
      if (generacionCobroRef.current === miGeneracion) {
        cobrandoRef.current = false
        setCobrando(false)
      }
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
    modoPresupuesto: boolean
    presupuestoCargado: boolean
    precondicionesListas: boolean
    pagosCubrenElTotal: boolean
    puedeCobrar: boolean
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
      buscadorAbierto,
      gateTurno,
      modoPresupuesto,
      presupuestoCargado: presupuesto !== null,
      precondicionesListas,
      pagosCubrenElTotal,
      puedeCobrar,
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

      // stage-pos-modales-de-cobro: el modal "Venta finalizada" también cierra con F9 —
      // `repeat` sigue ignorado por la misma razón que en `confirmandoCobro` de abajo: un F9
      // todavía sostenido desde la propia confirmación de cobro no debe alcanzar a cerrar ESTE
      // modal por accidente apenas aparece.
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

      if (evento.key !== 'F9') return
      if (evento.repeat) return
      if (m.buscadorAbierto || m.gateTurno) return
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
          <Link className="btn btn-outline-secondary rounded-0" to="/presupuestos">
            Volver a presupuestos
          </Link>
        </Box>
      </div>
    )
  }

  if (gateTurno && puntoVentaSeleccionada) {
    return (
      <PanelGateTurno
        idPuntoVenta={puntoVentaSeleccionada.id}
        onAbierto={(turnoAbierto) => {
          setGateTurno(false)
          setErrorCobro('')
          turnoConfirmadoAbierto(turnoAbierto)
        }}
      />
    )
  }

  return (
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
          <Box titulo="Carrito">
            {/* stage-pos-turno-y-foco: aviso claro de por qué escaneo/carrito/cliente/pagos están
                deshabilitados — solo cuando el turno está CONFIRMADO cerrado (nunca mientras
                todavía se está consultando, ni si la consulta falló: `errorTurno` tiene su propio
                aviso en "Datos de la venta"). */}
            {bloqueadoPorTurno && !cargandoTurno && errorTurno === '' && (
              <div className="alert alert-warning rounded-0 py-1 px-2 small">Turno cerrado: abrí un turno para vender.</div>
            )}
            {errorEscaneo && !modoPresupuesto && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorEscaneo}</div>}
            {avisoPrecios && !modoPresupuesto && (
              <div className="alert alert-warning rounded-0 py-1 px-2 small d-flex justify-content-between align-items-center gap-2">
                <span>{avisoPrecios}</span>
                <button
                  type="button"
                  className="btn btn-sm btn-outline-warning rounded-0"
                  disabled={pantallaCobroInerte || resolviendo}
                  onClick={reintentarPrecios}
                >
                  Reintentar
                </button>
              </div>
            )}

            {!modoPresupuesto && (
              <div className="input-group mb-3">
                <input
                  ref={inputEscaneoRef}
                  type="text"
                  className="form-control rounded-0"
                  placeholder="Escanear o tipear un código (ej. 3*7790001234567)"
                  aria-label="Código escaneado"
                  value={entradaEscaneo}
                  disabled={escaneando || pantallaCobroInerte || bloqueadoPorTurno}
                  onChange={(e) => setEntradaEscaneo(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), escanear())}
                />
                <button
                  type="button"
                  className="btn btn-primary rounded-0"
                  disabled={escaneando || pantallaCobroInerte || bloqueadoPorTurno}
                  onClick={escanear}
                >
                  {escaneando ? 'Buscando…' : 'Agregar'}
                </button>
                {/* "Buscar artículo" en vez de "Buscar" a secas: ya existe un botón "Buscar" en el
                    panel de cliente de esta misma pantalla (`Buscar cliente`) — un nombre
                    accesible idéntico rompería cualquier `getByRole('button', { name: 'Buscar' })`
                    (ambos quedarían matcheados a la vez, tests preexistentes incluidos). El
                    `(F2)` es `aria-hidden`: solo decoración visual, el nombre accesible sigue
                    siendo "Buscar artículo" a secas — `aria-keyshortcuts` es la forma correcta de
                    exponer el atajo a tecnología asistiva. */}
                <button
                  type="button"
                  className="btn btn-outline-primary rounded-0"
                  disabled={escaneando || pantallaCobroInerte}
                  aria-keyshortcuts="F2"
                  onClick={() => setBuscadorAbierto(true)}
                >
                  Buscar artículo <sup aria-hidden="true">(F2)</sup>
                </button>
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
                          <td className="text-end">{formatearMoneda(item.total)}</td>
                          <td className="text-end">—</td>
                        </tr>
                      ))
                    : lineas.map((l) => {
                        const resultado = precios[l.idArticulo]
                        const previa = previaDeLinea(l, resultado)
                        const tieneDescuento = previa.descuentoUnitario > 0 && resultado?.precioOriginal != null
                        return (
                          <tr key={l.idArticulo}>
                            <td>{l.codigoBarra ?? l.codigoInterno}</td>
                            <td>{l.nombre}</td>
                            <td>
                              <input
                                type="number"
                                step={CANTIDAD_MINIMA}
                                min={CANTIDAD_MINIMA}
                                className="form-control form-control-sm rounded-0"
                                aria-label={`Cantidad de ${l.nombre}`}
                                value={textoCantidad(l)}
                                disabled={pantallaCobroInerte || bloqueadoPorTurno}
                                onChange={(e) => cambiarCantidad(l.idArticulo, e.target.value)}
                                onBlur={() => confirmarCantidad(l.idArticulo)}
                              />
                            </td>
                            <td className="text-end">
                              {previa.precioUnitario === null ? (
                                '—'
                              ) : (
                                <>
                                  {tieneDescuento && (
                                    <div className="text-decoration-line-through text-muted small">
                                      {formatearMoneda(resultado.precioOriginal as number)}
                                    </div>
                                  )}
                                  <div>
                                    {formatearMoneda(previa.precioUnitario)}
                                    {tieneDescuento && resultado.aplicadas.length > 0 && (
                                      <span
                                        className="badge bg-success ms-1"
                                        title={resultado.aplicadas.map((a) => a.nombre).join(', ')}
                                      >
                                        {resultado.aplicadas.length === 1
                                          ? resultado.aplicadas[0].nombre
                                          : `${resultado.aplicadas[0].nombre} +${resultado.aplicadas.length - 1}`}
                                      </span>
                                    )}
                                  </div>
                                </>
                              )}
                            </td>
                            <td className="text-end">{previa.total === null ? '—' : formatearMoneda(previa.total)}</td>
                            <td className="text-end">
                              <button
                                type="button"
                                className="btn btn-sm btn-outline-danger rounded-0"
                                disabled={pantallaCobroInerte || bloqueadoPorTurno}
                                onClick={() => mutarCarrito({ tipo: 'quitarLinea', idArticulo: l.idArticulo })}
                              >
                                Quitar
                              </button>
                            </td>
                          </tr>
                        )
                      })}
                  {(modoPresupuesto ? (presupuesto?.items.length ?? 0) === 0 : lineas.length === 0) && (
                    <tr>
                      <td colSpan={6} className="text-center text-muted py-4">
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
                className="btn btn-outline-secondary btn-sm rounded-0"
                disabled={pantallaCobroInerte || bloqueadoPorTurno}
                onClick={() => mutarCarrito({ tipo: 'vaciar' })}
              >
                Vaciar carrito
              </button>
            )}
          </Box>
        </div>

        <div className="col-lg-4">
          <Box titulo="Datos de la venta">
            <div className="mb-3">
              {puntoVentaSeleccionada ? (
                <>
                  <span className="text-muted small">Punto de venta:</span> <strong>{puntoVentaSeleccionada.nombre}</strong>
                </>
              ) : (
                <div className="alert alert-warning rounded-0 py-1 px-2 small">Sin puntos de venta disponibles</div>
              )}
            </div>

            {/* stage-pos-turno-y-foco: estado del turno del punto de venta + acción directa —
                "Abrir turno" (reutiliza el mismo `PanelGateTurno` del safety net de 409) cuando
                está cerrado, "Cerrar caja" (navega con el `idTurno` real, nunca a ciegas) cuando
                está abierto. Nunca se muestra bajo `?idPresupuesto=` (esa conversión no tiene
                escaneo/carrito propio, el 409 la sigue cubriendo igual). */}
            {!modoPresupuesto && puntoVentaSeleccionada && (
              <div className="mb-3">
                <div className="small text-muted">Turno</div>
                {cargandoTurno ? (
                  <span className="text-muted">Consultando…</span>
                ) : errorTurno ? (
                  // judgment-day ronda 1 (T1, CRITICAL): sin "Reintentar" acá, un error de red
                  // dejaba `bloqueadoPorTurno` en `true` para siempre — ni "Abrir turno" ni
                  // "Cerrar caja" se renderizan en este ternario, así que la única salida era
                  // recargar la página entera (imposible en el shell de escritorio, sin selector
                  // de punto de venta que provoque un remount).
                  <div className="alert alert-danger rounded-0 py-1 px-2 small d-flex justify-content-between align-items-center gap-2">
                    <span>{errorTurno}</span>
                    <button
                      type="button"
                      className="btn btn-sm btn-outline-danger rounded-0"
                      disabled={cargandoTurno}
                      onClick={reintentarTurno}
                    >
                      Reintentar
                    </button>
                  </div>
                ) : (
                  <div className="d-flex justify-content-between align-items-center">
                    {turno ? (
                      <>
                        <span className="badge bg-success">Turno abierto</span>
                        <button
                          type="button"
                          className="btn btn-outline-danger btn-sm rounded-0"
                          disabled={verificandoCierre || pantallaCobroInerte}
                          onClick={() => void irACerrarCaja()}
                        >
                          {verificandoCierre ? 'Verificando…' : 'Cerrar caja'}
                        </button>
                      </>
                    ) : (
                      <>
                        <span className="badge bg-secondary">Turno cerrado</span>
                        <button type="button" className="btn btn-primary btn-sm rounded-0" onClick={() => setGateTurno(true)}>
                          Abrir turno
                        </button>
                      </>
                    )}
                  </div>
                )}
                {/* judgment-day ronda 1 (T2): aviso propio de la consulta fresca de "Cerrar caja"
                    — nunca comparte slot con `errorTurno` (regla 14), esa consulta es del mount/
                    reintentar, no del click. */}
                {avisoCerrarCaja && (
                  <div className="alert alert-warning rounded-0 py-1 px-2 small mt-2">{avisoCerrarCaja}</div>
                )}
              </div>
            )}

            {errorClientes && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorClientes}</div>}

            <div className="mb-2">
              <label className="form-label" htmlFor="pos-cliente">
                Cliente
              </label>
              <select
                id="pos-cliente"
                className="form-select rounded-0"
                value={clienteSeleccionado?.id ?? ''}
                disabled={pantallaCobroInerte || modoPresupuesto || bloqueadoPorTurno}
                onChange={(e) => cambiarCliente(Number(e.target.value))}
              >
                {fusionarOpcionesCliente(opcionesClientes, clienteSeleccionado).map((c) => (
                  <option key={c.id} value={c.id}>
                    {etiquetaDeCliente(c)}
                  </option>
                ))}
              </select>
            </div>

            {!modoPresupuesto && (
              <div className="input-group input-group-sm mb-3">
                <input
                  type="search"
                  className="form-control rounded-0"
                  placeholder="Buscar otro cliente…"
                  aria-label="Buscar cliente"
                  value={terminoCliente}
                  disabled={buscandoClientes || pantallaCobroInerte || bloqueadoPorTurno}
                  onChange={(e) => setTerminoCliente(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), buscarClientes())}
                />
                <button
                  type="button"
                  className="btn btn-outline-primary rounded-0"
                  disabled={buscandoClientes || pantallaCobroInerte || bloqueadoPorTurno}
                  onClick={buscarClientes}
                >
                  {buscandoClientes ? 'Buscando…' : 'Buscar'}
                </button>
              </div>
            )}

            <hr />

            <div className="d-flex justify-content-between mb-3">
              <strong>Total previo</strong>
              <strong>
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

            {errorMedios && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorMedios}</div>}
            {errorParametros && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorParametros}</div>}
            {errorCobro && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorCobro}</div>}

            <h6>Pagos</h6>
            {filasPago.map((fila) => {
              const medioDeFila = fila.idMedioPago === '' ? null : (medioPorId[fila.idMedioPago] ?? null)
              const pagoDeFila = pagosConVuelto.find((p) => p.idFila === fila.id) ?? null
              const vueltoMostrado = fila.vueltoManual !== null ? fila.vueltoManual : (pagoDeFila?.vuelto ?? 0)

              return (
                <div className="row g-2 mb-2 align-items-center" key={fila.id}>
                  <div className="col-4">
                    <select
                      className="form-select form-select-sm rounded-0"
                      aria-label="Medio de pago"
                      value={fila.idMedioPago}
                      disabled={pantallaCobroInerte || medios === null || bloqueadoPorTurno}
                      onChange={(e) => cambiarMedioDeFila(fila.id, e.target.value === '' ? '' : Number(e.target.value))}
                    >
                      <option value="">Elegir medio…</option>
                      {(medios ?? [])
                        .filter((m) => medioDisponibleParaCliente(m, clienteSeleccionado?.esConsumidorFinal ?? false))
                        .map((m) => (
                          <option key={m.id} value={m.id}>
                            {m.nombre}
                          </option>
                        ))}
                    </select>
                  </div>
                  <div className="col-3">
                    <CampoImporte
                      id={`pos-fila-pago-importe-${fila.id}`}
                      className="form-control form-control-sm rounded-0"
                      aria-label={etiquetaDeCampoFila('Importe', medioDeFila, fila.id)}
                      valor={fila.importe}
                      disabled={pantallaCobroInerte || bloqueadoPorTurno}
                      onChange={(v) => cambiarImporteDeFila(fila.id, v)}
                    />
                  </div>
                  <div className="col-3">
                    <input
                      type="text"
                      className="form-control form-control-sm rounded-0"
                      aria-label={etiquetaDeCampoFila('Referencia', medioDeFila, fila.id)}
                      placeholder={medioDeFila?.requiereReferencia ? 'Referencia (requerida)' : 'Referencia'}
                      value={fila.referencia}
                      disabled={pantallaCobroInerte || bloqueadoPorTurno || !medioDeFila?.requiereReferencia}
                      onChange={(e) => cambiarReferenciaDeFila(fila.id, e.target.value)}
                    />
                  </div>
                  <div className="col-2 d-flex align-items-center gap-1">
                    <CampoImporte
                      className="form-control form-control-sm rounded-0"
                      aria-label={etiquetaDeCampoFila('Vuelto', medioDeFila, fila.id)}
                      valor={vueltoMostrado}
                      disabled={pantallaCobroInerte || bloqueadoPorTurno || !medioDeFila?.admiteVuelto}
                      onChange={(v) => cambiarVueltoDeFila(fila.id, v)}
                    />
                    {filasPago.length > 1 && (
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger rounded-0"
                        disabled={pantallaCobroInerte || bloqueadoPorTurno}
                        aria-label="Quitar medio de pago"
                        onClick={() => quitarFilaPago(fila.id)}
                      >
                        ×
                      </button>
                    )}
                  </div>
                </div>
              )
            })}

            <button
              type="button"
              className="btn btn-outline-secondary btn-sm rounded-0 mb-3"
              disabled={pantallaCobroInerte || bloqueadoPorTurno}
              onClick={agregarFilaPago}
            >
              + Agregar medio de pago
            </button>

            <div className="d-flex justify-content-between small">
              <span>Falta</span>
              <span>{previaFallida ? 'se confirma al cobrar' : formatearMoneda(faltante)}</span>
            </div>
            <div className="d-flex justify-content-between small mb-2">
              <span>Vuelto</span>
              <span>{previaFallida ? 'se confirma al cobrar' : formatearMoneda(excedente)}</span>
            </div>

            {rechazoLocal && <div className="alert alert-warning rounded-0 py-1 px-2 small">{rechazoLocal.mensaje}</div>}

            <button
              type="button"
              className="btn btn-success w-100 rounded-0"
              disabled={!puedeCobrar || confirmandoCobro || ventaFinalizada !== null}
              aria-keyshortcuts="F9"
              onClick={cobrar}
            >
              {cobrando ? (
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
          centrado con su propio backdrop, nunca un bloque que agrande el panel. */}
      {confirmandoCobro && (
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

      {ventaFinalizada && (
        <VentaFinalizada
          numeroVisible={ventaFinalizada.numeroVisible}
          total={ventaFinalizada.total}
          medios={ventaFinalizada.medios}
          vuelto={ventaFinalizada.vuelto}
          itemsVencidos={ventaFinalizada.itemsVencidos}
          onCerrar={cerrarVentaFinalizada}
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
    </div>
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
  alEmitir?: (comprobante: ComprobanteEmitido, cliente: ClienteListado, medios: MedioPagoListado[]) => void
  alIrACerrarCaja?: (idTurno: number) => void
}

export function Pos({ alEmitir, alIrACerrarCaja }: PropsPos = {}) {
  const [searchParams] = useSearchParams()
  const { puntoVenta } = usePuntoVenta()
  const crudo = searchParams.get('idPresupuesto')
  const idPresupuesto = crudo !== null && Number.isFinite(Number(crudo)) ? Number(crudo) : null

  return (
    <PantallaPos
      key={`${idPresupuesto ?? 'libre'}:${puntoVenta?.id ?? 'sin-pv'}`}
      idPresupuesto={idPresupuesto}
      alEmitir={alEmitir}
      alIrACerrarCaja={alIrACerrarCaja}
    />
  )
}
