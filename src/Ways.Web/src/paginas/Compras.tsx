import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { clienteDeCompras, etiquetaDeEstadoCompra, etiquetaDeEstadoPago, filtrosDeComprasVacios, type FiltrosDeCompras } from '../api/compras'
import { clienteDeCatalogosFiscales } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type { CompraListada, EstadoCompra, EstadoPago, PaginaDe, PaginaDeCompras, ProveedorListado, TipoComprobanteListado } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

const OPCIONES_ESTADO: { valor: EstadoCompra | ''; etiqueta: string }[] = [
  { valor: '', etiqueta: 'Todos' },
  { valor: 'Borrador', etiqueta: 'Borrador' },
  { valor: 'Confirmada', etiqueta: 'Confirmada' },
  { valor: 'Anulada', etiqueta: 'Anulada' },
]

function formatearMoneda(valor: number): string {
  return `$${valor.toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

function formatearFecha(iso: string | null): string {
  return iso ? new Date(iso).toLocaleDateString('es-AR') : '—'
}

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
 * Listado de comprobantes de compra (stage-8-compras-transferencias-inventario, Slice 5, design:
 * Web Composition): filtros proveedor/estado/fecha, estado de pago por fila (solo cuando el
 * listado está filtrado por un proveedor puntual — el endpoint de saldo es por-proveedor, el
 * panel completo con su propio estado lo construye `Proveedores.tsx` en la Slice 6) y entrada al
 * editor de borrador. La ruta sigue `Politicas.OperacionDePos` (decisión 11: la lectura queda
 * abierta a Vendedor/Supervisor/Admin) — `puedeEscribir` oculta el botón "Nueva compra" como
 * defensa en profundidad cosmética, la política de escritura real es `GestionDeCatalogo` del
 * lado del servidor.
 */
export function Compras() {
  const { usuario } = useAuth()
  const puedeEscribir = usuario !== null && usuario.rolId === ROL.Admin
  const navigate = useNavigate()

  const [proveedores, setProveedores] = useState<ProveedorListado[] | null>(null)
  const [errorProveedores, setErrorProveedores] = useState('')

  const [tipos, setTipos] = useState<TipoComprobanteListado[] | null>(null)

  const [filtros, setFiltros] = useState<FiltrosDeCompras>(filtrosDeComprasVacios())

  const [pagina, setPagina] = useState<PaginaDeCompras | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  const [estadosPago, setEstadosPago] = useState<Record<number, EstadoPago>>({})

  useEffect(() => {
    let vigente = true
    api
      .get<PaginaDe<ProveedorListado>>('/proveedores?tamanio=200')
      .then((p) => {
        if (!vigente) return
        setProveedores(p.items)
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
      .listar(filtros)
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
  }, [filtros])

  useEffect(() => {
    cargar()
  }, [cargar])

  useEffect(() => {
    setEstadosPago({})
    if (filtros.idProveedor === null) return

    let vigente = true
    clienteDeCompras
      .obtenerSaldoDeProveedor(filtros.idProveedor)
      .then((saldo) => {
        if (!vigente) return
        const indice: Record<number, EstadoPago> = {}
        for (const c of saldo.compras) indice[c.idComprobanteCompra] = c.estadoPago
        setEstadosPago(indice)
      })
      .catch(() => {
        // El estado de pago es un enriquecimiento, no un dato crítico del listado — una falla acá
        // no bloquea la tabla, la columna simplemente queda vacía para esas filas.
        if (vigente) setEstadosPago({})
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

  function cambiarPagina(delta: number) {
    setFiltros((prev) => ({ ...prev, pagina: Math.max(1, prev.pagina + delta) }))
  }

  function crearBorrador() {
    navigate('/compras/nueva')
  }

  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1

  const herramientas = puedeEscribir ? (
    <nav className="p-2 d-flex gap-2">
      <button type="button" className="btn btn-sm btn-success rounded-0 text-nowrap" onClick={crearBorrador}>
        Nueva compra
      </button>
    </nav>
  ) : undefined

  return (
    <div className="container-fluid py-4">
      <Box titulo="Compras" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {errorProveedores && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorProveedores}</div>}

        <div className="row g-2 align-items-end mb-3">
          <div className="col-md-3">
            <label className="form-label" htmlFor="compras-filtro-proveedor">
              Proveedor
            </label>
            <select
              id="compras-filtro-proveedor"
              className="form-select rounded-0"
              value={filtros.idProveedor ?? ''}
              onChange={(e) => cambiarFiltro({ idProveedor: e.target.value === '' ? null : Number(e.target.value) })}
            >
              <option value="">Todos</option>
              {(proveedores ?? []).map((p) => (
                <option key={p.id} value={p.id}>
                  {p.razonSocial}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="compras-filtro-estado">
              Estado
            </label>
            <select
              id="compras-filtro-estado"
              className="form-select rounded-0"
              value={filtros.estado ?? ''}
              onChange={(e) => cambiarFiltro({ estado: e.target.value === '' ? null : (e.target.value as EstadoCompra) })}
            >
              {OPCIONES_ESTADO.map((o) => (
                <option key={o.valor} value={o.valor}>
                  {o.etiqueta}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="compras-filtro-desde">
              Desde
            </label>
            <input
              id="compras-filtro-desde"
              type="date"
              className="form-control rounded-0"
              value={filtros.desde}
              onChange={(e) => cambiarFiltro({ desde: e.target.value })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="compras-filtro-hasta">
              Hasta
            </label>
            <input
              id="compras-filtro-hasta"
              type="date"
              className="form-control rounded-0"
              value={filtros.hasta}
              onChange={(e) => cambiarFiltro({ hasta: e.target.value })}
            />
          </div>
        </div>

        {cargando && !pagina && <Cargando />}

        {pagina && (
          <>
            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Número</th>
                    <th>Proveedor</th>
                    <th>Tipo</th>
                    <th>Estado</th>
                    <th>Estado de pago</th>
                    <th>Fecha de recepción</th>
                    <th className="text-end">Total</th>
                  </tr>
                </thead>
                <tbody>
                  {pagina.items.map((c: CompraListada) => (
                    <tr key={c.id}>
                      <td>
                        <Link className="link-underline-opacity-0" to={`/compras/${c.id}`}>
                          {c.numeroExterno ?? `#${c.id}`}
                        </Link>
                      </td>
                      <td>{proveedorPorId[c.idProveedor]?.razonSocial ?? `Proveedor #${c.idProveedor}`}</td>
                      <td>{tipoPorId[c.idTipoComprobante]?.codigo ?? `#${c.idTipoComprobante}`}</td>
                      <td>
                        <span className={`badge rounded-0 ${claseDeBadgeDeEstado(c.estado)}`}>{etiquetaDeEstadoCompra(c.estado)}</span>
                      </td>
                      <td>{estadosPago[c.id] ? etiquetaDeEstadoPago(estadosPago[c.id]) : '—'}</td>
                      <td>{formatearFecha(c.fechaRecepcion)}</td>
                      <td className="text-end">{formatearMoneda(c.total)}</td>
                    </tr>
                  ))}
                  {pagina.items.length === 0 && (
                    <tr>
                      <td colSpan={7} className="text-center text-muted py-4">
                        No hay compras que coincidan con los filtros.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                Página {pagina.pagina} de {totalPaginas} — {pagina.total} compra(s)
              </span>
              <div className="d-flex gap-2">
                <button
                  type="button"
                  className="btn btn-sm btn-outline-secondary rounded-0"
                  disabled={pagina.pagina <= 1 || cargando}
                  onClick={() => cambiarPagina(-1)}
                >
                  Anterior
                </button>
                <button
                  type="button"
                  className="btn btn-sm btn-outline-secondary rounded-0"
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
