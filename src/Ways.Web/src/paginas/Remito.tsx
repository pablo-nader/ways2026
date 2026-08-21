import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { clienteDeArticulos } from '../api/articulos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import {
  aSolicitudDeRemito,
  claseDeBadgeDeEstadoRemito,
  clienteDeRemitos,
  encabezadoDeRemitoVacio,
  etiquetaDeEstadoRemito,
  itemDeRemitoAFormulario,
  lineaDeRemitoCompletaParaEnvio,
  lineaDeRemitoVacia,
  type EncabezadoDeRemitoFormulario,
  type LineaDeRemitoFormulario,
} from '../api/remitos'
import type { ArticuloListado, ClienteListado, ComprobanteEmitido, RemitoDetalle, PuntoVentaListado } from '../api/tipos'
import { clienteDeVentas } from '../api/ventas'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { SelectorDeLote } from '../componentes/SelectorDeLote'

function formatearFechaHora(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString('es-AR') : '—'
}

function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

function etiquetaDeCliente(c: ClienteListado): string {
  const nombreCompleto = c.razonSocial ?? [c.nombre, c.apellido].filter(Boolean).join(' ')
  return `#${c.numero} — ${nombreCompleto}`
}

// ---- Selector de artículo por búsqueda (search-as-you-type) — mismo shape que el de
// Presupuesto.tsx/OrdenDeCompra.tsx/CompraEditor.tsx, propio de cada fila ---------------------------

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

// ---- Fila editable del grid de items — con el pick de lote (design.md: "lot picker reusing
// SelectorDeLote") sobre el mismo `idPuntoVenta` del encabezado ------------------------------------

type PropsFilaDeItem = {
  linea: LineaDeRemitoFormulario
  idPuntoVenta: number | ''
  disabled: boolean
  onCambio: (clave: number, cambios: Partial<LineaDeRemitoFormulario>) => void
  onQuitar: (clave: number) => void
}

function FilaDeItem({ linea, idPuntoVenta, disabled, onCambio, onQuitar }: PropsFilaDeItem) {
  const incompleta = !lineaDeRemitoCompletaParaEnvio(linea)

  return (
    <tr className={incompleta ? 'table-warning text-muted' : undefined}>
      <td style={{ minWidth: 220 }}>
        <SelectorDeArticulo
          descripcion={linea.descripcion}
          disabled={disabled}
          onElegir={(a) => onCambio(linea.clave, { idArticulo: a.id, descripcion: a.nombre, idLote: null })}
        />
        {incompleta && <div className="small text-warning-emphasis">Línea incompleta — no se va a guardar.</div>}
      </td>
      <td style={{ width: 120 }}>
        <input
          type="number"
          step="0.001"
          min="0"
          className="form-control form-control-sm rounded-0"
          aria-label="Cantidad"
          value={linea.cantidad}
          disabled={disabled}
          onChange={(e) => onCambio(linea.clave, { cantidad: e.target.value })}
        />
      </td>
      <td style={{ minWidth: 160 }}>
        {linea.idArticulo !== '' && idPuntoVenta !== '' ? (
          <SelectorDeLote
            idPuntoVenta={idPuntoVenta}
            idArticulo={linea.idArticulo}
            nombreArticulo={linea.descripcion || `Artículo #${linea.idArticulo}`}
            idLoteElegido={linea.idLote}
            disabled={disabled}
            onElegir={(idLote) => onCambio(linea.clave, { idLote })}
          />
        ) : (
          <span className="small text-muted">—</span>
        )}
      </td>
      <td>
        <button type="button" className="btn btn-outline-danger btn-sm rounded-0" disabled={disabled} onClick={() => onQuitar(linea.clave)}>
          Quitar
        </button>
      </td>
    </tr>
  )
}

// ---- Pantalla principal -----------------------------------------------------------------------

type PropsPantalla = { idRemito: number | null }

/** Remontada por `key={idRemito ?? 'nuevo'}` (react-async-state regla 8) — ningún estado de acá
 * (borrador en edición, avisos, paneles) sobrevive a un cambio de remito. */
function PantallaRemito({ idRemito }: PropsPantalla) {
  const navigate = useNavigate()
  const esNuevo = idRemito === null

  // ---- referencia (puntos de venta/clientes) ----------------------------------------------------
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [clientes, setClientes] = useState<ClienteListado[] | null>(null)
  const [errorReferencia, setErrorReferencia] = useState('')

  useEffect(() => {
    let vigente = true
    api
      .get<PuntoVentaListado[]>('/puntos-venta')
      .then((lista) => vigente && setPuntosVenta(lista))
      .catch((e) => {
        setPuntosVenta([])
        setErrorReferencia((prev) => (prev ? prev : e instanceof ErrorApi ? e.message : 'No se pudieron cargar los puntos de venta.'))
      })

    clienteDeClientes
      .listar('', false)
      .then((p) => vigente && setClientes(p.items))
      .catch((e) => {
        setClientes([])
        setErrorReferencia((prev) => (prev ? prev : e instanceof ErrorApi ? e.message : 'No se pudieron cargar los clientes.'))
      })

    return () => {
      vigente = false
    }
  }, [])

  const referenciaOk = puntosVenta !== null && clientes !== null && errorReferencia === ''

  // ---- detalle (lectura, GET /{id} — única fuente de estado real) --------------------------------
  const [detalle, setDetalle] = useState<RemitoDetalle | null>(null)
  const [cargandoDetalle, setCargandoDetalle] = useState(!esNuevo)
  const [errorDetalle, setErrorDetalle] = useState('')
  const generacionRef = useRef(0)

  const cargarDetalle = useCallback(() => {
    if (esNuevo || idRemito === null) return
    const miGeneracion = (generacionRef.current += 1)
    setCargandoDetalle(true)
    setErrorDetalle('')

    clienteDeRemitos
      .obtener(idRemito)
      .then((d) => {
        if (generacionRef.current !== miGeneracion) return
        setDetalle(d)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        // regla 6: un refetch posterior a una escritura 2xx nunca vacía la pantalla a un error
        // total — `detalle` queda como estaba, solo el aviso de arriba se muestra.
        setErrorDetalle(
          e instanceof ErrorApi ? e.message : 'No se pudo actualizar el remito — los datos mostrados pueden estar desactualizados.',
        )
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargandoDetalle(false)
      })
  }, [esNuevo, idRemito])

  useEffect(() => {
    cargarDetalle()
  }, [cargarDetalle])

  // ---- factura del remito facturado (OD10: GET /api/ventas/{id}, el read model del TXR) ---------
  const [factura, setFactura] = useState<ComprobanteEmitido | null>(null)
  const [cargandoFactura, setCargandoFactura] = useState(false)
  const [errorFactura, setErrorFactura] = useState('')
  const facturaGeneracionRef = useRef(0)
  const idComprobanteVenta = detalle?.idComprobanteVenta ?? null

  useEffect(() => {
    setFactura(null)
    setErrorFactura('')
    if (idComprobanteVenta === null) return

    let vigente = true
    const miGeneracion = (facturaGeneracionRef.current += 1)
    setCargandoFactura(true)

    clienteDeVentas
      .obtener(idComprobanteVenta)
      .then((c) => {
        if (!vigente || facturaGeneracionRef.current !== miGeneracion) return
        setFactura(c)
      })
      .catch((e) => {
        if (!vigente || facturaGeneracionRef.current !== miGeneracion) return
        setErrorFactura(e instanceof ErrorApi ? e.message : 'No se pudo cargar la factura.')
      })
      .finally(() => {
        if (!vigente || facturaGeneracionRef.current !== miGeneracion) return
        setCargandoFactura(false)
      })

    return () => {
      vigente = false
    }
  }, [idComprobanteVenta])

  // ---- formulario editable (nuevo o borrador existente) ------------------------------------------
  const proximaClaveRef = useRef(1)
  const [encabezado, setEncabezado] = useState<EncabezadoDeRemitoFormulario>(encabezadoDeRemitoVacio())
  const [lineas, setLineas] = useState<LineaDeRemitoFormulario[]>([])

  useEffect(() => {
    if (detalle === null) return
    setEncabezado({
      idPuntoVenta: detalle.idPuntoVenta,
      idCliente: detalle.idCliente,
      direccionEntrega: detalle.direccionEntrega ?? '',
      observaciones: detalle.observaciones ?? '',
    })
    setLineas(detalle.items.map((item) => itemDeRemitoAFormulario(proximaClaveRef.current++, item)))
  }, [detalle])

  // ---- escrituras: guardar/emitir/anular — cada una con su propio guard de reentrancia
  // (react-async-state regla 9) y su propio flag de "en vuelo" (regla 5) ---------------------------
  const [guardando, setGuardando] = useState(false)
  const guardandoRef = useRef(false)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')

  const [emitiendo, setEmitiendo] = useState(false)
  const emitiendoRef = useRef(false)
  const [errorEmitir, setErrorEmitir] = useState('')

  const [anulando, setAnulando] = useState(false)
  const anulandoRef = useRef(false)
  const [errorAnular, setErrorAnular] = useState('')

  const ocupado = guardando || emitiendo || anulando

  function cambiarLinea(clave: number, cambios: Partial<LineaDeRemitoFormulario>) {
    if (ocupado) return
    // regla 1: updater funcional, nunca lee `lineas` del cierre.
    setLineas((prev) => prev.map((l) => (l.clave === clave ? { ...l, ...cambios } : l)))
  }

  function agregarLinea() {
    if (ocupado) return
    setLineas((prev) => [...prev, lineaDeRemitoVacia(proximaClaveRef.current++)])
  }

  function quitarLinea(clave: number) {
    if (ocupado) return
    setLineas((prev) => prev.filter((l) => l.clave !== clave))
  }

  const encabezadoCompleto = encabezado.idPuntoVenta !== ''
  const puedeGuardar = referenciaOk && encabezadoCompleto && !ocupado

  async function guardarBorrador() {
    // regla 9: guard de reentrancia de primera línea.
    if (guardandoRef.current) return
    if (!puedeGuardar) return

    guardandoRef.current = true
    setGuardando(true)
    setError('')
    setAviso('')
    // regla 3: bumpear la generación de carga ANTES de la escritura.
    generacionRef.current += 1

    try {
      const solicitud = aSolicitudDeRemito(encabezado, lineas)
      if (esNuevo) {
        const creado = await clienteDeRemitos.crear(solicitud)
        guardandoRef.current = false
        setGuardando(false)
        // Remontaje completo vía cambio de `key` (regla 8): navega a la ruta real del remito
        // recién creado, nunca reutiliza este estado de "nuevo" para simular la edición.
        navigate(`/remitos/${creado.id}`, { replace: true })
      } else if (idRemito !== null) {
        await clienteDeRemitos.actualizar(idRemito, solicitud)
        guardandoRef.current = false
        setGuardando(false)
        setAviso('Borrador guardado.')
        // regla 6: el refetch posterior vive aislado de este try/catch.
        cargarDetalle()
      }
    } catch (e) {
      guardandoRef.current = false
      setGuardando(false)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar el borrador.')
    }
  }

  async function emitir() {
    if (ocupado) return
    if (emitiendoRef.current || idRemito === null) return

    emitiendoRef.current = true
    setEmitiendo(true)
    setErrorEmitir('')
    generacionRef.current += 1

    try {
      await clienteDeRemitos.emitir(idRemito)
      emitiendoRef.current = false
      setEmitiendo(false)
      setAviso('Remito emitido — stock actualizado.')
      cargarDetalle()
    } catch (e) {
      emitiendoRef.current = false
      setEmitiendo(false)
      setErrorEmitir(e instanceof ErrorApi ? e.message : 'No se pudo emitir el remito.')
    }
  }

  async function anular() {
    if (ocupado) return
    if (anulandoRef.current || idRemito === null) return

    anulandoRef.current = true
    setAnulando(true)
    setErrorAnular('')
    generacionRef.current += 1

    try {
      await clienteDeRemitos.anular(idRemito)
      anulandoRef.current = false
      setAnulando(false)
      setAviso('Remito anulado.')
      cargarDetalle()
    } catch (e) {
      anulandoRef.current = false
      setAnulando(false)
      setErrorAnular(e instanceof ErrorApi ? e.message : 'No se pudo anular el remito.')
    }
  }

  if (!esNuevo && cargandoDetalle && detalle === null) {
    return (
      <div className="container-fluid py-4">
        <Cargando />
      </div>
    )
  }

  if (!esNuevo && errorDetalle && detalle === null) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Remito" variante="danger">
          <p className="text-muted">{errorDetalle}</p>
          <Link className="btn btn-outline-secondary rounded-0" to="/remitos">
            Volver a remitos
          </Link>
        </Box>
      </div>
    )
  }

  const esBorrador = esNuevo || detalle?.estado === 'Borrador'
  const esFacturado = !esNuevo && detalle?.estado === 'Facturado'
  const puedeEmitir = !esNuevo && detalle?.estado === 'Borrador'
  const puedeAnular = detalle?.estado === 'Borrador' || detalle?.estado === 'Emitido'

  return (
    <div className="container-fluid py-4">
      <Box
        titulo={esNuevo ? 'Nuevo remito' : `Remito ${detalle?.numeroFormateado ?? `#${idRemito}`}`}
        variante="inverse"
        herramientas={
          <Link className="btn btn-sm btn-outline-light rounded-0" to="/remitos">
            Volver a remitos
          </Link>
        }
      >
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {errorReferencia && (
          <div className="alert alert-warning rounded-0 py-1 px-2 small">
            {errorReferencia} No se pueden registrar operaciones de remito hasta que esto se resuelva.
          </div>
        )}
        {errorDetalle && detalle && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorDetalle}</div>}

        {!esNuevo && detalle && (
          <div className="mb-3">
            <span className={`badge rounded-0 me-2 ${claseDeBadgeDeEstadoRemito(detalle.estado)}`}>{etiquetaDeEstadoRemito(detalle.estado)}</span>
            {detalle.fechaSalida && <span className="small text-muted me-2">Salió: {formatearFechaHora(detalle.fechaSalida)}</span>}
          </div>
        )}

        {/* facturado: link a la factura (OD10, GET /api/ventas/{id}) + CERO acciones — el design
            lo pide explícito ("facturado renders its invoice link and no actions"). */}
        {esFacturado && (
          <div className="mb-3 p-2 border rounded-0 bg-light-subtle">
            {cargandoFactura && <span className="small text-muted">Cargando factura…</span>}
            {errorFactura && <span className="small text-danger">{errorFactura}</span>}
            {factura && (
              // Sin una pantalla de detalle de venta en esta web todavía (fuera del alcance de
              // este slice — ver Deviations del apply), el "link a la factura" que design.md pide
              // se muestra como la referencia identificatoria de la factura (número visible +
              // total), sourceada en vivo del read model de OD10 (`GET /api/ventas/{id}`), nunca
              // un `<Link>` a una ruta que no existe.
              <span className="small">
                Facturado en el comprobante <strong>{factura.numeroVisible}</strong> — {formatearMoneda(factura.total)}
              </span>
            )}
          </div>
        )}

        <div className="row g-2 mb-3">
          <div className="col-md-4">
            <label className="form-label" htmlFor="rem-punto-venta">
              Punto de venta
            </label>
            <select
              id="rem-punto-venta"
              className="form-select rounded-0"
              value={encabezado.idPuntoVenta}
              disabled={!esBorrador || ocupado || !referenciaOk}
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
          <div className="col-md-4">
            <label className="form-label" htmlFor="rem-cliente">
              Cliente
            </label>
            <select
              id="rem-cliente"
              className="form-select rounded-0"
              value={encabezado.idCliente}
              disabled={!esBorrador || ocupado || !referenciaOk}
              onChange={(e) => setEncabezado((prev) => ({ ...prev, idCliente: e.target.value === '' ? '' : Number(e.target.value) }))}
            >
              <option value="">Consumidor Final (por defecto)</option>
              {(clientes ?? []).map((c) => (
                <option key={c.id} value={c.id}>
                  {etiquetaDeCliente(c)}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-4">
            <label className="form-label" htmlFor="rem-direccion-entrega">
              Dirección de entrega
            </label>
            <input
              id="rem-direccion-entrega"
              type="text"
              className="form-control rounded-0"
              value={encabezado.direccionEntrega}
              disabled={!esBorrador || ocupado}
              onChange={(e) => setEncabezado((prev) => ({ ...prev, direccionEntrega: e.target.value }))}
            />
          </div>
          <div className="col-12">
            <label className="form-label" htmlFor="rem-observaciones">
              Observaciones
            </label>
            <input
              id="rem-observaciones"
              type="text"
              className="form-control rounded-0"
              value={encabezado.observaciones}
              disabled={!esBorrador || ocupado}
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
                    <th>Cantidad</th>
                    <th>Lote</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  {lineas.map((l) => (
                    <FilaDeItem
                      key={l.clave}
                      linea={l}
                      idPuntoVenta={encabezado.idPuntoVenta}
                      disabled={ocupado || !referenciaOk}
                      onCambio={cambiarLinea}
                      onQuitar={quitarLinea}
                    />
                  ))}
                  {lineas.length === 0 && (
                    <tr>
                      <td colSpan={4} className="text-center text-muted py-3">
                        Todavía no hay items cargados.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <button type="button" className="btn btn-outline-secondary btn-sm rounded-0 mb-3" disabled={ocupado || !referenciaOk} onClick={agregarLinea}>
              + Agregar línea
            </button>

            <div className="d-flex gap-2 align-items-end mb-3 flex-wrap">
              <button type="button" className="btn btn-primary rounded-0" disabled={!puedeGuardar} onClick={guardarBorrador}>
                {guardando ? 'Guardando…' : esNuevo ? 'Crear borrador' : 'Guardar borrador'}
              </button>
              {puedeEmitir && (
                <button type="button" className="btn btn-success rounded-0" disabled={ocupado} onClick={emitir}>
                  {emitiendo ? 'Emitiendo…' : 'Emitir'}
                </button>
              )}
              {puedeAnular && (
                <button type="button" className="btn btn-danger rounded-0" disabled={ocupado} onClick={anular}>
                  {anulando ? 'Anulando…' : 'Anular'}
                </button>
              )}
            </div>
            {errorEmitir && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorEmitir}</div>}
            {errorAnular && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorAnular}</div>}
          </>
        ) : (
          detalle && (
            <>
              <h6>Items</h6>
              <div className="table-responsive mb-3">
                <table className="table table-sm table-bordered align-middle">
                  <thead>
                    <tr>
                      <th>Artículo</th>
                      <th className="text-end">Cantidad</th>
                      <th className="text-end">Precio unit.</th>
                      <th className="text-end">Descuento</th>
                      <th className="text-end">Total</th>
                    </tr>
                  </thead>
                  <tbody>
                    {detalle.items.map((item) => (
                      <tr key={item.orden}>
                        <td>{item.descripcion}</td>
                        <td className="text-end">{item.cantidad}</td>
                        <td className="text-end">{formatearMoneda(item.precioUnitario)}</td>
                        <td className="text-end">{item.descuento > 0 ? formatearMoneda(item.descuento) : '—'}</td>
                        <td className="text-end">{formatearMoneda(item.total)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              <div className="row g-3 my-3">
                <div className="col-md-4">
                  <div className="small text-muted">Subtotal</div>
                  <div>{formatearMoneda(detalle.subtotal)}</div>
                </div>
                <div className="col-md-4">
                  <div className="small text-muted">Descuento</div>
                  <div>{formatearMoneda(detalle.descuentoTotal)}</div>
                </div>
                <div className="col-md-4">
                  <div className="small text-muted">Total</div>
                  <div className="fs-5">
                    <strong>{formatearMoneda(detalle.total)}</strong>
                  </div>
                </div>
              </div>

              {/* Un remito emitido (no facturado) sigue pudiendo anularse — un facturado no
                  renderiza ninguna acción (esFacturado ya cubrió su propio bloque arriba). */}
              {!esFacturado && (
                <>
                  <div className="d-flex gap-2 mb-3">
                    {puedeAnular && (
                      <button type="button" className="btn btn-danger rounded-0" disabled={ocupado} onClick={anular}>
                        {anulando ? 'Anulando…' : 'Anular'}
                      </button>
                    )}
                  </div>
                  {errorAnular && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorAnular}</div>}
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
 * Remitos — borrador/detalle (stage-17-presupuestos-y-remitos, Slice 8; design: Web composition):
 * `/remitos/nuevo` crea un borrador desde cero, `/remitos/:id` lo edita (borrador), lo emite/anula
 * (reusando `SelectorDeLote` para el pick de lote per-línea antes de emitir), o solo lo muestra —
 * un remito `facturado` renderiza el link a su factura y CERO acciones. Mismo gate que
 * `/presupuestos` (`RutaProtegida`, App.tsx).
 */
export function Remito() {
  const { id } = useParams<{ id: string }>()
  const esNuevo = id === undefined || id === 'nuevo'
  const idNumerico = esNuevo ? null : Number(id)
  const idValido = esNuevo || Number.isFinite(idNumerico)

  if (!idValido) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Remito" variante="warning">
          <p className="text-muted">No se especificó un remito válido.</p>
          <Link className="btn btn-outline-secondary rounded-0" to="/remitos">
            Volver a remitos
          </Link>
        </Box>
      </div>
    )
  }

  return <PantallaRemito key={idNumerico ?? 'nuevo'} idRemito={idNumerico} />
}
