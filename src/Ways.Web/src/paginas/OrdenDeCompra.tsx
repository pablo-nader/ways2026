import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useLocation, useNavigate, useParams } from 'react-router'
import { clienteDeArticulos } from '../api/articulos'
import { api, ErrorApi } from '../api/cliente'
import {
  aSolicitudDeOrdenDeCompra,
  claseDeBadgeDeEstadoOrdenCompra,
  clienteDeOrdenesDeCompra,
  encabezadoDeOrdenVacio,
  etiquetaDeEstadoOrdenCompra,
  formatearCantidadNullable,
  formatearDesvio,
  formatearMonedaNullable,
  itemDeOrdenAFormulario,
  lineaDeOrdenCompletaParaEnvio,
  lineaDeOrdenVacia,
  type EncabezadoDeOrdenFormulario,
  type LineaDeOrdenFormulario,
} from '../api/ordenesDeCompra'
import { ROL } from '../api/tipos'
import type {
  ArticuloListado,
  CoberturaDeArticulo,
  LineaDeOrdenSolicitada,
  OrdenDeCompraDetalle,
  PaginaDe,
  ProveedorListado,
  PuntoVentaListado,
} from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

function formatearFechaHora(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString('es-AR') : '—'
}

/** `location.state` que llega desde `Reposicion.tsx` — un `Link`/`navigate` de entrada con datos
 * ya resueltos, nunca un fetch decorativo en esta pantalla para recuperarlos (lección de la Slice
 * 6 de la etapa 15). */
type EstadoDePrecarga = { idProveedor: number; idPuntoVenta: number; items: LineaDeOrdenSolicitada[] }

// ---- Selector de artículo por búsqueda (search-as-you-type) — mismo shape que el de
// CompraEditor.tsx, propio de cada fila -----------------------------------------------------------

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

// ---- Fila editable del grid de items -------------------------------------------------------------

type PropsFilaDeItem = {
  linea: LineaDeOrdenFormulario
  disabled: boolean
  onCambio: (clave: number, cambios: Partial<LineaDeOrdenFormulario>) => void
  onQuitar: (clave: number) => void
}

function FilaDeItem({ linea, disabled, onCambio, onQuitar }: PropsFilaDeItem) {
  const incompleta = !lineaDeOrdenCompletaParaEnvio(linea)

  return (
    <tr className={incompleta ? 'table-warning text-muted' : undefined}>
      <td style={{ minWidth: 220 }}>
        <SelectorDeArticulo
          descripcion={linea.descripcion}
          disabled={disabled}
          onElegir={(a) => onCambio(linea.clave, { idArticulo: a.id, descripcion: a.nombre })}
        />
        {incompleta && <div className="small text-warning-emphasis">Línea incompleta — no se va a guardar.</div>}
      </td>
      <td style={{ width: 120 }}>
        <input
          type="number"
          step="0.001"
          min="0"
          className="form-control form-control-sm rounded-0"
          aria-label="Cantidad pedida"
          value={linea.cantidadPedida}
          disabled={disabled}
          onChange={(e) => onCambio(linea.clave, { cantidadPedida: e.target.value })}
        />
      </td>
      <td style={{ width: 140 }}>
        <input
          type="number"
          step="0.0001"
          min="0"
          className="form-control form-control-sm rounded-0"
          aria-label="Costo estimado"
          value={linea.costoUnitarioEstimado}
          disabled={disabled}
          onChange={(e) => onCambio(linea.clave, { costoUnitarioEstimado: e.target.value })}
        />
      </td>
      <td>
        <button type="button" className="btn btn-outline-danger btn-sm rounded-0" disabled={disabled} onClick={() => onQuitar(linea.clave)}>
          Quitar
        </button>
      </td>
    </tr>
  )
}

// ---- Tabla de cobertura por artículo (solo lectura) ----------------------------------------------

function TablaDeCobertura({ cobertura, descripcionPorArticulo }: { cobertura: CoberturaDeArticulo[]; descripcionPorArticulo: Record<number, string> }) {
  return (
    <div className="table-responsive">
      <table className="table table-sm table-bordered align-middle">
        <thead>
          <tr>
            <th>Artículo</th>
            <th className="text-end">Pedida</th>
            <th className="text-end">Recibida</th>
            <th className="text-end">Pendiente</th>
            <th className="text-end">Costo estimado</th>
            <th className="text-end">Costo real</th>
            <th className="text-end">Desvío</th>
          </tr>
        </thead>
        <tbody>
          {cobertura.map((c) => (
            <tr key={c.idArticulo}>
              <td>{descripcionPorArticulo[c.idArticulo] ?? `Artículo #${c.idArticulo}`}</td>
              <td className="text-end">{formatearCantidadNullable(c.pedida)}</td>
              <td className="text-end">{formatearCantidadNullable(c.recibida)}</td>
              <td className="text-end">{formatearCantidadNullable(c.pendiente)}</td>
              <td className="text-end">{formatearMonedaNullable(c.costoEstimado)}</td>
              <td className="text-end">{formatearMonedaNullable(c.costoReal)}</td>
              <td className="text-end">{formatearDesvio(c.desvio)}</td>
            </tr>
          ))}
          {cobertura.length === 0 && (
            <tr>
              <td colSpan={7} className="text-center text-muted py-3">
                Sin artículos pedidos.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}

// ---- Pantalla principal -----------------------------------------------------------------------

type PropsPantalla = { idOrden: number | null; precarga: EstadoDePrecarga | null }

/** Remontada por `key={idOrden ?? 'nueva-' + ...}` (react-async-state regla 8) — ningún estado de
 * acá (borrador en edición, avisos, paneles) sobrevive a un cambio de orden. */
function PantallaOrdenDeCompra({ idOrden, precarga }: PropsPantalla) {
  const navigate = useNavigate()
  const { usuario } = useAuth()
  const puedeEscribir = usuario !== null && usuario.rolId === ROL.Admin

  const esNuevo = idOrden === null

  // ---- referencia (proveedores/puntos de venta) ------------------------------------------------
  const [proveedores, setProveedores] = useState<ProveedorListado[] | null>(null)
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorReferencia, setErrorReferencia] = useState('')

  useEffect(() => {
    let vigente = true
    api
      .get<PaginaDe<ProveedorListado>>('/proveedores?tamanio=200')
      .then((p) => vigente && setProveedores(p.items))
      .catch((e) => {
        setProveedores([])
        setErrorReferencia((prev) => (prev ? prev : e instanceof ErrorApi ? e.message : 'No se pudieron cargar los proveedores.'))
      })

    api
      .get<PuntoVentaListado[]>('/puntos-venta')
      .then((lista) => vigente && setPuntosVenta(lista))
      .catch((e) => {
        setPuntosVenta([])
        setErrorReferencia((prev) => (prev ? prev : e instanceof ErrorApi ? e.message : 'No se pudieron cargar los puntos de venta.'))
      })

    return () => {
      vigente = false
    }
  }, [])

  const referenciaLista = proveedores !== null && puntosVenta !== null
  const referenciaOk = referenciaLista && errorReferencia === ''

  // ---- detalle (lectura, GET /{id} — es la única fuente que trae cobertura/desvío/estado real) --
  const [detalle, setDetalle] = useState<OrdenDeCompraDetalle | null>(null)
  const [cargandoDetalle, setCargandoDetalle] = useState(!esNuevo)
  const [errorDetalle, setErrorDetalle] = useState('')
  const generacionRef = useRef(0)

  const cargarDetalle = useCallback(() => {
    if (esNuevo || idOrden === null) return
    const miGeneracion = (generacionRef.current += 1)
    setCargandoDetalle(true)
    setErrorDetalle('')

    clienteDeOrdenesDeCompra
      .obtener(idOrden)
      .then((d) => {
        if (generacionRef.current !== miGeneracion) return
        setDetalle(d)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        // regla 6: un refetch posterior a una escritura 2xx (enviar/cerrar/anular/guardar) NUNCA
        // vacía la pantalla a un error total — `detalle` queda como estaba (stale, mejor que
        // nada) y solo el mensaje se muestra; el aviso de éxito de la escritura, en su propio
        // estado, sigue visible. Solo la carga INICIAL (detalle todavía null) llega al error
        // bloqueante de abajo.
        setErrorDetalle(
          e instanceof ErrorApi
            ? e.message
            : 'No se pudo actualizar la orden de compra — los datos mostrados pueden estar desactualizados.',
        )
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargandoDetalle(false)
      })
  }, [esNuevo, idOrden])

  useEffect(() => {
    cargarDetalle()
  }, [cargarDetalle])

  // ---- formulario editable (nuevo — opcionalmente precargado desde Reposición — o borrador
  // existente) -------------------------------------------------------------------------------------
  const proximaClaveRef = useRef(1)
  const [encabezado, setEncabezado] = useState<EncabezadoDeOrdenFormulario>(() =>
    precarga ? { ...encabezadoDeOrdenVacio(), idProveedor: precarga.idProveedor, idPuntoVenta: precarga.idPuntoVenta } : encabezadoDeOrdenVacio(),
  )
  const [lineas, setLineas] = useState<LineaDeOrdenFormulario[]>(() =>
    precarga
      ? precarga.items.map((item) => ({
          clave: proximaClaveRef.current++,
          idArticulo: item.idArticulo,
          descripcion: item.descripcion,
          cantidadPedida: String(item.cantidadPedida),
          costoUnitarioEstimado: item.costoUnitarioEstimado === null ? '' : String(item.costoUnitarioEstimado),
        }))
      : [],
  )

  useEffect(() => {
    if (detalle === null) return
    setEncabezado({
      idProveedor: detalle.idProveedor,
      idPuntoVenta: detalle.idPuntoVenta,
      fechaEsperada: detalle.fechaEsperada ?? '',
      observaciones: detalle.observaciones ?? '',
    })
    setLineas(detalle.items.map((item) => itemDeOrdenAFormulario(proximaClaveRef.current++, item)))
  }, [detalle])

  const descripcionPorArticulo: Record<number, string> = {}
  for (const item of detalle?.items ?? []) descripcionPorArticulo[item.idArticulo] = item.descripcion

  // ---- escrituras: guardar/enviar/cerrar/anular — cada una con su propio guard de reentrancia
  // (react-async-state regla 9) y su propio flag de "en vuelo" (regla 5: por acción, no una sola
  // bandera de página) -----------------------------------------------------------------------------
  const [guardando, setGuardando] = useState(false)
  const guardandoRef = useRef(false)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')

  const [enviando, setEnviando] = useState(false)
  const enviandoRef = useRef(false)
  const [errorEnviar, setErrorEnviar] = useState('')

  const [cerrando, setCerrando] = useState(false)
  const cerrandoRef = useRef(false)
  const [errorCerrar, setErrorCerrar] = useState('')

  const [anulando, setAnulando] = useState(false)
  const anulandoRef = useRef(false)
  const [errorAnular, setErrorAnular] = useState('')

  const ocupado = guardando || enviando || cerrando || anulando

  function cambiarLinea(clave: number, cambios: Partial<LineaDeOrdenFormulario>) {
    if (ocupado) return
    // regla 1: updater funcional, nunca lee `lineas` del cierre.
    setLineas((prev) => prev.map((l) => (l.clave === clave ? { ...l, ...cambios } : l)))
  }

  function agregarLinea() {
    if (ocupado) return
    setLineas((prev) => [...prev, lineaDeOrdenVacia(proximaClaveRef.current++)])
  }

  function quitarLinea(clave: number) {
    if (ocupado) return
    setLineas((prev) => prev.filter((l) => l.clave !== clave))
  }

  const encabezadoCompleto = encabezado.idProveedor !== '' && encabezado.idPuntoVenta !== ''
  const puedeGuardar = referenciaOk && puedeEscribir && encabezadoCompleto && !ocupado

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
      const solicitud = aSolicitudDeOrdenDeCompra(encabezado, lineas)
      if (esNuevo) {
        const creada = await clienteDeOrdenesDeCompra.crear(solicitud)
        guardandoRef.current = false
        setGuardando(false)
        // Remontaje completo vía cambio de `key` (regla 8): navega a la ruta real de la orden
        // recién creada, nunca reutiliza este estado de "nueva" para simular la edición.
        navigate(`/ordenes-compra/${creada.id}`, { replace: true })
      } else if (idOrden !== null) {
        await clienteDeOrdenesDeCompra.actualizar(idOrden, solicitud)
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

  async function enviar() {
    if (ocupado) return
    if (enviandoRef.current || idOrden === null) return

    enviandoRef.current = true
    setEnviando(true)
    setErrorEnviar('')
    generacionRef.current += 1

    try {
      await clienteDeOrdenesDeCompra.enviar(idOrden)
      enviandoRef.current = false
      setEnviando(false)
      setAviso('Orden enviada.')
      cargarDetalle()
    } catch (e) {
      enviandoRef.current = false
      setEnviando(false)
      setErrorEnviar(e instanceof ErrorApi ? e.message : 'No se pudo enviar la orden.')
    }
  }

  async function cerrar() {
    if (ocupado) return
    if (cerrandoRef.current || idOrden === null) return

    cerrandoRef.current = true
    setCerrando(true)
    setErrorCerrar('')
    generacionRef.current += 1

    try {
      await clienteDeOrdenesDeCompra.cerrar(idOrden)
      cerrandoRef.current = false
      setCerrando(false)
      setAviso('Orden cerrada.')
      cargarDetalle()
    } catch (e) {
      cerrandoRef.current = false
      setCerrando(false)
      setErrorCerrar(e instanceof ErrorApi ? e.message : 'No se pudo cerrar la orden.')
    }
  }

  async function anular() {
    if (ocupado) return
    if (anulandoRef.current || idOrden === null) return

    anulandoRef.current = true
    setAnulando(true)
    setErrorAnular('')
    generacionRef.current += 1

    try {
      await clienteDeOrdenesDeCompra.anular(idOrden)
      anulandoRef.current = false
      setAnulando(false)
      setAviso('Orden anulada.')
      cargarDetalle()
    } catch (e) {
      anulandoRef.current = false
      setAnulando(false)
      setErrorAnular(e instanceof ErrorApi ? e.message : 'No se pudo anular la orden.')
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
        <Box titulo="Orden de compra" variante="danger">
          <p className="text-muted">{errorDetalle}</p>
          <Link className="btn btn-outline-secondary rounded-0" to="/ordenes-compra">
            Volver a órdenes de compra
          </Link>
        </Box>
      </div>
    )
  }

  const esBorrador = esNuevo || detalle?.estado === 'Borrador'
  const puedeEnviar = puedeEscribir && !esNuevo && detalle?.estado === 'Borrador'
  const puedeCerrar = puedeEscribir && (detalle?.estado === 'Enviada' || detalle?.estado === 'RecibidaParcial')
  const puedeAnular = puedeEscribir && (detalle?.estado === 'Borrador' || detalle?.estado === 'Enviada')
  const puedeRecepcionar = detalle !== null && (detalle.estado === 'Enviada' || detalle.estado === 'RecibidaParcial' || detalle.estado === 'Cerrada')

  return (
    <div className="container-fluid py-4">
      <Box
        titulo={esNuevo ? 'Nueva orden de compra' : `Orden de compra ${detalle?.numero ?? `#${idOrden}`}`}
        variante="inverse"
        herramientas={
          <Link className="btn btn-sm btn-outline-light rounded-0" to="/ordenes-compra">
            Volver a órdenes de compra
          </Link>
        }
      >
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {errorReferencia && (
          <div className="alert alert-warning rounded-0 py-1 px-2 small">
            {errorReferencia} No se pueden registrar operaciones de orden de compra hasta que esto se resuelva.
          </div>
        )}
        {/* regla 6: un refetch fallido posterior a una escritura ya exitosa nunca se disfraza de
            error de la escritura — se muestra chico, sin ocultar el `aviso` de arriba ni el resto
            de la pantalla (que sigue mostrando el último `detalle` conocido). */}
        {errorDetalle && detalle && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorDetalle}</div>}

        {!esNuevo && detalle && (
          <div className="mb-3">
            <span className={`badge rounded-0 me-2 ${claseDeBadgeDeEstadoOrdenCompra(detalle.estado)}`}>
              {etiquetaDeEstadoOrdenCompra(detalle.estado)}
            </span>
            {detalle.fechaEnvio && <span className="small text-muted me-2">Enviada: {formatearFechaHora(detalle.fechaEnvio)}</span>}
            {detalle.fechaCierre && <span className="small text-muted">Cerrada: {formatearFechaHora(detalle.fechaCierre)}</span>}
          </div>
        )}

        <div className="row g-2 mb-3">
          <div className="col-md-4">
            <label className="form-label" htmlFor="oc-proveedor">
              Proveedor
            </label>
            <select
              id="oc-proveedor"
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
          <div className="col-md-3">
            <label className="form-label" htmlFor="oc-punto-venta">
              Punto de venta
            </label>
            <select
              id="oc-punto-venta"
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
            <label className="form-label" htmlFor="oc-fecha-esperada">
              Fecha esperada
            </label>
            <input
              id="oc-fecha-esperada"
              type="date"
              className="form-control rounded-0"
              value={encabezado.fechaEsperada}
              disabled={!esBorrador || ocupado || !puedeEscribir}
              onChange={(e) => setEncabezado((prev) => ({ ...prev, fechaEsperada: e.target.value }))}
            />
          </div>
          <div className="col-12">
            <label className="form-label" htmlFor="oc-observaciones">
              Observaciones
            </label>
            <input
              id="oc-observaciones"
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
                    <th>Cantidad pedida</th>
                    <th>Costo estimado</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  {lineas.map((l) => (
                    <FilaDeItem
                      key={l.clave}
                      linea={l}
                      disabled={ocupado || !referenciaOk || !puedeEscribir}
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

            <div className="d-flex gap-2 mb-3">
              {puedeEscribir && (
                <button type="button" className="btn btn-primary rounded-0" disabled={!puedeGuardar} onClick={guardarBorrador}>
                  {guardando ? 'Guardando…' : esNuevo ? 'Crear borrador' : 'Guardar borrador'}
                </button>
              )}
              {puedeEnviar && (
                <button type="button" className="btn btn-success rounded-0" disabled={ocupado} onClick={enviar}>
                  {enviando ? 'Enviando…' : 'Enviar'}
                </button>
              )}
              {puedeAnular && (
                <button type="button" className="btn btn-danger rounded-0" disabled={ocupado} onClick={anular}>
                  {anulando ? 'Anulando…' : 'Anular'}
                </button>
              )}
            </div>
            {errorEnviar && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorEnviar}</div>}
            {errorAnular && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorAnular}</div>}
          </>
        ) : (
          detalle && (
            <>
              <h6>Items pedidos</h6>
              <div className="table-responsive mb-3">
                <table className="table table-sm table-bordered align-middle">
                  <thead>
                    <tr>
                      <th>Artículo</th>
                      <th className="text-end">Cantidad pedida</th>
                      <th className="text-end">Costo estimado</th>
                    </tr>
                  </thead>
                  <tbody>
                    {detalle.items.map((item) => (
                      <tr key={item.orden}>
                        <td>{item.descripcion}</td>
                        <td className="text-end">{formatearCantidadNullable(item.cantidadPedida)}</td>
                        <td className="text-end">{formatearMonedaNullable(item.costoUnitarioEstimado)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              <h6>Cobertura por artículo</h6>
              <TablaDeCobertura cobertura={detalle.cobertura} descripcionPorArticulo={descripcionPorArticulo} />

              <div className="row g-3 my-3">
                <div className="col-md-4">
                  <div className="small text-muted">Total estimado</div>
                  <div>{formatearMonedaNullable(detalle.totalEstimado)}</div>
                </div>
                <div className="col-md-4">
                  <div className="small text-muted">Total real</div>
                  <div>{formatearMonedaNullable(detalle.totalReal)}</div>
                </div>
                <div className="col-md-4">
                  <div className="small text-muted">Desvío total</div>
                  <div>{formatearDesvio(detalle.desvioTotal)}</div>
                </div>
              </div>

              {detalle.comprobantesLigados.length > 0 && (
                <div className="mb-3">
                  <div className="small text-muted">Comprobantes de compra ligados</div>
                  <div className="d-flex gap-2 flex-wrap">
                    {detalle.comprobantesLigados.map((idComprobante) => (
                      <Link key={idComprobante} className="btn btn-sm btn-outline-secondary rounded-0" to={`/compras/${idComprobante}`}>
                        #{idComprobante}
                      </Link>
                    ))}
                  </div>
                </div>
              )}

              <div className="d-flex gap-2 mb-3">
                {puedeRecepcionar && (
                  <button
                    type="button"
                    className="btn btn-primary rounded-0"
                    onClick={() => navigate(`/compras/nueva?idOrdenCompra=${detalle.id}`)}
                  >
                    Registrar recepción
                  </button>
                )}
                {puedeCerrar && (
                  <button type="button" className="btn btn-success rounded-0" disabled={ocupado} onClick={cerrar}>
                    {cerrando ? 'Cerrando…' : 'Cerrar'}
                  </button>
                )}
                {puedeAnular && (
                  <button type="button" className="btn btn-danger rounded-0" disabled={ocupado} onClick={anular}>
                    {anulando ? 'Anulando…' : 'Anular'}
                  </button>
                )}
              </div>
              {errorCerrar && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorCerrar}</div>}
              {errorAnular && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorAnular}</div>}
            </>
          )
        )}
      </Box>
    </div>
  )
}

/**
 * Órdenes de compra — borrador/detalle (stage-16-ordenes-de-compra, Slice 6; design: Web
 * composition): `/ordenes-compra/nueva` crea un borrador desde cero (opcionalmente precargado
 * desde `Reposicion.tsx` vía `location.state`) o `/ordenes-compra/:id` lo edita (borrador), lo
 * envía/cierra/anula, o solo lo muestra con la cobertura por artículo y el desvío de precio. Mismo
 * gate de lectura que `/ordenes-compra` (`RutaProtegida`, App.tsx) — `puedeEscribir` oculta las
 * acciones de escritura, `GestionDeCatalogo` es la autoridad real del lado del servidor.
 */
export function OrdenDeCompra() {
  const { id } = useParams<{ id: string }>()
  const location = useLocation()
  const esNuevo = id === undefined || id === 'nueva'
  const idNumerico = esNuevo ? null : Number(id)
  const idValido = esNuevo || Number.isFinite(idNumerico)

  const precarga = esNuevo ? ((location.state as EstadoDePrecarga | null) ?? null) : null

  if (!idValido) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Orden de compra" variante="warning">
          <p className="text-muted">No se especificó una orden de compra válida.</p>
          <Link className="btn btn-outline-secondary rounded-0" to="/ordenes-compra">
            Volver a órdenes de compra
          </Link>
        </Box>
      </div>
    )
  }

  return <PantallaOrdenDeCompra key={idNumerico ?? `nueva-${precarga?.idProveedor ?? 's'}`} idOrden={idNumerico} precarga={precarga} />
}
