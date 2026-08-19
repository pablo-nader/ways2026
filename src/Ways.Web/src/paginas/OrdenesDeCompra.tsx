import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { api, ErrorApi } from '../api/cliente'
import {
  claseDeBadgeDeEstadoOrdenCompra,
  clienteDeOrdenesDeCompra,
  etiquetaDeEstadoOrdenCompra,
  filtrosDeOrdenesDeCompraVacios,
  type FiltrosDeOrdenesDeCompra,
} from '../api/ordenesDeCompra'
import { ROL } from '../api/tipos'
import type { EstadoOrdenCompra, OrdenDeCompraListada, PaginaDe, PaginaDeOrdenesDeCompra, ProveedorListado, PuntoVentaListado } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

const OPCIONES_ESTADO: { valor: EstadoOrdenCompra | ''; etiqueta: string }[] = [
  { valor: '', etiqueta: 'Todos' },
  { valor: 'Borrador', etiqueta: 'Borrador' },
  { valor: 'Enviada', etiqueta: 'Enviada' },
  { valor: 'RecibidaParcial', etiqueta: 'Recibida parcial' },
  { valor: 'Cerrada', etiqueta: 'Cerrada' },
  { valor: 'Anulada', etiqueta: 'Anulada' },
]

function formatearFecha(iso: string | null): string {
  return iso ? new Date(iso).toLocaleDateString('es-AR') : '—'
}

/**
 * Listado de órdenes de compra (stage-16-ordenes-de-compra, Slice 6; design: Web composition,
 * decisión 16): filtros proveedor/punto de venta/estado/fecha + pager (mismo shape que
 * `CuentaCorrienteDeProveedor.tsx`/`Compras.tsx`), entrada al editor de borrador. La ruta sigue
 * `Politicas.OperacionDePos` (el gate de lectura, `RutaProtegida` con los tres roles operativos) —
 * `puedeEscribir` oculta "Nueva orden" como defensa en profundidad cosmética, la política de
 * escritura real es `GestionDeCatalogo` del lado del servidor.
 */
export function OrdenesDeCompra() {
  const { usuario } = useAuth()
  const puedeEscribir = usuario !== null && usuario.rolId === ROL.Admin
  const navigate = useNavigate()

  const [proveedores, setProveedores] = useState<ProveedorListado[] | null>(null)
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorReferencia, setErrorReferencia] = useState('')

  const [filtros, setFiltros] = useState<FiltrosDeOrdenesDeCompra>(filtrosDeOrdenesDeCompraVacios())

  const [pagina, setPagina] = useState<PaginaDeOrdenesDeCompra | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

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
        setErrorReferencia((prev) => (prev ? prev : e instanceof ErrorApi ? e.message : 'No se pudieron cargar los proveedores.'))
      })

    api
      .get<PuntoVentaListado[]>('/puntos-venta')
      .then((lista) => {
        if (!vigente) return
        setPuntosVenta(lista)
      })
      .catch((e) => {
        if (!vigente) return
        setPuntosVenta([])
        setErrorReferencia((prev) => (prev ? prev : e instanceof ErrorApi ? e.message : 'No se pudieron cargar los puntos de venta.'))
      })

    return () => {
      vigente = false
    }
  }, [])

  // regla 2: cada cambio de filtro/página dispara una nueva consulta — una respuesta
  // desactualizada nunca puede pisar la más reciente.
  const cargar = useCallback(() => {
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    clienteDeOrdenesDeCompra
      .listar(filtros)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las órdenes de compra.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [filtros])

  useEffect(() => {
    cargar()
  }, [cargar])

  const proveedorPorId: Record<number, ProveedorListado> = {}
  for (const p of proveedores ?? []) proveedorPorId[p.id] = p

  const puntoVentaPorId: Record<number, PuntoVentaListado> = {}
  for (const pv of puntosVenta ?? []) puntoVentaPorId[pv.id] = pv

  function cambiarFiltro(cambios: Partial<Omit<FiltrosDeOrdenesDeCompra, 'pagina' | 'tamanio'>>) {
    setFiltros((prev) => ({ ...prev, ...cambios, pagina: 1 }))
  }

  function cambiarPagina(delta: number) {
    setFiltros((prev) => ({ ...prev, pagina: Math.max(1, prev.pagina + delta) }))
  }

  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1

  const herramientas = puedeEscribir ? (
    <nav className="p-2 d-flex gap-2">
      <button
        type="button"
        className="btn btn-sm btn-success rounded-0 text-nowrap"
        onClick={() => navigate('/ordenes-compra/nueva')}
      >
        Nueva orden de compra
      </button>
    </nav>
  ) : undefined

  return (
    <div className="container-fluid py-4">
      <Box titulo="Órdenes de compra" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {errorReferencia && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorReferencia}</div>}

        <div className="row g-2 align-items-end mb-3">
          <div className="col-md-3">
            <label className="form-label" htmlFor="oc-filtro-proveedor">
              Proveedor
            </label>
            <select
              id="oc-filtro-proveedor"
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
            <label className="form-label" htmlFor="oc-filtro-punto-venta">
              Punto de venta
            </label>
            <select
              id="oc-filtro-punto-venta"
              className="form-select rounded-0"
              value={filtros.idPuntoVenta ?? ''}
              onChange={(e) => cambiarFiltro({ idPuntoVenta: e.target.value === '' ? null : Number(e.target.value) })}
            >
              <option value="">Todos</option>
              {(puntosVenta ?? []).map((pv) => (
                <option key={pv.id} value={pv.id}>
                  {pv.nombre}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="oc-filtro-estado">
              Estado
            </label>
            <select
              id="oc-filtro-estado"
              className="form-select rounded-0"
              value={filtros.estado ?? ''}
              onChange={(e) => cambiarFiltro({ estado: e.target.value === '' ? null : (e.target.value as EstadoOrdenCompra) })}
            >
              {OPCIONES_ESTADO.map((o) => (
                <option key={o.valor} value={o.valor}>
                  {o.etiqueta}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="oc-filtro-desde">
              Desde
            </label>
            <input
              id="oc-filtro-desde"
              type="date"
              className="form-control rounded-0"
              value={filtros.desde}
              onChange={(e) => cambiarFiltro({ desde: e.target.value })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="oc-filtro-hasta">
              Hasta
            </label>
            <input
              id="oc-filtro-hasta"
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
                    <th>Punto de venta</th>
                    <th>Estado</th>
                    <th>Fecha de emisión</th>
                    <th>Fecha esperada</th>
                  </tr>
                </thead>
                <tbody>
                  {pagina.items.map((o: OrdenDeCompraListada) => (
                    <tr key={o.id}>
                      <td>
                        <Link className="link-underline-opacity-0" to={`/ordenes-compra/${o.id}`}>
                          {o.numero ?? `#${o.id}`}
                        </Link>
                      </td>
                      <td>{proveedorPorId[o.idProveedor]?.razonSocial ?? `Proveedor #${o.idProveedor}`}</td>
                      <td>{puntoVentaPorId[o.idPuntoVenta]?.nombre ?? `PV #${o.idPuntoVenta}`}</td>
                      <td>
                        <span className={`badge rounded-0 ${claseDeBadgeDeEstadoOrdenCompra(o.estado)}`}>
                          {etiquetaDeEstadoOrdenCompra(o.estado)}
                        </span>
                      </td>
                      <td>{formatearFecha(o.fechaEmision)}</td>
                      <td>{formatearFecha(o.fechaEsperada)}</td>
                    </tr>
                  ))}
                  {pagina.items.length === 0 && (
                    <tr>
                      <td colSpan={6} className="text-center text-muted py-4">
                        No hay órdenes de compra que coincidan con los filtros.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                Página {pagina.pagina} de {totalPaginas} — {pagina.total} orden(es)
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
