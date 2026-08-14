import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import {
  aSolicitudDeCompra,
  calcularTotalesDeCompra,
  clienteDeCompras,
  etiquetaDeEstadoCompra,
  itemAFormulario,
  lineaCompletaParaEnvio,
  lineaDeCompraVacia,
  lineaFormularioACalculo,
  lineaConDescuentoInvalido,
  type EncabezadoDeCompraFormulario,
  type LineaDeCompraFormulario,
} from '../api/compras'
import { clienteDeArticulos } from '../api/articulos'
import { clienteDeCatalogosFiscales } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDePrecios } from '../api/precios'
import { ROL } from '../api/tipos'
import type {
  AlicuotaIvaListado,
  ArticuloListado,
  CompraDetalle,
  ListaPrecioListado,
  PaginaDe,
  ProveedorListado,
  PuntoVentaListado,
  ResultadoAnulacion,
  ResultadoAplicarPrecio,
  TipoComprobanteListado,
} from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

function formatearFechaHora(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString('es-AR') : '—'
}

function encabezadoVacio(): EncabezadoDeCompraFormulario {
  return { idProveedor: '', idTipoComprobante: '', idPuntoVenta: '', numeroExterno: '', fechaComprobante: '', observaciones: '' }
}

function encabezadoDesdeDetalle(c: CompraDetalle): EncabezadoDeCompraFormulario {
  return {
    idProveedor: c.idProveedor,
    idTipoComprobante: c.idTipoComprobante,
    idPuntoVenta: c.idPuntoVenta,
    numeroExterno: c.numeroExterno ?? '',
    fechaComprobante: c.fechaComprobante ?? '',
    observaciones: c.observaciones ?? '',
  }
}

// ---- Selector de artículo por búsqueda (search-as-you-type, propio de cada fila) --------------

type PropsSelectorDeArticulo = {
  descripcion: string
  disabled: boolean
  onElegir: (articulo: ArticuloListado) => void
}

function SelectorDeArticulo({ descripcion, disabled, onElegir }: PropsSelectorDeArticulo) {
  const [termino, setTermino] = useState('')
  const [resultados, setResultados] = useState<ArticuloListado[]>([])
  const [buscando, setBuscando] = useState(false)
  const generacionRef = useRef(0)

  useEffect(() => {
    if (termino.trim().length < 2) {
      setResultados([])
      return
    }

    let vigente = true
    const miGeneracion = (generacionRef.current += 1)
    setBuscando(true)

    const temporizador = setTimeout(() => {
      clienteDeArticulos
        .listar(termino, false)
        .then((pagina) => {
          if (!vigente || generacionRef.current !== miGeneracion) return
          setResultados(pagina.items)
        })
        .catch(() => {
          if (!vigente || generacionRef.current !== miGeneracion) return
          setResultados([])
        })
        .finally(() => {
          if (!vigente || generacionRef.current !== miGeneracion) return
          setBuscando(false)
        })
    }, 300)

    return () => {
      vigente = false
      clearTimeout(temporizador)
    }
  }, [termino])

  return (
    <div className="position-relative">
      <input
        type="text"
        className="form-control form-control-sm rounded-0"
        placeholder="Buscar artículo…"
        value={termino}
        disabled={disabled}
        onChange={(e) => setTermino(e.target.value)}
      />
      {descripcion && <div className="small text-muted">Elegido: {descripcion}</div>}
      {buscando && <div className="small text-muted">Buscando…</div>}
      {!buscando && resultados.length > 0 && (
        <div className="list-group position-absolute w-100" style={{ zIndex: 10 }}>
          {resultados.map((a) => (
            <button
              key={a.id}
              type="button"
              className="list-group-item list-group-item-action py-1 px-2 small"
              onClick={() => {
                onElegir(a)
                setTermino('')
                setResultados([])
              }}
            >
              {a.codigoInterno} — {a.nombre}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

// ---- Fila editable del grid de items -----------------------------------------------------------

type PropsFilaDeItem = {
  linea: LineaDeCompraFormulario
  alicuotas: AlicuotaIvaListado[]
  disabled: boolean
  discriminaIva: boolean
  porcentajePorAlicuota: Record<number, number>
  onCambio: (clave: number, cambios: Partial<LineaDeCompraFormulario>) => void
  onQuitar: (clave: number) => void
}

function FilaDeItem({ linea, alicuotas, disabled, discriminaIva, porcentajePorAlicuota, onCambio, onQuitar }: PropsFilaDeItem) {
  const calculo = lineaFormularioACalculo(linea, porcentajePorAlicuota)
  const item = calcularTotalesDeCompra([calculo], discriminaIva).items[0]
  const descuentoInvalido = lineaConDescuentoInvalido(calculo)
  const incompleta = !lineaCompletaParaEnvio(linea)

  return (
    <tr className={incompleta ? 'table-warning text-muted' : undefined}>
      <td style={{ minWidth: 220 }}>
        <SelectorDeArticulo
          descripcion={linea.descripcion}
          disabled={disabled}
          onElegir={(a) => {
            // Cambiar el artículo de una línea invalida cualquier lote ya cargado (era del
            // artículo anterior): sin este reset, codigoLote/fechaVencimiento quedan stale y
            // viajan en el payload — la validación del servidor es incondicional y los persiste
            // (judgment-day, slice 14, MAJOR juez A).
            const cambioDeArticulo = linea.idArticulo !== a.id
            onCambio(linea.clave, {
              idArticulo: a.id,
              descripcion: a.nombre,
              controlaLote: a.controlaLote,
              ...(cambioDeArticulo ? { codigoLote: '', fechaVencimiento: '' } : {}),
            })
          }}
        />
        {incompleta && <div className="small text-warning-emphasis">Línea incompleta — no se va a guardar.</div>}
      </td>
      <td style={{ minWidth: 180 }}>
        {linea.controlaLote ? (
          <>
            <input
              type="text"
              className="form-control form-control-sm rounded-0 mb-1"
              aria-label="Código de lote"
              placeholder="Código de lote (opcional)"
              value={linea.codigoLote}
              disabled={disabled}
              onChange={(e) => onCambio(linea.clave, { codigoLote: e.target.value })}
            />
            <input
              type="date"
              className={`form-control form-control-sm rounded-0 ${linea.fechaVencimiento.trim() === '' ? 'is-invalid' : ''}`}
              aria-label="Fecha de vencimiento"
              value={linea.fechaVencimiento}
              disabled={disabled}
              onChange={(e) => onCambio(linea.clave, { fechaVencimiento: e.target.value })}
            />
            {linea.fechaVencimiento.trim() === '' && (
              <div className="invalid-feedback">Este artículo controla lote — la fecha de vencimiento es obligatoria.</div>
            )}
          </>
        ) : (
          <span className="text-muted small">No controla lote</span>
        )}
      </td>
      <td style={{ width: 90 }}>
        <input
          type="number"
          step="0.001"
          min="0"
          className="form-control form-control-sm rounded-0"
          aria-label="Unidades"
          value={linea.unidades}
          disabled={disabled}
          onChange={(e) => onCambio(linea.clave, { unidades: e.target.value })}
        />
      </td>
      <td style={{ width: 80 }}>
        <input
          type="number"
          step="1"
          min="0"
          className="form-control form-control-sm rounded-0"
          aria-label="Bultos"
          value={linea.bultos}
          disabled={disabled}
          onChange={(e) => onCambio(linea.clave, { bultos: e.target.value })}
        />
      </td>
      <td style={{ width: 100 }}>
        <input
          type="number"
          step="0.001"
          min="0"
          className="form-control form-control-sm rounded-0"
          aria-label="Unidades por bulto"
          value={linea.unidadesPorBulto}
          disabled={disabled}
          onChange={(e) => onCambio(linea.clave, { unidadesPorBulto: e.target.value })}
        />
      </td>
      <td style={{ width: 110 }}>
        <input
          type="number"
          step="0.0001"
          min="0"
          className="form-control form-control-sm rounded-0"
          aria-label="Costo unitario"
          value={linea.costoUnitario}
          disabled={disabled}
          onChange={(e) => onCambio(linea.clave, { costoUnitario: e.target.value })}
        />
      </td>
      <td style={{ width: 100 }}>
        <input
          type="number"
          step="0.01"
          min="0"
          className={`form-control form-control-sm rounded-0 ${descuentoInvalido ? 'is-invalid' : ''}`}
          aria-label="Descuento"
          value={linea.descuento}
          disabled={disabled}
          onChange={(e) => onCambio(linea.clave, { descuento: e.target.value })}
        />
        {descuentoInvalido && <div className="invalid-feedback">Mayor al bruto de la línea.</div>}
      </td>
      <td style={{ width: 100 }}>
        <select
          className="form-select form-select-sm rounded-0"
          aria-label="Alícuota de IVA"
          value={linea.idAlicuotaIva}
          disabled={disabled}
          onChange={(e) => onCambio(linea.clave, { idAlicuotaIva: e.target.value === '' ? '' : Number(e.target.value) })}
        >
          <option value="">Elegir…</option>
          {alicuotas.map((al) => (
            <option key={al.id} value={al.id}>
              {al.nombre}
            </option>
          ))}
        </select>
      </td>
      <td className="text-center" style={{ width: 60 }}>
        <input
          type="checkbox"
          className="form-check-input rounded-0"
          aria-label="Actualiza costo"
          checked={linea.actualizaCosto}
          disabled={disabled}
          onChange={(e) => onCambio(linea.clave, { actualizaCosto: e.target.checked })}
        />
      </td>
      <td className="text-end">{formatearMoneda(item.total)}</td>
      <td>
        <button
          type="button"
          className="btn btn-outline-danger btn-sm rounded-0"
          disabled={disabled}
          onClick={() => onQuitar(linea.clave)}
        >
          Quitar
        </button>
      </td>
    </tr>
  )
}

// ---- Tabla de items de solo lectura (compra confirmada/anulada) -------------------------------

function TablaDeItemsDeSoloLectura({ compra }: { compra: CompraDetalle }) {
  return (
    <div className="table-responsive">
      <table className="table table-sm table-striped table-bordered align-middle">
        <thead>
          <tr>
            <th>Artículo</th>
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
              <td>{item.descripcion}</td>
              <td>{item.codigoLote ?? '—'}</td>
              <td>{item.fechaVencimiento ?? '—'}</td>
              <td className="text-end">{item.cantidad}</td>
              <td className="text-end">{formatearMoneda(item.costoUnitario)}</td>
              <td className="text-end">{formatearMoneda(item.descuento)}</td>
              <td className="text-end">{item.porcentajeIva}%</td>
              <td className="text-end">{formatearMoneda(item.total)}</td>
              <td>{item.actualizaCosto ? 'Sí' : 'No'}</td>
              <td className="text-end">{item.precioSugerido === null ? '—' : formatearMoneda(item.precioSugerido)}</td>
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
      {error && <div className="alert alert-danger rounded-0 py-1 px-2 small mt-2">{error}</div>}
      <div className="row g-2 align-items-end mt-1">
        <div className="col-md-4">
          <label className="form-label" htmlFor="compra-lista-precio">
            Lista de precios
          </label>
          <select
            id="compra-lista-precio"
            className="form-select rounded-0"
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
              className="form-check-input rounded-0"
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
          <button type="button" className="btn btn-primary rounded-0 w-100" disabled={disabled || aplicando} onClick={aplicar}>
            {aplicando ? 'Aplicando…' : 'Aplicar'}
          </button>
        </div>
      </div>

      {resultados && (
        <table className="table table-sm table-bordered mt-3 mb-0">
          <thead>
            <tr>
              <th>Artículo</th>
              <th>Resultado</th>
              <th className="text-end">Precio</th>
            </tr>
          </thead>
          <tbody>
            {resultados.map((r) => (
              <tr key={r.idArticulo}>
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

type PropsPantalla = { idCompra: number | null }

/** Remontada por `key={idCompra ?? 'nuevo'}` (react-async-state regla 8) — ningún estado de acá
 * (borrador en edición, paneles de confirmar/anular) sobrevive a un cambio de compra. */
function PantallaCompraEditor({ idCompra }: PropsPantalla) {
  const navigate = useNavigate()
  const { usuario } = useAuth()
  const puedeEscribir = usuario !== null && usuario.rolId === ROL.Admin

  const esNuevo = idCompra === null

  // ---- referencia (proveedores/tipos/alícuotas/puntos de venta/listas) ------------------------
  const [proveedores, setProveedores] = useState<ProveedorListado[] | null>(null)
  const [tipos, setTipos] = useState<TipoComprobanteListado[] | null>(null)
  const [alicuotas, setAlicuotas] = useState<AlicuotaIvaListado[] | null>(null)
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
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

  // ---- formulario editable (nuevo o borrador existente) ------------------------------------------
  const [encabezado, setEncabezado] = useState<EncabezadoDeCompraFormulario>(encabezadoVacio())
  const proximaClaveRef = useRef(1)
  const [lineas, setLineas] = useState<LineaDeCompraFormulario[]>([])

  useEffect(() => {
    if (compra === null) return
    setEncabezado(encabezadoDesdeDetalle(compra))
    setLineas(compra.items.map((item) => itemAFormulario(proximaClaveRef.current++, item)))
  }, [compra])

  const tipoSeleccionado = (tipos ?? []).find((t) => t.id === encabezado.idTipoComprobante) ?? null
  const discriminaIva = tipoSeleccionado?.discriminaIva ?? false

  // El mirror de totales se calcula SOLO sobre las líneas completas (`lineaCompletaParaEnvio`):
  // es la misma fuente de verdad que decide qué líneas viajan en `aSolicitudDeCompra` — una fila a
  // medio llenar nunca debe sumar al Subtotal en pantalla, porque tampoco se va a guardar.
  const lineasCompletas = useMemo(() => lineas.filter(lineaCompletaParaEnvio), [lineas])
  const lineasIncompletas = lineas.length - lineasCompletas.length
  const calculo = useMemo(
    () => lineasCompletas.map((l) => lineaFormularioACalculo(l, porcentajePorAlicuota)),
    [lineasCompletas, porcentajePorAlicuota],
  )
  const totales = useMemo(() => calcularTotalesDeCompra(calculo, discriminaIva), [calculo, discriminaIva])

  const [guardando, setGuardando] = useState(false)
  const guardandoRef = useRef(false)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')

  const [confirmando, setConfirmando] = useState(false)
  const confirmandoRef = useRef(false)
  const [panelConfirmarAbierto, setPanelConfirmarAbierto] = useState(false)
  const [confirmadoParaConfirmar, setConfirmadoParaConfirmar] = useState(false)
  const [errorConfirmar, setErrorConfirmar] = useState('')

  const [anulando, setAnulando] = useState(false)
  const anulandoRef = useRef(false)
  const [panelAnularAbierto, setPanelAnularAbierto] = useState(false)
  const [confirmadoParaAnular, setConfirmadoParaAnular] = useState(false)
  const [errorAnular, setErrorAnular] = useState('')
  const [resultadoAnulacion, setResultadoAnulacion] = useState<ResultadoAnulacion | null>(null)

  // El panel de aplicar precio sugerido es local a `PanelAplicarPrecios`, pero su "en vuelo" se
  // levanta acá (regla 9): sin esto, `ocupado` no lo ve y anular puede dispararse con un aplicar
  // todavía en curso — el gate queda asimétrico.
  const [aplicandoPrecios, setAplicandoPrecios] = useState(false)

  const ocupado = guardando || confirmando || anulando || aplicandoPrecios

  function cambiarLinea(clave: number, cambios: Partial<LineaDeCompraFormulario>) {
    if (ocupado) return
    // regla 1: updater funcional, nunca lee `lineas` del cierre.
    setLineas((prev) => prev.map((l) => (l.clave === clave ? { ...l, ...cambios } : l)))
  }

  function agregarLinea() {
    if (ocupado) return
    setLineas((prev) => [...prev, lineaDeCompraVacia(proximaClaveRef.current++)])
  }

  function quitarLinea(clave: number) {
    if (ocupado) return
    setLineas((prev) => prev.filter((l) => l.clave !== clave))
  }

  const encabezadoCompleto = encabezado.idProveedor !== '' && encabezado.idTipoComprobante !== '' && encabezado.idPuntoVenta !== ''
  const puedeGuardar = referenciaOk && puedeEscribir && encabezadoCompleto && !ocupado

  async function guardarBorrador() {
    // regla 9: guard de reentrancia de primera línea.
    if (guardandoRef.current) return
    if (!puedeGuardar) return

    guardandoRef.current = true
    setGuardando(true)
    setError('')
    setAviso('')
    // regla 3: bumpear la generación de carga ANTES de la escritura — invalida cualquier GET en
    // vuelo de la compra que este guardado está a punto de reemplazar.
    generacionRef.current += 1

    try {
      const solicitud = aSolicitudDeCompra(encabezado, lineas)
      if (esNuevo) {
        const creada = await clienteDeCompras.crear(solicitud)
        guardandoRef.current = false
        setGuardando(false)
        // Remontaje completo vía cambio de `key` (regla 8): navega a la ruta real de la compra
        // recién creada, nunca reutiliza este estado de "nuevo" para simular la edición.
        navigate(`/compras/${creada.id}`, { replace: true })
      } else if (idCompra !== null) {
        const actualizada = await clienteDeCompras.actualizar(idCompra, solicitud)
        guardandoRef.current = false
        setGuardando(false)
        setCompra(actualizada)
        setAviso('Borrador guardado.')
      }
    } catch (e) {
      guardandoRef.current = false
      setGuardando(false)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar el borrador.')
    }
  }

  async function confirmar() {
    // regla 9: guard de reentrancia de primera línea.
    if (confirmandoRef.current) return
    if (!confirmadoParaConfirmar || idCompra === null) return

    confirmandoRef.current = true
    setConfirmando(true)
    setErrorConfirmar('')
    generacionRef.current += 1

    try {
      const confirmada = await clienteDeCompras.confirmar(idCompra)
      confirmandoRef.current = false
      setConfirmando(false)
      // regla 6: un 2xx de confirmar nunca se reporta como fallo — la respuesta ES el resultado.
      setCompra(confirmada)
      setPanelConfirmarAbierto(false)
      setConfirmadoParaConfirmar(false)
      setAviso('Compra confirmada: el stock y el costo ya se actualizaron.')
    } catch (e) {
      confirmandoRef.current = false
      setConfirmando(false)
      // El perdedor de la carrera de doble confirm (409 compra_no_es_borrador) se muestra tal
      // cual, mismo criterio verbatim que el resto de esta pantalla (react-async-state regla 10 —
      // Transferencias.tsx replica la misma copia de recuperación para su sibling error).
      setErrorConfirmar(e instanceof ErrorApi ? e.message : 'No se pudo confirmar la compra.')
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
          <Link className="btn btn-outline-secondary rounded-0" to="/compras">
            Volver a compras
          </Link>
        </Box>
      </div>
    )
  }

  const esBorrador = esNuevo || compra?.estado === 'Borrador'
  const esConfirmada = compra?.estado === 'Confirmada'
  const tienePreciosSugeridos = compra?.items.some((i) => i.precioSugerido !== null) ?? false

  return (
    <div className="container-fluid py-4">
      <Box
        titulo={esNuevo ? 'Nueva compra' : `Compra ${compra?.numeroExterno ?? `#${idCompra}`}`}
        variante="inverse"
        herramientas={
          <Link className="btn btn-sm btn-outline-light rounded-0" to="/compras">
            Volver a compras
          </Link>
        }
      >
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {errorReferencia && (
          <div className="alert alert-warning rounded-0 py-1 px-2 small">
            {errorReferencia} No se pueden registrar operaciones de compra hasta que esto se resuelva.
          </div>
        )}

        {!esNuevo && compra && (
          <div className="mb-3">
            <span className={`badge rounded-0 me-2 ${esBorrador ? 'text-bg-secondary' : esConfirmada ? 'text-bg-success' : 'text-bg-danger'}`}>
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
              className="form-select rounded-0"
              value={encabezado.idProveedor}
              disabled={!esBorrador || ocupado || !referenciaOk || !puedeEscribir}
              onChange={(e) => setEncabezado((prev) => ({ ...prev, idProveedor: e.target.value === '' ? '' : Number(e.target.value) }))}
            >
              <option value="">Elegir…</option>
              {(proveedores ?? []).map((p) => (
                <option key={p.id} value={p.id}>
                  {p.razonSocial}
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
              className="form-select rounded-0"
              value={encabezado.idTipoComprobante}
              disabled={!esBorrador || ocupado || !referenciaOk || !puedeEscribir}
              onChange={(e) =>
                setEncabezado((prev) => ({ ...prev, idTipoComprobante: e.target.value === '' ? '' : Number(e.target.value) }))
              }
            >
              <option value="">Elegir…</option>
              {(tipos ?? []).map((t) => (
                <option key={t.id} value={t.id}>
                  {t.codigo}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-3">
            <label className="form-label" htmlFor="compra-punto-venta">
              Punto de venta
            </label>
            <select
              id="compra-punto-venta"
              className="form-select rounded-0"
              value={encabezado.idPuntoVenta}
              disabled={!esBorrador || ocupado || !referenciaOk || !puedeEscribir}
              onChange={(e) => setEncabezado((prev) => ({ ...prev, idPuntoVenta: e.target.value === '' ? '' : Number(e.target.value) }))}
            >
              <option value="">Elegir…</option>
              {(puntosVenta ?? []).map((pv) => (
                <option key={pv.id} value={pv.id}>
                  {pv.nombre}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="compra-numero-externo">
              Número de comprobante
            </label>
            <input
              id="compra-numero-externo"
              type="text"
              className="form-control rounded-0"
              value={encabezado.numeroExterno}
              disabled={!esBorrador || ocupado || !puedeEscribir}
              onChange={(e) => setEncabezado((prev) => ({ ...prev, numeroExterno: e.target.value }))}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="compra-fecha-comprobante">
              Fecha del comprobante
            </label>
            <input
              id="compra-fecha-comprobante"
              type="date"
              className="form-control rounded-0"
              value={encabezado.fechaComprobante}
              disabled={!esBorrador || ocupado || !puedeEscribir}
              onChange={(e) => setEncabezado((prev) => ({ ...prev, fechaComprobante: e.target.value }))}
            />
          </div>
          <div className="col-12">
            <label className="form-label" htmlFor="compra-observaciones">
              Observaciones
            </label>
            <input
              id="compra-observaciones"
              type="text"
              className="form-control rounded-0"
              value={encabezado.observaciones}
              disabled={!esBorrador || ocupado || !puedeEscribir}
              onChange={(e) => setEncabezado((prev) => ({ ...prev, observaciones: e.target.value }))}
            />
          </div>
        </div>

        {esBorrador ? (
          <>
            <div className="table-responsive">
              <table className="table table-sm table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Artículo</th>
                    <th>Lote</th>
                    <th>Unidades</th>
                    <th>Bultos</th>
                    <th>Un./bulto</th>
                    <th>Costo unitario</th>
                    <th>Descuento</th>
                    <th>IVA</th>
                    <th>Act. costo</th>
                    <th className="text-end">Total</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  {lineas.map((l) => (
                    <FilaDeItem
                      key={l.clave}
                      linea={l}
                      alicuotas={alicuotas ?? []}
                      disabled={ocupado || !referenciaOk || !puedeEscribir}
                      discriminaIva={discriminaIva}
                      porcentajePorAlicuota={porcentajePorAlicuota}
                      onCambio={cambiarLinea}
                      onQuitar={quitarLinea}
                    />
                  ))}
                  {lineas.length === 0 && (
                    <tr>
                      <td colSpan={11} className="text-center text-muted py-3">
                        Todavía no hay items cargados.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            {puedeEscribir && (
              <button
                type="button"
                className="btn btn-outline-secondary btn-sm rounded-0 mb-3"
                disabled={ocupado || !referenciaOk}
                onClick={agregarLinea}
              >
                + Agregar línea
              </button>
            )}

            <div className="row g-3 mb-3">
              <div className="col-md-3">
                <div className="small text-muted">Subtotal (mirror, no autoritativo)</div>
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
              <div className="col-md-3">
                <div className="small text-muted">Total</div>
                <div className="fs-6">{formatearMoneda(totales.total)}</div>
              </div>
            </div>

            {lineasIncompletas > 0 && (
              <div className="alert alert-warning rounded-0 py-1 px-2 small mb-3">
                {lineasIncompletas} línea(s) incompleta(s) — no se van a guardar.
              </div>
            )}

            <div className="d-flex gap-2 mb-3">
              {puedeEscribir && (
                <button type="button" className="btn btn-primary rounded-0" disabled={!puedeGuardar} onClick={guardarBorrador}>
                  {guardando ? 'Guardando…' : esNuevo ? 'Crear borrador' : 'Guardar borrador'}
                </button>
              )}
              {!esNuevo && puedeEscribir && (
                <button
                  type="button"
                  className="btn btn-success rounded-0"
                  disabled={ocupado || !referenciaOk}
                  onClick={() => setPanelConfirmarAbierto(true)}
                >
                  Confirmar compra
                </button>
              )}
            </div>

            {panelConfirmarAbierto && (
              <div className="border p-3 mb-3">
                <strong>Confirmar compra</strong>
                {errorConfirmar && <div className="alert alert-danger rounded-0 py-1 px-2 small mt-2">{errorConfirmar}</div>}
                <div className="form-check my-2">
                  <input
                    id="compra-confirmacion-confirmar"
                    type="checkbox"
                    className="form-check-input rounded-0"
                    checked={confirmadoParaConfirmar}
                    disabled={confirmando}
                    onChange={(e) => setConfirmadoParaConfirmar(e.target.checked)}
                  />
                  <label className="form-check-label" htmlFor="compra-confirmacion-confirmar">
                    Confirmo que quiero confirmar esta compra. Es irreversible: el stock entra, el costo del artículo
                    se actualiza y no se puede volver a borrador.
                  </label>
                </div>
                <div className="d-flex gap-2">
                  <button
                    type="button"
                    className="btn btn-outline-secondary rounded-0"
                    disabled={confirmando}
                    onClick={() => {
                      setPanelConfirmarAbierto(false)
                      setConfirmadoParaConfirmar(false)
                    }}
                  >
                    Cancelar
                  </button>
                  <button type="button" className="btn btn-success rounded-0" disabled={!confirmadoParaConfirmar || confirmando} onClick={confirmar}>
                    {confirmando ? 'Confirmando…' : 'Confirmar'}
                  </button>
                </div>
              </div>
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

              {resultadoAnulacion && (
                <div className="alert alert-warning rounded-0">
                  Compra anulada. {resultadoAnulacion.gastosLigados > 0
                    ? `Quedan ${resultadoAnulacion.gastosLigados} gasto(s) ligado(s) a esta compra sin desvincular — la anulación no los revierte, quedan como historial de un pago ya realizado.`
                    : 'No había ningún gasto ligado a esta compra.'}
                </div>
              )}

              {esConfirmada && puedeEscribir && (
                <>
                  <div className="d-flex gap-2 mb-3">
                    <button
                      type="button"
                      className="btn btn-danger rounded-0"
                      disabled={ocupado}
                      onClick={() => setPanelAnularAbierto(true)}
                    >
                      Anular compra
                    </button>
                  </div>

                  {panelAnularAbierto && (
                    <div className="border p-3 mb-3">
                      <strong>Anular compra</strong>
                      {errorAnular && <div className="alert alert-danger rounded-0 py-1 px-2 small mt-2">{errorAnular}</div>}
                      <div className="form-check my-2">
                        <input
                          id="compra-confirmacion-anular"
                          type="checkbox"
                          className="form-check-input rounded-0"
                          checked={confirmadoParaAnular}
                          disabled={anulando}
                          onChange={(e) => setConfirmadoParaAnular(e.target.checked)}
                        />
                        <label className="form-check-label" htmlFor="compra-confirmacion-anular">
                          Confirmo que quiero anular esta compra. Es irreversible: se revierte el stock que entró, el
                          costo del artículo NO se corrige solo (se edita aparte).
                        </label>
                      </div>
                      <div className="d-flex gap-2">
                        <button
                          type="button"
                          className="btn btn-outline-secondary rounded-0"
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
                          className="btn btn-danger rounded-0"
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
  const esNuevo = id === undefined || id === 'nueva'
  const idNumerico = esNuevo ? null : Number(id)
  const idValido = esNuevo || Number.isFinite(idNumerico)

  if (!idValido) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Compra" variante="warning">
          <p className="text-muted">No se especificó una compra válida.</p>
          <Link className="btn btn-outline-secondary rounded-0" to="/compras">
            Volver a compras
          </Link>
        </Box>
      </div>
    )
  }

  return <PantallaCompraEditor key={idNumerico ?? 'nuevo'} idCompra={idNumerico} />
}
