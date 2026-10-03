import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import {
  aSolicitudDeCompra,
  calcularTotalesDeCompra,
  clienteDeCompras,
  discriminaIvaEfectivo,
  etiquetaDeEstadoCompra,
  itemAFormulario,
  ivaImpresoDesdeDetalle,
  lineaCompletaParaEnvio,
  lineaDeCompraVacia,
  lineaDeConceptoVacia,
  lineaDesdeCoberturaDeOrden,
  lineaDesdeTotal,
  lineaFormularioACalculo,
  lineaConDescuentoInvalido,
  percepcionesAplicables,
  sePuedePagarLaCompra,
  type EncabezadoDeCompraFormulario,
  type LineaDeCompraFormulario,
} from '../api/compras'
import { clienteDeArticulos } from '../api/articulos'
import { clienteDeCatalogosFiscales } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeGastos, clienteDeGastosDeAdministracion } from '../api/gastos'
import { clienteDeOrdenesDeCompra } from '../api/ordenesDeCompra'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDePrecios } from '../api/precios'
import { ROL } from '../api/tipos'
import type {
  AlicuotaIvaListado,
  ArticuloListado,
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
import { CampoImporte } from '../componentes/CampoImporte'
import { Cargando } from '../componentes/Cargando'
import { ModalDePagoDeCompra } from '../componentes/ModalDePagoDeCompra'
import { formatearImporte } from '../formato/importes'
import { DesgloseDeIvaDeCompra } from './DesgloseDeIvaDeCompra'
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

function formatearMoneda(valor: number | null): string {
  return formatearImporte(valor, { simbolo: true })
}

function formatearFechaHora(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString('es-AR') : '—'
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
        className="form-control form-control-sm"
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
  const esConcepto = linea.tipo === 'concepto'

  return (
    <tr className={incompleta ? 'table-warning text-muted' : undefined}>
      <td style={{ minWidth: 220 }}>
        {esConcepto ? (
          <>
            <span className="badge text-bg-info mb-1">Concepto</span>
            <input
              type="text"
              className="form-control form-control-sm"
              aria-label="Descripción del concepto"
              placeholder="Descripción (ej. Flete)"
              value={linea.descripcion}
              disabled={disabled}
              onChange={(e) => onCambio(linea.clave, { descripcion: e.target.value })}
            />
          </>
        ) : (
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
        )}
        {incompleta && <div className="small text-warning-emphasis">Línea incompleta — no se va a guardar.</div>}
      </td>
      <td style={{ minWidth: 180 }}>
        {esConcepto ? (
          <span className="text-muted small">—</span>
        ) : linea.controlaLote ? (
          <>
            <input
              type="text"
              className="form-control form-control-sm mb-1"
              aria-label="Código de lote"
              placeholder="Código de lote (opcional)"
              value={linea.codigoLote}
              disabled={disabled}
              onChange={(e) => onCambio(linea.clave, { codigoLote: e.target.value })}
            />
            <input
              type="date"
              className={`form-control form-control-sm ${linea.fechaVencimiento.trim() === '' ? 'is-invalid' : ''}`}
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
          className="form-control form-control-sm"
          aria-label="Unidades"
          value={linea.unidades}
          disabled={disabled}
          onChange={(e) => onCambio(linea.clave, { unidades: e.target.value })}
        />
      </td>
      <td style={{ width: 80 }}>
        {esConcepto ? (
          <span className="text-muted small">—</span>
        ) : (
          <input
            type="number"
            step="1"
            min="0"
            className="form-control form-control-sm"
            aria-label="Bultos"
            value={linea.bultos}
            disabled={disabled}
            onChange={(e) => onCambio(linea.clave, { bultos: e.target.value })}
          />
        )}
      </td>
      <td style={{ width: 100 }}>
        {esConcepto ? (
          <span className="text-muted small">—</span>
        ) : (
          <input
            type="number"
            step="0.001"
            min="0"
            className="form-control form-control-sm"
            aria-label="Unidades por bulto"
            value={linea.unidadesPorBulto}
            disabled={disabled}
            onChange={(e) => onCambio(linea.clave, { unidadesPorBulto: e.target.value })}
          />
        )}
      </td>
      <td style={{ width: 110 }}>
        <CampoImporte
          className="form-control form-control-sm"
          aria-label="Costo unitario"
          decimales={4}
          valor={linea.costoUnitario}
          disabled={disabled}
          onChange={(v) => onCambio(linea.clave, { costoUnitario: v })}
        />
      </td>
      <td style={{ width: 100 }}>
        <CampoImporte
          className={`form-control form-control-sm ${descuentoInvalido ? 'is-invalid' : ''}`}
          aria-label="Descuento"
          valor={linea.descuento}
          disabled={disabled}
          onChange={(v) => onCambio(linea.clave, { descuento: v })}
        />
        {descuentoInvalido && <div className="invalid-feedback">Mayor al bruto de la línea.</div>}
      </td>
      <td style={{ width: 100 }}>
        <select
          className="form-select form-select-sm"
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
        {esConcepto ? (
          <span className="text-muted small">—</span>
        ) : (
          <input
            type="checkbox"
            className="form-check-input"
            aria-label="Actualiza costo"
            checked={linea.actualizaCosto}
            disabled={disabled}
            onChange={(e) => onCambio(linea.clave, { actualizaCosto: e.target.checked })}
          />
        )}
      </td>
      <td className="text-end">{formatearMoneda(item.total)}</td>
      <td>
        <button
          type="button"
          className="btn btn-outline-danger btn-sm"
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

type PropsPantalla = { idCompra: number | null; idOrdenCompra: number | null; idDesdeGasto: number | null }

/** Remontada por `key={idCompra ?? 'nuevo-' + (idOrdenCompra ?? 's')}` (react-async-state regla 8)
 * — ningún estado de acá (borrador en edición, paneles de confirmar/anular) sobrevive a un cambio
 * de compra. */
function PantallaCompraEditor({ idCompra, idOrdenCompra, idDesdeGasto }: PropsPantalla) {
  const navigate = useNavigate()
  const { usuario } = useAuth()
  const puedeEscribir = usuario !== null && usuario.rolId === ROL.Admin

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
  const [lineas, setLineas] = useState<LineaDeCompraFormulario[]>([])

  useEffect(() => {
    if (ordenParaPrecargar === null) return
    setEncabezado((prev) => ({
      ...prev,
      idProveedor: ordenParaPrecargar.idProveedor,
      idPuntoVenta: ordenParaPrecargar.idPuntoVenta,
      idOrdenCompra: ordenParaPrecargar.id,
    }))
    const pendientes = ordenParaPrecargar.cobertura.filter((c) => c.pendiente > 0)
    setLineas(pendientes.map((c) => lineaDesdeCoberturaDeOrden(proximaClaveRef.current++, c, ordenParaPrecargar.items)))
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
    setLineas(compra.items.map((item) => itemAFormulario(proximaClaveRef.current++, item)))
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
  // medio llenar nunca debe sumar al Subtotal en pantalla, porque tampoco se va a guardar.
  const lineasCompletas = useMemo(() => lineas.filter(lineaCompletaParaEnvio), [lineas])
  const lineasIncompletas = lineas.length - lineasCompletas.length
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

  const [panelTotalAbierto, setPanelTotalAbierto] = useState(false)
  const [totalImporte, setTotalImporte] = useState<number | null>(null)
  const [totalDescripcion, setTotalDescripcion] = useState('')
  const [totalIdAlicuota, setTotalIdAlicuota] = useState<number | ''>('')

  const [guardando, setGuardando] = useState(false)
  const guardandoRef = useRef(false)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')

  const [confirmando, setConfirmando] = useState(false)
  const confirmandoRef = useRef(false)
  const [panelConfirmarAbierto, setPanelConfirmarAbierto] = useState(false)
  const [confirmadoParaConfirmar, setConfirmadoParaConfirmar] = useState(false)
  const [errorConfirmar, setErrorConfirmar] = useState('')

  // ---- vínculo automático con el gasto de origen, DESPUÉS de confirmar (stage-gasto-a-compra,
  // PR4): un fallo acá NUNCA revierte la confirmación — la compra queda confirmada igual, con un
  // botón para reintentar el vínculo solo. --------------------------------------------------------
  const [gastoVinculado, setGastoVinculado] = useState(false)
  const [vinculandoGasto, setVinculandoGasto] = useState(false)
  const vinculandoGastoRef = useRef(false)
  const [errorVincularGasto, setErrorVincularGasto] = useState('')

  async function vincularConGastoDeOrigen(idCompraConfirmada: number) {
    if (idDesdeGasto === null || vinculandoGastoRef.current) return
    vinculandoGastoRef.current = true
    setVinculandoGasto(true)
    setErrorVincularGasto('')

    try {
      await clienteDeGastos.vincularCompra(idDesdeGasto, idCompraConfirmada)
      setGastoVinculado(true)
    } catch (e) {
      setErrorVincularGasto(e instanceof ErrorApi ? e.message : 'No se pudo vincular el gasto de origen a esta compra.')
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

  const ocupado = guardando || confirmando || anulando || aplicandoPrecios || vinculandoGasto

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

  function cambiarProveedor(valor: string) {
    if (ocupado) return
    const hayCostoTipeado = lineas.some((l) => l.costoUnitario !== null)
    setEncabezado((prev) =>
      alElegirProveedor(prev, valor === '' ? '' : Number(valor), referenciaDePercepciones, hayCostoTipeado),
    )
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
    // regla 1: updater funcional, nunca lee `lineas` del cierre.
    setLineas((prev) => prev.map((l) => (l.clave === clave ? { ...l, ...cambios } : l)))
  }

  function agregarLinea() {
    if (ocupado) return
    setLineas((prev) => [...prev, lineaDeCompraVacia(proximaClaveRef.current++)])
  }

  function agregarConcepto() {
    if (ocupado) return
    setLineas((prev) => [...prev, lineaDeConceptoVacia(proximaClaveRef.current++)])
  }

  function abrirCargaPorTotal() {
    if (ocupado) return
    const alicuotaHabitual = (alicuotas ?? []).find((a) => a.porcentaje === 21) ?? (alicuotas ?? [])[0]
    setTotalIdAlicuota(alicuotaHabitual?.id ?? '')
    setTotalImporte(null)
    setTotalDescripcion('Total del comprobante')
    setPanelTotalAbierto(true)
  }

  function cargarPorTotal() {
    if (ocupado || totalImporte === null || totalImporte <= 0 || totalDescripcion.trim() === '') return
    setLineas((prev) => [
      ...prev,
      lineaDesdeTotal(proximaClaveRef.current++, totalDescripcion.trim(), totalImporte, totalIdAlicuota),
    ])
    setPanelTotalAbierto(false)
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
      const solicitud = aSolicitudDeCompra(
        { ...encabezado, percepciones: percepcionesResueltas },
        lineas,
        discriminaIva,
        registraLibroIva,
      )
      if (esNuevo) {
        const creada = await clienteDeCompras.crear(solicitud)
        guardandoRef.current = false
        setGuardando(false)
        // Remontaje completo vía cambio de `key` (regla 8): navega a la ruta real de la compra
        // recién creada, nunca reutiliza este estado de "nuevo" para simular la edición.
        // `desdeGasto` viaja en la nueva URL — sin esto, el remontaje (nuevo → existente) perdería
        // el vínculo pendiente y `confirmar()` no sabría a qué gasto ligar.
        navigate(`/compras/${creada.id}${idDesdeGasto !== null ? `?desdeGasto=${idDesdeGasto}` : ''}`, { replace: true })
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
      setAviso(
        confirmada.items.some((i) => i.idArticulo !== null)
          ? 'Compra confirmada: el stock y el costo ya se actualizaron.'
          : 'Compra confirmada.',
      )

      // El vínculo con el gasto de origen corre DESPUÉS de que la confirmación ya comiteó — un
      // fallo acá se muestra aparte (errorVincularGasto) y nunca deshace la confirmación.
      if (idDesdeGasto !== null) {
        void vincularConGastoDeOrigen(confirmada.id)
      }
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
        {error && <div className="alert alert-danger">{error}</div>}
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
              onClick={() => compra && void vincularConGastoDeOrigen(compra.id)}
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
              onChange={(e) => cambiarProveedor(e.target.value)}
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
              className="form-select"
              value={encabezado.idTipoComprobante}
              disabled={!esBorrador || ocupado || !referenciaOk || !puedeEscribir}
              onChange={(e) => cambiarTipo(e.target.value)}
            >
              <option value="">Elegir…</option>
              {(tipos ?? []).map((t) => (
                <option key={t.id} value={t.id} title={t.nombre}>
                  {t.codigo}
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
          <div className="col-md-3">
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
          <div className="col-md-2">
            <label className="form-label" htmlFor="compra-numero-externo">
              Número de comprobante
            </label>
            <input
              id="compra-numero-externo"
              type="text"
              className="form-control"
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
              className="form-control"
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
              className="form-control"
              value={encabezado.observaciones}
              disabled={!esBorrador || ocupado || !puedeEscribir}
              onChange={(e) => setEncabezado((prev) => ({ ...prev, observaciones: e.target.value }))}
            />
          </div>
        </div>

        {mostrarFormulario ? (
          <>
            <div className="table-responsive">
              <table className="table table-sm table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Artículo / concepto</th>
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
              <div className="d-flex flex-wrap gap-2 mb-3">
                <button
                  type="button"
                  className="btn btn-outline-secondary btn-sm"
                  disabled={ocupado || !referenciaOk}
                  onClick={agregarLinea}
                >
                  + Agregar línea
                </button>
                <button
                  type="button"
                  className="btn btn-outline-secondary btn-sm"
                  disabled={ocupado || !referenciaOk}
                  onClick={agregarConcepto}
                >
                  + Agregar concepto
                </button>
                <button
                  type="button"
                  className="btn btn-outline-secondary btn-sm"
                  disabled={ocupado || !referenciaOk}
                  onClick={abrirCargaPorTotal}
                >
                  Cargar por total
                </button>
              </div>
            )}

            {puedeEscribir && panelTotalAbierto && (
              <div className="border p-3 mb-3">
                <strong>Cargar por total</strong>
                <div className="small text-muted mb-2">
                  {discriminaIva
                    ? preciosFinales
                      ? 'Este comprobante discrimina IVA y sus precios lo incluyen: cargá el importe final, el IVA se discrimina.'
                      : 'Este tipo de comprobante discrimina IVA: cargá el importe neto, el IVA se suma.'
                    : 'Cargá el total del comprobante.'}{' '}
                  Se agrega una sola línea por concepto: no mueve stock ni actualiza costos.
                </div>
                <div className="row g-2 align-items-end">
                  <div className="col-md-4">
                    <label className="form-label" htmlFor="compra-total-descripcion">
                      Descripción
                    </label>
                    <input
                      id="compra-total-descripcion"
                      type="text"
                      className="form-control form-control-sm"
                      value={totalDescripcion}
                      disabled={ocupado}
                      onChange={(e) => setTotalDescripcion(e.target.value)}
                    />
                  </div>
                  <div className="col-md-3">
                    <label className="form-label" htmlFor="compra-total-importe">
                      Importe total
                    </label>
                    <CampoImporte
                      id="compra-total-importe"
                      className="form-control form-control-sm"
                      valor={totalImporte}
                      disabled={ocupado}
                      onChange={setTotalImporte}
                    />
                  </div>
                  <div className="col-md-2">
                    <label className="form-label" htmlFor="compra-total-alicuota">
                      IVA del total
                    </label>
                    <select
                      id="compra-total-alicuota"
                      className="form-select form-select-sm"
                      value={totalIdAlicuota}
                      disabled={ocupado}
                      onChange={(e) => setTotalIdAlicuota(e.target.value === '' ? '' : Number(e.target.value))}
                    >
                      <option value="">Elegir…</option>
                      {(alicuotas ?? []).map((al) => (
                        <option key={al.id} value={al.id}>
                          {al.nombre}
                        </option>
                      ))}
                    </select>
                  </div>
                  <div className="col-md-3 d-flex gap-2">
                    <button
                      type="button"
                      className="btn btn-primary btn-sm"
                      disabled={
                        ocupado ||
                        totalImporte === null ||
                        totalImporte <= 0 ||
                        totalDescripcion.trim() === '' ||
                        totalIdAlicuota === ''
                      }
                      onClick={cargarPorTotal}
                    >
                      Agregar como concepto
                    </button>
                    <button
                      type="button"
                      className="btn btn-outline-secondary btn-sm"
                      onClick={() => setPanelTotalAbierto(false)}
                    >
                      Cancelar
                    </button>
                  </div>
                </div>
              </div>
            )}

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

            {lineasIncompletas > 0 && (
              <div className="alert alert-warning py-1 px-2 small mb-3">
                {lineasIncompletas} línea(s) incompleta(s) — no se van a guardar.
              </div>
            )}

            <div className="d-flex gap-2 mb-3">
              {puedeEscribir && (
                <button type="button" className="btn btn-primary" disabled={!puedeGuardar} onClick={guardarBorrador}>
                  {guardando ? 'Guardando…' : esNuevo ? 'Crear borrador' : 'Guardar borrador'}
                </button>
              )}
              {!esNuevo && puedeEscribir && (
                <button
                  type="button"
                  className="btn btn-success"
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
                {errorConfirmar && <div className="alert alert-danger py-1 px-2 small mt-2">{errorConfirmar}</div>}
                <div className="form-check my-2">
                  <input
                    id="compra-confirmacion-confirmar"
                    type="checkbox"
                    className="form-check-input"
                    checked={confirmadoParaConfirmar}
                    disabled={confirmando}
                    onChange={(e) => setConfirmadoParaConfirmar(e.target.checked)}
                  />
                  <label className="form-check-label" htmlFor="compra-confirmacion-confirmar">
                    {tieneArticulos
                      ? 'Confirmo que quiero confirmar esta compra. Es irreversible: el stock entra, el costo del artículo se actualiza y no se puede volver a borrador.'
                      : 'Confirmo que quiero confirmar esta compra. Es irreversible: no se puede volver a borrador.'}
                  </label>
                </div>
                <div className="d-flex gap-2">
                  <button
                    type="button"
                    className="btn btn-outline-secondary"
                    disabled={confirmando}
                    onClick={() => {
                      setPanelConfirmarAbierto(false)
                      setConfirmadoParaConfirmar(false)
                    }}
                  >
                    Cancelar
                  </button>
                  <button type="button" className="btn btn-success" disabled={!confirmadoParaConfirmar || confirmando} onClick={confirmar}>
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
