import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { MouseEvent } from 'react'
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router'
import {
  aSolicitudDeCompra,
  calcularTotalesDeCompra,
  clienteDeCompras,
  discriminaIvaEfectivo,
  esBorradorCargadoPorElAsistente,
  etiquetaDeEstadoCompra,
  itemAFormulario,
  ivaImpresoDesdeDetalle,
  lineaCompletaParaEnvio,
  lineaDeCompraVacia,
  lineaDesdeCoberturaDeOrden,
  lineaFormularioACalculo,
  normalizarFilas,
  completarConArticulo,
  percepcionesAplicables,
  sePuedePagarLaCompra,
  type EncabezadoDeCompraFormulario,
  type LineaDeCompraFormulario,
} from '../api/compras'
import { clienteDeCatalogosFiscales } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeGastos, clienteDeGastosDeAdministracion } from '../api/gastos'
import { clienteDeOrdenesDeCompra } from '../api/ordenesDeCompra'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDePrecios } from '../api/precios'
import { ROL } from '../api/tipos'
import type {
  AlicuotaIvaListado,
  CompraDetalle,
  EmpresaListado,
  GastoDeAdministracionListado,
  ListaPrecioListado,
  OrdenDeCompraDetalle,
  PaginaDe,
  ProveedorListado,
  PuntoVentaListado,
  ResultadoAnulacion,
  ResultadoAplicarPrecio,
  ResultadoDePagoDeCompra,
  TipoComprobanteListado,
  TipoDePercepcion,
} from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { ModalDePagoDeCompra } from '../componentes/ModalDePagoDeCompra'
import { formatearImporte } from '../formato/importes'
import { DesgloseDeIvaDeCompra } from './DesgloseDeIvaDeCompra'
import { useMontado } from './familias/useMontado'
import {
  alElegirProveedor,
  alicuotaDeEmpresaParaCompra,
  baseDePercepcion,
  conSugerenciasDePercepcion,
  editarPercepcion,
  percepcionesDesdeDetalle,
  percepcionManual,
  resolverPercepciones,
  type ReferenciaDePercepciones,
} from './sugerenciasDePercepcion'
import { PercepcionesDeCompra } from './PercepcionesDeCompra'
import { useUnidadesDeVentaDeLineas } from './useUnidadesDeVentaDeLineas'
import { etiquetaDeProveedor } from './etiquetaDeProveedor'
import { AvisoDeNumeroDuplicado } from './compras/AvisoDeNumeroDuplicado'
import { CampoNumeroDeComprobante } from './compras/CampoNumeroDeComprobante'
import { useDuplicadosDeCompra } from './compras/useDuplicadosDeCompra'
import { GrillaDeItemsDeCompra } from './compras/GrillaDeItemsDeCompra'
import {
  hayCambiosSinGuardar,
  huellaDeFormulario,
  lineasIncompletas,
  mensajeDeLoQueImpideGuardar,
} from './compras/validacionDeCompra'
import { CampoFecha } from '../componentes/CampoFecha'
import { fechaHoraATexto } from '../formato/fechas'

function formatearMoneda(valor: number | null): string {
  return formatearImporte(valor, { simbolo: true })
}

function formatearFechaHora(iso: string | null): string {
  return iso ? fechaHoraATexto(iso) : '—'
}

/** `gasto.fecha` es un `timestamptz` (instante) — el `<input type="date">` de
 * `fechaComprobante` necesita el día LOCAL, mismo criterio que el resto de esta web (nunca el día
 * UTC, que puede desplazarse un día en ART). */
function fechaLocalDesdeIso(iso: string): string {
  const d = new Date(iso)
  const anio = d.getFullYear()
  const mes = String(d.getMonth() + 1).padStart(2, '0')
  const dia = String(d.getDate()).padStart(2, '0')
  return `${anio}-${mes}-${dia}`
}

function encabezadoVacio(): EncabezadoDeCompraFormulario {
  return {
    idProveedor: '',
    idTipoComprobante: '',
    idPuntoVenta: '',
    numeroExterno: '',
    fechaComprobante: '',
    observaciones: '',
    idOrdenCompra: null,
    discriminaIva: null,
    ivaImpreso: {},
    preciosIncluyenIva: false,
    percepciones: [],
    percepcionesDescartadas: [],
  }
}

function encabezadoDesdeDetalle(c: CompraDetalle): EncabezadoDeCompraFormulario {
  return {
    idProveedor: c.idProveedor,
    idTipoComprobante: c.idTipoComprobante,
    idPuntoVenta: c.idPuntoVenta,
    numeroExterno: c.numeroExterno ?? '',
    fechaComprobante: c.fechaComprobante ?? '',
    observaciones: c.observaciones ?? '',
    idOrdenCompra: c.idOrdenCompra,
    discriminaIva: c.discriminaIva,
    ivaImpreso: ivaImpresoDesdeDetalle(c),
    preciosIncluyenIva: c.preciosIncluyenIva,
    percepciones: percepcionesDesdeDetalle(c.percepciones),
    percepcionesDescartadas: [],
  }
}

// ---- Tabla de items de solo lectura (compra confirmada/anulada) -------------------------------

function TablaDeItemsDeSoloLectura({ compra }: { compra: CompraDetalle }) {
  return (
    <div className="table-responsive">
      <table className="table table-sm table-striped table-bordered align-middle">
        <thead>
          <tr>
            <th>Artículo / concepto</th>
            <th>Lote</th>
            <th>Vencimiento</th>
            <th className="text-end">Cantidad</th>
            <th className="text-end">Costo unitario</th>
            <th className="text-end">Descuento</th>
            <th className="text-end">IVA %</th>
            <th className="text-end">Total</th>
            <th>Actualiza costo</th>
            <th className="text-end">Precio sugerido</th>
          </tr>
        </thead>
        <tbody>
          {compra.items.map((item) => (
            <tr key={item.orden}>
              <td>
                {item.idArticulo === null && <span className="badge text-bg-info me-1">Concepto</span>}
                {item.descripcion}
              </td>
              <td>{item.codigoLote ?? '—'}</td>
              <td>{item.fechaVencimiento ?? '—'}</td>
              <td className="text-end">{item.cantidad}</td>
              <td className="text-end">{formatearMoneda(item.costoUnitario)}</td>
              <td className="text-end">{formatearMoneda(item.descuento)}</td>
              <td className="text-end">{item.porcentajeIva}%</td>
              <td className="text-end">{formatearMoneda(item.total)}</td>
              <td>{item.actualizaCosto ? 'Sí' : 'No'}</td>
              <td className="text-end">{formatearMoneda(item.precioSugerido)}</td>
            </tr>
          ))}
          {compra.items.length === 0 && (
            <tr>
              <td colSpan={10} className="text-center text-muted py-3">
                Esta compra no tiene items.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}

// ---- Panel de aplicar precio sugerido ----------------------------------------------------------

type PropsPanelAplicarPrecios = {
  idCompra: number
  listas: ListaPrecioListado[]
  disabled: boolean
  onAntesDeEscribir: () => void
  onAplicado: (resultados: ResultadoAplicarPrecio[]) => void
  onError: () => void
}

function PanelAplicarPrecios({ idCompra, listas, disabled, onAntesDeEscribir, onAplicado, onError }: PropsPanelAplicarPrecios) {
  const [idListaPrecio, setIdListaPrecio] = useState<number | ''>(listas[0]?.id ?? '')
  const [confirmarReemplazo, setConfirmarReemplazo] = useState(false)
  const [aplicando, setAplicando] = useState(false)
  const aplicandoRef = useRef(false)
  const [error, setError] = useState('')
  const [resultados, setResultados] = useState<ResultadoAplicarPrecio[] | null>(null)

  async function aplicar() {
    // regla 9: guard de reentrancia de primera línea.
    if (aplicandoRef.current) return
    if (idListaPrecio === '') {
      setError('Elegí una lista de precios.')
      return
    }

    aplicandoRef.current = true
    setAplicando(true)
    setError('')
    // El flag de "en vuelo" también se levanta en el padre (`aplicandoPrecios`, plegado en
    // `ocupado`): mientras esta escritura está en curso, anular (y cualquier otra acción que la
    // pudiera supersedear) queda bloqueado — regla 9, gate simétrico con anulando.
    onAntesDeEscribir()

    try {
      const resultado = await clienteDeCompras.aplicarPrecios(idCompra, { idListaPrecio, confirmarReemplazo })
      aplicandoRef.current = false
      setAplicando(false)
      setResultados(resultado)
      // regla 6: un 2xx nunca se reporta como fallo — incluso si alguna línea individual vino con
      // `aplicado: false`, la respuesta 2xx en sí es un éxito de la operación (partial success es
      // el contrato honesto, design decisión 8).
      onAplicado(resultado)
    } catch (e) {
      aplicandoRef.current = false
      setAplicando(false)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo aplicar el precio sugerido.')
      onError()
    }
  }

  return (
    <div className="border p-3 mt-3">
      <strong>Aplicar precio sugerido</strong>
      {error && <div className="alert alert-danger py-1 px-2 small mt-2">{error}</div>}
      <div className="row g-2 align-items-end mt-1">
        <div className="col-md-4">
          <label className="form-label" htmlFor="compra-lista-precio">
            Lista de precios
          </label>
          <select
            id="compra-lista-precio"
            className="form-select"
            value={idListaPrecio}
            disabled={disabled || aplicando}
            onChange={(e) => setIdListaPrecio(e.target.value === '' ? '' : Number(e.target.value))}
          >
            <option value="">Elegir…</option>
            {listas.map((l) => (
              <option key={l.id} value={l.id}>
                {l.nombre}
              </option>
            ))}
          </select>
        </div>
        <div className="col-md-5">
          <div className="form-check">
            <input
              id="compra-confirmar-reemplazo"
              type="checkbox"
              className="form-check-input"
              checked={confirmarReemplazo}
              disabled={disabled || aplicando}
              onChange={(e) => setConfirmarReemplazo(e.target.checked)}
            />
            <label className="form-check-label" htmlFor="compra-confirmar-reemplazo">
              Confirmar reemplazo de un precio pendiente existente
            </label>
          </div>
        </div>
        <div className="col-md-3">
          <button type="button" className="btn btn-primary w-100" disabled={disabled || aplicando} onClick={aplicar}>
            {aplicando ? 'Aplicando…' : 'Aplicar'}
          </button>
        </div>
      </div>

      {resultados && (
        <table className="table table-sm table-bordered mt-3 mb-0">
          <thead>
            <tr>
              <th>Línea</th>
              <th>Artículo</th>
              <th>Resultado</th>
              <th className="text-end">Precio</th>
            </tr>
          </thead>
          <tbody>
            {resultados.map((r) => (
              <tr key={r.orden}>
                <td>{r.orden}</td>
                <td>#{r.idArticulo}</td>
                <td>{r.aplicado ? 'Aplicado' : (r.error ?? 'No aplicado')}</td>
                <td className="text-end">{r.precio === null ? '—' : formatearMoneda(r.precio)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}

// ---- Pantalla principal -------------------------------------------------------------------------

type PropsPantalla = { idCompra: number | null; idOrdenCompra: number | null; idDesdeGasto: number | null }

/** Lo que `guardar` deja en el estado de la navegación al aterrizar en el borrador recién creado:
 * la pantalla de destino arranca con el resultado de la acción a la vista. */
type EstadoDeNavegacion = {
  aviso?: string
  advertencia?: string
  error?: string
  gastoVinculado?: boolean
  errorVincularGasto?: string
}

/** Remontada por `key={idCompra ?? 'nuevo-' + (idOrdenCompra ?? 's')}` (react-async-state regla 8)
 * — ningún estado de acá (borrador en edición, paneles de confirmar/anular) sobrevive a un cambio
 * de compra. */
function PantallaCompraEditor({ idCompra, idOrdenCompra, idDesdeGasto }: PropsPantalla) {
  const navigate = useNavigate()
  const { usuario } = useAuth()
  const puedeEscribir = usuario !== null && usuario.rolId === ROL.Admin
  // Crear artículos es del Admin (`/articulos` es admin-only): se pide aparte de escribir compras para que ampliar una no abra la otra.
  const puedeCrearArticulos = usuario !== null && usuario.rolId === ROL.Admin

  const esNuevo = idCompra === null

  // ---- referencia (proveedores/tipos/alícuotas/puntos de venta/listas) ------------------------
  const [proveedores, setProveedores] = useState<ProveedorListado[] | null>(null)
  const [tipos, setTipos] = useState<TipoComprobanteListado[] | null>(null)
  const [alicuotas, setAlicuotas] = useState<AlicuotaIvaListado[] | null>(null)
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [empresas, setEmpresas] = useState<EmpresaListado[]>([])
  const [errorEmpresas, setErrorEmpresas] = useState(false)
  const [listasPrecio, setListasPrecio] = useState<ListaPrecioListado[] | null>(null)
  const [errorReferencia, setErrorReferencia] = useState('')

  useEffect(() => {
    let vigente = true
    const marcarError = (mensaje: string) => {
      if (vigente) setErrorReferencia((prev) => (prev ? `${prev} ${mensaje}` : mensaje))
    }

    api
      .get<PaginaDe<ProveedorListado>>('/proveedores?tamanio=200')
      .then((p) => vigente && setProveedores(p.items))
      .catch(() => {
        setProveedores([])
        marcarError('No se pudieron cargar los proveedores.')
      })

    clienteDeCatalogosFiscales
      .tiposComprobante()
      .then((lista) => vigente && setTipos(lista.filter((t) => t.clase === 'Compra' && t.activo)))
      .catch(() => {
        setTipos([])
        marcarError('No se pudieron cargar los tipos de comprobante.')
      })

    clienteDeCatalogosFiscales
      .alicuotasIva()
      .then((lista) => vigente && setAlicuotas(lista))
      .catch(() => {
        setAlicuotas([])
        marcarError('No se pudieron cargar las alícuotas de IVA.')
      })

    clienteDeOrganizacion
      .listarPuntosVenta()
      .then((lista) => vigente && setPuntosVenta(lista))
      .catch(() => {
        setPuntosVenta([])
        marcarError('No se pudieron cargar los puntos de venta.')
      })

    clienteDeOrganizacion
      .listarEmpresas()
      .then((lista) => vigente && setEmpresas(lista))
      .catch(() => {
        // Las alícuotas de percepción de la empresa solo sirven para pre-cargar la percepción: sin
        // ellas se carga a mano, así que no bloquea el resto de la pantalla; se avisa junto al
        // bloque de percepciones.
        if (vigente) setErrorEmpresas(true)
      })

    clienteDePrecios
      .listasDePrecio()
      .then((lista) => vigente && setListasPrecio(lista.filter((l) => l.activo)))
      .catch(() => {
        setListasPrecio([])
        // La lista de precios solo hace falta para "aplicar precio sugerido" — no bloquea el
        // resto de la pantalla, a diferencia de proveedores/tipos/alícuotas/puntos de venta.
      })

    return () => {
      vigente = false
    }
  }, [])

  const referenciaLista = proveedores !== null && tipos !== null && alicuotas !== null && puntosVenta !== null
  const referenciaOk = referenciaLista && errorReferencia === ''

  const porcentajePorAlicuota = useMemo(() => {
    const indice: Record<number, number> = {}
    for (const a of alicuotas ?? []) indice[a.id] = a.porcentaje
    return indice
  }, [alicuotas])

  // ---- carga de la compra existente (edición) --------------------------------------------------
  const [compra, setCompra] = useState<CompraDetalle | null>(null)
  const [cargandoCompra, setCargandoCompra] = useState(!esNuevo)
  const [errorCompra, setErrorCompra] = useState('')
  const generacionRef = useRef(0)

  useEffect(() => {
    if (esNuevo || idCompra === null) return
    let vigente = true
    const miGeneracion = (generacionRef.current += 1)
    setCargandoCompra(true)
    setErrorCompra('')

    clienteDeCompras
      .obtener(idCompra)
      .then((detalle) => {
        if (!vigente || generacionRef.current !== miGeneracion) return
        setCompra(detalle)
      })
      .catch((e) => {
        if (!vigente || generacionRef.current !== miGeneracion) return
        setCompra(null)
        setErrorCompra(e instanceof ErrorApi ? e.message : 'No se pudo cargar la compra.')
      })
      .finally(() => {
        if (!vigente || generacionRef.current !== miGeneracion) return
        setCargandoCompra(false)
      })

    return () => {
      vigente = false
    }
  }, [esNuevo, idCompra])

  // ---- pre-carga desde una orden de compra (stage-16-ordenes-de-compra, Slice 6): "Registrar
  // recepción" de OrdenDeCompra.tsx navega acá con `?idOrdenCompra=`; se resuelve UNA vez, solo
  // para un borrador NUEVO (una compra existente ya trae su propio idOrdenCompra, decisión
  // congelada al confirmar) — design: "pre-fills idProveedor, idPuntoVenta, idOrdenCompra and one
  // line per artículo with Pendiente > 0". -----------------------------------------------------
  const [ordenParaPrecargar, setOrdenParaPrecargar] = useState<OrdenDeCompraDetalle | null>(null)
  const [errorOrdenParaPrecargar, setErrorOrdenParaPrecargar] = useState('')

  useEffect(() => {
    if (!esNuevo || idOrdenCompra === null) return
    let vigente = true

    clienteDeOrdenesDeCompra
      .obtener(idOrdenCompra)
      .then((detalle) => {
        if (!vigente) return
        setOrdenParaPrecargar(detalle)
      })
      .catch((e) => {
        if (!vigente) return
        setErrorOrdenParaPrecargar(e instanceof ErrorApi ? e.message : 'No se pudo cargar la orden de compra a recepcionar.')
      })

    return () => {
      vigente = false
    }
  }, [esNuevo, idOrdenCompra])

  // ---- pre-carga desde un gasto (stage-gasto-a-compra, PR4): "Crear compra" de Gastos.tsx navega
  // acá con `?desdeGasto=` — se resuelve UNA vez, para un borrador NUEVO, y el vínculo se re-arma
  // automáticamente al confirmar (ver `confirmar` más abajo). El id sobrevive al remontaje
  // nuevo→existente porque `guardarBorrador` lo reenvía en el `navigate` de creación. ------------
  const [gastoOrigen, setGastoOrigen] = useState<GastoDeAdministracionListado | null>(null)
  const [errorGastoOrigen, setErrorGastoOrigen] = useState('')

  useEffect(() => {
    if (idDesdeGasto === null) return
    let vigente = true

    clienteDeGastosDeAdministracion
      .obtener(idDesdeGasto)
      .then((gasto) => {
        if (!vigente) return
        setGastoOrigen(gasto)
      })
      .catch((e) => {
        if (!vigente) return
        setErrorGastoOrigen(e instanceof ErrorApi ? e.message : 'No se pudo cargar el gasto de origen.')
      })

    return () => {
      vigente = false
    }
  }, [idDesdeGasto])

  // ---- formulario editable (nuevo o borrador existente) ------------------------------------------
  const [encabezado, setEncabezado] = useState<EncabezadoDeCompraFormulario>(encabezadoVacio())
  const proximaClaveRef = useRef(1)
  const [lineas, setLineas] = useState<LineaDeCompraFormulario[]>(() => [lineaDeCompraVacia(0)])
  useUnidadesDeVentaDeLineas(lineas, setLineas, esNuevo || compra?.estado === 'Borrador', completarConArticulo)

  useEffect(() => {
    if (ordenParaPrecargar === null) return
    setEncabezado((prev) => ({
      ...prev,
      idProveedor: ordenParaPrecargar.idProveedor,
      idPuntoVenta: ordenParaPrecargar.idPuntoVenta,
      idOrdenCompra: ordenParaPrecargar.id,
    }))
    const pendientes = ordenParaPrecargar.cobertura.filter((c) => c.pendiente > 0)
    const filas = pendientes.map((c) => lineaDesdeCoberturaDeOrden(proximaClaveRef.current++, c, ordenParaPrecargar.items))
    setLineas(normalizarFilas(filas, proximaClaveRef.current++))
  }, [ordenParaPrecargar])

  // Prefill desde el gasto de origen: SOLO para un borrador nuevo (una compra ya creada/en edición
  // no vuelve a pisar lo que el operador ya tocó — mismo criterio que ordenParaPrecargar arriba).
  useEffect(() => {
    if (!esNuevo || gastoOrigen === null) return
    setEncabezado((prev) => ({
      ...prev,
      idProveedor: gastoOrigen.idProveedor ?? prev.idProveedor,
      idPuntoVenta: gastoOrigen.idPuntoVenta ?? prev.idPuntoVenta,
      numeroExterno: gastoOrigen.numeroFactura ?? prev.numeroExterno,
      fechaComprobante: fechaLocalDesdeIso(gastoOrigen.fecha),
      observaciones: gastoOrigen.concepto,
    }))
  }, [esNuevo, gastoOrigen])

  useEffect(() => {
    if (compra === null) return
    setEncabezado(encabezadoDesdeDetalle(compra))
    const filas = compra.items.map((item) => itemAFormulario(proximaClaveRef.current++, item))
    setLineas(normalizarFilas(filas, proximaClaveRef.current++))
  }, [compra])

  const tipoSeleccionado = (tipos ?? []).find((t) => t.id === encabezado.idTipoComprobante) ?? null
  const discriminaIva = discriminaIvaEfectivo(tipoSeleccionado, encabezado.discriminaIva)
  const registraLibroIva = tipoSeleccionado?.registraLibroIva ?? false
  const referenciaDePercepciones = useMemo<ReferenciaDePercepciones>(
    () => ({ proveedores: proveedores ?? [], tipos: tipos ?? [], puntosVenta: puntosVenta ?? [], empresas }),
    [proveedores, tipos, puntosVenta, empresas],
  )

  // Un borrador NUEVO que viene de una orden de compra o de un gasto fija proveedor y punto de venta
  // por efecto, antes de que la referencia termine de cargar: el modo de precios del proveedor se
  // aplica UNA vez, cuando su lista llega, por el mismo camino que elegirlo a mano. Si la orden ya
  // trae costos estimados, el modo actual se conserva.
  const origenAplicadoRef = useRef(false)
  useEffect(() => {
    if (!esNuevo || origenAplicadoRef.current || proveedores === null) return
    if (ordenParaPrecargar === null && gastoOrigen === null) return
    origenAplicadoRef.current = true
    const conservarModo = ordenParaPrecargar?.cobertura.some((c) => c.pendiente > 0 && c.costoEstimado !== null) ?? false
    setEncabezado((prev) => alElegirProveedor(prev, prev.idProveedor, referenciaDePercepciones, conservarModo))
  }, [esNuevo, proveedores, ordenParaPrecargar, gastoOrigen, referenciaDePercepciones])

  // Con un único punto de venta no hay nada que elegir: se preselecciona UNA vez, cuando llega su
  // lista, y solo si el borrador nuevo todavía no tiene uno (orden de compra o gasto de origen).
  const puntoVentaPreseleccionadoRef = useRef(false)
  useEffect(() => {
    if (!esNuevo || puntoVentaPreseleccionadoRef.current || puntosVenta === null) return
    puntoVentaPreseleccionadoRef.current = true
    if (puntosVenta.length !== 1) return
    const unico = puntosVenta[0].id
    setEncabezado((prev) =>
      prev.idPuntoVenta === ''
        ? conSugerenciasDePercepcion({ ...prev, idPuntoVenta: unico }, referenciaDePercepciones)
        : prev,
    )
  }, [esNuevo, puntosVenta, referenciaDePercepciones])

  // Las sugerencias dependen de datos que llegan por separado (proveedores, tipos, puntos de venta,
  // empresas): cada vez que cambia la referencia de un borrador NUEVO se recomputan. Las filas que el
  // operador tocó o quitó se respetan; un borrador existente nunca recibe sugerencias nuevas.
  useEffect(() => {
    if (!esNuevo) return
    setEncabezado((prev) => conSugerenciasDePercepcion(prev, referenciaDePercepciones))
  }, [esNuevo, referenciaDePercepciones])
  const nombrePorAlicuota = useMemo(
    () => Object.fromEntries((alicuotas ?? []).map((a) => [a.id, a.nombre])) as Record<number, string>,
    [alicuotas],
  )

  // El mirror de totales se calcula SOLO sobre las líneas completas (`lineaCompletaParaEnvio`):
  // es la misma fuente de verdad que decide qué líneas viajan en `aSolicitudDeCompra` — una fila a
  // medio llenar nunca debe sumar al Subtotal en pantalla, porque no se guarda: bloquea el guardado.
  const lineasCompletas = useMemo(() => lineas.filter(lineaCompletaParaEnvio), [lineas])
  const cantidadDeIncompletas = useMemo(() => lineasIncompletas(lineas).length, [lineas])
  const huellaGuardada = useMemo(
    () => (compra === null ? null : huellaDeFormulario(encabezadoDesdeDetalle(compra), compra.items.map((i) => itemAFormulario(0, i)))),
    [compra],
  )
  const calculo = useMemo(
    () => lineasCompletas.map((l) => lineaFormularioACalculo(l, porcentajePorAlicuota)),
    [lineasCompletas, porcentajePorAlicuota],
  )
  const preciosFinales = discriminaIva && encabezado.preciosIncluyenIva
  // La base de las percepciones automáticas sale de los totales SIN percepciones; recién con las
  // filas ya resueltas se calcula el total del comprobante.
  const basePercepcion = useMemo(
    () => baseDePercepcion(calcularTotalesDeCompra(calculo, discriminaIva, encabezado.ivaImpreso, preciosFinales), discriminaIva),
    [calculo, discriminaIva, encabezado.ivaImpreso, preciosFinales],
  )
  const percepcionesResueltas = useMemo(
    () => resolverPercepciones(encabezado.percepciones, basePercepcion),
    [encabezado.percepciones, basePercepcion],
  )
  const percepcionesVisibles = useMemo(
    () => percepcionesAplicables(percepcionesResueltas, registraLibroIva, discriminaIva),
    [percepcionesResueltas, registraLibroIva, discriminaIva],
  )
  const totales = useMemo(
    () => calcularTotalesDeCompra(calculo, discriminaIva, encabezado.ivaImpreso, preciosFinales, percepcionesVisibles),
    [calculo, discriminaIva, encabezado.ivaImpreso, preciosFinales, percepcionesVisibles],
  )
  const tiposAgregables = useMemo(
    () =>
      (['iibb', ...(discriminaIva ? (['iva'] as const) : [])] as TipoDePercepcion[]).filter(
        (tipo) => !percepcionesVisibles.some((p) => p.tipo === tipo),
      ),
    [discriminaIva, percepcionesVisibles],
  )

  const montado = useMontado()
  const ubicacion = useLocation()
  // Lo que dejó la pantalla anterior al navegar a este borrador recién guardado (confirmación
  // lograda, o guardado sin confirmar): se lee una sola vez y se limpia del historial.
  const [estadoInicial] = useState<EstadoDeNavegacion>(() => (ubicacion.state as EstadoDeNavegacion | null) ?? {})

  const [guardando, setGuardando] = useState(false)
  const [accion, setAccion] = useState<'guardar' | 'borrador' | null>(null)
  const guardandoRef = useRef(false)
  // Códigos de proveedor que la grilla está consultando: guardar espera a que terminen, así una fila
  // nunca se guarda como concepto y después la muta una respuesta tardía.
  const consultandoCodigosRef = useRef(false)
  const [esperandoCodigos, setEsperandoCodigos] = useState(false)
  // Sube cuando el operador cambia el proveedor (no cuando lo fija una orden o un borrador al cargar).
  const [cambiosDeProveedor, setCambiosDeProveedor] = useState(0)

  const alCambiarConsultasEnVuelo = useCallback((hay: boolean) => {
    consultandoCodigosRef.current = hay
    if (!hay) setEsperandoCodigos(false)
  }, [])
  // El alta de un artículo desde una fila deja inerte la pantalla de atrás: mientras está abierta no se guarda la compra.
  const creandoArticuloRef = useRef(false)
  const [creandoArticulo, setCreandoArticulo] = useState(false)
  const alCambiarAltaDeArticulo = useCallback((abierta: boolean) => {
    creandoArticuloRef.current = abierta
    setCreandoArticulo(abierta)
  }, [])
  const [error, setError] = useState(estadoInicial.error ?? '')
  const [advertencia, setAdvertencia] = useState(estadoInicial.advertencia ?? '')
  const [aviso, setAviso] = useState(estadoInicial.aviso ?? '')
  // Acción con la que se intentó guardar y faltaba algo: el mensaje y las celdas marcadas se recalculan
  // en vivo, así desaparecen solos cuando el operador completa o quita lo que faltaba.
  const [intentoFallido, setIntentoFallido] = useState<'guardar' | 'borrador' | null>(null)
  const [descartarAbierto, setDescartarAbierto] = useState(false)
  const [disparadorDeDescartar, setDisparadorDeDescartar] = useState<HTMLElement | null>(null)
  // Proveedor elegido que espera confirmación porque hay filas resueltas por código que se van a perder.
  const [proveedorPendiente, setProveedorPendiente] = useState<string | null>(null)
  const [disparadorDeProveedor, setDisparadorDeProveedor] = useState<HTMLElement | null>(null)

  useEffect(() => {
    if (ubicacion.state !== null) navigate(`${ubicacion.pathname}${ubicacion.search}`, { replace: true, state: null })
    // Solo al montar: el estado ya quedó copiado arriba.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // ---- vínculo automático con el gasto de origen, DESPUÉS de confirmar (stage-gasto-a-compra,
  // PR4): un fallo acá NUNCA revierte la confirmación — la compra queda confirmada igual, con un
  // botón para reintentar el vínculo solo. --------------------------------------------------------
  const [gastoVinculado, setGastoVinculado] = useState(estadoInicial.gastoVinculado ?? false)
  const [vinculandoGasto, setVinculandoGasto] = useState(false)
  const vinculandoGastoRef = useRef(false)
  const [errorVincularGasto, setErrorVincularGasto] = useState(estadoInicial.errorVincularGasto ?? '')

  async function vincularGasto(idCompraConfirmada: number): Promise<string | null> {
    if (idDesdeGasto === null) return null
    try {
      await clienteDeGastos.vincularCompra(idDesdeGasto, idCompraConfirmada)
      return null
    } catch (e) {
      return e instanceof ErrorApi ? e.message : 'No se pudo vincular el gasto de origen a esta compra.'
    }
  }

  async function reintentarVinculoConGasto(idCompraConfirmada: number) {
    if (vinculandoGastoRef.current) return
    vinculandoGastoRef.current = true
    setVinculandoGasto(true)
    setErrorVincularGasto('')

    try {
      const errorDelVinculo = await vincularGasto(idCompraConfirmada)
      if (errorDelVinculo === null) setGastoVinculado(true)
      else setErrorVincularGasto(errorDelVinculo)
    } finally {
      vinculandoGastoRef.current = false
      setVinculandoGasto(false)
    }
  }

  const [anulando, setAnulando] = useState(false)
  const anulandoRef = useRef(false)
  const [panelAnularAbierto, setPanelAnularAbierto] = useState(false)
  const [confirmadoParaAnular, setConfirmadoParaAnular] = useState(false)
  const [errorAnular, setErrorAnular] = useState('')
  const [resultadoAnulacion, setResultadoAnulacion] = useState<ResultadoAnulacion | null>(null)

  // Descartar el borrador (borrado físico): puerta de confirmación + escritura propia. La puerta sigue abierta durante
  // todo el DELETE, así que `ocupado` deja inerte el resto de la pantalla hasta que la respuesta llega y nada puede
  // reemplazar lo que ella escribe; la respuesta navega, por eso pregunta si la pantalla sigue montada. Su rechazo se muestra en el `error` de la
  // pantalla, que ningún otro flujo escribe mientras éste está en curso.
  const [descartandoBorrador, setDescartandoBorrador] = useState(false)
  const descartandoBorradorRef = useRef(false)
  const [descartarBorradorAbierto, setDescartarBorradorAbierto] = useState(false)
  const [disparadorDeDescartarBorrador, setDisparadorDeDescartarBorrador] = useState<HTMLElement | null>(null)

  const [pagoAbierto, setPagoAbierto] = useState(false)

  // El pago ya trae el pagado y el saldo pendiente leídos bajo el lock de la compra: se vuelcan al
  // detalle en pantalla sin volver a pedirlo, así no hay una segunda lectura que pueda fallar o
  // llegar vieja después de un pago que sí se registró.
  function alPagar(resultado: ResultadoDePagoDeCompra) {
    setPagoAbierto(false)
    setAviso(`Pago registrado: ${formatearMoneda(resultado.gasto.importe)}.`)
    setCompra((prev) =>
      prev === null ? prev : { ...prev, pagado: resultado.pagado, saldoPendiente: resultado.saldoPendiente },
    )
  }

  // El panel de aplicar precio sugerido es local a `PanelAplicarPrecios`, pero su "en vuelo" se
  // levanta acá (regla 9): sin esto, `ocupado` no lo ve y anular puede dispararse con un aplicar
  // todavía en curso — el gate queda asimétrico.
  const [aplicandoPrecios, setAplicandoPrecios] = useState(false)

  // Las puertas de "descartar cambios" y de "cambiar el proveedor" dejan inerte el resto de la
  // pantalla mientras están abiertas.
  const ocupado =
    guardando ||
    anulando ||
    aplicandoPrecios ||
    vinculandoGasto ||
    descartarBorradorAbierto ||
    descartarAbierto ||
    proveedorPendiente !== null

  function cambiarTipo(valor: string) {
    if (ocupado) return
    const idTipo = valor === '' ? '' : Number(valor)
    const tipo = (tipos ?? []).find((t) => t.id === idTipo) ?? null
    const anterior = (tipos ?? []).find((t) => t.id === encabezado.idTipoComprobante) ?? null
    // Una factura fija si discrimina IVA (el tipo manda); un remito o comprobante no fiscal lo
    // elige quien carga y arranca en lo que dice el tipo. Solo se conserva una elección ya hecha
    // sobre otro tipo libre: la de una factura no era una elección. Los IVA impresos pertenecen a
    // un desglose concreto, así que un cambio de tipo los descarta.
    const conservaEleccion = tipo !== null && !tipo.registraLibroIva && anterior !== null && !anterior.registraLibroIva
    setEncabezado((prev) =>
      conSugerenciasDePercepcion(
        {
          ...prev,
          idTipoComprobante: idTipo,
          discriminaIva: conservaEleccion ? prev.discriminaIva : null,
          ivaImpreso: {},
        },
        referenciaDePercepciones,
      ),
    )
  }

  function cambiarDiscriminaIva(valor: boolean) {
    if (ocupado) return
    setEncabezado((prev) =>
      conSugerenciasDePercepcion({ ...prev, discriminaIva: valor, ivaImpreso: {} }, referenciaDePercepciones),
    )
  }

  function cambiarProveedor(valor: string, disparador: HTMLElement) {
    if (ocupado) return
    if (lineas.some((l) => l.resueltaPorCodigo)) {
      setDisparadorDeProveedor(disparador)
      setProveedorPendiente(valor)
      return
    }
    aplicarProveedor(valor)
  }

  function aplicarProveedor(valor: string) {
    const hayCostoTipeado = lineas.some((l) => !l.resueltaPorCodigo && l.importe !== null)
    setEncabezado((prev) =>
      alElegirProveedor(prev, valor === '' ? '' : Number(valor), referenciaDePercepciones, hayCostoTipeado),
    )
    setCambiosDeProveedor((n) => n + 1)
  }

  function cambiarPuntoVenta(valor: string) {
    if (ocupado) return
    setEncabezado((prev) =>
      conSugerenciasDePercepcion({ ...prev, idPuntoVenta: valor === '' ? '' : Number(valor) }, referenciaDePercepciones),
    )
  }

  function cambiarPreciosIncluyenIva(valor: boolean) {
    if (ocupado) return
    // Los IVA impresos pertenecen a un desglose concreto: cambiar la modalidad los invalida.
    setEncabezado((prev) => ({ ...prev, preciosIncluyenIva: valor, ivaImpreso: {} }))
  }

  function cambiarPercepcion(
    tipo: TipoDePercepcion,
    cambios: Parameters<typeof editarPercepcion>[1],
  ) {
    if (ocupado) return
    // Parte de la fila RESUELTA (lo que el operador ve) para congelar sus valores al primer cambio.
    const resuelta = percepcionesResueltas.find((p) => p.tipo === tipo)
    if (!resuelta) return
    const editada = editarPercepcion(resuelta, cambios)
    setEncabezado((prev) => ({ ...prev, percepciones: prev.percepciones.map((p) => (p.tipo === tipo ? editada : p)) }))
  }

  function quitarPercepcion(tipo: TipoDePercepcion) {
    if (ocupado) return
    setEncabezado((prev) => ({
      ...prev,
      percepciones: prev.percepciones.filter((p) => p.tipo !== tipo),
      percepcionesDescartadas: prev.percepcionesDescartadas.includes(tipo)
        ? prev.percepcionesDescartadas
        : [...prev.percepcionesDescartadas, tipo],
    }))
  }

  function agregarPercepcion(tipo: TipoDePercepcion) {
    if (ocupado) return
    const alicuota = alicuotaDeEmpresaParaCompra(encabezado, referenciaDePercepciones, tipo)
    const nueva = percepcionManual(tipo, basePercepcion, alicuota)
    setEncabezado((prev) => ({
      ...prev,
      percepciones: [...prev.percepciones.filter((p) => p.tipo !== tipo), nueva],
      percepcionesDescartadas: prev.percepcionesDescartadas.filter((t) => t !== tipo),
    }))
  }

  function cambiarIvaImpreso(idAlicuotaIva: number, valor: number | null) {
    if (ocupado) return
    const calculada = totales.alicuotas.find((a) => a.idAlicuotaIva === idAlicuotaIva)?.ivaCalculado
    // Tipear el mismo importe que sale del neto no es un override: si no, quedaría fijo un valor
    // que el próximo cambio de líneas dejaría desactualizado.
    const override = valor === null || valor === calculada ? null : valor
    setEncabezado((prev) => ({ ...prev, ivaImpreso: { ...prev.ivaImpreso, [idAlicuotaIva]: override } }))
  }

  function cambiarLinea(clave: number, cambios: Partial<LineaDeCompraFormulario>) {
    if (ocupado) return
    // regla 1: updater funcional, nunca lee `lineas` del cierre. La clave de la fila vacía que
    // `normalizarFilas` puede necesitar se toma acá: el updater tiene que ser puro.
    const claveNueva = proximaClaveRef.current++
    setLineas((prev) => normalizarFilas(prev.map((l) => (l.clave === clave ? { ...l, ...cambios } : l)), claveNueva))
  }

  function quitarLinea(clave: number) {
    if (ocupado) return
    const claveNueva = proximaClaveRef.current++
    setLineas((prev) => normalizarFilas(prev.filter((l) => l.clave !== clave), claveNueva))
  }

  const encabezadoCompleto = encabezado.idProveedor !== '' && encabezado.idTipoComprobante !== '' && encabezado.idPuntoVenta !== ''
  const puedeGuardar = referenciaOk && puedeEscribir && encabezadoCompleto && !ocupado && !creandoArticulo
  const impedimentoParaGuardar =
    intentoFallido === null ? null : mensajeDeLoQueImpideGuardar(encabezado, lineas, intentoFallido === 'guardar')
  const hayCambios = hayCambiosSinGuardar({ esNuevo, encabezado, lineas, huellaGuardada })
  const duplicados = useDuplicadosDeCompra({
    idProveedor: encabezado.idProveedor,
    idTipoComprobante: encabezado.idTipoComprobante,
    numeroExterno: encabezado.numeroExterno,
    excluirId: idCompra,
    activo: puedeEscribir && (esNuevo || compra?.estado === 'Borrador'),
  })

  const cantidadPorCodigo = lineas.filter((l) => l.resueltaPorCodigo).length
  const hayPorCodigoEnBultos = lineas.some((l) => l.resueltaPorCodigo && l.unidadDeMedida === 'Bulto')

  function pedirCancelar(evento: MouseEvent<HTMLButtonElement>) {
    if (!hayCambios) {
      navigate('/compras')
      return
    }
    // Se captura acá, sincrónicamente: para cuando corra un efecto el botón ya está deshabilitado.
    setDisparadorDeDescartar(evento.currentTarget)
    setDescartarAbierto(true)
  }

  /**
   * Guarda el borrador (POST si es nuevo, PUT si ya existe) y, con `confirmar`, lo confirma a
   * continuación en la misma acción. El borrador queda guardado aunque la confirmación falle: se
   * aterriza en él con el error a la vista, y reintentar actualiza ESE borrador — nunca crea otro.
   */
  async function guardar(confirmar: boolean) {
    // regla 11: la guarda de reentrancia es el ref; el `disabled` de los botones cubre el estado.
    if (guardandoRef.current || creandoArticuloRef.current) return
    if (consultandoCodigosRef.current) {
      setEsperandoCodigos(true)
      return
    }
    setEsperandoCodigos(false)
    if (mensajeDeLoQueImpideGuardar(encabezado, lineas, confirmar) !== null) {
      setAviso('')
      setAdvertencia('')
      setError('')
      setIntentoFallido(confirmar ? 'guardar' : 'borrador')
      return
    }

    guardandoRef.current = true
    setGuardando(true)
    setAccion(confirmar ? 'guardar' : 'borrador')
    setError('')
    setAdvertencia('')
    setAviso('')
    setIntentoFallido(null)
    // regla 3: bumpear la generación de carga ANTES de la escritura — invalida cualquier GET en
    // vuelo de la compra que este guardado está a punto de reemplazar.
    generacionRef.current += 1
    const liberar = () => {
      guardandoRef.current = false
      setGuardando(false)
      setAccion(null)
    }
    // `desdeGasto` viaja en la URL de la compra recién guardada: sin esto, el remontaje (nuevo →
    // existente) perdería el vínculo pendiente.
    const rutaDeLaCompra = (id: number) => `/compras/${id}${idDesdeGasto !== null ? `?desdeGasto=${idDesdeGasto}` : ''}`

    let guardada: CompraDetalle
    try {
      const solicitud = aSolicitudDeCompra({ ...encabezado, percepciones: percepcionesResueltas }, lineas, discriminaIva, registraLibroIva)
      guardada = esNuevo || idCompra === null ? await clienteDeCompras.crear(solicitud) : await clienteDeCompras.actualizar(idCompra, solicitud)
    } catch (e) {
      liberar()
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar el borrador.')
      return
    }

    if (!confirmar) {
      if (esNuevo) {
        // Remontaje completo vía cambio de `key` (regla 8): navega a la ruta real de la compra
        // recién creada, nunca reutiliza este estado de "nuevo" para simular la edición. No se
        // libera la pantalla: queda inerte hasta que la navegación la reemplace, así un clic en
        // esa ventana no puede crear una segunda compra.
        if (montado.current) navigate(rutaDeLaCompra(guardada.id), { replace: true })
        else liberar()
        return
      }
      liberar()
      setCompra(guardada)
      setAviso('Borrador guardado.')
      return
    }

    let confirmada: CompraDetalle
    try {
      confirmada = await clienteDeCompras.confirmar(guardada.id)
    } catch (e) {
      // El 409 compra_no_es_borrador del perdedor de una carrera de doble confirmación se muestra
      // tal cual, como el resto de esta pantalla.
      const falla = e instanceof ErrorApi ? e.message : 'No se pudo confirmar la compra.'
      const nota = 'El borrador quedó guardado, pero la compra NO se confirmó.'
      if (esNuevo) {
        // Sin liberar: la pantalla queda inerte hasta que la navegación la reemplace.
        if (montado.current) navigate(rutaDeLaCompra(guardada.id), { replace: true, state: { advertencia: nota, error: falla } satisfies EstadoDeNavegacion })
        else liberar()
      } else {
        liberar()
        setCompra(guardada)
        setAdvertencia(nota)
        setError(falla)
      }
      return
    }

    // El vínculo con el gasto de origen corre DESPUÉS de que la confirmación ya comiteó — un
    // fallo acá se muestra aparte (errorVincularGasto) y nunca deshace la confirmación.
    const errorDelVinculo = await vincularGasto(confirmada.id)
    const avisoDeConfirmacion = confirmada.items.some((i) => i.idArticulo !== null)
      ? 'Compra confirmada: el stock y el costo ya se actualizaron.'
      : 'Compra confirmada.'
    const vinculo = {
      gastoVinculado: idDesdeGasto !== null && errorDelVinculo === null,
      errorVincularGasto: errorDelVinculo ?? undefined,
    }
    if (esNuevo) {
      // Sin liberar: la pantalla queda inerte hasta que la navegación la reemplace.
      if (montado.current) navigate(rutaDeLaCompra(confirmada.id), { replace: true, state: { aviso: avisoDeConfirmacion, ...vinculo } satisfies EstadoDeNavegacion })
      else liberar()
      return
    }
    liberar()
    // regla 6: un 2xx de confirmar nunca se reporta como fallo — la respuesta ES el resultado.
    setCompra(confirmada)
    setAviso(avisoDeConfirmacion)
    setGastoVinculado(vinculo.gastoVinculado)
    setErrorVincularGasto(errorDelVinculo ?? '')
  }

  function pedirDescartarBorrador(evento: MouseEvent<HTMLButtonElement>) {
    // Se captura acá, sincrónicamente: para cuando corra un efecto el botón ya está deshabilitado.
    setDisparadorDeDescartarBorrador(evento.currentTarget)
    setError('')
    setDescartarBorradorAbierto(true)
  }

  async function descartarBorrador() {
    if (descartandoBorradorRef.current) return
    if (idCompra === null) return

    descartandoBorradorRef.current = true
    setDescartandoBorrador(true)

    try {
      await clienteDeCompras.descartar(idCompra)
      if (montado.current) navigate('/compras')
    } catch (e) {
      setDescartarBorradorAbierto(false)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo descartar el borrador.')
    } finally {
      descartandoBorradorRef.current = false
      setDescartandoBorrador(false)
    }
  }

  async function anular() {
    // regla 9: guard de reentrancia de primera línea.
    if (ocupado) return
    if (anulandoRef.current) return
    if (!confirmadoParaAnular || idCompra === null) return

    anulandoRef.current = true
    setAnulando(true)
    setErrorAnular('')
    generacionRef.current += 1

    try {
      const resultado = await clienteDeCompras.anular(idCompra)
      anulandoRef.current = false
      setAnulando(false)
      // regla 6: un 2xx de anular nunca se reporta como fallo. El stock refusal (409
      // compra_anulacion_stock_negativo) cae en el catch de abajo, nunca acá.
      setCompra(resultado.compra)
      setResultadoAnulacion(resultado)
      setPanelAnularAbierto(false)
      setConfirmadoParaAnular(false)
    } catch (e) {
      anulandoRef.current = false
      setAnulando(false)
      // El refusal por stock negativo (compra_anulacion_stock_negativo) nombra el artículo
      // ofensivo en `e.message` — se muestra tal cual, sin envolver el mensaje del servidor
      // (stage-8, Slice 6, react-async-state regla 10: misma copia de recuperación replicada en
      // Transferencias.tsx para stock_insuficiente_para_transferencia — sibling surfaces, mismo
      // criterio de "nombra el artículo, nunca lo envuelvas").
      setErrorAnular(e instanceof ErrorApi ? e.message : 'No se pudo anular la compra.')
    }
  }

  if (!esNuevo && cargandoCompra) {
    return (
      <div className="container-fluid py-4">
        <Cargando />
      </div>
    )
  }

  if (!esNuevo && errorCompra) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Compra" variante="danger">
          <p className="text-muted">{errorCompra}</p>
          <Link className="btn btn-outline-secondary" to="/compras">
            Volver a compras
          </Link>
        </Box>
      </div>
    )
  }

  const esBorrador = esNuevo || compra?.estado === 'Borrador'
  const esConfirmada = compra?.estado === 'Confirmada'
  const tienePreciosSugeridos = compra?.items.some((i) => i.precioSugerido !== null) ?? false
  const tieneArticulos = compra?.items.some((i) => i.idArticulo !== null) ?? false
  // Un rol sin escritura ve el borrador existente en la tabla de solo lectura: el formulario
  // recalcula los totales desde los costos de cada línea, que el vendedor no recibe.
  const mostrarFormulario = esBorrador && (puedeEscribir || compra === null)

  return (
    <div className="container-fluid py-4">
      <Box
        titulo={esNuevo ? 'Nueva compra' : `Compra ${compra?.numeroExterno ?? `#${idCompra}`}`}
        variante="inverse"
        herramientas={
          <Link className="btn btn-sm btn-outline-secondary" to="/compras">
            Volver a compras
          </Link>
        }
      >
        {aviso && <div className="alert alert-success">{aviso}</div>}
        {errorReferencia && (
          <div className="alert alert-warning py-1 px-2 small">
            {errorReferencia} No se pueden registrar operaciones de compra hasta que esto se resuelva.
          </div>
        )}
        {errorOrdenParaPrecargar && <div className="alert alert-warning py-1 px-2 small">{errorOrdenParaPrecargar}</div>}
        {errorGastoOrigen && <div className="alert alert-warning py-1 px-2 small">{errorGastoOrigen}</div>}
        {encabezado.idOrdenCompra !== null && (
          <div className="alert alert-info py-1 px-2 small">
            Vinculada a la orden de compra{' '}
            <Link to={`/ordenes-compra/${encabezado.idOrdenCompra}`}>#{encabezado.idOrdenCompra}</Link>.
          </div>
        )}
        {esBorradorCargadoPorElAsistente(compra) && (
          <div className="alert alert-warning py-1 px-2 small" role="status">
            Cargado por el asistente: revisá contra la factura antes de confirmar.
          </div>
        )}
        {idDesdeGasto !== null && !gastoVinculado && (
          <div className="alert alert-info py-1 px-2 small">
            Se vinculará al gasto #{idDesdeGasto} al confirmar.
          </div>
        )}
        {gastoVinculado && (
          <div className="alert alert-success py-1 px-2 small">Vinculada al gasto #{idDesdeGasto}.</div>
        )}
        {errorVincularGasto && (
          <div className="alert alert-danger py-1 px-2 small d-flex justify-content-between align-items-center">
            <span>{errorVincularGasto}</span>
            <button
              type="button"
              className="btn btn-sm btn-outline-danger"
              disabled={vinculandoGasto}
              onClick={() => compra && void reintentarVinculoConGasto(compra.id)}
            >
              {vinculandoGasto ? 'Reintentando…' : 'Reintentar vínculo'}
            </button>
          </div>
        )}

        {!esNuevo && compra && (
          <div className="mb-3">
            <span className={`badge me-2 ${esBorrador ? 'text-bg-secondary' : esConfirmada ? 'text-bg-success' : 'text-bg-danger'}`}>
              {etiquetaDeEstadoCompra(compra.estado)}
            </span>
            {compra.fechaRecepcion && <span className="small text-muted">Recibida: {formatearFechaHora(compra.fechaRecepcion)}</span>}
          </div>
        )}

        <div className="row g-2 mb-3">
          <div className="col-md-3">
            <label className="form-label" htmlFor="compra-proveedor">
              Proveedor
            </label>
            <select
              id="compra-proveedor"
              className="form-select"
              value={encabezado.idProveedor}
              disabled={!esBorrador || ocupado || !referenciaOk || !puedeEscribir}
              onChange={(e) => cambiarProveedor(e.target.value, e.currentTarget)}
            >
              <option value="">Elegir…</option>
              {(proveedores ?? []).map((p) => (
                <option key={p.id} value={p.id}>
                  {etiquetaDeProveedor(p)}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="compra-tipo">
              Tipo
            </label>
            <select
              id="compra-tipo"
              className="form-select"
              value={encabezado.idTipoComprobante}
              disabled={!esBorrador || ocupado || !referenciaOk || !puedeEscribir}
              onChange={(e) => cambiarTipo(e.target.value)}
            >
              <option value="">Elegir…</option>
              {(tipos ?? []).map((t) => (
                <option key={t.id} value={t.id}>
                  {t.nombre}
                </option>
              ))}
            </select>
            {tipoSeleccionado !== null && !tipoSeleccionado.registraLibroIva && (
              <div className="form-check mt-1">
                <input
                  id="compra-discrimina-iva"
                  type="checkbox"
                  className="form-check-input"
                  checked={discriminaIva}
                  disabled={!esBorrador || ocupado || !referenciaOk || !puedeEscribir}
                  onChange={(e) => cambiarDiscriminaIva(e.target.checked)}
                />
                <label className="form-check-label small" htmlFor="compra-discrimina-iva">
                  Discrimina IVA
                </label>
              </div>
            )}
            {discriminaIva && (
              <div className="form-check mt-1">
                <input
                  id="compra-precios-incluyen-iva"
                  type="checkbox"
                  className="form-check-input"
                  checked={encabezado.preciosIncluyenIva}
                  disabled={!esBorrador || ocupado || !referenciaOk || !puedeEscribir}
                  onChange={(e) => cambiarPreciosIncluyenIva(e.target.checked)}
                />
                <label className="form-check-label small" htmlFor="compra-precios-incluyen-iva">
                  Precios con IVA incluido
                </label>
              </div>
            )}
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="compra-punto-venta">
              Punto de venta
            </label>
            <select
              id="compra-punto-venta"
              className="form-select"
              value={encabezado.idPuntoVenta}
              disabled={!esBorrador || ocupado || !referenciaOk || !puedeEscribir}
              onChange={(e) => cambiarPuntoVenta(e.target.value)}
            >
              <option value="">Elegir…</option>
              {(puntosVenta ?? []).map((pv) => (
                <option key={pv.id} value={pv.id}>
                  {pv.nombre}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-3">
            <span className="form-label d-block" id="compra-numero-externo-etiqueta">
              Número de comprobante
            </span>
            <CampoNumeroDeComprobante
              idBase="compra-numero-externo"
              valor={encabezado.numeroExterno}
              disabled={!esBorrador || ocupado || !puedeEscribir}
              onChange={(numeroExterno) => setEncabezado((prev) => ({ ...prev, numeroExterno }))}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="compra-fecha-comprobante">
              Fecha del comprobante
            </label>
            <CampoFecha
              id="compra-fecha-comprobante"
              className="form-control"
              value={encabezado.fechaComprobante}
              disabled={!esBorrador || ocupado || !puedeEscribir}
              onChange={(valor) => setEncabezado((prev) => ({ ...prev, fechaComprobante: valor }))}
            />
          </div>
          {duplicados.length > 0 && (
            <div className="col-12">
              <AvisoDeNumeroDuplicado duplicados={duplicados} />
            </div>
          )}
          <div className="col-12">
            <label className="form-label" htmlFor="compra-observaciones">
              Observaciones
            </label>
            <input
              id="compra-observaciones"
              type="text"
              className="form-control"
              value={encabezado.observaciones}
              disabled={!esBorrador || ocupado || !puedeEscribir}
              onChange={(e) => setEncabezado((prev) => ({ ...prev, observaciones: e.target.value }))}
            />
          </div>
        </div>

        {mostrarFormulario ? (
          <>
            <GrillaDeItemsDeCompra
              lineas={lineas}
              alicuotas={alicuotas ?? []}
              disabled={ocupado || !referenciaOk || !puedeEscribir}
              discriminaIva={discriminaIva}
              preciosFinales={preciosFinales}
              porcentajePorAlicuota={porcentajePorAlicuota}
              idProveedor={encabezado.idProveedor === '' ? null : encabezado.idProveedor}
              puedeCrearArticulos={puedeCrearArticulos}
              marcarFaltantes={intentoFallido !== null}
              cambiosDeProveedor={cambiosDeProveedor}
              onConsultando={alCambiarConsultasEnVuelo}
              onCreandoArticulo={alCambiarAltaDeArticulo}
              onCambio={cambiarLinea}
              onQuitar={quitarLinea}
            />

            <div className="row g-3 mb-3">
              <div className="col-md-3">
                <div className="small text-muted">
                  Subtotal{preciosFinales ? ' con IVA' : ''} (mirror, no autoritativo)
                </div>
                <div>{formatearMoneda(totales.subtotal)}</div>
              </div>
              <div className="col-md-3">
                <div className="small text-muted">Descuento</div>
                <div>{formatearMoneda(totales.descuentoTotal)}</div>
              </div>
              <div className="col-md-3">
                <div className="small text-muted">IVA</div>
                <div>{totales.ivaTotal === null ? '—' : formatearMoneda(totales.ivaTotal)}</div>
              </div>
              {totales.percepcionesTotal !== 0 && (
                <div className="col-md-3">
                  <div className="small text-muted">Percepciones</div>
                  <div>{formatearMoneda(totales.percepcionesTotal)}</div>
                </div>
              )}
              <div className="col-md-3">
                <div className="small text-muted">Total</div>
                <div className="fs-6">{formatearMoneda(totales.total)}</div>
              </div>
            </div>

            {discriminaIva && (
              <DesgloseDeIvaDeCompra
                filas={totales.alicuotas}
                nombrePorAlicuota={nombrePorAlicuota}
                onCambiarIva={cambiarIvaImpreso}
                disabled={ocupado || !puedeEscribir}
              />
            )}

            {registraLibroIva && errorEmpresas && (
              <div className="alert alert-warning py-1 px-2 small">
                No se pudieron cargar las alícuotas de percepción de la empresa: las percepciones no se sugieren,
                cargalas a mano.
              </div>
            )}

            {registraLibroIva && (
              <PercepcionesDeCompra
                filas={percepcionesVisibles}
                onCambiar={cambiarPercepcion}
                onQuitar={quitarPercepcion}
                agregables={puedeEscribir ? tiposAgregables : []}
                onAgregar={agregarPercepcion}
                disabled={ocupado || !referenciaOk || !puedeEscribir}
              />
            )}

            {cantidadDeIncompletas > 0 && (
              <div className="alert alert-warning py-1 px-2 small mb-3">
                {cantidadDeIncompletas} línea(s) incompleta(s) — complételas o quítelas para poder guardar.
              </div>
            )}

            {advertencia && (
              <div className="alert alert-warning py-1 px-2 small" role="alert">
                {advertencia}
              </div>
            )}
            {esperandoCodigos && (
              <div className="alert alert-info py-1 px-2 small" role="alert">
                Se están buscando códigos de proveedor: espere a que terminen y vuelva a guardar.
              </div>
            )}
            {(impedimentoParaGuardar ?? error) && (
              <div className="alert alert-danger py-1 px-2 small" role="alert">
                {impedimentoParaGuardar ?? error}
              </div>
            )}

            {puedeEscribir && (
              <>
                <div className="d-flex flex-wrap gap-2 mb-2">
                  <button type="button" className="btn btn-primary" disabled={!puedeGuardar} onClick={() => void guardar(true)}>
                    {accion === 'guardar' ? 'Guardando…' : 'Guardar'}
                  </button>
                  <button type="button" className="btn btn-outline-primary" disabled={!puedeGuardar} onClick={() => void guardar(false)}>
                    {accion === 'borrador' ? 'Guardando…' : 'Guardar borrador'}
                  </button>
                  <button type="button" className="btn btn-outline-secondary" disabled={ocupado} onClick={pedirCancelar}>
                    Cancelar
                  </button>
                  {!esNuevo && (
                    <button type="button" className="btn btn-outline-danger ms-auto" disabled={ocupado} onClick={pedirDescartarBorrador}>
                      Descartar borrador
                    </button>
                  )}
                </div>

                <div className="small text-muted mb-3">
                  {lineasCompletas.some((l) => l.idArticulo !== '')
                    ? 'Guardar confirma la compra y es irreversible: el stock entra, el costo del artículo se actualiza y no se puede volver a borrador.'
                    : 'Guardar confirma la compra y es irreversible: no se puede volver a borrador.'}{' '}
                  Para dejarla pendiente, use Guardar borrador.
                </div>
              </>
            )}

            {proveedorPendiente !== null && (
              <ConfirmacionDeBaja
                titulo="el proveedor"
                pregunta="Cambiar"
                nota={`Cambiar el proveedor vuelve a buscar ${cantidadPorCodigo} ${cantidadPorCodigo === 1 ? 'línea' : 'líneas'} por su código y borra sus precios.${hayPorCodigoEnBultos ? ' Las líneas cargadas por bulto también pierden la cantidad.' : ''}`}
                etiquetaConfirmar="Cambiar proveedor"
                ocupado={false}
                disparador={disparadorDeProveedor}
                onConfirmar={() => {
                  aplicarProveedor(proveedorPendiente)
                  setProveedorPendiente(null)
                }}
                onCancelar={() => setProveedorPendiente(null)}
              />
            )}

            {descartarBorradorAbierto && (
              <ConfirmacionDeBaja
                titulo="este borrador"
                pregunta="Descartar"
                nota="Se borra con todas sus líneas y no se puede recuperar. El número de comprobante del proveedor queda libre para cargarlo de nuevo."
                etiquetaConfirmar="Confirmar descarte"
                etiquetaEnCurso="Descartando…"
                ocupado={descartandoBorrador}
                disparador={disparadorDeDescartarBorrador}
                onConfirmar={() => void descartarBorrador()}
                onCancelar={() => setDescartarBorradorAbierto(false)}
              />
            )}

            {descartarAbierto && (
              <ConfirmacionDeBaja
                titulo="los cambios sin guardar de esta compra"
                pregunta="Descartar"
                nota="Lo que no se guardó se pierde."
                etiquetaConfirmar="Descartar cambios"
                ocupado={false}
                disparador={disparadorDeDescartar}
                onConfirmar={() => navigate('/compras')}
                onCancelar={() => setDescartarAbierto(false)}
              />
            )}
          </>
        ) : (
          compra && (
            <>
              <TablaDeItemsDeSoloLectura compra={compra} />

              <div className="row g-3 my-3">
                <div className="col-md-3">
                  <div className="small text-muted">Subtotal</div>
                  <div>{formatearMoneda(compra.subtotal)}</div>
                </div>
                <div className="col-md-3">
                  <div className="small text-muted">Descuento</div>
                  <div>{formatearMoneda(compra.descuentoTotal)}</div>
                </div>
                <div className="col-md-3">
                  <div className="small text-muted">IVA</div>
                  <div>{compra.ivaTotal === null ? '—' : formatearMoneda(compra.ivaTotal)}</div>
                </div>
                <div className="col-md-3">
                  <div className="small text-muted">Total</div>
                  <div className="fs-6">{formatearMoneda(compra.total)}</div>
                </div>
              </div>

              {esConfirmada && (
                <div className="row g-3 mb-3">
                  <div className="col-md-3">
                    <div className="small text-muted">Pagado</div>
                    <div>{formatearMoneda(compra.pagado)}</div>
                  </div>
                  <div className="col-md-3">
                    <div className="small text-muted">Saldo pendiente</div>
                    <div className="fs-6">{formatearMoneda(compra.saldoPendiente)}</div>
                  </div>
                </div>
              )}

              {compra.discriminaIva && (
                <DesgloseDeIvaDeCompra filas={compra.alicuotas} nombrePorAlicuota={nombrePorAlicuota} />
              )}

              {compra.discriminaIva && compra.preciosIncluyenIva && (
                <div className="small text-muted mb-3">Los precios del comprobante incluyen IVA.</div>
              )}

              <PercepcionesDeCompra filas={percepcionesDesdeDetalle(compra.percepciones)} />

              {resultadoAnulacion && (
                <div className="alert alert-warning">
                  Compra anulada. {resultadoAnulacion.gastosLigados > 0
                    ? `Quedan ${resultadoAnulacion.gastosLigados} gasto(s) ligado(s) a esta compra sin desvincular — la anulación no los revierte, quedan como historial de un pago ya realizado.`
                    : 'No había ningún gasto ligado a esta compra.'}
                </div>
              )}

              {esConfirmada && puedeEscribir && (
                <>
                  <div className="d-flex gap-2 mb-3">
                    {sePuedePagarLaCompra(compra) && (
                      <button type="button" className="btn btn-primary" disabled={ocupado} onClick={() => setPagoAbierto(true)}>
                        Pagar
                      </button>
                    )}
                    <button
                      type="button"
                      className="btn btn-danger"
                      disabled={ocupado}
                      onClick={() => setPanelAnularAbierto(true)}
                    >
                      Anular compra
                    </button>
                  </div>

                  {pagoAbierto && (
                    <ModalDePagoDeCompra
                      idCompra={compra.id}
                      etiquetaDeLaCompra={compra.numeroExterno ?? `#${compra.id}`}
                      saldoPendiente={compra.saldoPendiente}
                      onCerrar={() => setPagoAbierto(false)}
                      onPagado={alPagar}
                    />
                  )}

                  {panelAnularAbierto && (
                    <div className="border p-3 mb-3">
                      <strong>Anular compra</strong>
                      {errorAnular && <div className="alert alert-danger py-1 px-2 small mt-2">{errorAnular}</div>}
                      <div className="form-check my-2">
                        <input
                          id="compra-confirmacion-anular"
                          type="checkbox"
                          className="form-check-input"
                          checked={confirmadoParaAnular}
                          disabled={anulando}
                          onChange={(e) => setConfirmadoParaAnular(e.target.checked)}
                        />
                        <label className="form-check-label" htmlFor="compra-confirmacion-anular">
                          {tieneArticulos
                            ? 'Confirmo que quiero anular esta compra. Es irreversible: se revierte el stock que entró, el costo del artículo NO se corrige solo (se edita aparte).'
                            : 'Confirmo que quiero anular esta compra. Es irreversible.'}
                        </label>
                      </div>
                      <div className="d-flex gap-2">
                        <button
                          type="button"
                          className="btn btn-outline-secondary"
                          disabled={anulando}
                          onClick={() => {
                            setPanelAnularAbierto(false)
                            setConfirmadoParaAnular(false)
                          }}
                        >
                          Cancelar
                        </button>
                        <button
                          type="button"
                          className="btn btn-danger"
                          disabled={!confirmadoParaAnular || ocupado}
                          onClick={anular}
                        >
                          {anulando ? 'Anulando…' : 'Anular'}
                        </button>
                      </div>
                    </div>
                  )}

                  {tienePreciosSugeridos && listasPrecio && listasPrecio.length > 0 && (
                    <PanelAplicarPrecios
                      idCompra={compra.id}
                      listas={listasPrecio}
                      disabled={ocupado}
                      onAntesDeEscribir={() => {
                        generacionRef.current += 1
                        setAplicandoPrecios(true)
                      }}
                      onAplicado={() => {
                        setAplicandoPrecios(false)
                        setAviso('Precios aplicados — revisá el detalle por línea abajo.')
                      }}
                      onError={() => setAplicandoPrecios(false)}
                    />
                  )}
                </>
              )}
            </>
          )
        )}
      </Box>
    </div>
  )
}

/**
 * Editor de un comprobante de compra (stage-8-compras-transferencias-inventario, Slice 5, design:
 * Web Composition): `/compras/nueva` crea un borrador desde cero; `/compras/:id` lo edita
 * (borrador), lo confirma/anula (confirmada) o solo lo muestra (anulada). La ruta sigue
 * `Politicas.OperacionDePos` (decisión 11: la lectura queda abierta a Vendedor/Supervisor/Admin,
 * igual que `/clientes/:id/cuenta-corriente`) — `puedeEscribir` oculta las acciones de escritura,
 * `GestionDeCatalogo` es la autoridad real del lado del servidor.
 */
export function CompraEditor() {
  const { id } = useParams<{ id: string }>()
  const [searchParams] = useSearchParams()
  const esNuevo = id === undefined || id === 'nueva'
  const idNumerico = esNuevo ? null : Number(id)
  const idValido = esNuevo || Number.isFinite(idNumerico)

  // stage-16-ordenes-de-compra, Slice 6: solo aplica a un borrador NUEVO — una compra existente
  // ya tiene su propio `idOrdenCompra` congelado (design: "reads idOrdenCompra from
  // useSearchParams").
  const idOrdenCompraParam = esNuevo ? searchParams.get('idOrdenCompra') : null
  const idOrdenCompra =
    idOrdenCompraParam !== null && Number.isFinite(Number(idOrdenCompraParam)) ? Number(idOrdenCompraParam) : null

  // stage-gasto-a-compra (PR4): a diferencia de `idOrdenCompra` (solo aplica a un borrador nuevo),
  // `desdeGasto` se lee SIEMPRE — sobrevive al remontaje nuevo→existente (`guardarBorrador` lo
  // reenvía en el `navigate` de creación) porque el vínculo real recién se dispara al CONFIRMAR,
  // no al crear el borrador.
  const idDesdeGastoParam = searchParams.get('desdeGasto')
  const idDesdeGasto =
    idDesdeGastoParam !== null && Number.isFinite(Number(idDesdeGastoParam)) ? Number(idDesdeGastoParam) : null

  if (!idValido) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Compra" variante="warning">
          <p className="text-muted">No se especificó una compra válida.</p>
          <Link className="btn btn-outline-secondary" to="/compras">
            Volver a compras
          </Link>
        </Box>
      </div>
    )
  }

  return (
    <PantallaCompraEditor
      key={idNumerico ?? `nuevo-${idOrdenCompra ?? 's'}-${idDesdeGasto ?? 'g'}`}
      idCompra={idNumerico}
      idOrdenCompra={idOrdenCompra}
      idDesdeGasto={idDesdeGasto}
    />
  )
}
