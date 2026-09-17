import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
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
import { Cargando } from '../componentes/Cargando'
import { ModalDeBusquedaDeArticulos } from '../componentes/ModalDeBusquedaDeArticulos'
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

/** Formato monetario con signo correcto para negativos (`-$50,00`, nunca `$-50,00`) — incluye el
 * símbolo `$` para que ningún call-site tenga que prefijarlo a mano y arriesgarse a mal-ubicar
 * el signo. */
function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
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
  const [fondoInicial, setFondoInicial] = useState('')
  const [observaciones, setObservaciones] = useState('')
  const [abriendo, setAbriendo] = useState(false)
  const abriendoRef = useRef(false)
  const [error, setError] = useState('')

  async function abrir() {
    // regla 9 (react-async-state): guard de reentrancia de primera línea.
    if (abriendoRef.current) return

    const fondo = Number(fondoInicial)
    if (fondoInicial.trim() === '' || !Number.isFinite(fondo) || fondo < 0) {
      setError('El fondo inicial tiene que ser un número mayor o igual a 0.')
      return
    }

    abriendoRef.current = true
    setAbriendo(true)
    setError('')

    try {
      const turno = await clienteDeCaja.abrir({
        idPuntoVenta,
        fondoInicial: fondo,
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
                <input
                  id="pos-gate-fondo-inicial"
                  type="number"
                  step="0.01"
                  min="0"
                  className="form-control rounded-0"
                  value={fondoInicial}
                  disabled={abriendo}
                  onChange={(e) => setFondoInicial(e.target.value)}
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
  // stage-pos-turno-y-foco (fix real, verificado en navegador real — ver el efecto de foco más
  // abajo): arranca en `true` a propósito, para que ESE MISMO efecto cubra también el foco
  // inicial al entrar a la pantalla — reemplaza el atributo JSX `autoFocus` que tenía este input
  // antes. Causa real encontrada: el `autoFocus` nativo (HTML/React) no hace nada cuando el
  // documento todavía no tiene foco de ventana/SO en el momento del mount — confirmado a mano en
  // Chrome real: `document.hasFocus()` da `false` justo después de una navegación dura, y en ese
  // estado un `<input autoFocus>` NUNCA queda como `document.activeElement` (se queda en `body`
  // indefinidamente, no es una carrera que se resuelva sola un instante después) — pero un
  // `elemento.focus()` imperativo, llamado en el mismo momento, SÍ lo enfoca igual, con
  // `document.hasFocus()` todavía en `false`. jsdom no reproduce esta restricción (cualquier
  // mecanismo de foco "funciona" ahí sin importar si el documento "tiene foco de ventana"), así
  // que un test en jsdom en verde nunca fue evidencia de que el `autoFocus` nativo funcionara en
  // la app real — el mismo tipo de brecha que la regla 12 de react-async-state ya documenta para
  // la restauración de foco, acá aplica también a la ADQUISICIÓN inicial. Mutación que lo prueba:
  // arrancar este ref en `false` (como antes) reabre el hueco — el test de "autofocus al entrar"
  // pasa a depender otra vez del `autoFocus` nativo que este comentario documenta como no
  // confiable.
  const focoPendienteRef = useRef(true)

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

  const [ventaEmitida, setVentaEmitida] = useState<{ comprobante: ComprobanteEmitido; cliente: ClienteListado } | null>(null)

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

  // stage-pos-turno-y-foco: consulta el turno abierto del punto de venta — mismo endpoint y
  // criterio de generación que `PantallaCaja` en Caja.tsx (react-async-state regla 2). Nunca
  // corre bajo `?idPresupuesto=` (el banner/gate de turno es una noción del camino libre).
  useEffect(() => {
    if (modoPresupuesto || !puntoVentaSeleccionada) {
      setTurno(null)
      setCargandoTurno(false)
      setErrorTurno('')
      return
    }

    const miGeneracion = (generacionTurnoRef.current += 1)
    let vigente = true
    setCargandoTurno(true)
    setErrorTurno('')

    clienteDeCaja
      .obtenerAbierto(puntoVentaSeleccionada.id)
      .then((t) => {
        if (!vigente || generacionTurnoRef.current !== miGeneracion) return
        setTurno(t)
      })
      .catch((e) => {
        if (!vigente || generacionTurnoRef.current !== miGeneracion) return
        setTurno(null)
        setErrorTurno(
          e instanceof ErrorApi ? e.message : 'No se pudo consultar el turno abierto de este punto de venta.',
        )
      })
      .finally(() => {
        if (!vigente || generacionTurnoRef.current !== miGeneracion) return
        setCargandoTurno(false)
      })

    return () => {
      vigente = false
    }
  }, [modoPresupuesto, puntoVentaSeleccionada])

  /** El turno recién confirmado (apertura propia, autocuración del gate, o el 409 safety-net) es
   * la fuente más autoritativa posible: bumpear la generación invalida cualquier `GET …/abierto`
   * que siguiera en vuelo desde antes (mismo criterio que `turnoAbierto` en Caja.tsx). */
  function turnoConfirmadoAbierto(nuevoTurno: TurnoResumen) {
    generacionTurnoRef.current += 1
    setErrorTurno('')
    setCargandoTurno(false)
    setTurno(nuevoTurno)
  }

  /** "Cerrar caja" de "Datos de la venta" — navega a `CierreDeCaja` con el `idTurno` real, nunca
   * a ciegas. `alIrACerrarCaja` es el seam del shell de escritorio (ruta `/cerrar-caja`, distinta
   * de la ruta libre `/caja/cierre` de la app web); sin el prop, navega con `useNavigate` como
   * siempre. */
  function irACerrarCaja() {
    if (!turno) return
    if (alIrACerrarCaja) {
      alIrACerrarCaja(turno.id)
    } else {
      navigate(`/caja/cierre?idTurno=${turno.id}`)
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
        return { ...f, idMedioPago: '' as const, vueltoManual: '' }
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
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, idMedioPago, vueltoManual: '' } : f)))
  }

  function cambiarImporteDeFila(id: number, importe: string) {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, importe } : f)))
  }

  function cambiarReferenciaDeFila(id: number, referencia: string) {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, referencia } : f)))
  }

  function cambiarVueltoDeFila(id: number, vueltoManual: string) {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, vueltoManual } : f)))
  }

  // El resto del estado de la venta anterior (filas de pago, overrides de edición de cantidad,
  // precios/aviso) ya queda limpio desde el propio éxito de `cobrar()` — acá solo falta
  // `errorEscaneo`, que sobrevive a una venta completa si el cajero había escaneado mal un
  // código antes de cobrar.
  function nuevaVenta() {
    if (cobrandoRef.current) return
    // stage-17-presupuestos-y-remitos (Slice 7): el presupuesto que gobernó esta venta ya quedó
    // `convertido` — "Nueva venta" navega a la ruta libre en vez de reabrir esta misma pantalla
    // remontada (react-async-state regla 8: el `key` de `Pos()` es el propio `idPresupuesto`, un
    // reset local acá dejaría el modo presupuesto pegado a una conversión ya consumida).
    if (modoPresupuesto) {
      navigate('/pos', { replace: true })
      return
    }
    setVentaEmitida(null)
    setErrorCobro('')
    setErrorEscaneo('')
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
      if (modoPresupuesto || cobrando || buscadorAbierto || gateTurno || ventaEmitida) return
      evento.preventDefault()
      setBuscadorAbierto(true)
    }
    document.addEventListener('keydown', alTeclado)
    return () => document.removeEventListener('keydown', alTeclado)
  }, [modoPresupuesto, cobrando, buscadorAbierto, gateTurno, ventaEmitida])

  // Devuelve el foco al input de código recién cuando queda realmente habilitado (react-async-state
  // regla 9): un click en "Cobrar" mientras un escaneo o un agregado del buscador siguen en vuelo
  // no debe dejarlo enfocado ni operable durante el checkout. Con `focoPendienteRef` arrancando en
  // `true` (ver su declaración más arriba), esta misma corrida cubre TAMBIÉN el foco inicial al
  // entrar a la pantalla — un solo mecanismo para las dos necesidades, en vez de un `autoFocus`
  // nativo que no es confiable en un mount real (ver el comentario del ref).
  //
  // `bloqueadoPorTurno` es parte del guard por la MISMA razón que `escaneando`/`cobrando`: el
  // input está `disabled` mientras es `true` (turno todavía cargando o confirmado cerrado) y un
  // elemento deshabilitado no puede recibir foco — sin este conjunct, el pedido de foco inicial se
  // consumía en el primer commit (turno todavía en `null`, recién arrancando la consulta) contra
  // un input ya deshabilitado, un no-op silencioso que dejaba el pedido perdido para siempre (el
  // `focoPendienteRef.current = false` ya lo había apagado) — encontrado reproduciendo en un
  // navegador real: la consulta de turno resuelve casi al instante, así que el efecto corría
  // ANTES de que `bloqueadoPorTurno` pasara a `false`, y nunca se reintentaba. Con el conjunct acá,
  // el efecto vuelve a correr cuando `bloqueadoPorTurno` cambia y recién ahí consume el pedido.
  useEffect(() => {
    if (!focoPendienteRef.current) return
    if (escaneando || cobrando || buscadorAbierto || bloqueadoPorTurno) return
    focoPendienteRef.current = false
    inputEscaneoRef.current?.focus()
  }, [escaneando, cobrando, buscadorAbierto, bloqueadoPorTurno])

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

      setVentaEmitida({ comprobante: emitido, cliente: clienteSeleccionado })
      // stage-desktop-pos: `puedeCobrar`/`precondicionesListas` ya exigieron `medios !== null`
      // para llegar hasta acá — el seam nunca dispara con la lista todavía sin cargar.
      alEmitir?.(emitido, clienteSeleccionado, medios ?? [])
      setLineas([])
      setPrecios({})
      setCantidadesEnEdicion({})
      setFilasPago([filaPagoInicial(proximaFilaPagoIdRef.current++, medios)])
      setEntradaEscaneo('')
      setTerminoCliente('')
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

  if (ventaEmitida) {
    const { comprobante, cliente } = ventaEmitida
    return (
      <div className="container-fluid py-4" key={comprobante.id}>
        <div className="row g-3">
          <div className="col-12">
            <Box titulo={`Venta ${comprobante.numeroVisible}`} variante="success">
              <p className="text-muted mb-3">
                {formatearFechaHora(comprobante.fecha)} — {etiquetaDeCliente(cliente)}
              </p>

              <div className="table-responsive">
                <table className="table table-striped table-bordered align-middle">
                  <thead>
                    <tr>
                      <th>Artículo</th>
                      <th style={{ width: 100 }}>Cantidad</th>
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
                          {item.codigoLote && <div className="small text-muted">Lote {item.codigoLote}</div>}
                          {item.loteVencido && (
                            // Escalada visual deliberada (design decisión 12: "Expired Lot Sale
                            // Warns, Never Blocks"): más fuerte que el hint pre-submit del picker
                            // (`opcionDeLote`, texto plano "vencido" en el `<option>`) porque acá
                            // la venta ya se emitió — es la última chance de que el operador se
                            // entere, nunca un bloqueo.
                            <div className="small text-danger fw-bold">⚠ Lote vencido</div>
                          )}
                        </td>
                        <td>{item.cantidad}</td>
                        <td className="text-end">{formatearMoneda(item.precioUnitario)}</td>
                        <td className="text-end">
                          {item.descuento > 0 ? (
                            <span className="badge bg-success">-{formatearMoneda(item.descuento)}</span>
                          ) : (
                            '—'
                          )}
                        </td>
                        <td className="text-end">{formatearMoneda(item.total)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              <div className="row g-3">
                <div className="col-md-6">
                  <h6>Pagos</h6>
                  <ul className="list-unstyled mb-0">
                    {comprobante.pagos.map((pago, indice) => (
                      <li key={indice}>
                        {medioPorId[pago.idMedioPago]?.nombre ?? `Medio #${pago.idMedioPago}`}:{' '}
                        {formatearMoneda(pago.importe)}
                        {pago.referencia && ` (ref. ${pago.referencia})`}
                        {pago.vuelto > 0 && ` — vuelto ${formatearMoneda(pago.vuelto)}`}
                      </li>
                    ))}
                  </ul>
                </div>
                <div className="col-md-6 text-md-end">
                  <div>Subtotal: {formatearMoneda(comprobante.subtotal)}</div>
                  <div>Descuento: {formatearMoneda(comprobante.descuentoTotal)}</div>
                  <div className="fs-5">
                    <strong>Total: {formatearMoneda(comprobante.total)}</strong>
                  </div>
                </div>
              </div>

              <button type="button" className="btn btn-primary mt-4 rounded-0" onClick={nuevaVenta}>
                Nueva venta
              </button>
            </Box>
          </div>
        </div>
      </div>
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
                  disabled={cobrando || resolviendo}
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
                  disabled={escaneando || cobrando || bloqueadoPorTurno}
                  onChange={(e) => setEntradaEscaneo(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), escanear())}
                />
                <button
                  type="button"
                  className="btn btn-primary rounded-0"
                  disabled={escaneando || cobrando || bloqueadoPorTurno}
                  onClick={escanear}
                >
                  {escaneando ? 'Buscando…' : 'Agregar'}
                </button>
                {/* "Buscar artículo" en vez de "Buscar" a secas: ya existe un botón "Buscar" en el
                    panel de cliente de esta misma pantalla (`Buscar cliente`) — un nombre
                    accesible idéntico rompería cualquier `getByRole('button', { name: 'Buscar' })`
                    (ambos quedarían matcheados a la vez, tests preexistentes incluidos). */}
                <button
                  type="button"
                  className="btn btn-outline-primary rounded-0"
                  disabled={escaneando || cobrando}
                  onClick={() => setBuscadorAbierto(true)}
                >
                  Buscar artículo
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
                                disabled={cobrando || bloqueadoPorTurno}
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
                                disabled={cobrando || bloqueadoPorTurno}
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
                disabled={cobrando || bloqueadoPorTurno}
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
                  <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorTurno}</div>
                ) : (
                  <div className="d-flex justify-content-between align-items-center">
                    {turno ? (
                      <>
                        <span className="badge bg-success">Turno abierto</span>
                        <button type="button" className="btn btn-outline-danger btn-sm rounded-0" onClick={irACerrarCaja}>
                          Cerrar caja
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
                disabled={cobrando || modoPresupuesto || bloqueadoPorTurno}
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
                  disabled={buscandoClientes || cobrando || bloqueadoPorTurno}
                  onChange={(e) => setTerminoCliente(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), buscarClientes())}
                />
                <button
                  type="button"
                  className="btn btn-outline-primary rounded-0"
                  disabled={buscandoClientes || cobrando || bloqueadoPorTurno}
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
              const vueltoMostrado = fila.vueltoManual !== '' ? fila.vueltoManual : String(pagoDeFila?.vuelto ?? 0)

              return (
                <div className="row g-2 mb-2 align-items-center" key={fila.id}>
                  <div className="col-4">
                    <select
                      className="form-select form-select-sm rounded-0"
                      aria-label="Medio de pago"
                      value={fila.idMedioPago}
                      disabled={cobrando || medios === null || bloqueadoPorTurno}
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
                    <input
                      type="number"
                      step="0.01"
                      min="0"
                      className="form-control form-control-sm rounded-0"
                      aria-label={etiquetaDeCampoFila('Importe', medioDeFila, fila.id)}
                      value={fila.importe}
                      disabled={cobrando || bloqueadoPorTurno}
                      onChange={(e) => cambiarImporteDeFila(fila.id, e.target.value)}
                    />
                  </div>
                  <div className="col-3">
                    <input
                      type="text"
                      className="form-control form-control-sm rounded-0"
                      aria-label={etiquetaDeCampoFila('Referencia', medioDeFila, fila.id)}
                      placeholder={medioDeFila?.requiereReferencia ? 'Referencia (requerida)' : 'Referencia'}
                      value={fila.referencia}
                      disabled={cobrando || bloqueadoPorTurno || !medioDeFila?.requiereReferencia}
                      onChange={(e) => cambiarReferenciaDeFila(fila.id, e.target.value)}
                    />
                  </div>
                  <div className="col-2 d-flex align-items-center gap-1">
                    <input
                      type="number"
                      step="0.01"
                      min="0"
                      className="form-control form-control-sm rounded-0"
                      aria-label={etiquetaDeCampoFila('Vuelto', medioDeFila, fila.id)}
                      value={vueltoMostrado}
                      disabled={cobrando || bloqueadoPorTurno || !medioDeFila?.admiteVuelto}
                      onChange={(e) => cambiarVueltoDeFila(fila.id, e.target.value)}
                    />
                    {filasPago.length > 1 && (
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger rounded-0"
                        disabled={cobrando || bloqueadoPorTurno}
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
              disabled={cobrando || bloqueadoPorTurno}
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

            <button type="button" className="btn btn-success w-100 rounded-0" disabled={!puedeCobrar} onClick={cobrar}>
              {cobrando ? 'Cobrando…' : 'Cobrar'}
            </button>
          </Box>
        </div>
      </div>

      {buscadorAbierto && !modoPresupuesto && (
        <ModalDeBusquedaDeArticulos
          idListaPrecio={clienteSeleccionado?.idListaPrecio ?? null}
          idEmpresa={puntoVentaSeleccionada?.idEmpresa ?? null}
          puedeAgregar={!bloqueadoPorTurno}
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
