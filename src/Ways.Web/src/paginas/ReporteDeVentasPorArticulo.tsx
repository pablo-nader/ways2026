import { useCallback, useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../api/articulos'
import { clienteDeCatalogo } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import {
  clienteDeReportes,
  rangoDeLaSemanaActual,
  rutasDeExportacion,
  type FiltrosDeVentasPorArticulo,
} from '../api/reportes'
import type {
  AreaAlta,
  AreaListado,
  ArticuloVendido,
  CategoriaListado,
  EmpresaListado,
  GrupoAlta,
  GrupoListado,
  PuntoVentaListado,
  VentasPorArticulo,
} from '../api/tipos'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { Box } from '../componentes/Box'
import { CampoFecha } from '../componentes/CampoFecha'
import { Cargando } from '../componentes/Cargando'
import { SelectorConBusqueda, type OpcionDeSelector } from '../componentes/SelectorConBusqueda'
import { formatearImporte } from '../formato/importes'

const clienteAreas = clienteDeCatalogo<AreaListado, AreaAlta>('areas')
const clienteGrupos = clienteDeCatalogo<GrupoListado, GrupoAlta>('grupos')

/** Las cantidades enteras se muestran sin decimales; las de peso, con los tres que admite el servidor. */
function formatearCantidad(cantidad: number): string {
  return formatearImporte(cantidad, { decimales: Number.isInteger(cantidad) ? 0 : 3 })
}

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function CeldaOpcional({ valor }: { valor: string | null }) {
  if (valor === null) return <span className="text-muted fst-italic">Sin asignar</span>
  return <>{valor}</>
}

function idODefecto(valor: string): number | null {
  return valor === '' ? null : Number(valor)
}

/**
 * Cuánto y a cuánto se vendió de cada artículo en un rango, filtrable por área, grupo, categoría y artículo
 * (`Politicas.LecturaDeReportes`: Supervisor + Admin). Cada cambio de filtro lanza una consulta nueva gateada
 * por un único `useRef` de generación (`react-async-state`): una respuesta vieja nunca pisa un filtro que el
 * usuario ya cambió.
 */
export function ReporteDeVentasPorArticulo() {
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[]>([])
  const [areas, setAreas] = useState<AreaListado[]>([])
  const [grupos, setGrupos] = useState<GrupoListado[]>([])
  const [categorias, setCategorias] = useState<CategoriaListado[]>([])
  const [erroresCatalogos, setErroresCatalogos] = useState<string[]>([])

  const [idEmpresa, setIdEmpresa] = useState<number | null>(null)
  const [idPuntoVenta, setIdPuntoVenta] = useState<number | null>(null)
  const [desde, setDesde] = useState(() => rangoDeLaSemanaActual().desde)
  const [hasta, setHasta] = useState(() => rangoDeLaSemanaActual().hasta)
  const [idArea, setIdArea] = useState<number | null>(null)
  const [idGrupo, setIdGrupo] = useState<number | null>(null)
  const [idCategoria, setIdCategoria] = useState<number | null>(null)
  const [articulo, setArticulo] = useState<OpcionDeSelector | null>(null)

  const [datos, setDatos] = useState<VentasPorArticulo | null>(null)
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const [errorDescarga, setErrorDescarga] = useState('')
  const generacionRef = useRef(0)

  useEffect(() => {
    let vigente = true
    const avisar = (mensaje: string) => {
      if (vigente) setErroresCatalogos((prev) => (prev.includes(mensaje) ? prev : [...prev, mensaje]))
    }

    Promise.all([clienteDeOrganizacion.listarEmpresas(), clienteDeOrganizacion.listarPuntosVenta()])
      .then(([listaEmpresas, listaPuntosVenta]) => {
        if (!vigente) return
        setEmpresas(listaEmpresas)
        setPuntosVenta(listaPuntosVenta)
        if (listaEmpresas.length > 0) setIdEmpresa(listaEmpresas[0].id)
      })
      .catch((e) => {
        if (!vigente) return
        setEmpresas([])
        avisar(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las empresas.')
      })
    clienteAreas
      .listar(false)
      .then((l) => {
        if (vigente) setAreas(l)
      })
      .catch(() => avisar('No se pudieron cargar las áreas.'))
    clienteGrupos
      .listar(false)
      .then((l) => {
        if (vigente) setGrupos(l)
      })
      .catch(() => avisar('No se pudieron cargar los grupos.'))
    api
      .get<CategoriaListado[]>('/catalogos/categorias')
      .then((l) => {
        if (vigente) setCategorias(l)
      })
      .catch(() => avisar('No se pudieron cargar las categorías.'))

    return () => {
      vigente = false
    }
  }, [])

  const rangoValido = desde !== '' && hasta !== '' && desde <= hasta
  const idArticulo = articulo === null ? null : Number(articulo.id)
  const filtros: FiltrosDeVentasPorArticulo | null =
    idEmpresa !== null && rangoValido
      ? { idEmpresa, idPuntoVenta, desde, hasta, idArea, idGrupo, idCategoria, idArticulo }
      : null

  // La consulta depende de los valores y no de la identidad del objeto: se recrea solo si cambia un filtro.
  const clave = filtros === null ? null : JSON.stringify(filtros)

  const cargar = useCallback(() => {
    const miGeneracion = (generacionRef.current += 1)
    if (clave === null) {
      setDatos(null)
      setCargando(false)
      setError('')
      return
    }
    setCargando(true)
    setError('')

    clienteDeReportes
      .ventasPorArticulo(JSON.parse(clave) as FiltrosDeVentasPorArticulo)
      .then((respuesta) => {
        if (generacionRef.current !== miGeneracion) return
        setDatos(respuesta)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setDatos(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el reporte de ventas por artículo.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [clave])

  useEffect(() => {
    cargar()
  }, [cargar])

  // Incluye los dados de baja: el reporte sigue mostrando lo que se vendió de ellos en el período.
  const buscarArticulos = useCallback(
    (texto: string) =>
      clienteDeArticulos
        .listar(texto, true)
        .then((pagina) => pagina.items.map((a) => ({ id: a.id, etiqueta: `${a.codigoInterno} — ${a.nombre}` }))),
    [],
  )

  const puntosVentaDeLaEmpresa = puntosVenta.filter((pv) => pv.idEmpresa === idEmpresa)
  const filas: ArticuloVendido[] = datos?.articulos ?? []

  return (
    <div className="container-fluid py-4">
      <Box titulo="Ventas por artículo" variante="inverse">
        {erroresCatalogos.map((mensaje) => (
          <div key={mensaje} className="alert alert-warning py-1 px-2 small">
            {mensaje}
          </div>
        ))}

        {empresas === null ? (
          <Cargando />
        ) : empresas.length === 0 ? (
          <p className="text-muted text-center py-4">No hay empresas visibles para mostrar el reporte.</p>
        ) : (
          <>
            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-2">
                <label className="form-label" htmlFor="ventas-articulo-empresa">
                  Empresa
                </label>
                <select
                  id="ventas-articulo-empresa"
                  className="form-select"
                  value={idEmpresa ?? ''}
                  onChange={(e) => {
                    setIdEmpresa(Number(e.target.value))
                    setIdPuntoVenta(null)
                  }}
                >
                  {empresas.map((e) => (
                    <option key={e.id} value={e.id}>
                      {e.razonSocial}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="ventas-articulo-punto-venta">
                  Punto de venta
                </label>
                <select
                  id="ventas-articulo-punto-venta"
                  className="form-select"
                  value={idPuntoVenta ?? ''}
                  onChange={(e) => setIdPuntoVenta(idODefecto(e.target.value))}
                >
                  <option value="">Todos</option>
                  {puntosVentaDeLaEmpresa.map((pv) => (
                    <option key={pv.id} value={pv.id}>
                      {pv.nombre}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="ventas-articulo-desde">
                  Desde
                </label>
                <CampoFecha id="ventas-articulo-desde" className="form-control" value={desde} onChange={setDesde} />
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="ventas-articulo-hasta">
                  Hasta
                </label>
                <CampoFecha id="ventas-articulo-hasta" className="form-control" value={hasta} onChange={setHasta} />
              </div>
            </div>

            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-2">
                <label className="form-label" htmlFor="ventas-articulo-area">
                  Área
                </label>
                <select
                  id="ventas-articulo-area"
                  className="form-select"
                  value={idArea ?? ''}
                  onChange={(e) => setIdArea(idODefecto(e.target.value))}
                >
                  <option value="">Todas</option>
                  {areas.map((a) => (
                    <option key={a.id} value={a.id}>
                      {a.nombre}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="ventas-articulo-grupo">
                  Grupo
                </label>
                <select
                  id="ventas-articulo-grupo"
                  className="form-select"
                  value={idGrupo ?? ''}
                  onChange={(e) => setIdGrupo(idODefecto(e.target.value))}
                >
                  <option value="">Todos</option>
                  {grupos.map((g) => (
                    <option key={g.id} value={g.id}>
                      {g.nombre}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="ventas-articulo-categoria">
                  Categoría
                </label>
                <select
                  id="ventas-articulo-categoria"
                  className="form-select"
                  value={idCategoria ?? ''}
                  onChange={(e) => setIdCategoria(idODefecto(e.target.value))}
                >
                  <option value="">Todas</option>
                  {categorias.map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.nombre}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-4">
                <label className="form-label" htmlFor="ventas-articulo-articulo">
                  Artículo
                </label>
                <div className="d-flex gap-2">
                  <div className="flex-grow-1" style={{ minWidth: 0 }}>
                    <SelectorConBusqueda<OpcionDeSelector>
                      id="ventas-articulo-articulo"
                      nombreAccesible="Artículo"
                      opciones={articulo ? [articulo] : []}
                      valor={articulo?.id ?? null}
                      placeholder="Todos"
                      alBuscar={buscarArticulos}
                      onChange={setArticulo}
                    />
                  </div>
                  {articulo !== null && (
                    <button type="button" className="btn btn-outline-secondary" onClick={() => setArticulo(null)}>
                      Quitar
                    </button>
                  )}
                </div>
              </div>
              <div className="col-auto">
                {filtros !== null && (
                  <BotonDeDescarga
                    ruta={rutasDeExportacion.ventasPorArticulo(filtros)}
                    etiqueta="Descargar"
                    onError={setErrorDescarga}
                    onInicio={() => setErrorDescarga('')}
                  />
                )}
              </div>
            </div>

            {!rangoValido && (
              <div className="alert alert-warning py-1 px-2 small">
                Elegí un rango de fechas válido: la fecha inicial no puede ser posterior a la final.
              </div>
            )}
            {error && (
              <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2">
                <span>{error}</span>
                <button type="button" className="btn btn-sm btn-outline-danger" onClick={cargar}>
                  Reintentar
                </button>
              </div>
            )}
            {errorDescarga && <div className="alert alert-danger py-1 px-2 small mb-2">{errorDescarga}</div>}

            {cargando && !datos && <Cargando />}

            {cargando && datos && (
              <div className="small text-muted mb-2" role="status">
                Actualizando…
              </div>
            )}
            {datos && (
              <div className="table-responsive" aria-busy={cargando} style={cargando ? { opacity: 0.5 } : undefined}>
                <table className="table table-sm table-striped table-bordered align-middle">
                  <thead>
                    <tr>
                      <th>Código</th>
                      <th>Descripción</th>
                      <th>Área</th>
                      <th>Grupo</th>
                      <th>Categoría</th>
                      <th className="text-end">Cantidad</th>
                      <th className="text-end">Total</th>
                    </tr>
                  </thead>
                  <tbody>
                    {filas.map((f) => (
                      <tr key={f.idArticulo}>
                        <td>
                          <CeldaOpcional valor={f.codigo} />
                        </td>
                        <td>{f.descripcion}</td>
                        <td>
                          <CeldaOpcional valor={f.area} />
                        </td>
                        <td>
                          <CeldaOpcional valor={f.grupo} />
                        </td>
                        <td>
                          <CeldaOpcional valor={f.categoria} />
                        </td>
                        <td className="text-end">{formatearCantidad(f.cantidad)}</td>
                        <td className="text-end">{formatearMoneda(f.total)}</td>
                      </tr>
                    ))}
                    {filas.length === 0 && (
                      <tr>
                        <td colSpan={7} className="text-center text-muted py-4">
                          No hay ventas de artículos que coincidan con los filtros.
                        </td>
                      </tr>
                    )}
                  </tbody>
                  {filas.length > 0 && (
                    <tfoot>
                      <tr className="fw-bold">
                        <td colSpan={5}>Total</td>
                        <td className="text-end">{formatearCantidad(datos.cantidadTotal)}</td>
                        <td className="text-end">{formatearMoneda(datos.total)}</td>
                      </tr>
                    </tfoot>
                  )}
                </table>
              </div>
            )}
          </>
        )}
      </Box>
    </div>
  )
}
