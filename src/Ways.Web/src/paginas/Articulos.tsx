import { useCallback, useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../api/articulos'
import { clienteDeCatalogo, clienteDeCatalogosFiscales } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDePrecios } from '../api/precios'
import { UNIDADES_VENTA } from '../api/tipos'
import type {
  AlicuotaIvaListado,
  AreaAlta,
  AreaListado,
  ArticuloListado,
  CategoriaListado,
  EmpresaListado,
  GrupoAlta,
  GrupoListado,
  ListaPrecioListado,
  MarcaAlta,
  MarcaListado,
  PaginaDe,
  ProveedorListado,
} from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { aAlta, aEdicion, aFormulario, formularioVacio, FormularioArticulo, type Formulario } from './articulos/FormularioArticulo'
import { elegirAlicuotaPorDefecto, etiquetaDeProveedor, insertarOrdenadoPor, ordenarProveedoresPorEtiqueta } from './articulos/helpers'

const clienteAreas = clienteDeCatalogo<AreaListado, AreaAlta>('areas')
const clienteMarcas = clienteDeCatalogo<MarcaListado, MarcaAlta>('marcas')
const clienteGrupos = clienteDeCatalogo<GrupoListado, GrupoAlta>('grupos')

/**
 * ABM dedicado de artículos (design decision 1: no la máquina genérica de catálogos) — la
 * pantalla más pesada a la fecha (identificación + códigos de barra + clasificación + costos +
 * disponibilidad por empresa + precios por lista). El código de barras y el editor de precios
 * solo se habilitan una vez que el artículo tiene `id` persistido: ambos endpoints cuelgan de
 * `/api/articulos/{id}/...`, no existen antes del alta.
 */
export function Articulos() {
  const [pagina, setPagina] = useState<PaginaDe<ArticuloListado> | null>(null)
  const [areas, setAreas] = useState<AreaListado[]>([])
  const [categorias, setCategorias] = useState<CategoriaListado[]>([])
  const [marcas, setMarcas] = useState<MarcaListado[]>([])
  const [grupos, setGrupos] = useState<GrupoListado[]>([])
  const [proveedores, setProveedores] = useState<ProveedorListado[]>([])
  const [proveedoresTruncados, setProveedoresTruncados] = useState(false)
  const [alicuotasIva, setAlicuotasIva] = useState<AlicuotaIvaListado[]>([])
  const [empresas, setEmpresas] = useState<EmpresaListado[]>([])
  const [listasPrecio, setListasPrecio] = useState<ListaPrecioListado[]>([])
  const [busqueda, setBusqueda] = useState('')
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [guardando, setGuardando] = useState(false)
  const [eliminando, setEliminando] = useState(false)
  const [erroresCatalogosRequeridos, setErroresCatalogosRequeridos] = useState<string[]>([])
  const [avisoListasPrecio, setAvisoListasPrecio] = useState('')
  const [escriturasHijas, setEscriturasHijas] = useState(0)
  const tokenEdicionRef = useRef(0)
  const generacionCargaRef = useRef(0)
  const cargaInicialHechaRef = useRef(false)
  const ocupado = guardando || eliminando || escriturasHijas > 0

  // Token del fetch de edición en curso: solo protege contra la staleness del fetch de "Editar"
  // (abrir otra fila mientras el detalle anterior sigue en vuelo). El "supersede" de una edición
  // por otra acción durante un guardado ya no depende del token — mientras `ocupado` es true, los
  // controles que podrían dispararlo (Nuevo, Editar, Baja) quedan deshabilitados.
  function invalidarEdicionEnCurso(): number {
    return (tokenEdicionRef.current += 1)
  }

  // Cuenta escrituras hijas en vuelo (códigos de barra, precios): mientras haya al menos una,
  // `ocupado` se mantiene true para que Nuevo/Editar/Baja no puedan borrar o cambiar de artículo
  // en medio de un POST de un componente hijo.
  const alDeEscribir = useCallback((enCurso: boolean) => {
    setEscriturasHijas((n) => (enCurso ? n + 1 : Math.max(0, n - 1)))
  }, [])

  function agregarErrorCatalogoRequerido(mensaje: string) {
    setErroresCatalogosRequeridos((prev) => (prev.includes(mensaje) ? prev : [...prev, mensaje]))
  }

  const cargar = useCallback(async (termino: string, opciones?: { relanzarError?: boolean }) => {
    // Generación: mount, búsqueda y los refrescos post-guardado/post-baja pueden solaparse — sin
    // esto, la respuesta que llega tarde pisa el estado con datos desactualizados.
    const generacion = (generacionCargaRef.current += 1)
    setCargando(true)
    setError('')
    try {
      const p = await clienteDeArticulos.listar(termino, false)
      if (generacionCargaRef.current !== generacion) return
      setPagina(p)
    } catch (e) {
      if (generacionCargaRef.current === generacion) {
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los artículos.')
      }
      if (opciones?.relanzarError && generacionCargaRef.current === generacion) throw e
    } finally {
      if (generacionCargaRef.current === generacion) {
        setCargando(false)
        cargaInicialHechaRef.current = true
      }
    }
  }, [])

  useEffect(() => {
    void cargar('')
    clienteAreas
      .listar(false)
      .then(setAreas)
      .catch(() => {
        setAreas([])
        agregarErrorCatalogoRequerido('No se pudieron cargar las áreas.')
      })
    api.get<CategoriaListado[]>('/catalogos/categorias').then(setCategorias).catch(() => setCategorias([]))
    clienteMarcas.listar(false).then(setMarcas).catch(() => setMarcas([]))
    clienteGrupos.listar(false).then(setGrupos).catch(() => setGrupos([]))
    // tamanio grande a propósito: es un selector de referencia, no un listado paginado. Si el
    // tenant tiene más proveedores que el clamp del servidor, avisamos que la lista quedó
    // truncada en vez de esconder el resto en silencio.
    api
      .get<PaginaDe<ProveedorListado>>('/proveedores?tamanio=200')
      .then((p) => {
        // Pre-ordenado por la MISMA etiqueta que el select muestra (nombre de fantasía o razón
        // social, nunca la razón social cruda) — el alta rápida inserta manteniendo este orden.
        setProveedores(ordenarProveedoresPorEtiqueta(p.items))
        setProveedoresTruncados(p.total > p.items.length)
      })
      .catch(() => setProveedores([]))
    clienteDeCatalogosFiscales
      .alicuotasIva()
      .then(setAlicuotasIva)
      .catch(() => {
        setAlicuotasIva([])
        agregarErrorCatalogoRequerido('No se pudieron cargar las alícuotas de IVA.')
      })
    clienteDeOrganizacion
      .listarEmpresas()
      .then(setEmpresas)
      .catch(() => {
        setEmpresas([])
        agregarErrorCatalogoRequerido('No se pudieron cargar las empresas.')
      })
    clienteDePrecios
      .listasDePrecio()
      .then(setListasPrecio)
      .catch(() => {
        setListasPrecio([])
        setAvisoListasPrecio(
          'No se pudieron cargar las listas de precio: el editor de precios no está disponible. Recargá la página para reintentar.',
        )
      })
  }, [cargar])

  const areaPorDefecto = areas[0]?.id ?? ''
  const alicuotaPorDefecto = elegirAlicuotaPorDefecto(alicuotasIva)

  // Altas rápidas de padrones (Categoría/Marca/Grupo/Proveedor habitual) desde el propio
  // formulario de artículo: cada handler solo inserta el item nuevo en la lista ya ordenada — la
  // selección en el formulario y el cierre del modal los resuelve `FormularioArticulo`, que es
  // quien tiene el `valor`/`onCambio` del artículo en edición.
  function alCrearCategoria(nueva: CategoriaListado) {
    setCategorias((prev) => insertarOrdenadoPor(prev, nueva, (c) => c.nombre))
  }

  function alCrearMarca(nueva: MarcaListado) {
    setMarcas((prev) => insertarOrdenadoPor(prev, nueva, (m) => m.nombre))
  }

  function alCrearGrupo(nuevo: GrupoListado) {
    setGrupos((prev) => insertarOrdenadoPor(prev, nuevo, (g) => g.nombre))
  }

  function alCrearProveedor(nuevo: ProveedorListado) {
    setProveedores((prev) => insertarOrdenadoPor(prev, nuevo, etiquetaDeProveedor))
  }

  async function abrirNuevo() {
    if (ocupado) return
    invalidarEdicionEnCurso()
    setGuardando(false)
    setFormulario({ ...formularioVacio(), idArea: areaPorDefecto, idAlicuotaIva: alicuotaPorDefecto })
    setAviso('')
    setError('')
  }

  function cancelarEdicion() {
    if (ocupado) return
    invalidarEdicionEnCurso()
    setGuardando(false)
    setFormulario(null)
  }

  async function abrirEdicion(a: ArticuloListado) {
    if (ocupado) return
    setError('')
    const token = invalidarEdicionEnCurso()
    try {
      // El listado no completa idsEmpresas (evita el N+1) — el detalle sí.
      const detalle = await clienteDeArticulos.obtener(a.id)
      if (tokenEdicionRef.current !== token) return
      setFormulario(aFormulario(detalle))
      setGuardando(false)
      setAviso('')
    } catch (e) {
      if (tokenEdicionRef.current !== token) return
      setGuardando(false)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo abrir el artículo.')
    }
  }

  async function guardar() {
    if (ocupado) return
    if (!formulario) return

    const token = invalidarEdicionEnCurso()
    setGuardando(true)
    setError('')
    setAviso('')

    try {
      if (formulario.id === null) {
        const creado = await clienteDeArticulos.crear(aAlta(formulario))
        if (tokenEdicionRef.current === token) {
          setAviso(`Artículo "${formulario.nombre}" creado con código interno ${creado.codigoInterno}.`)
          setFormulario(aFormulario(creado))
        }
      } else {
        const actualizado = await clienteDeArticulos.actualizar(formulario.id, aEdicion(formulario))
        if (tokenEdicionRef.current === token) {
          setAviso(`Artículo "${formulario.nombre}" actualizado.`)
          setFormulario(aFormulario(actualizado))
        }
      }

      // El refresco de la tabla no pertenece al token de edición: la fila afectada debe
      // quedar al día sin importar si el formulario abierto ahora es otro. El guardado ya tuvo
      // éxito acá, así que un fallo de este refresco es solo de vista, no de guardado.
      try {
        await cargar(busqueda, { relanzarError: true })
      } catch {
        if (tokenEdicionRef.current === token) {
          setError('El artículo se guardó, pero no se pudo actualizar el listado. Volvé a buscar para verlo.')
        }
      }
    } catch (e) {
      if (tokenEdicionRef.current === token) {
        setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
      }
    } finally {
      if (tokenEdicionRef.current === token) setGuardando(false)
    }
  }

  async function eliminar(a: ArticuloListado) {
    if (ocupado) return
    if (!confirm(`¿Dar de baja el artículo "${a.nombre}"?`)) return

    invalidarEdicionEnCurso()
    setError('')
    setAviso('')
    setEliminando(true)
    try {
      await clienteDeArticulos.eliminar(a.id)
      setAviso(`Artículo "${a.nombre}" dado de baja.`)
      if (formulario?.id === a.id) setFormulario(null)
      try {
        await cargar(busqueda, { relanzarError: true })
      } catch {
        setError('El artículo se dio de baja, pero no se pudo actualizar el listado. Volvé a buscar para verlo.')
      }
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo dar de baja.')
    } finally {
      setEliminando(false)
    }
  }

  function nombreDe(lista: { id: number; nombre: string }[], id: number | null) {
    return lista.find((x) => x.id === id)?.nombre ?? '—'
  }

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <input
        type="search"
        className="form-control form-control-sm rounded-0"
        placeholder="Buscar por nombre, código interno o código de barras…"
        value={busqueda}
        onChange={(e) => setBusqueda(e.target.value)}
        onKeyDown={(e) => e.key === 'Enter' && cargar(busqueda)}
      />
      <button type="button" className="btn btn-sm btn-outline-light rounded-0" onClick={() => cargar(busqueda)}>
        Buscar
      </button>
      <button
        type="button"
        className="btn btn-sm btn-success rounded-0 text-nowrap"
        disabled={ocupado}
        onClick={abrirNuevo}
      >
        Nuevo
      </button>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Artículos" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}
        {erroresCatalogosRequeridos.length > 0 && (
          <div className="alert alert-warning rounded-0">
            {erroresCatalogosRequeridos.join(' ')} El guardado (alta o edición) de artículos va a quedar bloqueado
            hasta que se puedan cargar — recargá la página para reintentar.
          </div>
        )}
        {avisoListasPrecio && <div className="alert alert-warning rounded-0">{avisoListasPrecio}</div>}

        {formulario && (
          <FormularioArticulo
            // Clave por artículo (id, o 'nuevo' para el alta): sin esto React reutiliza la
            // misma instancia del subárbol al pasar de "Editar" en una fila a otra sin
            // cancelar, y filtra estado por-artículo entre medio (historial de precios,
            // sugerencia de margen, códigos de barra cargados).
            key={formulario.id ?? 'nuevo'}
            valor={formulario}
            areas={areas}
            categorias={categorias}
            marcas={marcas}
            grupos={grupos}
            proveedores={proveedores}
            proveedoresTruncados={proveedoresTruncados}
            alicuotasIva={alicuotasIva}
            empresas={empresas}
            listasPrecio={listasPrecio}
            guardando={guardando}
            ocupado={ocupado}
            bloqueadoPorCatalogos={erroresCatalogosRequeridos.length > 0}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={cancelarEdicion}
            alDeEscribir={alDeEscribir}
            onCategoriaCreada={alCrearCategoria}
            onMarcaCreada={alCrearMarca}
            onGrupoCreada={alCrearGrupo}
            onProveedorCreado={alCrearProveedor}
          />
        )}

        {cargando && !cargaInicialHechaRef.current ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th>Código</th>
                  <th>Nombre</th>
                  <th>Área</th>
                  <th>Unidad de venta</th>
                  <th>Disponibilidad</th>
                  <th>Estado</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {pagina?.items.map((a) => (
                  <tr key={a.id}>
                    <td>{a.codigoInterno}</td>
                    <td>{a.nombre}</td>
                    <td>{nombreDe(areas, a.idArea)}</td>
                    <td>{UNIDADES_VENTA.find((u) => u.valor === a.unidadVenta)?.etiqueta ?? a.unidadVenta}</td>
                    <td>{a.disponibleParaTodas ? 'Todas las empresas' : 'Subconjunto'}</td>
                    <td>
                      <span className={`badge rounded-0 ${a.activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
                        {a.activo ? 'Activo' : 'Inactivo'}
                      </span>
                    </td>
                    <td className="text-end text-nowrap">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-primary rounded-0 me-1"
                        disabled={ocupado}
                        onClick={() => abrirEdicion(a)}
                      >
                        Editar
                      </button>
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger rounded-0"
                        disabled={ocupado}
                        onClick={() => eliminar(a)}
                      >
                        Baja
                      </button>
                    </td>
                  </tr>
                ))}
                {pagina !== null && pagina.items.length === 0 && (
                  <tr>
                    <td colSpan={7} className="text-center text-muted py-4">
                      No hay artículos que coincidan con la búsqueda.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </Box>
    </div>
  )
}
