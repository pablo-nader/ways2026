import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { clienteDeArticulos, filtrosDeGrillaDeArticulosVacios } from '../../api/articulos'
import { ErrorApi } from '../../api/cliente'
import type { FilaDeGrillaDeArticulos, FiltrosDeGrillaDeArticulos, PaginaDeGrillaDeArticulos, ProveedorListado } from '../../api/tipos'
import { CampoImporte } from '../../componentes/CampoImporte'
import { Cargando } from '../../componentes/Cargando'
import { formatearImporte } from '../../formato/importes'
import { etiquetaDeProveedor } from './helpers'

const DEMORA_DEBOUNCE_MS = 300
const TAMANIOS_DE_PAGINA = [25, 50, 100]
const AVISO_SIN_LISTA_DE_PRECIO = 'El tenant no tiene una lista de precio por defecto configurada.'

type BorradorDeTexto = Pick<FiltrosDeGrillaDeArticulos, 'codigo' | 'nombre' | 'precioDesde' | 'precioHasta'>

function borradorVacio(): BorradorDeTexto {
  return { codigo: '', nombre: '', precioDesde: null, precioHasta: null }
}

function formatearPrecio(valor: number | null): string {
  return formatearImporte(valor, { simbolo: true })
}

type Props = {
  /** Ya ordenados por `ordenarProveedoresPorEtiqueta` — la grilla no reordena, solo lista. */
  proveedores: ProveedorListado[]
  /** Mismo flag que gobierna el modal en `Articulos.tsx` (guardando/eliminando/escrituras
   * hijas en vuelo): mientras es `true`, Editar/Baja quedan inertes en toda fila. */
  ocupado: boolean
  /** El padre pide un refresco (post guardado/baja) incrementando este contador — la grilla
   * refetchea con los MISMOS `filtros`/página en curso, nunca los resetea. */
  pedidoDeRefresco: number
  onEliminar: (fila: FilaDeGrillaDeArticulos) => void
}

/**
 * Grilla de artículos con filtros por columna y paginación real (feat: articulos-grilla-web) —
 * reemplaza la búsqueda libre + primera página fija de 25 que tenía `Articulos.tsx`. Dueña de su
 * propio fetch/filtros/página: el padre no conoce si un refresco de VISTA (`pedidoDeRefresco`)
 * tuvo éxito — un fallo ahí es un error de esta grilla, no del guardado/baja que ya se
 * confirmó del lado del padre (react-async-state regla 6: un guardado comprometido nunca se
 * reporta como fallido; acá el guardado y el refresco de la grilla son dos fuentes de error
 * completamente separadas, cada una con su propio banner — regla 14).
 */
export function GrillaDeArticulos({ proveedores, ocupado, pedidoDeRefresco, onEliminar }: Props) {
  const [filtros, setFiltros] = useState<FiltrosDeGrillaDeArticulos>(filtrosDeGrillaDeArticulosVacios())
  const [borrador, setBorrador] = useState<BorradorDeTexto>(borradorVacio())
  const [pagina, setPagina] = useState<PaginaDeGrillaDeArticulos | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')

  const borradorRef = useRef(borrador)
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  // Generación: cada cambio de filtro puede solapar al fetch anterior — sin esto, una respuesta
  // que llega tarde pisa el estado con datos de un filtro que el usuario ya cambió.
  const generacionRef = useRef(0)
  const cargaInicialHechaRef = useRef(false)
  const esPrimerRenderDeRefrescoRef = useRef(true)

  useEffect(() => {
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current)
    }
  }, [])

  const cargar = useCallback(() => {
    const generacion = (generacionRef.current += 1)
    setCargando(true)
    setError('')
    clienteDeArticulos
      .grilla(filtros)
      .then((p) => {
        if (generacionRef.current !== generacion) return
        // Una Baja puede vaciar la última página en curso: si el servidor todavía tiene artículos
        // (`total > 0`) pero la página pedida quedó fuera de rango, se pide la última página válida
        // en vez de mostrar "No hay artículos…" con una paginación inconsistente ("Página 2 de 1").
        // Con `total === 0` no hay página válida a la que volver, así que se deja tal cual (sin esto
        // se entraría en un loop pidiendo siempre la página 1 con resultado vacío).
        if (p.total > 0) {
          const ultimaPaginaValida = Math.max(1, Math.ceil(p.total / p.tamanio))
          if (p.pagina > ultimaPaginaValida) {
            setFiltros((prev) => (prev.pagina === ultimaPaginaValida ? prev : { ...prev, pagina: ultimaPaginaValida }))
            return
          }
        }
        setPagina(p)
      })
      .catch((e) => {
        if (generacionRef.current !== generacion) return
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los artículos.')
      })
      .finally(() => {
        if (generacionRef.current !== generacion) return
        setCargando(false)
        cargaInicialHechaRef.current = true
      })
  }, [filtros])

  useEffect(() => {
    cargar()
  }, [cargar])

  // Refresco pedido por el padre tras guardar/dar de baja: el efecto de arriba ya cubrió el mount
  // y cualquier cambio de `filtros` — este solo reacciona a bumps POSTERIORES de
  // `pedidoDeRefresco`, con los mismos `filtros` en curso (no los toca).
  useEffect(() => {
    if (esPrimerRenderDeRefrescoRef.current) {
      esPrimerRenderDeRefrescoRef.current = false
      return
    }
    cargar()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pedidoDeRefresco])

  function programarCambioDeTexto(cambios: Partial<BorradorDeTexto>) {
    const nuevo = { ...borradorRef.current, ...cambios }
    borradorRef.current = nuevo
    setBorrador(nuevo)
    if (debounceRef.current) clearTimeout(debounceRef.current)
    debounceRef.current = setTimeout(() => {
      setFiltros((prev) => ({ ...prev, ...nuevo, pagina: 1 }))
    }, DEMORA_DEBOUNCE_MS)
  }

  function cambiarFiltroInmediato(cambios: Partial<Omit<FiltrosDeGrillaDeArticulos, 'pagina' | 'tamanio'>>) {
    setFiltros((prev) => ({ ...prev, ...cambios, pagina: 1 }))
  }

  function cambiarPagina(delta: number) {
    setFiltros((prev) => ({ ...prev, pagina: Math.max(1, prev.pagina + delta) }))
  }

  function cambiarTamanio(tamanio: number) {
    setFiltros((prev) => ({ ...prev, tamanio, pagina: 1 }))
  }

  function limpiarFiltros() {
    if (debounceRef.current) clearTimeout(debounceRef.current)
    borradorRef.current = borradorVacio()
    setBorrador(borradorVacio())
    // El tamaño de página es una preferencia de visualización, no un filtro: Limpiar no lo toca.
    setFiltros((prev) => ({ ...filtrosDeGrillaDeArticulosVacios(), tamanio: prev.tamanio }))
  }

  // Mismo criterio que la grilla anterior (`Articulos.tsx`): mientras `ocupado`, un click simple
  // no navega, pero Ctrl/Cmd/Shift/click-del-medio sí abren en pestaña nueva sin bloquearse.
  function alClickearEditar(evento: React.MouseEvent<HTMLAnchorElement>) {
    const esClicSimple = evento.button === 0 && !evento.metaKey && !evento.ctrlKey && !evento.shiftKey && !evento.altKey
    if (ocupado && esClicSimple) evento.preventDefault()
  }

  const nombreListaPrecio = pagina?.nombreListaPrecio ?? null
  // Antes de la primera respuesta no se sabe si hay lista default: el filtro arranca habilitado y
  // solo se deshabilita si la respuesta real confirma que no hay una.
  const precioDeshabilitado = pagina !== null && nombreListaPrecio === null
  const tituloColumnaPrecio = nombreListaPrecio ? `Precio (${nombreListaPrecio})` : 'Precio'
  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1
  const valorSelectProveedor = filtros.sinProveedor ? 'sin-proveedor' : (filtros.idProveedor?.toString() ?? '')

  return (
    <>
      {error && (
        <div className="alert alert-danger rounded-0 d-flex justify-content-between align-items-center gap-2">
          <span>{error}</span>
          <button type="button" className="btn btn-sm btn-outline-danger rounded-0" onClick={cargar}>
            Reintentar
          </button>
        </div>
      )}

      {cargando && !cargaInicialHechaRef.current ? (
        <Cargando />
      ) : (
        <>
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th scope="col">Código</th>
                  <th scope="col">Nombre</th>
                  <th scope="col">{tituloColumnaPrecio}</th>
                  <th scope="col">Proveedor</th>
                  <th scope="col">Estado</th>
                  <th scope="col" className="text-end">
                    Acciones
                  </th>
                </tr>
                <tr>
                  <th scope="col">
                    <input
                      type="search"
                      className="form-control form-control-sm rounded-0"
                      aria-label="Filtrar por código"
                      value={borrador.codigo}
                      onChange={(e) => programarCambioDeTexto({ codigo: e.target.value })}
                    />
                  </th>
                  <th scope="col">
                    <input
                      type="search"
                      className="form-control form-control-sm rounded-0"
                      aria-label="Filtrar por nombre"
                      value={borrador.nombre}
                      onChange={(e) => programarCambioDeTexto({ nombre: e.target.value })}
                    />
                  </th>
                  <th scope="col">
                    <div className="d-flex gap-1">
                      <CampoImporte
                        aria-label="Precio desde"
                        className="form-control form-control-sm rounded-0"
                        valor={borrador.precioDesde}
                        onChange={(valor) => programarCambioDeTexto({ precioDesde: valor })}
                        disabled={precioDeshabilitado}
                        title={precioDeshabilitado ? AVISO_SIN_LISTA_DE_PRECIO : undefined}
                      />
                      <CampoImporte
                        aria-label="Precio hasta"
                        className="form-control form-control-sm rounded-0"
                        valor={borrador.precioHasta}
                        onChange={(valor) => programarCambioDeTexto({ precioHasta: valor })}
                        disabled={precioDeshabilitado}
                        title={precioDeshabilitado ? AVISO_SIN_LISTA_DE_PRECIO : undefined}
                      />
                    </div>
                  </th>
                  <th scope="col">
                    <select
                      className="form-select form-select-sm rounded-0"
                      aria-label="Filtrar por proveedor"
                      value={valorSelectProveedor}
                      onChange={(e) => {
                        const valor = e.target.value
                        if (valor === '') cambiarFiltroInmediato({ idProveedor: null, sinProveedor: false })
                        else if (valor === 'sin-proveedor') cambiarFiltroInmediato({ idProveedor: null, sinProveedor: true })
                        else cambiarFiltroInmediato({ idProveedor: Number(valor), sinProveedor: false })
                      }}
                    >
                      <option value="">Todos</option>
                      <option value="sin-proveedor">Sin proveedor</option>
                      {proveedores.map((p) => (
                        <option key={p.id} value={p.id}>
                          {etiquetaDeProveedor(p)}
                        </option>
                      ))}
                    </select>
                  </th>
                  <th scope="col">
                    <select
                      className="form-select form-select-sm rounded-0"
                      aria-label="Filtrar por estado"
                      value={filtros.activo === null ? '' : String(filtros.activo)}
                      onChange={(e) => {
                        const valor = e.target.value
                        cambiarFiltroInmediato({ activo: valor === '' ? null : valor === 'true' })
                      }}
                    >
                      <option value="">Todos</option>
                      <option value="true">Activos</option>
                      <option value="false">Inactivos</option>
                    </select>
                  </th>
                  <th scope="col" className="text-end">
                    <button type="button" className="btn btn-sm btn-outline-secondary rounded-0" onClick={limpiarFiltros}>
                      Limpiar
                    </button>
                  </th>
                </tr>
              </thead>
              <tbody style={cargando ? { opacity: 0.6 } : undefined}>
                {pagina?.items.map((a) => (
                  <tr key={a.id}>
                    <td>{a.codigoInterno}</td>
                    <td>{a.nombre}</td>
                    <td>{formatearPrecio(a.precio)}</td>
                    <td>{a.proveedor ?? '—'}</td>
                    <td>
                      <span className={`badge rounded-0 ${a.activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
                        {a.activo ? 'Activo' : 'Inactivo'}
                      </span>
                    </td>
                    <td className="text-end text-nowrap">
                      {/* <Link> real (no un botón con navigate): permite click-del-medio/Ctrl-click
                          para abrir en pestaña nueva, con la navegación en ESTA pestaña bloqueada
                          mientras `ocupado` — ver `alClickearEditar`. */}
                      <Link
                        to={`/articulos/edit/${a.id}`}
                        className="btn btn-sm btn-outline-primary rounded-0 me-1"
                        style={ocupado ? { opacity: 0.65 } : undefined}
                        aria-disabled={ocupado}
                        onClick={alClickearEditar}
                      >
                        Editar
                      </Link>
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger rounded-0"
                        disabled={ocupado}
                        onClick={() => onEliminar(a)}
                      >
                        Baja
                      </button>
                    </td>
                  </tr>
                ))}
                {pagina !== null && pagina.items.length === 0 && (
                  <tr>
                    <td colSpan={6} className="text-center text-muted py-4">
                      No hay artículos que coincidan con los filtros.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>

          {pagina && (
            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                Página {pagina.pagina} de {totalPaginas} — {pagina.total} artículo(s)
              </span>
              <div className="d-flex gap-2 align-items-center">
                <select
                  className="form-select form-select-sm rounded-0 w-auto"
                  aria-label="Artículos por página"
                  value={filtros.tamanio}
                  onChange={(e) => cambiarTamanio(Number(e.target.value))}
                >
                  {TAMANIOS_DE_PAGINA.map((t) => (
                    <option key={t} value={t}>
                      {t}
                    </option>
                  ))}
                </select>
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
          )}
        </>
      )}
    </>
  )
}
