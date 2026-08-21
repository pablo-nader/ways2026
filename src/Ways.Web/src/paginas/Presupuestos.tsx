import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import {
  claseDeBadgeDeEstadoPresupuesto,
  claseDeBadgeDeVencimiento,
  clienteDePresupuestos,
  etiquetaDeEstadoPresupuesto,
  etiquetaDeVencimiento,
  filtrosDePresupuestosVacios,
  type FiltrosDePresupuestos,
} from '../api/presupuestos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import type { ClienteListado, EstadoPresupuesto, PaginaDePresupuestos, PresupuestoListado, PuntoVentaListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

const OPCIONES_ESTADO: { valor: EstadoPresupuesto | ''; etiqueta: string }[] = [
  { valor: '', etiqueta: 'Todos' },
  { valor: 'Borrador', etiqueta: 'Borrador' },
  { valor: 'Enviado', etiqueta: 'Enviado' },
  { valor: 'Convertido', etiqueta: 'Convertido' },
  { valor: 'Anulado', etiqueta: 'Anulado' },
]

function formatearFecha(iso: string | null): string {
  return iso ? new Date(iso).toLocaleDateString('es-AR') : '—'
}

function etiquetaDeCliente(c: ClienteListado): string {
  const nombreCompleto = c.razonSocial ?? [c.nombre, c.apellido].filter(Boolean).join(' ')
  return `#${c.numero} — ${nombreCompleto}`
}

/**
 * Listado de presupuestos (stage-17-presupuestos-y-remitos, Slice 7; design: Web composition):
 * filtros punto de venta/cliente/estado/`vencido`/desde-hasta + el pager de `HistoricoDeCajas.tsx`/
 * `OrdenesDeCompra.tsx`. El `vencido` toggle queda deshabilitado hasta elegir un punto de venta
 * (design decisión 16 hecha visible, nunca un 400 que el usuario tenga que leer). La ruta sigue
 * `Politicas.OperacionDePos` — Vendedor/Supervisor/Admin leen Y escriben (design decisión 17: "un
 * Vendedor tiene que poder vender"), sin la distinción admin-only de OrdenesDeCompra.tsx.
 */
export function Presupuestos() {
  const navigate = useNavigate()

  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [clientes, setClientes] = useState<ClienteListado[] | null>(null)
  const [errorReferencia, setErrorReferencia] = useState('')

  const [filtros, setFiltros] = useState<FiltrosDePresupuestos>(filtrosDePresupuestosVacios())

  const [pagina, setPagina] = useState<PaginaDePresupuestos | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  useEffect(() => {
    let vigente = true
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

    clienteDeClientes
      .listar('', false)
      .then((p) => {
        if (!vigente) return
        setClientes(p.items)
      })
      .catch((e) => {
        if (!vigente) return
        setClientes([])
        setErrorReferencia((prev) => (prev ? prev : e instanceof ErrorApi ? e.message : 'No se pudieron cargar los clientes.'))
      })

    return () => {
      vigente = false
    }
  }, [])

  // react-async-state regla 2: cada cambio de filtro/página dispara una nueva consulta — una
  // respuesta desactualizada nunca puede pisar la más reciente.
  const cargar = useCallback(() => {
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    clienteDePresupuestos
      .listar(filtros)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los presupuestos.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [filtros])

  useEffect(() => {
    cargar()
  }, [cargar])

  const puntoVentaPorId: Record<number, PuntoVentaListado> = {}
  for (const pv of puntosVenta ?? []) puntoVentaPorId[pv.id] = pv

  const clientePorId: Record<number, ClienteListado> = {}
  for (const c of clientes ?? []) clientePorId[c.id] = c

  function cambiarFiltro(cambios: Partial<Omit<FiltrosDePresupuestos, 'pagina' | 'tamanio'>>) {
    setFiltros((prev) => ({ ...prev, ...cambios, pagina: 1 }))
  }

  function cambiarPuntoVenta(idPuntoVenta: number | null) {
    // Un cambio de punto de venta que deja el filtro sin PV invalida cualquier `vencido` ya
    // elegido — el toggle queda deshabilitado de nuevo y nunca viaja huérfano al servidor
    // (construirQueryDePresupuestos ya lo filtra, esto además limpia la UI).
    setFiltros((prev) => ({ ...prev, idPuntoVenta, vencido: idPuntoVenta === null ? null : prev.vencido, pagina: 1 }))
  }

  function cambiarPagina(delta: number) {
    setFiltros((prev) => ({ ...prev, pagina: Math.max(1, prev.pagina + delta) }))
  }

  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <button type="button" className="btn btn-sm btn-success rounded-0 text-nowrap" onClick={() => navigate('/presupuestos/nuevo')}>
        Nuevo presupuesto
      </button>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Presupuestos" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {errorReferencia && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorReferencia}</div>}

        <div className="row g-2 align-items-end mb-3">
          <div className="col-md-2">
            <label className="form-label" htmlFor="pres-filtro-punto-venta">
              Punto de venta
            </label>
            <select
              id="pres-filtro-punto-venta"
              className="form-select rounded-0"
              value={filtros.idPuntoVenta ?? ''}
              onChange={(e) => cambiarPuntoVenta(e.target.value === '' ? null : Number(e.target.value))}
            >
              <option value="">Todos</option>
              {(puntosVenta ?? []).map((pv) => (
                <option key={pv.id} value={pv.id}>
                  {pv.nombre}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-3">
            <label className="form-label" htmlFor="pres-filtro-cliente">
              Cliente
            </label>
            <select
              id="pres-filtro-cliente"
              className="form-select rounded-0"
              value={filtros.idCliente ?? ''}
              onChange={(e) => cambiarFiltro({ idCliente: e.target.value === '' ? null : Number(e.target.value) })}
            >
              <option value="">Todos</option>
              {(clientes ?? []).map((c) => (
                <option key={c.id} value={c.id}>
                  {etiquetaDeCliente(c)}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="pres-filtro-estado">
              Estado
            </label>
            <select
              id="pres-filtro-estado"
              className="form-select rounded-0"
              value={filtros.estado ?? ''}
              onChange={(e) => cambiarFiltro({ estado: e.target.value === '' ? null : (e.target.value as EstadoPresupuesto) })}
            >
              {OPCIONES_ESTADO.map((o) => (
                <option key={o.valor} value={o.valor}>
                  {o.etiqueta}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-auto d-flex align-items-center gap-1">
            <div className="form-check">
              <input
                id="pres-filtro-vencido"
                type="checkbox"
                className="form-check-input"
                checked={filtros.vencido ?? false}
                disabled={filtros.idPuntoVenta === null}
                onChange={(e) => cambiarFiltro({ vencido: e.target.checked ? true : null })}
              />
              <label className="form-check-label" htmlFor="pres-filtro-vencido">
                Solo vencidos
              </label>
            </div>
            {filtros.idPuntoVenta === null && <span className="small text-muted">(elegí un punto de venta)</span>}
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="pres-filtro-desde">
              Desde
            </label>
            <input
              id="pres-filtro-desde"
              type="date"
              className="form-control rounded-0"
              value={filtros.desde}
              onChange={(e) => cambiarFiltro({ desde: e.target.value })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="pres-filtro-hasta">
              Hasta
            </label>
            <input
              id="pres-filtro-hasta"
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
                    <th>Cliente</th>
                    <th>Punto de venta</th>
                    <th>Estado</th>
                    <th>Vencimiento</th>
                    <th>Fecha de emisión</th>
                    <th className="text-end">Total</th>
                  </tr>
                </thead>
                <tbody>
                  {pagina.items.map((p: PresupuestoListado) => (
                    <tr key={p.id}>
                      <td>
                        <Link className="link-underline-opacity-0" to={`/presupuestos/${p.id}`}>
                          {p.numeroFormateado ?? `#${p.id}`}
                        </Link>
                      </td>
                      <td>{clientePorId[p.idCliente] ? etiquetaDeCliente(clientePorId[p.idCliente]) : `Cliente #${p.idCliente}`}</td>
                      <td>{puntoVentaPorId[p.idPuntoVenta]?.nombre ?? `PV #${p.idPuntoVenta}`}</td>
                      <td>
                        <span className={`badge rounded-0 ${claseDeBadgeDeEstadoPresupuesto(p.estado)}`}>{etiquetaDeEstadoPresupuesto(p.estado)}</span>
                      </td>
                      <td>
                        {p.estado === 'Enviado' ? (
                          <span className={`badge rounded-0 ${claseDeBadgeDeVencimiento(p.vencimiento, p.vencido)}`}>
                            {etiquetaDeVencimiento(p.vencimiento, p.vencido)}
                          </span>
                        ) : (
                          '—'
                        )}
                      </td>
                      <td>{formatearFecha(p.fechaEmision)}</td>
                      <td className="text-end">
                        ${p.total.toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
                      </td>
                    </tr>
                  ))}
                  {pagina.items.length === 0 && (
                    <tr>
                      <td colSpan={7} className="text-center text-muted py-4">
                        No hay presupuestos que coincidan con los filtros.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                Página {pagina.pagina} de {totalPaginas} — {pagina.total} presupuesto(s)
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
