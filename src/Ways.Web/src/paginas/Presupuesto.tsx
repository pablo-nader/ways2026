import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { clienteDeArticulos } from '../api/articulos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import {
  aSolicitudDeEnvio,
  aSolicitudDePresupuesto,
  claseDeBadgeDeEstadoPresupuesto,
  claseDeBadgeDeVencimiento,
  clienteDePresupuestos,
  encabezadoDePresupuestoVacio,
  etiquetaDeEstadoPresupuesto,
  etiquetaDeVencimiento,
  itemDePresupuestoAFormulario,
  lineaDePresupuestoCompletaParaEnvio,
  lineaDePresupuestoVacia,
  vencimientoSugerido,
  type EncabezadoDePresupuestoFormulario,
  type LineaDePresupuestoFormulario,
} from '../api/presupuestos'
import type { ArticuloListado, ClienteListado, PresupuestoDetalle, PuntoVentaListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

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
// OrdenDeCompra.tsx/CompraEditor.tsx, propio de cada fila ------------------------------------------

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

// ---- Fila editable del grid de items — sin ningún campo de dinero (design decisión 2: el precio
// lo resuelve el motor al guardar el borrador, nunca lo tipea el operador) -------------------------

type PropsFilaDeItem = {
  linea: LineaDePresupuestoFormulario
  disabled: boolean
  onCambio: (clave: number, cambios: Partial<LineaDePresupuestoFormulario>) => void
  onQuitar: (clave: number) => void
}

function FilaDeItem({ linea, disabled, onCambio, onQuitar }: PropsFilaDeItem) {
  const incompleta = !lineaDePresupuestoCompletaParaEnvio(linea)

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
          aria-label="Cantidad"
          value={linea.cantidad}
          disabled={disabled}
          onChange={(e) => onCambio(linea.clave, { cantidad: e.target.value })}
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

// ---- Pantalla principal -----------------------------------------------------------------------

type PropsPantalla = { idPresupuesto: number | null }

/** Remontada por `key={idPresupuesto ?? 'nuevo'}` (react-async-state regla 8) — ningún estado de
 * acá (borrador en edición, avisos, paneles) sobrevive a un cambio de presupuesto. */
function PantallaPresupuesto({ idPresupuesto }: PropsPantalla) {
  const navigate = useNavigate()
  const esNuevo = idPresupuesto === null

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

  // ---- detalle (lectura, GET /{id} — única fuente de vencido/convertible/estado real) ------------
  const [detalle, setDetalle] = useState<PresupuestoDetalle | null>(null)
  const [cargandoDetalle, setCargandoDetalle] = useState(!esNuevo)
  const [errorDetalle, setErrorDetalle] = useState('')
  const generacionRef = useRef(0)

  const cargarDetalle = useCallback(() => {
    if (esNuevo || idPresupuesto === null) return
    const miGeneracion = (generacionRef.current += 1)
    setCargandoDetalle(true)
    setErrorDetalle('')

    clienteDePresupuestos
      .obtener(idPresupuesto)
      .then((d) => {
        if (generacionRef.current !== miGeneracion) return
        setDetalle(d)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        // regla 6: un refetch posterior a una escritura 2xx nunca vacía la pantalla a un error
        // total — `detalle` queda como estaba, solo el aviso de arriba se muestra.
        setErrorDetalle(
          e instanceof ErrorApi ? e.message : 'No se pudo actualizar el presupuesto — los datos mostrados pueden estar desactualizados.',
        )
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargandoDetalle(false)
      })
  }, [esNuevo, idPresupuesto])

  useEffect(() => {
    cargarDetalle()
  }, [cargarDetalle])

  // ---- formulario editable (nuevo o borrador existente) ------------------------------------------
  const proximaClaveRef = useRef(1)
  const [encabezado, setEncabezado] = useState<EncabezadoDePresupuestoFormulario>(encabezadoDePresupuestoVacio())
  const [lineas, setLineas] = useState<LineaDePresupuestoFormulario[]>([])

  useEffect(() => {
    if (detalle === null) return
    setEncabezado({
      idPuntoVenta: detalle.idPuntoVenta,
      idCliente: detalle.idCliente,
      observaciones: detalle.observaciones ?? '',
    })
    setLineas(detalle.items.map((item) => itemDePresupuestoAFormulario(proximaClaveRef.current++, item)))
  }, [detalle])

  // ---- escrituras: guardar/enviar/anular — cada una con su propio guard de reentrancia
  // (react-async-state regla 9) y su propio flag de "en vuelo" (regla 5) ---------------------------
  const [guardando, setGuardando] = useState(false)
  const guardandoRef = useRef(false)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')

  const [vencimientoAEnviar, setVencimientoAEnviar] = useState(() => vencimientoSugerido(new Date()))
  const [enviando, setEnviando] = useState(false)
  const enviandoRef = useRef(false)
  const [errorEnviar, setErrorEnviar] = useState('')

  const [anulando, setAnulando] = useState(false)
  const anulandoRef = useRef(false)
  const [errorAnular, setErrorAnular] = useState('')

  const ocupado = guardando || enviando || anulando

  function cambiarLinea(clave: number, cambios: Partial<LineaDePresupuestoFormulario>) {
    if (ocupado) return
    // regla 1: updater funcional, nunca lee `lineas` del cierre.
    setLineas((prev) => prev.map((l) => (l.clave === clave ? { ...l, ...cambios } : l)))
  }

  function agregarLinea() {
    if (ocupado) return
    setLineas((prev) => [...prev, lineaDePresupuestoVacia(proximaClaveRef.current++)])
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
      const solicitud = aSolicitudDePresupuesto(encabezado, lineas)
      if (esNuevo) {
        const creado = await clienteDePresupuestos.crear(solicitud)
        guardandoRef.current = false
        setGuardando(false)
        // Remontaje completo vía cambio de `key` (regla 8): navega a la ruta real del presupuesto
        // recién creado, nunca reutiliza este estado de "nuevo" para simular la edición.
        navigate(`/presupuestos/${creado.id}`, { replace: true })
      } else if (idPresupuesto !== null) {
        await clienteDePresupuestos.actualizar(idPresupuesto, solicitud)
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
    if (enviandoRef.current || idPresupuesto === null) return
    if (vencimientoAEnviar.trim() === '') {
      setErrorEnviar('Elegí una fecha de vencimiento.')
      return
    }

    enviandoRef.current = true
    setEnviando(true)
    setErrorEnviar('')
    generacionRef.current += 1

    try {
      await clienteDePresupuestos.enviar(idPresupuesto, aSolicitudDeEnvio(vencimientoAEnviar))
      enviandoRef.current = false
      setEnviando(false)
      setAviso('Presupuesto enviado.')
      cargarDetalle()
    } catch (e) {
      enviandoRef.current = false
      setEnviando(false)
      setErrorEnviar(e instanceof ErrorApi ? e.message : 'No se pudo enviar el presupuesto.')
    }
  }

  async function anular() {
    if (ocupado) return
    if (anulandoRef.current || idPresupuesto === null) return

    anulandoRef.current = true
    setAnulando(true)
    setErrorAnular('')
    generacionRef.current += 1

    try {
      await clienteDePresupuestos.anular(idPresupuesto)
      anulandoRef.current = false
      setAnulando(false)
      setAviso('Presupuesto anulado.')
      cargarDetalle()
    } catch (e) {
      anulandoRef.current = false
      setAnulando(false)
      setErrorAnular(e instanceof ErrorApi ? e.message : 'No se pudo anular el presupuesto.')
    }
  }

  function convertirEnVenta() {
    if (ocupado || idPresupuesto === null) return
    navigate(`/pos?idPresupuesto=${idPresupuesto}`)
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
        <Box titulo="Presupuesto" variante="danger">
          <p className="text-muted">{errorDetalle}</p>
          <Link className="btn btn-outline-secondary rounded-0" to="/presupuestos">
            Volver a presupuestos
          </Link>
        </Box>
      </div>
    )
  }

  const esBorrador = esNuevo || detalle?.estado === 'Borrador'
  const puedeEnviar = !esNuevo && detalle?.estado === 'Borrador'
  const puedeAnular = detalle?.estado === 'Borrador' || detalle?.estado === 'Enviado'
  // El botón de conversión SOLO renderiza cuando el servidor reporta `Convertible` — nunca una
  // condición client-side sobre `estado`/`vencimiento` sola (tarea 7.8: la fuente de verdad de
  // "convertible" es siempre la lectura del servidor, `ReglaDePresupuestos`, jamás recalculada acá).
  const puedeConvertir = !esNuevo && detalle?.estado === 'Enviado' && detalle?.convertible === true

  return (
    <div className="container-fluid py-4">
      <Box
        titulo={esNuevo ? 'Nuevo presupuesto' : `Presupuesto ${detalle?.numeroFormateado ?? `#${idPresupuesto}`}`}
        variante="inverse"
        herramientas={
          <Link className="btn btn-sm btn-outline-light rounded-0" to="/presupuestos">
            Volver a presupuestos
          </Link>
        }
      >
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {errorReferencia && (
          <div className="alert alert-warning rounded-0 py-1 px-2 small">
            {errorReferencia} No se pueden registrar operaciones de presupuesto hasta que esto se resuelva.
          </div>
        )}
        {errorDetalle && detalle && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorDetalle}</div>}

        {!esNuevo && detalle && (
          <div className="mb-3">
            <span className={`badge rounded-0 me-2 ${claseDeBadgeDeEstadoPresupuesto(detalle.estado)}`}>
              {etiquetaDeEstadoPresupuesto(detalle.estado)}
            </span>
            {detalle.estado === 'Enviado' && (
              <span className={`badge rounded-0 me-2 ${claseDeBadgeDeVencimiento(detalle.vencimiento, detalle.vencido)}`}>
                {etiquetaDeVencimiento(detalle.vencimiento, detalle.vencido)}
              </span>
            )}
            {detalle.fechaEnvio && <span className="small text-muted me-2">Enviado: {formatearFechaHora(detalle.fechaEnvio)}</span>}
            {detalle.idComprobanteVenta !== null && <span className="small text-muted">Convertido en venta #{detalle.idComprobanteVenta}</span>}
          </div>
        )}

        <div className="row g-2 mb-3">
          <div className="col-md-4">
            <label className="form-label" htmlFor="pres-punto-venta">
              Punto de venta
            </label>
            <select
              id="pres-punto-venta"
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
            <label className="form-label" htmlFor="pres-cliente">
              Cliente
            </label>
            <select
              id="pres-cliente"
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
          <div className="col-12">
            <label className="form-label" htmlFor="pres-observaciones">
              Observaciones
            </label>
            <input
              id="pres-observaciones"
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
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  {lineas.map((l) => (
                    <FilaDeItem key={l.clave} linea={l} disabled={ocupado || !referenciaOk} onCambio={cambiarLinea} onQuitar={quitarLinea} />
                  ))}
                  {lineas.length === 0 && (
                    <tr>
                      <td colSpan={3} className="text-center text-muted py-3">
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
              {puedeEnviar && (
                <>
                  <div>
                    <label className="form-label small mb-0" htmlFor="pres-vencimiento">
                      Vence el
                    </label>
                    <input
                      id="pres-vencimiento"
                      type="date"
                      className="form-control form-control-sm rounded-0"
                      value={vencimientoAEnviar}
                      disabled={ocupado}
                      onChange={(e) => setVencimientoAEnviar(e.target.value)}
                    />
                  </div>
                  <button type="button" className="btn btn-success rounded-0" disabled={ocupado} onClick={enviar}>
                    {enviando ? 'Enviando…' : 'Enviar'}
                  </button>
                </>
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

              <div className="d-flex gap-2 mb-3">
                {puedeConvertir && (
                  <button type="button" className="btn btn-primary rounded-0" onClick={convertirEnVenta}>
                    Convertir en venta
                  </button>
                )}
                {puedeAnular && (
                  <button type="button" className="btn btn-danger rounded-0" disabled={ocupado} onClick={anular}>
                    {anulando ? 'Anulando…' : 'Anular'}
                  </button>
                )}
              </div>
              {errorAnular && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorAnular}</div>}
            </>
          )
        )}
      </Box>
    </div>
  )
}

/**
 * Presupuestos — borrador/detalle (stage-17-presupuestos-y-remitos, Slice 7; design: Web
 * composition): `/presupuestos/nuevo` crea un borrador desde cero, `/presupuestos/:id` lo edita
 * (borrador), lo envía/anula, o solo lo muestra con la acción de conversión cuando el servidor
 * reporta `Convertible`. Mismo gate que `/presupuestos` (`RutaProtegida`, App.tsx).
 */
export function Presupuesto() {
  const { id } = useParams<{ id: string }>()
  const esNuevo = id === undefined || id === 'nuevo'
  const idNumerico = esNuevo ? null : Number(id)
  const idValido = esNuevo || Number.isFinite(idNumerico)

  if (!idValido) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Presupuesto" variante="warning">
          <p className="text-muted">No se especificó un presupuesto válido.</p>
          <Link className="btn btn-outline-secondary rounded-0" to="/presupuestos">
            Volver a presupuestos
          </Link>
        </Box>
      </div>
    )
  }

  return <PantallaPresupuesto key={idNumerico ?? 'nuevo'} idPresupuesto={idNumerico} />
}
