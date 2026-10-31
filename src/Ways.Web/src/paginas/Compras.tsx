import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import {
  clienteDeCompras,
  esColumnaOrdenableDeCompras,
  claseDeBadgeDeEstadoPagoDeLista,
  etiquetaDeEstadoCompra,
  etiquetaDeEstadoPagoDeLista,
  filtrosDeComprasVacios,
  type FiltroDeEstadoPagoDeCompras,
  type FiltrosDeCompras,
  type OrdenDeCompras,
} from '../api/compras'
import { clienteDeCatalogosFiscales } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type {
  CompraListada,
  EstadoCompra,
  OrigenCompra,
  PaginaDe,
  PaginaDeCompras,
  ProveedorListado,
  SaldoDeProveedor,
  TipoComprobanteListado,
} from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { EncabezadoOrdenable } from '../componentes/grilla'
import { siguienteOrden } from '../componentes/grilla/ordenamiento'
import { ResumenSaldoDeProveedor } from '../componentes/ResumenSaldoDeProveedor'
import { formatearImporte } from '../formato/importes'
import { CampoFecha } from '../componentes/CampoFecha'
import { etiquetaDeProveedor, ordenarProveedoresPorEtiqueta } from './articulos/helpers'

const DEMORA_DEBOUNCE_NUMERO_MS = 400

const OPCIONES_ESTADO: { valor: EstadoCompra | ''; etiqueta: string }[] = [
  { valor: '', etiqueta: 'Todos' },
  { valor: 'Borrador', etiqueta: 'Borrador' },
  { valor: 'Confirmada', etiqueta: 'Confirmada' },
  { valor: 'Anulada', etiqueta: 'Anulada' },
]

const OPCIONES_ORIGEN: { valor: OrigenCompra | ''; etiqueta: string }[] = [
  { valor: '', etiqueta: 'Todas' },
  { valor: 'Manual', etiqueta: 'Manual' },
  { valor: 'Asistente', etiqueta: 'Asistente' },
]

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function formatearFecha(iso: string | null): string {
  return iso ? new Date(iso).toLocaleDateString('es-AR') : '—'
}

/** La fecha del comprobante es una fecha de calendario (`yyyy-MM-dd`): se reordena sin pasar por `Date`, que la correría por la zona horaria. */
function formatearFechaDeCalendario(iso: string | null): string {
  if (!iso) return '—'
  const [anio, mes, dia] = iso.split('-')
  return `${dia}/${mes}/${anio}`
}

const OPCIONES_ESTADO_PAGO: { valor: FiltroDeEstadoPagoDeCompras | ''; etiqueta: string }[] = [
  { valor: '', etiqueta: 'Todos' },
  { valor: 'pagada', etiqueta: 'Pagado' },
  { valor: 'pendiente', etiqueta: 'Pendiente' },
]

function claseDeBadgeDeEstado(estado: EstadoCompra): string {
  switch (estado) {
    case 'Borrador':
      return 'text-bg-secondary'
    case 'Confirmada':
      return 'text-bg-success'
    case 'Anulada':
      return 'text-bg-danger'
  }
}

/**
 * Listado de comprobantes de compra (stage-8-compras-transferencias-inventario, Slice 5-6,
 * design: Web Composition): filtros por columna, estado de pago por fila (lo manda el listado) y saldo
 * agregado del proveedor (solo cuando el listado está filtrado por un proveedor puntual — el
 * endpoint de saldo es por-proveedor) y entrada al editor de borrador. La ruta sigue
 * `Politicas.OperacionDePos` (decisión 11: la lectura queda abierta a Vendedor/Supervisor/Admin)
 * — a diferencia de `/proveedores`, que es Admin-only, esta pantalla es el punto de entrada real
 * al saldo para Vendedor/Supervisor (judgment-day stage-8 Slice 6: el saldo tiene que ser
 * alcanzable por todo rol que opera `Politicas.OperacionDePos`, no solo Admin). Reusa la misma
 * pieza presentacional (`ResumenSaldoDeProveedor`) que el panel completo de `Proveedores.tsx`.
 * `puedeEscribir` oculta el botón "Nueva compra" como defensa en profundidad cosmética, la
 * política de escritura real es `GestionDeCatalogo` del lado del servidor.
 */
export function Compras() {
  const { usuario } = useAuth()
  const puedeEscribir = usuario !== null && usuario.rolId === ROL.Admin
  const navigate = useNavigate()

  const [proveedores, setProveedores] = useState<ProveedorListado[] | null>(null)
  const [errorProveedores, setErrorProveedores] = useState('')

  const [tipos, setTipos] = useState<TipoComprobanteListado[] | null>(null)

  const [filtros, setFiltros] = useState<FiltrosDeCompras>(filtrosDeComprasVacios())

  // `null` = el orden de siempre del listado (más nuevas primero). Si una consulta falla, la tabla desaparece junto con
  // su encabezado: nunca queda un encabezado que declare un orden que las filas no tienen.
  const [orden, setOrden] = useState<OrdenDeCompras>(null)
  const [pagina, setPagina] = useState<PaginaDeCompras | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  const [numeroBorrador, setNumeroBorrador] = useState('')
  const demoraNumeroRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const [saldoProveedor, setSaldoProveedor] = useState<SaldoDeProveedor | null>(null)

  useEffect(() => {
    let vigente = true
    api
      .get<PaginaDe<ProveedorListado>>('/proveedores?tamanio=200')
      .then((p) => {
        if (!vigente) return
        setProveedores(ordenarProveedoresPorEtiqueta(p.items))
      })
      .catch((e) => {
        if (!vigente) return
        setProveedores([])
        setErrorProveedores(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los proveedores.')
      })

    clienteDeCatalogosFiscales
      .tiposComprobante()
      .then((lista) => {
        if (!vigente) return
        setTipos(lista.filter((t) => t.clase === 'Compra'))
      })
      .catch(() => {
        if (vigente) setTipos([])
      })

    return () => {
      vigente = false
    }
  }, [])

  // regla 2: cada cambio de filtro dispara una nueva consulta — una respuesta desactualizada
  // nunca puede pisar la más reciente.
  const cargar = useCallback(() => {
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    clienteDeCompras
      .listar(filtros, orden)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las compras.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [filtros, orden])

  // Ascendente → descendente → orden de siempre. Cambiar el orden vuelve a la página 1 y conserva los filtros; los dos
  // setState se agrupan en un solo render, es decir en una sola consulta.
  function alternarOrden(columna: string) {
    if (!esColumnaOrdenableDeCompras(columna)) return
    setOrden((actual) => {
      const siguiente = siguienteOrden(actual, columna)
      return siguiente === null ? null : { columna, direccion: siguiente.direccion }
    })
    setFiltros((prev) => (prev.pagina === 1 ? prev : { ...prev, pagina: 1 }))
  }

  useEffect(() => {
    cargar()
  }, [cargar])

  useEffect(() => {
    setSaldoProveedor(null)
    if (filtros.idProveedor === null) return

    let vigente = true
    clienteDeCompras
      .obtenerSaldoDeProveedor(filtros.idProveedor)
      .then((saldo) => {
        if (!vigente) return
        setSaldoProveedor(saldo)
      })
      .catch(() => {
        // El saldo agregado es un enriquecimiento, no un dato crítico del listado: una falla acá no bloquea la tabla.
        if (vigente) setSaldoProveedor(null)
      })

    return () => {
      vigente = false
    }
  }, [filtros.idProveedor])

  const proveedorPorId = useMemo(() => {
    const indice: Record<number, ProveedorListado> = {}
    for (const p of proveedores ?? []) indice[p.id] = p
    return indice
  }, [proveedores])

  const tipoPorId = useMemo(() => {
    const indice: Record<number, TipoComprobanteListado> = {}
    for (const t of tipos ?? []) indice[t.id] = t
    return indice
  }, [tipos])

  function cambiarFiltro(cambios: Partial<Omit<FiltrosDeCompras, 'pagina' | 'tamanio'>>) {
    setFiltros((prev) => ({ ...prev, ...cambios, pagina: 1 }))
  }

  useEffect(
    () => () => {
      if (demoraNumeroRef.current) clearTimeout(demoraNumeroRef.current)
    },
    [],
  )

  function aplicarNumero(numero: string) {
    if (demoraNumeroRef.current) clearTimeout(demoraNumeroRef.current)
    demoraNumeroRef.current = null
    cambiarFiltro({ numero })
  }

  function escribirNumero(texto: string) {
    setNumeroBorrador(texto)
    if (demoraNumeroRef.current) clearTimeout(demoraNumeroRef.current)
    demoraNumeroRef.current = setTimeout(() => aplicarNumero(texto), DEMORA_DEBOUNCE_NUMERO_MS)
  }

  function limpiarFiltros() {
    if (demoraNumeroRef.current) clearTimeout(demoraNumeroRef.current)
    demoraNumeroRef.current = null
    setNumeroBorrador('')
    setFiltros((prev) => ({ ...filtrosDeComprasVacios(), tamanio: prev.tamanio }))
  }

  function cambiarPagina(delta: number) {
    setFiltros((prev) => ({ ...prev, pagina: Math.max(1, prev.pagina + delta) }))
  }

  function crearBorrador() {
    navigate('/compras/nueva')
  }

  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1

  const herramientas = puedeEscribir ? (
    <nav className="p-2 d-flex gap-2">
      <button type="button" className="btn btn-sm btn-success text-nowrap" onClick={crearBorrador}>
        Nueva compra
      </button>
    </nav>
  ) : undefined

  return (
    <div className="container-fluid py-4">
      <Box titulo="Compras" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger">{error}</div>}
        {errorProveedores && <div className="alert alert-warning py-1 px-2 small">{errorProveedores}</div>}

        {saldoProveedor && (
          <div className="border p-3 mb-3 bg-body">
            {/* stage-15-cc-proveedores-ledger (Slice 6, judgment-day hallazgo CRITICAL): `proveedorPorId`
                ya trae el listado completo (fetch de montaje) — se lo pasamos a `ResumenSaldoDeProveedor`
                para que el link real cargue con `location.state.proveedor`. Si el id todavía no está en
                el índice (listado aún cargando), el Link va sin `state` y la pantalla destino degrada
                con gracia. */}
            <ResumenSaldoDeProveedor
              saldo={saldoProveedor.saldo}
              idProveedor={saldoProveedor.idProveedor}
              proveedor={proveedorPorId[saldoProveedor.idProveedor]}
            />
          </div>
        )}

        <div className="table-responsive">
          <table className="table table-sm table-striped table-bordered align-middle">
            <thead>
              {pagina && (
                <tr>
                  <EncabezadoOrdenable columna="numero" orden={orden} onAlternar={alternarOrden}>
                    Número
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="proveedor" orden={orden} onAlternar={alternarOrden}>
                    Proveedor
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="tipo" orden={orden} onAlternar={alternarOrden}>
                    Tipo
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="estado" orden={orden} onAlternar={alternarOrden}>
                    Estado
                  </EncabezadoOrdenable>
                  <th>Estado de pago</th>
                  <EncabezadoOrdenable columna="fechaRecepcion" orden={orden} onAlternar={alternarOrden}>
                    Fecha de carga
                  </EncabezadoOrdenable>
                  <th>Fecha del comprobante</th>
                  <EncabezadoOrdenable columna="total" orden={orden} onAlternar={alternarOrden} className="text-end">
                    Total
                  </EncabezadoOrdenable>
                </tr>
              )}
              <tr>
                <td>
                  <input
                    type="search"
                    aria-label="Número"
                    placeholder="Número"
                    className="form-control form-control-sm mb-1"
                    value={numeroBorrador}
                    onChange={(e) => escribirNumero(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') aplicarNumero(numeroBorrador)
                    }}
                  />
                  <select
                    aria-label="Origen"
                    className="form-select form-select-sm"
                    value={filtros.origen ?? ''}
                    onChange={(e) => cambiarFiltro({ origen: e.target.value === '' ? null : (e.target.value as OrigenCompra) })}
                  >
                    {OPCIONES_ORIGEN.map((o) => (
                      <option key={o.valor} value={o.valor}>
                        {o.etiqueta}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <select
                    aria-label="Proveedor"
                    className="form-select form-select-sm"
                    value={filtros.idProveedor ?? ''}
                    onChange={(e) => cambiarFiltro({ idProveedor: e.target.value === '' ? null : Number(e.target.value) })}
                  >
                    <option value="">Todos</option>
                    {(proveedores ?? []).map((p) => (
                      <option key={p.id} value={p.id}>
                        {etiquetaDeProveedor(p)}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <select
                    aria-label="Tipo"
                    className="form-select form-select-sm"
                    value={filtros.idTipoComprobante ?? ''}
                    onChange={(e) => cambiarFiltro({ idTipoComprobante: e.target.value === '' ? null : Number(e.target.value) })}
                  >
                    <option value="">Todos</option>
                    {(tipos ?? []).map((t) => (
                      <option key={t.id} value={t.id}>
                        {t.codigo}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <select
                    aria-label="Estado"
                    className="form-select form-select-sm"
                    value={filtros.estado ?? ''}
                    onChange={(e) => cambiarFiltro({ estado: e.target.value === '' ? null : (e.target.value as EstadoCompra) })}
                  >
                    {OPCIONES_ESTADO.map((o) => (
                      <option key={o.valor} value={o.valor}>
                        {o.etiqueta}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <select
                    aria-label="Estado de pago"
                    className="form-select form-select-sm"
                    value={filtros.estadoPago ?? ''}
                    onChange={(e) =>
                      cambiarFiltro({ estadoPago: e.target.value === '' ? null : (e.target.value as FiltroDeEstadoPagoDeCompras) })
                    }
                  >
                    {OPCIONES_ESTADO_PAGO.map((o) => (
                      <option key={o.valor} value={o.valor}>
                        {o.etiqueta}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <CampoFecha
                    aria-label="Fecha de carga desde"
                    className="form-control form-control-sm mb-1"
                    value={filtros.desde}
                    onChange={(valor) => cambiarFiltro({ desde: valor })}
                  />
                  <CampoFecha
                    aria-label="Fecha de carga hasta"
                    className="form-control form-control-sm"
                    value={filtros.hasta}
                    onChange={(valor) => cambiarFiltro({ hasta: valor })}
                  />
                </td>
                <td>
                  <CampoFecha
                    aria-label="Fecha del comprobante desde"
                    className="form-control form-control-sm mb-1"
                    value={filtros.fechaComprobanteDesde}
                    onChange={(valor) => cambiarFiltro({ fechaComprobanteDesde: valor })}
                  />
                  <CampoFecha
                    aria-label="Fecha del comprobante hasta"
                    className="form-control form-control-sm"
                    value={filtros.fechaComprobanteHasta}
                    onChange={(valor) => cambiarFiltro({ fechaComprobanteHasta: valor })}
                  />
                </td>
                <td className="text-end">
                  <button type="button" className="btn btn-sm btn-outline-secondary" onClick={limpiarFiltros}>
                    Limpiar
                  </button>
                </td>
              </tr>
            </thead>
            {pagina && (
              <tbody>
                {pagina.items.map((c: CompraListada) => (
                  <tr key={c.id}>
                    <td>
                      <Link className="link-underline-opacity-0" to={`/compras/${c.id}`}>
                        {c.numeroExterno ?? `#${c.id}`}
                      </Link>
                      {c.origen === 'Asistente' && <span className="badge text-bg-info ms-2">Asistente</span>}
                    </td>
                    <td>{c.nombreProveedor ?? `Proveedor #${c.idProveedor}`}</td>
                    <td>{tipoPorId[c.idTipoComprobante]?.codigo ?? `#${c.idTipoComprobante}`}</td>
                    <td>
                      <span className={`badge ${claseDeBadgeDeEstado(c.estado)}`}>{etiquetaDeEstadoCompra(c.estado)}</span>
                    </td>
                    <td>
                      {etiquetaDeEstadoPagoDeLista(c.estadoPago) ? (
                        <span className={`badge ${claseDeBadgeDeEstadoPagoDeLista(c.estadoPago)}`}>
                          {etiquetaDeEstadoPagoDeLista(c.estadoPago)}
                        </span>
                      ) : (
                        '—'
                      )}
                    </td>
                    <td>{formatearFecha(c.fechaRecepcion)}</td>
                    <td>{formatearFechaDeCalendario(c.fechaComprobante)}</td>
                    <td className="text-end">{formatearMoneda(c.total)}</td>
                  </tr>
                ))}
                {pagina.items.length === 0 && (
                  <tr>
                    <td colSpan={8} className="text-center text-muted py-4">
                      No hay compras que coincidan con los filtros.
                    </td>
                  </tr>
                )}
              </tbody>
            )}
          </table>
        </div>

        {cargando && !pagina && <Cargando />}

        {pagina && (
          <>
            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                Página {pagina.pagina} de {totalPaginas} — {pagina.total} compra(s)
              </span>
              <div className="d-flex gap-2">
                <button
                  type="button"
                  className="btn btn-sm btn-outline-secondary"
                  disabled={pagina.pagina <= 1 || cargando}
                  onClick={() => cambiarPagina(-1)}
                >
                  Anterior
                </button>
                <button
                  type="button"
                  className="btn btn-sm btn-outline-secondary"
                  disabled={pagina.pagina >= totalPaginas || cargando}
                  onClick={() => cambiarPagina(1)}
                >
                  Siguiente
                </button>
              </div>
            </div>
          </>
        )}
      </Box>
    </div>
  )
}
