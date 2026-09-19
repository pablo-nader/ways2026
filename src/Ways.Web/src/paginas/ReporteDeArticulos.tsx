import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeCatalogo } from '../api/catalogos'
import {
  clienteDeReportes,
  filtrosDeReporteDeArticulosVacios,
  rutasDeExportacion,
  type FiltrosDeReporteDeArticulos,
} from '../api/reportes'
import type {
  AreaAlta,
  AreaListado,
  ArticuloDeReporte,
  CategoriaListado,
  GrupoAlta,
  GrupoListado,
  MarcaAlta,
  MarcaListado,
  PaginaDe,
  ProveedorListado,
} from '../api/tipos'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

const clienteAreas = clienteDeCatalogo<AreaListado, AreaAlta>('areas')
const clienteMarcas = clienteDeCatalogo<MarcaListado, MarcaAlta>('marcas')
const clienteGrupos = clienteDeCatalogo<GrupoListado, GrupoAlta>('grupos')

const VALOR_SIN = '__sin__'

/** Etiqueta de un proveedor en el selector: nombre de fantasía cuando no está vacío/en blanco, si
 * no razón social — mismo criterio que `ServicioDeReportesDeArticulos.ProyectarArticulosDeReporteAsync`
 * del lado del servidor (nunca dos reglas distintas para el mismo dato). */
export function etiquetaDeProveedor(p: Pick<ProveedorListado, 'nombreFantasia' | 'razonSocial'>): string {
  return p.nombreFantasia && p.nombreFantasia.trim() !== '' ? p.nombreFantasia : p.razonSocial
}

/** Ordena proveedores alfabéticamente por su etiqueta ya resuelta — no por razón social cruda,
 * para que el orden visible coincida con lo que el usuario en realidad lee en el selector. */
export function ordenarProveedoresPorEtiqueta(proveedores: ProveedorListado[]): ProveedorListado[] {
  return [...proveedores].sort((a, b) => etiquetaDeProveedor(a).localeCompare(etiquetaDeProveedor(b)))
}

function CeldaOpcional({ valor }: { valor: string | null }) {
  if (valor === null) {
    return <span className="text-muted fst-italic">Sin asignar</span>
  }
  return <>{valor}</>
}

/**
 * Reporte de completitud de catálogo (owner: "artículos sin proveedor, sin marca, sin categoría,
 * sin grupo... para verlos de un vistazo"). Misma política que el resto de `/reportes/*`
 * (`Politicas.LecturaDeReportes`: Supervisor + Admin).
 *
 * Per `react-async-state` reglas 2/4/9: cada cambio de filtro dispara una nueva consulta gateada
 * por un único `useRef` de generación — una respuesta desactualizada nunca pisa un filtro que el
 * usuario ya cambió mientras tanto.
 */
export function ReporteDeArticulos() {
  const [areas, setAreas] = useState<AreaListado[]>([])
  const [categorias, setCategorias] = useState<CategoriaListado[]>([])
  const [marcas, setMarcas] = useState<MarcaListado[]>([])
  const [grupos, setGrupos] = useState<GrupoListado[]>([])
  const [proveedores, setProveedores] = useState<ProveedorListado[]>([])
  const [erroresCatalogos, setErroresCatalogos] = useState<string[]>([])

  const [filtros, setFiltros] = useState<FiltrosDeReporteDeArticulos>(filtrosDeReporteDeArticulosVacios())
  const [pagina, setPagina] = useState<PaginaDe<ArticuloDeReporte> | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [errorDescarga, setErrorDescarga] = useState('')
  const generacionRef = useRef(0)

  function agregarErrorCatalogo(mensaje: string) {
    setErroresCatalogos((prev) => (prev.includes(mensaje) ? prev : [...prev, mensaje]))
  }

  useEffect(() => {
    clienteAreas
      .listar(false)
      .then(setAreas)
      .catch(() => {
        setAreas([])
        agregarErrorCatalogo('No se pudieron cargar las áreas.')
      })
    api
      .get<CategoriaListado[]>('/catalogos/categorias')
      .then(setCategorias)
      .catch(() => {
        setCategorias([])
        agregarErrorCatalogo('No se pudieron cargar las categorías.')
      })
    clienteMarcas
      .listar(false)
      .then(setMarcas)
      .catch(() => {
        setMarcas([])
        agregarErrorCatalogo('No se pudieron cargar las marcas.')
      })
    clienteGrupos
      .listar(false)
      .then(setGrupos)
      .catch(() => {
        setGrupos([])
        agregarErrorCatalogo('No se pudieron cargar los grupos.')
      })
    api
      .get<PaginaDe<ProveedorListado>>('/proveedores?tamanio=200')
      .then((p) => setProveedores(p.items))
      .catch(() => {
        setProveedores([])
        agregarErrorCatalogo('No se pudieron cargar los proveedores.')
      })
  }, [])

  const cargar = useCallback(() => {
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    clienteDeReportes
      .articulos(filtros)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el reporte de artículos.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [filtros])

  useEffect(() => {
    cargar()
  }, [cargar])

  function cambiarFiltro(cambios: Partial<Omit<FiltrosDeReporteDeArticulos, 'pagina' | 'tamanio'>>) {
    setFiltros((prev) => ({ ...prev, ...cambios, pagina: 1 }))
  }

  function cambiarPagina(delta: number) {
    setFiltros((prev) => ({ ...prev, pagina: Math.max(1, prev.pagina + delta) }))
  }

  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1
  const proveedoresOrdenados = ordenarProveedoresPorEtiqueta(proveedores)

  return (
    <div className="container-fluid py-4">
      <Box titulo="Reporte de artículos" variante="inverse">
        {error && (
          <div className="alert alert-danger rounded-0 d-flex justify-content-between align-items-center gap-2">
            <span>{error}</span>
            <button type="button" className="btn btn-sm btn-outline-danger rounded-0" onClick={cargar}>
              Reintentar
            </button>
          </div>
        )}
        {erroresCatalogos.map((mensaje) => (
          <div key={mensaje} className="alert alert-warning rounded-0 py-1 px-2 small">
            {mensaje}
          </div>
        ))}

        <div className="row g-2 align-items-end mb-3">
          <div className="col-md-2">
            <label className="form-label" htmlFor="reporte-articulos-area">
              Área
            </label>
            <select
              id="reporte-articulos-area"
              className="form-select rounded-0"
              value={filtros.idArea ?? ''}
              onChange={(e) => cambiarFiltro({ idArea: e.target.value === '' ? null : Number(e.target.value) })}
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
            <label className="form-label" htmlFor="reporte-articulos-categoria">
              Categoría
            </label>
            <select
              id="reporte-articulos-categoria"
              className="form-select rounded-0"
              value={filtros.sinCategoria ? VALOR_SIN : (filtros.idCategoria ?? '')}
              onChange={(e) => {
                const valor = e.target.value
                if (valor === VALOR_SIN) cambiarFiltro({ idCategoria: null, sinCategoria: true })
                else if (valor === '') cambiarFiltro({ idCategoria: null, sinCategoria: false })
                else cambiarFiltro({ idCategoria: Number(valor), sinCategoria: false })
              }}
            >
              <option value="">Todas</option>
              <option value={VALOR_SIN}>Sin categoría</option>
              {categorias.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.nombre}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="reporte-articulos-marca">
              Marca
            </label>
            <select
              id="reporte-articulos-marca"
              className="form-select rounded-0"
              value={filtros.sinMarca ? VALOR_SIN : (filtros.idMarca ?? '')}
              onChange={(e) => {
                const valor = e.target.value
                if (valor === VALOR_SIN) cambiarFiltro({ idMarca: null, sinMarca: true })
                else if (valor === '') cambiarFiltro({ idMarca: null, sinMarca: false })
                else cambiarFiltro({ idMarca: Number(valor), sinMarca: false })
              }}
            >
              <option value="">Todas</option>
              <option value={VALOR_SIN}>Sin marca</option>
              {marcas.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.nombre}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="reporte-articulos-grupo">
              Grupo
            </label>
            <select
              id="reporte-articulos-grupo"
              className="form-select rounded-0"
              value={filtros.sinGrupo ? VALOR_SIN : (filtros.idGrupo ?? '')}
              onChange={(e) => {
                const valor = e.target.value
                if (valor === VALOR_SIN) cambiarFiltro({ idGrupo: null, sinGrupo: true })
                else if (valor === '') cambiarFiltro({ idGrupo: null, sinGrupo: false })
                else cambiarFiltro({ idGrupo: Number(valor), sinGrupo: false })
              }}
            >
              <option value="">Todos</option>
              <option value={VALOR_SIN}>Sin grupo</option>
              {grupos.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.nombre}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="reporte-articulos-proveedor">
              Proveedor
            </label>
            <select
              id="reporte-articulos-proveedor"
              className="form-select rounded-0"
              value={filtros.sinProveedor ? VALOR_SIN : (filtros.idProveedor ?? '')}
              onChange={(e) => {
                const valor = e.target.value
                if (valor === VALOR_SIN) cambiarFiltro({ idProveedor: null, sinProveedor: true })
                else if (valor === '') cambiarFiltro({ idProveedor: null, sinProveedor: false })
                else cambiarFiltro({ idProveedor: Number(valor), sinProveedor: false })
              }}
            >
              <option value="">Todos</option>
              <option value={VALOR_SIN}>Sin proveedor</option>
              {proveedoresOrdenados.map((p) => (
                <option key={p.id} value={p.id}>
                  {etiquetaDeProveedor(p)}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="reporte-articulos-estado">
              Estado
            </label>
            <select
              id="reporte-articulos-estado"
              className="form-select rounded-0"
              value={filtros.activo === null ? '' : String(filtros.activo)}
              onChange={(e) => {
                const valor = e.target.value
                cambiarFiltro({ activo: valor === '' ? null : valor === 'true' })
              }}
            >
              <option value="">Todos</option>
              <option value="true">Activos</option>
              <option value="false">Inactivos</option>
            </select>
          </div>

          <div className="col-auto form-check mt-2">
            <input
              id="reporte-articulos-solo-incompletos"
              type="checkbox"
              className="form-check-input"
              checked={filtros.soloIncompletos}
              onChange={(e) => cambiarFiltro({ soloIncompletos: e.target.checked })}
            />
            <label className="form-check-label" htmlFor="reporte-articulos-solo-incompletos">
              Solo incompletos
            </label>
          </div>

          <div className="col-auto">
            <BotonDeDescarga
              ruta={rutasDeExportacion.articulos(filtros)}
              etiqueta="Descargar"
              onError={setErrorDescarga}
              onInicio={() => setErrorDescarga('')}
            />
          </div>
        </div>

        {errorDescarga && <div className="alert alert-danger rounded-0 py-1 px-2 small mb-2">{errorDescarga}</div>}

        {cargando && !pagina && <Cargando />}

        {pagina && (
          <>
            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Código</th>
                    <th>Nombre</th>
                    <th>Área</th>
                    <th>Categoría</th>
                    <th>Marca</th>
                    <th>Grupo</th>
                    <th>Proveedor</th>
                    <th>Estado</th>
                  </tr>
                </thead>
                <tbody>
                  {pagina.items.map((f) => (
                    <tr key={f.id}>
                      <td>{f.codigoInterno}</td>
                      <td>{f.nombre}</td>
                      <td>{f.area}</td>
                      <td>
                        <CeldaOpcional valor={f.categoria} />
                      </td>
                      <td>
                        <CeldaOpcional valor={f.marca} />
                      </td>
                      <td>
                        <CeldaOpcional valor={f.grupo} />
                      </td>
                      <td>
                        <CeldaOpcional valor={f.proveedor} />
                      </td>
                      <td>{f.activo ? 'Activo' : 'Inactivo'}</td>
                    </tr>
                  ))}
                  {pagina.items.length === 0 && (
                    <tr>
                      <td colSpan={8} className="text-center text-muted py-4">
                        No hay artículos que coincidan con los filtros.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                Página {pagina.pagina} de {totalPaginas} — {pagina.total} artículo(s)
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
