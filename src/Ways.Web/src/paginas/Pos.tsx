import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { clienteDeArticulos } from '../api/articulos'
import { clienteDeCaja } from '../api/caja'
import { reducirCarrito, type AccionCarrito, type LineaCarrito } from '../api/carrito'
import { clienteDeCatalogo } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import { clienteDeOfertas } from '../api/ofertas'
import { clienteDeOrganizacion } from '../api/organizacion'
import {
  aPagosDeVenta,
  calcularExcedente,
  calcularFaltante,
  filaPagoVacia,
  filasAPagosConVuelto,
  filasAPagosParaCalculo,
  medioDisponibleParaCliente,
  sumarImportes,
  validarPagosLocal,
  type FilaPago,
} from '../api/pagos'
import { aSolicitudDeVentaDesdePresupuesto, clienteDePresupuestos } from '../api/presupuestos'
import { clienteDeStock } from '../api/stock'
import type {
  ClienteListado,
  ComprobanteEmitido,
  LoteListado,
  MedioPagoAlta,
  MedioPagoListado,
  ParametroResuelto,
  PresupuestoParaVenta,
  PuntoVentaListado,
  ResultadoDeResolucion,
} from '../api/tipos'
import {
  aLineaDeCarritoDesdeEscaneo,
  aLineasDeResolucion,
  aSolicitudDeVenta,
  calcularSubtotalPrevia,
  clienteDeVentas,
  indexarResolucionPorArticulo,
  opcionDeLote,
  previaDeLinea,
  type LotesSeleccionados,
} from '../api/ventas'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

const CLAVE_PUNTO_VENTA = 'ways.pos.idPuntoVenta'

/** Piso de cantidad por línea, compartido entre el guard de edición y los atributos
 * `min`/`step` del input — evita que ambos se desincronicen (ej. el guard aceptando
 * cantidades que el input ya no permite tipear). */
const CANTIDAD_MINIMA = 0.001

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

function leerPuntoVentaGuardado(): number | null {
  try {
    const crudo = localStorage.getItem(CLAVE_PUNTO_VENTA)
    return crudo ? Number(crudo) : null
  } catch {
    return null
  }
}

function guardarPuntoVentaSeleccionado(id: number) {
  try {
    localStorage.setItem(CLAVE_PUNTO_VENTA, String(id))
  } catch {
    // localStorage puede no estar disponible (modo privado del navegador) — la selección
    // simplemente no persiste entre sesiones, el resto de la pantalla sigue funcionando.
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
  onAbierto: () => void
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
      await clienteDeCaja.abrir({
        idPuntoVenta,
        fondoInicial: fondo,
        observaciones: observaciones.trim() === '' ? null : observaciones.trim(),
      })
      onAbierto()
    } catch (e) {
      if (e instanceof ErrorApi && e.codigo === 'turno_ya_abierto') {
        // Autocuración (mismo criterio que `FormularioApertura` en Caja.tsx): otra
        // pestaña/cajero ganó la carrera de apertura entre que se abrió este gate y el click. El
        // turno YA está abierto, así que la continuación de éxito es la correcta — reintentar
        // solo repetiría el mismo 409. El carrito y los pagos siguen intactos, el cajero vuelve a
        // apretar "Cobrar" a mano (react-async-state regla 9: ningún reintento automático).
        onAbierto()
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

type PropsSelectorDeLote = {
  idPuntoVenta: number
  idArticulo: number
  nombreArticulo: string
  idLoteElegido: number | null
  disabled: boolean
  onElegir: (idLote: number | null) => void
}

/**
 * Picker de lote de una línea del carrito (stage-12-lotes-vencimientos, Slice 14, design decisión
 * 19): se pide bajo demanda (click en "Elegir lote") — el camino feliz de cero tecleo (omitir
 * `idLote`, el servidor resuelve FEFO solo) nunca dispara este fetch. `sugerido` llega ya resuelto
 * del servidor (`ReglaDeLotes.ElegirFefo`); acá solo se resalta, nunca se recalcula.
 */
function SelectorDeLote({ idPuntoVenta, idArticulo, nombreArticulo, idLoteElegido, disabled, onElegir }: PropsSelectorDeLote) {
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const [lotes, setLotes] = useState<LoteListado[] | null>(null)
  const tokenRef = useRef(0)

  // react-async-state regla 3: los saldos de lote son por punto de venta — un cambio de PV
  // invalida cualquier fetch en vuelo y cualquier lote ya cargado, nunca puede sobrevivir a la
  // selección anterior (mutation-proof-tests regla 7: probado en Pos.test.tsx resolviendo la
  // promesa vieja DESPUÉS del cambio de PV, dentro de `act`).
  useEffect(() => {
    tokenRef.current += 1
    setCargando(false)
    setError('')
    setLotes(null)
  }, [idPuntoVenta, idArticulo])

  async function cargar() {
    // El guard de reentrancia de primera línea es el `disabled` nativo del botón (más abajo):
    // JSDOM y los navegadores no despachan `click` sobre un elemento disabled, así que un
    // `cargandoRef` extra acá era inalcanzable — verificado por mutación en judgment-day
    // (slice 14, MAJOR 2b). `lotes !== null` sigue evitando un refetch tras una carga exitosa.
    if (lotes !== null) return

    const miToken = (tokenRef.current += 1)
    setCargando(true)
    setError('')

    try {
      const resultado = await clienteDeStock.listarLotes(idPuntoVenta, idArticulo)
      if (tokenRef.current !== miToken) return
      setLotes(resultado)
    } catch (e) {
      if (tokenRef.current !== miToken) return
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los lotes.')
    } finally {
      if (tokenRef.current === miToken) {
        setCargando(false)
      }
    }
  }

  if (lotes === null) {
    return (
      <div>
        <button
          type="button"
          className="btn btn-sm btn-outline-secondary rounded-0"
          disabled={disabled || cargando}
          onClick={cargar}
        >
          {cargando ? 'Cargando…' : 'Elegir lote'}
        </button>
        {error && <div className="small text-danger">{error}</div>}
      </div>
    )
  }

  if (lotes.length === 0) {
    return <span className="small text-muted">Sin lotes registrados — FEFO automático.</span>
  }

  const sugerido = lotes.find((l) => l.sugerido) ?? null
  const valorActual = idLoteElegido !== null ? String(idLoteElegido) : sugerido ? String(sugerido.idLote) : ''

  return (
    <select
      className="form-select form-select-sm rounded-0"
      aria-label={`Lote de ${nombreArticulo}`}
      value={valorActual}
      disabled={disabled}
      onChange={(e) => onElegir(e.target.value === '' ? null : Number(e.target.value))}
    >
      <option value="">FEFO automático (recomendado)</option>
      {lotes.map((l) => {
        const opcion = opcionDeLote(l)
        return (
          <option key={l.idLote} value={opcion.valor}>
            {opcion.etiqueta}
          </option>
        )
      })}
    </select>
  )
}

type PropsPantallaPos = { idPresupuesto: number | null }

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
 * al cambio de `?idPresupuesto=`.
 */
function PantallaPos({ idPresupuesto }: PropsPantallaPos) {
  const modoPresupuesto = idPresupuesto !== null
  const navigate = useNavigate()

  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [idPuntoVenta, setIdPuntoVenta] = useState<number | ''>('')
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

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

  // stage-12-lotes-vencimientos (Slice 14): elección explícita de lote por línea, indexada por
  // `idArticulo` — una línea ausente acá viaja con `idLote: null` (design decisión 19, camino
  // feliz de cero tecleo). Los saldos de lote son por punto de venta: cambiar de PV invalida
  // cualquier elección hecha (`cambiarPuntoVenta` la resetea entera).
  const [lotesSeleccionados, setLotesSeleccionados] = useState<LotesSeleccionados>({})

  const [entradaEscaneo, setEntradaEscaneo] = useState('')
  const [escaneando, setEscaneando] = useState(false)
  const [errorEscaneo, setErrorEscaneo] = useState('')
  const tokenEscaneoRef = useRef(0)

  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [errorMedios, setErrorMedios] = useState('')

  const [parametros, setParametros] = useState<{ toleranciaPago: number; vueltoMaximo: number } | null>(null)
  const [errorParametros, setErrorParametros] = useState('')
  const generacionParametrosRef = useRef(0)

  const proximaFilaPagoIdRef = useRef(1)
  const [filasPago, setFilasPago] = useState<FilaPago[]>(() => [filaPagoVacia(proximaFilaPagoIdRef.current++)])

  // react-async-state regla 9: mientras el checkout está en vuelo, TODO lo que podría
  // superponerse (escaneo, edición de carrito, cliente/punto de venta, filas de pago) queda
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

  // El punto de venta lo fija el presupuesto — nunca lo elige el cajero bajo este modo (el
  // servidor recibe `idPuntoVenta` del body igual que siempre, pero acá viaja el del presupuesto,
  // nunca uno distinto que el operador pudiera tipear).
  useEffect(() => {
    if (!presupuesto) return
    setIdPuntoVenta(presupuesto.idPuntoVenta)
  }, [presupuesto])

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

  const puntoVentaSeleccionada = puntosVenta?.find((p) => p.id === idPuntoVenta) ?? null

  const medioPorId = useMemo(() => {
    const indice: Record<number, MedioPagoListado> = {}
    for (const m of medios ?? []) indice[m.id] = m
    return indice
  }, [medios])

  // Carga inicial: puntos de venta (para el selector explícito de la operación, proposal
  // decisión 3 — sin sesión de "punto de venta actual" en el servidor), clientes (para
  // encontrar el Consumidor Final por defecto, spec: "Omitted idCliente defaults to Consumidor
  // Final") y medios de pago (panel de pagos, Slice 7). Cada uno con su propio try/catch: que
  // uno falle no bloquea a los otros.
  useEffect(() => {
    let vigente = true

    clienteDeOrganizacion
      .listarPuntosVenta()
      .then((lista) => {
        if (!vigente) return
        setPuntosVenta(lista)
        // stage-17-presupuestos-y-remitos (Slice 7): bajo `?idPresupuesto=` el punto de venta lo
        // fija el propio presupuesto (efecto dedicado más abajo) — este default NUNCA escribe
        // acá, o ganaría una carrera contra esa asignación según cuál de las dos respuestas
        // llegue primero.
        if (!modoPresupuesto) {
          const guardado = leerPuntoVentaGuardado()
          const porDefecto = lista.find((p) => p.id === guardado) ?? lista[0] ?? null
          setIdPuntoVenta(porDefecto ? porDefecto.id : '')
        }
      })
      .catch((e) => {
        if (!vigente) return
        setPuntosVenta([])
        setErrorPuntosVenta(
          e instanceof ErrorApi ? e.message : 'No se pudieron cargar los puntos de venta. Seleccioná uno para operar.',
        )
      })

    const generacionClientes = (generacionClientesRef.current += 1)

    clienteDeClientes
      .listar('', false)
      .then((pagina) => {
        if (!vigente || generacionClientesRef.current !== generacionClientes) return
        setOpcionesClientes(pagina.items)
        // stage-17-presupuestos-y-remitos (Slice 7): bajo `?idPresupuesto=` el cliente lo trae el
        // presupuesto (efecto dedicado más abajo, hidrata el registro completo por id) — el
        // default de Consumidor Final NUNCA escribe acá bajo este modo, mismo criterio que el
        // punto de venta.
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

  // react-async-state regla 2: cada cambio de punto de venta dispara la resolución de
  // tolerancia_pago/vuelto_maximo (ADR-13: punto de venta > empresa > default) — una respuesta
  // desactualizada nunca puede pisar la más reciente.
  useEffect(() => {
    if (!puntoVentaSeleccionada) {
      setParametros(null)
      setErrorParametros('')
      return
    }

    const generacion = (generacionParametrosRef.current += 1)
    let vigente = true

    Promise.all([
      api.get<ParametroResuelto>(
        `/parametros/tolerancia_pago?idEmpresa=${puntoVentaSeleccionada.idEmpresa}&idPuntoVenta=${puntoVentaSeleccionada.id}`,
      ),
      api.get<ParametroResuelto>(
        `/parametros/vuelto_maximo?idEmpresa=${puntoVentaSeleccionada.idEmpresa}&idPuntoVenta=${puntoVentaSeleccionada.id}`,
      ),
    ])
      .then(([tolerancia, vuelto]) => {
        if (!vigente || generacionParametrosRef.current !== generacion) return
        setParametros({ toleranciaPago: Number(tolerancia.valor), vueltoMaximo: Number(vuelto.valor) })
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

    // El lote elegido es propio de la línea, no de una unidad puntual: re-escanear el mismo
    // artículo (suma cantidad) no lo invalida — solo desaparece cuando la línea entera se va.
    const limpiarLoteDeFila = (idArticulo: number) =>
      setLotesSeleccionados((prev) => {
        if (!(idArticulo in prev)) return prev
        const { [idArticulo]: _omitido, ...resto } = prev
        return resto
      })

    switch (accion.tipo) {
      case 'quitarLinea':
        limpiarFila(accion.idArticulo)
        limpiarLoteDeFila(accion.idArticulo)
        break
      case 'escanear':
        limpiarFila(accion.linea.idArticulo)
        break
      case 'vaciar':
        setCantidadesEnEdicion({})
        setLotesSeleccionados({})
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

  function cambiarPuntoVenta(id: number) {
    if (cobrandoRef.current) return
    setIdPuntoVenta(id)
    guardarPuntoVentaSeleccionado(id)
    // Los saldos de lote son por punto de venta (stage-12-lotes-vencimientos, Slice 14): una
    // elección hecha contra el PV anterior no tiene sentido en el nuevo — cada `SelectorDeLote`
    // ya se resetea solo por su propio efecto, esto limpia el lado del carrito.
    setLotesSeleccionados({})
  }

  /** Elección explícita de un lote en la línea de `idArticulo`, o `null` para volver al camino
   * feliz (FEFO server-side, design decisión 19). */
  function elegirLote(idArticulo: number, idLote: number | null) {
    if (cobrandoRef.current) return
    setLotesSeleccionados((prev) => {
      if (idLote === null) {
        if (!(idArticulo in prev)) return prev
        const { [idArticulo]: _omitido, ...resto } = prev
        return resto
      }
      return { ...prev, [idArticulo]: idLote }
    })
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
          vueltoMaximo: parametros.vueltoMaximo,
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
    : lineas.length > 0 &&
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
              lotesSeleccionados,
              pagos: aPagosDeVenta(pagosConVuelto),
              direccionEntrega: null,
              observaciones: null,
            })

      const emitido = await clienteDeVentas.emitir(solicitud)
      if (generacionCobroRef.current !== miGeneracion) return

      setVentaEmitida({ comprobante: emitido, cliente: clienteSeleccionado })
      setLineas([])
      setPrecios({})
      setCantidadesEnEdicion({})
      setLotesSeleccionados({})
      setFilasPago([filaPagoVacia(proximaFilaPagoIdRef.current++)])
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
        onAbierto={() => {
          setGateTurno(false)
          setErrorCobro('')
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
                  type="text"
                  className="form-control rounded-0"
                  placeholder="Escanear o tipear un código (ej. 3*7790001234567)"
                  aria-label="Código escaneado"
                  value={entradaEscaneo}
                  disabled={escaneando || cobrando}
                  onChange={(e) => setEntradaEscaneo(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), escanear())}
                  autoFocus
                />
                <button type="button" className="btn btn-primary rounded-0" disabled={escaneando || cobrando} onClick={escanear}>
                  {escaneando ? 'Buscando…' : 'Agregar'}
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
                    <th style={{ width: 160 }}>Lote</th>
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
                          <td>—</td>
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
                                disabled={cobrando}
                                onChange={(e) => cambiarCantidad(l.idArticulo, e.target.value)}
                                onBlur={() => confirmarCantidad(l.idArticulo)}
                              />
                            </td>
                            <td>
                              {puntoVentaSeleccionada && (
                                <SelectorDeLote
                                  idPuntoVenta={puntoVentaSeleccionada.id}
                                  idArticulo={l.idArticulo}
                                  nombreArticulo={l.nombre}
                                  idLoteElegido={lotesSeleccionados[l.idArticulo] ?? null}
                                  disabled={cobrando}
                                  onElegir={(idLote) => elegirLote(l.idArticulo, idLote)}
                                />
                              )}
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
                                disabled={cobrando}
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
                className="btn btn-outline-secondary btn-sm rounded-0"
                disabled={cobrando}
                onClick={() => mutarCarrito({ tipo: 'vaciar' })}
              >
                Vaciar carrito
              </button>
            )}
          </Box>
        </div>

        <div className="col-lg-4">
          <Box titulo="Datos de la venta">
            {errorPuntosVenta && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorPuntosVenta}</div>}

            <div className="mb-3">
              <label className="form-label" htmlFor="pos-punto-venta">
                Punto de venta
              </label>
              <select
                id="pos-punto-venta"
                className="form-select rounded-0"
                value={idPuntoVenta}
                disabled={puntosVenta === null || cobrando || modoPresupuesto}
                onChange={(e) => cambiarPuntoVenta(Number(e.target.value))}
              >
                {puntosVenta === null && <option value="">Cargando…</option>}
                {puntosVenta !== null && puntosVenta.length === 0 && <option value="">Sin puntos de venta disponibles</option>}
                {puntosVenta?.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.nombre}
                  </option>
                ))}
              </select>
            </div>

            {errorClientes && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorClientes}</div>}

            <div className="mb-2">
              <label className="form-label" htmlFor="pos-cliente">
                Cliente
              </label>
              <select
                id="pos-cliente"
                className="form-select rounded-0"
                value={clienteSeleccionado?.id ?? ''}
                disabled={cobrando || modoPresupuesto}
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
                  disabled={buscandoClientes || cobrando}
                  onChange={(e) => setTerminoCliente(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), buscarClientes())}
                />
                <button
                  type="button"
                  className="btn btn-outline-primary rounded-0"
                  disabled={buscandoClientes || cobrando}
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
                      disabled={cobrando || medios === null}
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
                      disabled={cobrando}
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
                      disabled={cobrando || !medioDeFila?.requiereReferencia}
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
                      disabled={cobrando || !medioDeFila?.admiteVuelto}
                      onChange={(e) => cambiarVueltoDeFila(fila.id, e.target.value)}
                    />
                    {filasPago.length > 1 && (
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger rounded-0"
                        disabled={cobrando}
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
              disabled={cobrando}
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
    </div>
  )
}

/**
 * `/pos` (stage-17-presupuestos-y-remitos, Slice 7, design: Web composition — `react-async-state`
 * regla 8): lee `?idPresupuesto=` de la URL y remonta `PantallaPos` entera por `key` cuando
 * cambia — un `idPresupuesto` inválido o ausente se trata como el camino libre, nunca un error
 * bloqueante (la ruta sin query sigue siendo el POS de siempre).
 */
export function Pos() {
  const [searchParams] = useSearchParams()
  const crudo = searchParams.get('idPresupuesto')
  const idPresupuesto = crudo !== null && Number.isFinite(Number(crudo)) ? Number(crudo) : null

  return <PantallaPos key={idPresupuesto ?? 'libre'} idPresupuesto={idPresupuesto} />
}
