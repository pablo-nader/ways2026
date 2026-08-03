import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../api/articulos'
import { clienteDeCatalogo, clienteDeCatalogosFiscales } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDePrecios } from '../api/precios'
import { UNIDADES_VENTA } from '../api/tipos'
import type {
  AlicuotaIvaListado,
  AltaArticulo,
  AreaAlta,
  AreaListado,
  ArticuloListado,
  CategoriaListado,
  CodigoBarraListado,
  EdicionArticulo,
  EmpresaListado,
  GrupoAlta,
  GrupoListado,
  HistorialDePrecio,
  ListaPrecioListado,
  MarcaAlta,
  MarcaListado,
  PaginaDe,
  PrecioVigente,
  ProveedorListado,
  UnidadVenta,
} from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

type Formulario = {
  id: number | null
  codigoInterno: string
  nombre: string
  descripcion: string
  idArea: number | ''
  idCategoria: number | ''
  idMarca: number | ''
  idGrupo: number | ''
  idProveedorHabitual: number | ''
  idAlicuotaIva: number | ''
  unidadVenta: UnidadVenta
  unidadesPorBulto: string
  esProducto: boolean
  costoLista: string
  descuentoProveedor: string
  costoNominal: string
  disponibleParaTodas: boolean
  idsEmpresas: number[]
  activo: boolean
}

function formularioVacio(): Formulario {
  return {
    id: null,
    codigoInterno: '',
    nombre: '',
    descripcion: '',
    idArea: '',
    idCategoria: '',
    idMarca: '',
    idGrupo: '',
    idProveedorHabitual: '',
    idAlicuotaIva: '',
    unidadVenta: 'Unidad',
    unidadesPorBulto: '',
    esProducto: true,
    costoLista: '',
    descuentoProveedor: '',
    costoNominal: '',
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
  }
}

function aFormulario(a: ArticuloListado): Formulario {
  return {
    id: a.id,
    codigoInterno: a.codigoInterno,
    nombre: a.nombre,
    descripcion: a.descripcion ?? '',
    idArea: a.idArea,
    idCategoria: a.idCategoria ?? '',
    idMarca: a.idMarca ?? '',
    idGrupo: a.idGrupo ?? '',
    idProveedorHabitual: a.idProveedorHabitual ?? '',
    idAlicuotaIva: a.idAlicuotaIva,
    unidadVenta: a.unidadVenta,
    unidadesPorBulto: a.unidadesPorBulto === null ? '' : String(a.unidadesPorBulto),
    esProducto: a.esProducto,
    costoLista: a.costoLista === null ? '' : String(a.costoLista),
    descuentoProveedor: a.descuentoProveedor === null ? '' : String(a.descuentoProveedor),
    costoNominal: a.costoNominal === null ? '' : String(a.costoNominal),
    disponibleParaTodas: a.disponibleParaTodas,
    idsEmpresas: a.idsEmpresas,
    activo: a.activo,
  }
}

function aVacioNulo(valor: string): string | null {
  const limpio = valor.trim()
  return limpio === '' ? null : limpio
}

function numeroOpcional(valor: string): number | null {
  const limpio = valor.trim()
  return limpio === '' ? null : Number(limpio)
}

function camposComunes(f: Formulario) {
  return {
    nombre: f.nombre.trim(),
    descripcion: aVacioNulo(f.descripcion),
    idArea: f.idArea === '' ? 0 : f.idArea,
    idCategoria: f.idCategoria === '' ? null : f.idCategoria,
    idMarca: f.idMarca === '' ? null : f.idMarca,
    idGrupo: f.idGrupo === '' ? null : f.idGrupo,
    idProveedorHabitual: f.idProveedorHabitual === '' ? null : f.idProveedorHabitual,
    idAlicuotaIva: f.idAlicuotaIva === '' ? 0 : f.idAlicuotaIva,
    unidadVenta: f.unidadVenta,
    unidadesPorBulto: numeroOpcional(f.unidadesPorBulto),
    esProducto: f.esProducto,
    costoLista: numeroOpcional(f.costoLista),
    descuentoProveedor: numeroOpcional(f.descuentoProveedor),
    costoNominal: numeroOpcional(f.costoNominal),
    disponibleParaTodas: f.disponibleParaTodas,
    idsEmpresas: f.disponibleParaTodas ? null : f.idsEmpresas,
    activo: f.activo,
  }
}

function aAlta(f: Formulario): AltaArticulo {
  return { codigoInterno: aVacioNulo(f.codigoInterno), ...camposComunes(f) }
}

function aEdicion(f: Formulario): EdicionArticulo {
  return camposComunes(f)
}

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
        setProveedores(p.items)
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
  const alicuotaPorDefecto = alicuotasIva[0]?.id ?? ''

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

function FormularioArticulo({
  valor,
  areas,
  categorias,
  marcas,
  grupos,
  proveedores,
  proveedoresTruncados,
  alicuotasIva,
  empresas,
  listasPrecio,
  guardando,
  ocupado,
  bloqueadoPorCatalogos,
  onCambio,
  onGuardar,
  onCancelar,
  alDeEscribir,
}: {
  valor: Formulario
  areas: AreaListado[]
  categorias: CategoriaListado[]
  marcas: MarcaListado[]
  grupos: GrupoListado[]
  proveedores: ProveedorListado[]
  proveedoresTruncados: boolean
  alicuotasIva: AlicuotaIvaListado[]
  empresas: EmpresaListado[]
  listasPrecio: ListaPrecioListado[]
  guardando: boolean
  ocupado: boolean
  bloqueadoPorCatalogos: boolean
  onCambio: (f: Formulario) => void
  onGuardar: () => void
  onCancelar: () => void
  alDeEscribir: (enCurso: boolean) => void
}) {
  const esNuevo = valor.id === null

  function alternarEmpresa(id: number) {
    const yaEsta = valor.idsEmpresas.includes(id)
    onCambio({
      ...valor,
      idsEmpresas: yaEsta ? valor.idsEmpresas.filter((x) => x !== id) : [...valor.idsEmpresas, id],
    })
  }

  return (
    <div className="border p-3 mb-4 bg-white">
      <form
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          if (bloqueadoPorCatalogos || ocupado) return
          onGuardar()
        }}
      >
        {/* fieldset disabled: cascada nativa a todos los controles anidados mientras hay un guardado
            en vuelo, para que lo tipeado en la ventana de la request no se pise con la respuesta.
            Se le mueve acá la clase de grilla de Bootstrap (antes en el form) para no romper el
            layout de columnas; border-0/p-0/m-0 neutralizan el estilo por defecto del fieldset. */}
        <fieldset disabled={ocupado} className="row g-3 border-0 p-0 m-0">
          <div className="col-12">
            <strong>{esNuevo ? 'Nuevo artículo' : `Editando artículo ${valor.codigoInterno}`}</strong>
          </div>

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Identificación</strong>
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="art-codigo-interno">
              Código interno
            </label>
            <input
              id="art-codigo-interno"
              className="form-control rounded-0"
              maxLength={30}
              placeholder={esNuevo ? 'Se autogenera si se omite' : undefined}
              value={valor.codigoInterno}
              disabled={!esNuevo}
              onChange={(e) => onCambio({ ...valor, codigoInterno: e.target.value })}
            />
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="art-nombre">
              Nombre
            </label>
            <input
              id="art-nombre"
              className="form-control rounded-0"
              maxLength={150}
              value={valor.nombre}
              onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
              required
            />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="art-unidad-venta">
              Unidad de venta
            </label>
            <select
              id="art-unidad-venta"
              className="form-select rounded-0"
              value={valor.unidadVenta}
              onChange={(e) => onCambio({ ...valor, unidadVenta: e.target.value as UnidadVenta })}
            >
              {UNIDADES_VENTA.map((u) => (
                <option key={u.valor} value={u.valor}>
                  {u.etiqueta}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="art-unidades-por-bulto">
              Unidades por bulto
            </label>
            <input
              id="art-unidades-por-bulto"
              type="number"
              step="0.01"
              min="0"
              className="form-control rounded-0"
              value={valor.unidadesPorBulto}
              onChange={(e) => onCambio({ ...valor, unidadesPorBulto: e.target.value })}
            />
          </div>

          <div className="col-md-6 d-flex align-items-end">
            <div className="form-check">
              <input
                id="art-es-producto"
                type="checkbox"
                className="form-check-input rounded-0"
                checked={valor.esProducto}
                onChange={(e) => onCambio({ ...valor, esProducto: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="art-es-producto">
                Es producto (desmarcar si es un servicio)
              </label>
            </div>
          </div>

          <div className="col-12">
            <label className="form-label" htmlFor="art-descripcion">
              Descripción
            </label>
            <textarea
              id="art-descripcion"
              className="form-control rounded-0"
              rows={2}
              value={valor.descripcion}
              onChange={(e) => onCambio({ ...valor, descripcion: e.target.value })}
            />
          </div>

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Clasificación</strong>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="art-area">
              Área
            </label>
            <select
              id="art-area"
              className="form-select rounded-0"
              value={valor.idArea}
              onChange={(e) => onCambio({ ...valor, idArea: Number(e.target.value) })}
              required
            >
              <option value="" disabled>
                Elegir…
              </option>
              {areas.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.nombre}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="art-categoria">
              Categoría
            </label>
            <select
              id="art-categoria"
              className="form-select rounded-0"
              value={valor.idCategoria}
              onChange={(e) => onCambio({ ...valor, idCategoria: e.target.value === '' ? '' : Number(e.target.value) })}
            >
              <option value="">Sin especificar</option>
              {categorias.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.nombre}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="art-marca">
              Marca
            </label>
            <select
              id="art-marca"
              className="form-select rounded-0"
              value={valor.idMarca}
              onChange={(e) => onCambio({ ...valor, idMarca: e.target.value === '' ? '' : Number(e.target.value) })}
            >
              <option value="">Sin especificar</option>
              {marcas.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.nombre}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="art-grupo">
              Grupo
            </label>
            <select
              id="art-grupo"
              className="form-select rounded-0"
              value={valor.idGrupo}
              onChange={(e) => onCambio({ ...valor, idGrupo: e.target.value === '' ? '' : Number(e.target.value) })}
            >
              <option value="">Sin especificar</option>
              {grupos.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.nombre}
                  {g.margen !== null ? ` (margen ${g.margen}%)` : ''}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="art-proveedor-habitual">
              Proveedor habitual
            </label>
            <select
              id="art-proveedor-habitual"
              className="form-select rounded-0"
              value={valor.idProveedorHabitual}
              onChange={(e) =>
                onCambio({ ...valor, idProveedorHabitual: e.target.value === '' ? '' : Number(e.target.value) })
              }
            >
              <option value="">Sin especificar</option>
              {proveedores.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.razonSocial}
                  {p.nombreFantasia ? ` (${p.nombreFantasia})` : ''}
                </option>
              ))}
            </select>
            {proveedoresTruncados && (
              <div className="form-text">Se muestran solo los primeros 200 proveedores.</div>
            )}
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="art-alicuota-iva">
              Alícuota de IVA
            </label>
            <select
              id="art-alicuota-iva"
              className="form-select rounded-0"
              value={valor.idAlicuotaIva}
              onChange={(e) => onCambio({ ...valor, idAlicuotaIva: Number(e.target.value) })}
              required
            >
              <option value="" disabled>
                Elegir…
              </option>
              {alicuotasIva.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.nombre} ({a.porcentaje}%)
                </option>
              ))}
            </select>
          </div>

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Costos</strong>
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="art-costo-lista">
              Costo de lista
            </label>
            <input
              id="art-costo-lista"
              type="number"
              step="0.01"
              min="0"
              className="form-control rounded-0"
              value={valor.costoLista}
              onChange={(e) => onCambio({ ...valor, costoLista: e.target.value })}
            />
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="art-descuento-proveedor">
              Descuento de proveedor (%)
            </label>
            <input
              id="art-descuento-proveedor"
              type="number"
              step="0.01"
              min="0"
              className="form-control rounded-0"
              value={valor.descuentoProveedor}
              onChange={(e) => onCambio({ ...valor, descuentoProveedor: e.target.value })}
            />
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="art-costo-nominal">
              Costo nominal
            </label>
            <input
              id="art-costo-nominal"
              type="number"
              step="0.01"
              min="0"
              className="form-control rounded-0"
              value={valor.costoNominal}
              onChange={(e) => onCambio({ ...valor, costoNominal: e.target.value })}
            />
            <div className="form-text">Si se completa, tiene prioridad sobre costo de lista − descuento.</div>
          </div>

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Disponibilidad</strong>
          </div>

          <div className="col-12">
            <div className="form-check form-switch">
              <input
                id="art-disponible-para-todas"
                type="checkbox"
                className="form-check-input"
                checked={valor.disponibleParaTodas}
                onChange={(e) => onCambio({ ...valor, disponibleParaTodas: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="art-disponible-para-todas">
                Disponible para todas las empresas del tenant
              </label>
            </div>
          </div>

          {!valor.disponibleParaTodas && (
            <div className="col-12">
              <div className="form-text mb-1">
                Elegí al menos una empresa: sin ninguna marcada, el servidor rechaza el guardado.
              </div>
              <div className="d-flex flex-wrap gap-3">
                {empresas.map((e) => (
                  <div className="form-check" key={e.id}>
                    <input
                      id={`art-empresa-${e.id}`}
                      type="checkbox"
                      className="form-check-input rounded-0"
                      checked={valor.idsEmpresas.includes(e.id)}
                      onChange={() => alternarEmpresa(e.id)}
                    />
                    <label className="form-check-label" htmlFor={`art-empresa-${e.id}`}>
                      {e.razonSocial}
                      {e.nombreFantasia ? ` (${e.nombreFantasia})` : ''}
                    </label>
                  </div>
                ))}
              </div>
            </div>
          )}

          <div className="col-md-3 d-flex align-items-end">
            <div className="form-check">
              <input
                id="art-activo"
                type="checkbox"
                className="form-check-input rounded-0"
                checked={valor.activo}
                onChange={(e) => onCambio({ ...valor, activo: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="art-activo">
                Activo
              </label>
            </div>
          </div>

          <div className="col-12 d-flex gap-2">
            <button type="submit" className="btn btn-success rounded-0" disabled={ocupado || bloqueadoPorCatalogos}>
              {guardando ? 'Guardando…' : 'Guardar'}
            </button>
            <button type="button" className="btn btn-outline-secondary rounded-0" onClick={onCancelar} disabled={ocupado}>
              Cancelar
            </button>
          </div>
        </fieldset>
      </form>

      <hr />

      {valor.id === null ? (
        <p className="text-muted mb-0">Guardá el artículo para poder cargar códigos de barra y precios.</p>
      ) : (
        <>
          <GestorDeCodigosBarra idArticulo={valor.id} bloqueadoPorPadre={ocupado} alDeEscribir={alDeEscribir} />
          <hr />
          <EditorDePrecios
            idArticulo={valor.id}
            listasPrecio={listasPrecio.filter((l) => l.activo)}
            bloqueadoPorPadre={ocupado}
            alDeEscribir={alDeEscribir}
          />
        </>
      )}
    </div>
  )
}

/**
 * Códigos de barra: alta/baja independientes de editar el resto del artículo (spec: Barcode
 * Add/Remove Management). Hidrata desde `GET /api/articulos/{id}/codigos-barra` al montar, así
 * que también refleja los códigos cargados en altas anteriores, no solo los de esta sesión.
 */
function GestorDeCodigosBarra({
  idArticulo,
  bloqueadoPorPadre,
  alDeEscribir,
}: {
  idArticulo: number
  bloqueadoPorPadre: boolean
  alDeEscribir: (enCurso: boolean) => void
}) {
  const [codigos, setCodigos] = useState<CodigoBarraListado[]>([])
  const [nuevoCodigo, setNuevoCodigo] = useState('')
  const [error, setError] = useState('')
  const [ocupado, setOcupado] = useState(false)
  const [cargando, setCargando] = useState(true)

  useEffect(() => {
    let cancelado = false
    setCargando(true)
    clienteDeArticulos
      .codigosBarra(idArticulo)
      .then((lista) => {
        if (!cancelado) setCodigos(lista)
      })
      .catch((e) => {
        if (!cancelado) setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los códigos de barra.')
      })
      .finally(() => {
        if (!cancelado) setCargando(false)
      })
    return () => {
      cancelado = true
    }
  }, [idArticulo])

  async function agregar() {
    if (ocupado || bloqueadoPorPadre) return
    const codigo = nuevoCodigo.trim()
    if (!codigo) return

    setOcupado(true)
    setError('')
    alDeEscribir(true)
    try {
      const creado = await clienteDeArticulos.agregarCodigoBarra(idArticulo, { codigo })
      setCodigos((prev) => [...prev, creado])
      setNuevoCodigo('')
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo agregar el código de barras.')
    } finally {
      setOcupado(false)
      alDeEscribir(false)
    }
  }

  async function quitar(codigoBarra: CodigoBarraListado) {
    if (ocupado || bloqueadoPorPadre) return
    setOcupado(true)
    setError('')
    alDeEscribir(true)
    try {
      await clienteDeArticulos.eliminarCodigoBarra(idArticulo, codigoBarra.id)
      setCodigos((prev) => prev.filter((c) => c.id !== codigoBarra.id))
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo quitar el código de barras.')
    } finally {
      setOcupado(false)
      alDeEscribir(false)
    }
  }

  return (
    <div>
      <strong className="text-muted small text-uppercase">Códigos de barra</strong>
      {error && <div className="alert alert-danger rounded-0 py-1 px-2 small mt-2">{error}</div>}

      {cargando ? (
        <Cargando texto="Cargando códigos de barra…" />
      ) : (
        <div className="d-flex flex-wrap gap-2 mb-2 mt-2">
          {codigos.map((c) => (
            <span key={c.id} className="badge rounded-0 text-bg-light border d-flex align-items-center gap-2 py-2 px-2">
              {c.codigo}
              <button
                type="button"
                className="btn btn-sm btn-outline-danger rounded-0 py-0 px-1"
                disabled={ocupado || bloqueadoPorPadre}
                onClick={() => quitar(c)}
              >
                ×
              </button>
            </span>
          ))}
          {codigos.length === 0 && <span className="text-muted small">Sin códigos de barra cargados.</span>}
        </div>
      )}

      <div className="input-group" style={{ maxWidth: 320 }}>
        <input
          type="text"
          className="form-control rounded-0"
          maxLength={50}
          placeholder="Código de barras"
          value={nuevoCodigo}
          disabled={ocupado || bloqueadoPorPadre}
          onChange={(e) => setNuevoCodigo(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), agregar())}
        />
        <button
          type="button"
          className="btn btn-outline-primary rounded-0"
          disabled={ocupado || bloqueadoPorPadre}
          onClick={agregar}
        >
          Agregar
        </button>
      </div>
    </div>
  )
}

type EstadoDeLista = {
  monto: string
  programado: boolean
  vigenteDesde: string
  guardando: boolean
  refrescando: boolean
  error: string
  confirmarPendiente: boolean
}

function estadoDeListaVacio(): EstadoDeLista {
  return {
    monto: '',
    programado: false,
    vigenteDesde: '',
    guardando: false,
    refrescando: false,
    error: '',
    confirmarPendiente: false,
  }
}

/**
 * Precio por lista (design: ABM Composition) — precio vigente + badge de pendiente (dentro del
 * panel expandido, no en la fila colapsada, para no multiplicar el N+1 de historial por cada
 * lista al cargar la pantalla) + sugerencia de margen (propone, nunca aplica sola) + alta/
 * programación con el flujo de `confirmarReemplazo` en el 409 `precio_pendiente_existe` +
 * historial. Las listas `derivada` no admiten alta propia (se resuelven en lectura): solo
 * muestran el precio vigente resuelto.
 */
function EditorDePrecios({
  idArticulo,
  listasPrecio,
  bloqueadoPorPadre,
  alDeEscribir,
}: {
  idArticulo: number
  listasPrecio: ListaPrecioListado[]
  bloqueadoPorPadre: boolean
  alDeEscribir: (enCurso: boolean) => void
}) {
  const [vigentes, setVigentes] = useState<Record<number, PrecioVigente>>({})
  const [cargandoVigentes, setCargandoVigentes] = useState(true)
  const [errorVigentes, setErrorVigentes] = useState('')
  const cargaInicialHechaRef = useRef(false)
  const generacionVigentesRef = useRef(0)
  const generacionSugerenciaRef = useRef(0)
  // Generación por lista: la carga inicial de `alternarExpandida` y el refresco post-guardado
  // de `guardarPrecio` compiten por la misma lista — sin esto, la que resuelve primero pero
  // arrancó antes puede pisar el historial ya actualizado por la otra.
  const generacionHistorialRef = useRef<Record<number, number>>({})
  const [listaExpandida, setListaExpandida] = useState<number | null>(null)
  const [historiales, setHistoriales] = useState<Record<number, HistorialDePrecio[]>>({})
  const [estados, setEstados] = useState<Record<number, EstadoDeLista>>({})
  const [sugerencia, setSugerencia] = useState<number | null>(null)
  const [sinSugerencia, setSinSugerencia] = useState(false)
  const [cargandoSugerencia, setCargandoSugerencia] = useState(false)
  const [errorSugerencia, setErrorSugerencia] = useState('')

  const cargarVigentes = useCallback(
    async (opciones?: { relanzarError?: boolean }) => {
      // Generación: si mientras esta llamada está en vuelo se dispara otra (dos paneles
      // abriéndose/refrescando en simultáneo), la que llega tarde no debe pisar el estado
      // compartido con una respuesta desactualizada — solo aplica la más reciente en curso.
      const generacion = (generacionVigentesRef.current += 1)
      setCargandoVigentes(true)
      setErrorVigentes('')
      try {
        const lista = await clienteDePrecios.vigentes(idArticulo)
        if (generacionVigentesRef.current !== generacion) return
        const mapa: Record<number, PrecioVigente> = {}
        for (const p of lista) mapa[p.idListaPrecio] = p
        setVigentes(mapa)
      } catch (e) {
        if (generacionVigentesRef.current === generacion) {
          setErrorVigentes(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los precios vigentes.')
        }
        if (opciones?.relanzarError && generacionVigentesRef.current === generacion) throw e
      } finally {
        if (generacionVigentesRef.current === generacion) {
          setCargandoVigentes(false)
          cargaInicialHechaRef.current = true
        }
      }
    },
    [idArticulo],
  )

  useEffect(() => {
    void cargarVigentes()
  }, [cargarVigentes])

  function estadoDe(idLista: number): EstadoDeLista {
    return estados[idLista] ?? estadoDeListaVacio()
  }

  function actualizarEstado(idLista: number, parcial: Partial<EstadoDeLista>) {
    setEstados((prev) => ({ ...prev, [idLista]: { ...(prev[idLista] ?? estadoDeListaVacio()), ...parcial } }))
  }

  async function alternarExpandida(lista: ListaPrecioListado) {
    const abrir = listaExpandida !== lista.id
    setListaExpandida(abrir ? lista.id : null)

    if (abrir && lista.modo === 'Fija' && !historiales[lista.id]) {
      const generacion = (generacionHistorialRef.current[lista.id] = (generacionHistorialRef.current[lista.id] ?? 0) + 1)
      try {
        const historial = await clienteDePrecios.historial(idArticulo, lista.id)
        if (generacionHistorialRef.current[lista.id] !== generacion) return
        setHistoriales((prev) => ({ ...prev, [lista.id]: historial }))
      } catch (e) {
        if (generacionHistorialRef.current[lista.id] !== generacion) return
        actualizarEstado(lista.id, { error: e instanceof ErrorApi ? e.message : 'No se pudo cargar el historial.' })
      }
    }
  }

  async function pedirSugerencia() {
    if (cargandoSugerencia || bloqueadoPorPadre) return
    // Generación: protege contra una respuesta tardía de un pedido anterior pisando una
    // sugerencia más reciente que el usuario ya podría haber aplicado al borrador de precio.
    const generacion = (generacionSugerenciaRef.current += 1)
    setCargandoSugerencia(true)
    setErrorSugerencia('')
    setSinSugerencia(false)
    try {
      const { precioSugerido } = await clienteDeArticulos.sugerenciaDePrecio(idArticulo)
      if (generacionSugerenciaRef.current !== generacion) return
      setSugerencia(precioSugerido)
      setSinSugerencia(precioSugerido === null)
    } catch (e) {
      if (generacionSugerenciaRef.current === generacion) {
        setSugerencia(null)
        setErrorSugerencia(e instanceof ErrorApi ? e.message : 'No se pudo calcular la sugerencia de precio.')
      }
    } finally {
      if (generacionSugerenciaRef.current === generacion) setCargandoSugerencia(false)
    }
  }

  async function guardarPrecio(idLista: number, confirmarReemplazo: boolean) {
    const estado = estadoDe(idLista)
    if (estado.guardando || estado.refrescando || bloqueadoPorPadre) return
    const monto = Number(estado.monto)

    if (!estado.monto.trim() || Number.isNaN(monto)) {
      actualizarEstado(idLista, { error: 'Ingresá un precio válido.' })
      return
    }

    actualizarEstado(idLista, { guardando: true, error: '', confirmarPendiente: false })
    alDeEscribir(true)

    try {
      try {
        if (estado.programado) {
          if (!estado.vigenteDesde) {
            actualizarEstado(idLista, { guardando: false, error: 'Elegí la fecha de vigencia.' })
            return
          }
          await clienteDePrecios.programar(idArticulo, {
            idListaPrecio: idLista,
            precio: monto,
            vigenteDesde: new Date(estado.vigenteDesde).toISOString(),
            confirmarReemplazo,
          })
        } else {
          await clienteDePrecios.establecer(idArticulo, { idListaPrecio: idLista, precio: monto, confirmarReemplazo })
        }
      } catch (e) {
        if (e instanceof ErrorApi && e.codigo === 'precio_pendiente_existe') {
          actualizarEstado(idLista, { guardando: false, confirmarPendiente: true })
          return
        }
        actualizarEstado(idLista, {
          guardando: false,
          error: e instanceof ErrorApi ? e.message : 'No se pudo guardar el precio.',
        })
        return
      }

      // El precio ya quedó confirmado en el servidor: a partir de acá un fallo es solo de refresco
      // de vista, nunca "no se guardó" — evita que el usuario reintente un guardado que ya se aplicó.
      // `refrescando` mantiene el panel inerte hasta que el refresco termine, para no habilitar un
      // segundo guardado que corra en paralelo con este refresco.
      setEstados((prev) => ({ ...prev, [idLista]: { ...estadoDeListaVacio(), refrescando: true } }))

      try {
        await cargarVigentes({ relanzarError: true })
        const generacion = (generacionHistorialRef.current[idLista] = (generacionHistorialRef.current[idLista] ?? 0) + 1)
        const historial = await clienteDePrecios.historial(idArticulo, idLista)
        if (generacionHistorialRef.current[idLista] === generacion) {
          setHistoriales((prev) => ({ ...prev, [idLista]: historial }))
        }
        actualizarEstado(idLista, { refrescando: false })
      } catch {
        actualizarEstado(idLista, {
          refrescando: false,
          error: 'El precio se guardó, pero no se pudo actualizar la vista. Cerrá y volvé a abrir la lista para verlo.',
        })
      }
    } finally {
      alDeEscribir(false)
    }
  }

  // Solo la carga inicial gatea todo el panel: los refrescos posteriores (p.ej. tras guardar un
  // precio) mantienen el panel montado para no perder el foco ni parpadear en el camino feliz.
  if (cargandoVigentes && !cargaInicialHechaRef.current) return <Cargando texto="Cargando precios…" />

  return (
    <div>
      <div className="d-flex align-items-center justify-content-between">
        <strong className="text-muted small text-uppercase">Precios por lista</strong>
        <button
          type="button"
          className="btn btn-sm btn-outline-secondary rounded-0"
          disabled={cargandoSugerencia || bloqueadoPorPadre}
          onClick={pedirSugerencia}
        >
          {cargandoSugerencia ? 'Calculando…' : 'Calcular sugerencia de precio'}
        </button>
      </div>

      {sugerencia !== null && (
        <div className="alert alert-info rounded-0 py-2 px-2 small mt-2">
          Precio sugerido a partir de costo y margen: <strong>${sugerencia.toFixed(2)}</strong>. Usá "Usar sugerencia"
          en la lista que corresponda — nunca se aplica sola.
        </div>
      )}

      {sinSugerencia && (
        <div className="alert alert-info rounded-0 py-2 px-2 small mt-2">
          No hay costo o margen suficientes para sugerir un precio.
        </div>
      )}

      {errorSugerencia && <div className="alert alert-danger rounded-0 py-2 px-2 small mt-2">{errorSugerencia}</div>}

      {errorVigentes && <div className="alert alert-danger rounded-0 mt-2">{errorVigentes}</div>}

      <div className="table-responsive mt-2">
        <table className="table table-sm table-bordered align-middle mb-0">
          <thead>
            <tr>
              <th>Lista</th>
              <th>Precio vigente</th>
              <th className="text-end">Acciones</th>
            </tr>
          </thead>
          <tbody>
            {listasPrecio.map((lista) => {
              const vigente = vigentes[lista.id]
              const expandida = listaExpandida === lista.id
              const listaBase = lista.idListaBase !== null ? listasPrecio.find((l) => l.id === lista.idListaBase) : null

              return (
                <Fragment key={lista.id}>
                  <tr>
                    <td>
                      {lista.nombre}
                      {lista.esDefault && <span className="badge rounded-0 text-bg-secondary ms-1">Default</span>}
                      {lista.modo === 'Derivada' && (
                        <div className="text-muted small">
                          Derivada de {listaBase?.nombre ?? lista.idListaBase} (
                          {(lista.porcentaje ?? 0) >= 0 ? '+' : ''}
                          {lista.porcentaje}%)
                        </div>
                      )}
                    </td>
                    <td>{vigente?.precio !== null && vigente?.precio !== undefined ? `$${vigente.precio.toFixed(2)}` : '—'}</td>
                    <td className="text-end">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-primary rounded-0"
                        onClick={() => alternarExpandida(lista)}
                      >
                        {expandida ? 'Cerrar' : lista.modo === 'Fija' ? 'Gestionar' : 'Ver detalle'}
                      </button>
                    </td>
                  </tr>
                  {expandida && (
                    <tr>
                      <td colSpan={3} className="bg-light">
                        <PanelDeLista
                          lista={lista}
                          estado={estadoDe(lista.id)}
                          historial={historiales[lista.id] ?? []}
                          sugerencia={sugerencia}
                          cargandoSugerencia={cargandoSugerencia}
                          bloqueadoPorPadre={bloqueadoPorPadre}
                          onCambio={(parcial) => actualizarEstado(lista.id, parcial)}
                          onGuardar={(confirmarReemplazo) => guardarPrecio(lista.id, confirmarReemplazo)}
                        />
                      </td>
                    </tr>
                  )}
                </Fragment>
              )
            })}
            {listasPrecio.length === 0 && (
              <tr>
                <td colSpan={3} className="text-center text-muted py-3">
                  No hay listas de precio activas.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function PanelDeLista({
  lista,
  estado,
  historial,
  sugerencia,
  cargandoSugerencia,
  bloqueadoPorPadre,
  onCambio,
  onGuardar,
}: {
  lista: ListaPrecioListado
  estado: EstadoDeLista
  historial: HistorialDePrecio[]
  sugerencia: number | null
  cargandoSugerencia: boolean
  bloqueadoPorPadre: boolean
  onCambio: (parcial: Partial<EstadoDeLista>) => void
  onGuardar: (confirmarReemplazo: boolean) => void
}) {
  const ahora = new Date()
  const filaAbierta = historial.find((h) => h.vigenteHasta === null)
  const pendiente = filaAbierta && new Date(filaAbierta.vigenteDesde) > ahora ? filaAbierta : null
  const bloqueado = estado.guardando || estado.refrescando || bloqueadoPorPadre

  if (lista.modo !== 'Fija') {
    return (
      <div className="p-2">
        <p className="mb-0 text-muted">
          Lista derivada: el precio se resuelve solo a partir de la lista base, sin historial propio.
        </p>
      </div>
    )
  }

  return (
    <div className="p-2">
      {pendiente && (
        <div className="alert alert-warning rounded-0 py-1 px-2 small">
          Precio programado: ${pendiente.precio.toFixed(2)} desde {new Date(pendiente.vigenteDesde).toLocaleString()}
        </div>
      )}

      {estado.error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{estado.error}</div>}

      {estado.confirmarPendiente && (
        <div className="alert alert-warning rounded-0 py-2 px-2 small d-flex align-items-center justify-content-between">
          <span>Ya existe un precio programado para esta lista. ¿Confirmás el reemplazo?</span>
          <div className="d-flex gap-2">
            <button
              type="button"
              className="btn btn-sm btn-warning rounded-0"
              disabled={bloqueado}
              onClick={() => onGuardar(true)}
            >
              Reemplazar
            </button>
            <button
              type="button"
              className="btn btn-sm btn-outline-secondary rounded-0"
              onClick={() => onCambio({ confirmarPendiente: false })}
            >
              Cancelar
            </button>
          </div>
        </div>
      )}

      <div className="row g-2 align-items-end">
        <div className="col-auto">
          <label className="form-label mb-0 small">Precio</label>
          <input
            type="number"
            step="0.01"
            min="0"
            className="form-control form-control-sm rounded-0"
            style={{ width: 140 }}
            value={estado.monto}
            disabled={bloqueado}
            onChange={(e) => onCambio({ monto: e.target.value })}
          />
        </div>

        {sugerencia !== null && (
          <div className="col-auto">
            <button
              type="button"
              className="btn btn-sm btn-outline-info rounded-0"
              disabled={bloqueado || cargandoSugerencia}
              onClick={() => onCambio({ monto: String(sugerencia) })}
            >
              Usar sugerencia (${sugerencia.toFixed(2)})
            </button>
          </div>
        )}

        <div className="col-auto">
          <div className="form-check">
            <input
              id={`lp-programado-${lista.id}`}
              type="checkbox"
              className="form-check-input rounded-0"
              checked={estado.programado}
              disabled={bloqueado}
              onChange={(e) => onCambio({ programado: e.target.checked })}
            />
            <label className="form-check-label small" htmlFor={`lp-programado-${lista.id}`}>
              Programar a futuro
            </label>
          </div>
        </div>

        {estado.programado && (
          <div className="col-auto">
            <label className="form-label mb-0 small">Vigente desde</label>
            <input
              type="datetime-local"
              className="form-control form-control-sm rounded-0"
              value={estado.vigenteDesde}
              disabled={bloqueado}
              onChange={(e) => onCambio({ vigenteDesde: e.target.value })}
            />
          </div>
        )}

        <div className="col-auto">
          <button
            type="button"
            className="btn btn-sm btn-success rounded-0"
            disabled={bloqueado}
            onClick={() => onGuardar(false)}
          >
            {estado.guardando ? 'Guardando…' : estado.refrescando ? 'Actualizando…' : estado.programado ? 'Programar' : 'Establecer ahora'}
          </button>
        </div>
      </div>

      <div className="mt-3">
        <strong className="small text-uppercase text-muted">Historial</strong>
        <table className="table table-sm mb-0 mt-1">
          <thead>
            <tr>
              <th>Precio</th>
              <th>Vigente desde</th>
              <th>Vigente hasta</th>
            </tr>
          </thead>
          <tbody>
            {historial.map((h) => (
              <tr key={h.id}>
                <td>${h.precio.toFixed(2)}</td>
                <td>{new Date(h.vigenteDesde).toLocaleString()}</td>
                <td>{h.vigenteHasta ? new Date(h.vigenteHasta).toLocaleString() : '—'}</td>
              </tr>
            ))}
            {historial.length === 0 && (
              <tr>
                <td colSpan={3} className="text-center text-muted py-2">
                  Sin historial todavía.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}
