import { useEffect, useState } from 'react'
import { clienteDeCatalogo, clienteDeCatalogosFiscales } from '../../api/catalogos'
import { api } from '../../api/cliente'
import { clienteDeOrganizacion } from '../../api/organizacion'
import { clienteDePrecios } from '../../api/precios'
import type {
  AlicuotaIvaListado,
  AreaAlta,
  AreaListado,
  CategoriaAlta,
  CategoriaListado,
  EmpresaListado,
  GrupoAlta,
  GrupoListado,
  ListaPrecioListado,
  MarcaAlta,
  MarcaListado,
  PaginaDe,
  ProveedorListado,
} from '../../api/tipos'
import { elegirAlicuotaPorDefecto, etiquetaDeProveedor, insertarOrdenadoPor, ordenarProveedoresPorEtiqueta } from './helpers'

const clienteAreas = clienteDeCatalogo<AreaListado, AreaAlta>('areas')
const clienteCategorias = clienteDeCatalogo<CategoriaListado, CategoriaAlta>('categorias')
const clienteMarcas = clienteDeCatalogo<MarcaListado, MarcaAlta>('marcas')
const clienteGrupos = clienteDeCatalogo<GrupoListado, GrupoAlta>('grupos')

/**
 * Los catálogos que el formulario de artículo necesita (áreas, categorías, marcas, grupos, proveedores,
 * alícuotas de IVA, empresas y listas de precio), leídos una vez al montar. Los que el formulario exige para
 * guardar dejan su motivo en `erroresCatalogosRequeridos`; el de las listas de precio, en `avisoListasPrecio`.
 */
export function useCatalogosDeArticulo() {
  const [areas, setAreas] = useState<AreaListado[]>([])
  const [categorias, setCategorias] = useState<CategoriaListado[]>([])
  const [marcas, setMarcas] = useState<MarcaListado[]>([])
  const [grupos, setGrupos] = useState<GrupoListado[]>([])
  const [proveedores, setProveedores] = useState<ProveedorListado[]>([])
  const [proveedoresTruncados, setProveedoresTruncados] = useState(false)
  const [alicuotasIva, setAlicuotasIva] = useState<AlicuotaIvaListado[]>([])
  const [empresas, setEmpresas] = useState<EmpresaListado[]>([])
  const [listasPrecio, setListasPrecio] = useState<ListaPrecioListado[]>([])
  const [erroresCatalogosRequeridos, setErroresCatalogosRequeridos] = useState<string[]>([])
  const [avisoListasPrecio, setAvisoListasPrecio] = useState('')

  function agregarErrorCatalogoRequerido(mensaje: string) {
    setErroresCatalogosRequeridos((prev) => (prev.includes(mensaje) ? prev : [...prev, mensaje]))
  }

  useEffect(() => {
    // incluirInactivos: true en los cuatro — un artículo existente puede referenciar un área/
    // categoría/marca/grupo ya desactivada (la baja lógica es hoy la salida recomendada cuando la
    // guarda de referencias rechaza el borrado, fix/articulos-form-catalogos-inactivos) y el
    // select de edición necesita esa opción para poder mostrarla y guardarla sin tocarla. El alta
    // solo ofrece las activas: `opcionesConValorActual` filtra en el render, no acá.
    clienteAreas
      .listar(true)
      .then(setAreas)
      .catch(() => {
        setAreas([])
        agregarErrorCatalogoRequerido('No se pudieron cargar las áreas.')
      })
    clienteCategorias.listar(true).then(setCategorias).catch(() => setCategorias([]))
    clienteMarcas.listar(true).then(setMarcas).catch(() => setMarcas([]))
    clienteGrupos.listar(true).then(setGrupos).catch(() => setGrupos([]))
    // tamanio grande a propósito: es un selector de referencia, no un listado paginado. Si el
    // tenant tiene más proveedores que el clamp del servidor, avisamos que la lista quedó
    // truncada en vez de esconder el resto en silencio. Sin `incluirInactivos` acá: a diferencia
    // de los catálogos genéricos, `ServicioDeProveedores.ListarAsync` no filtra por `Activo` (solo
    // por `incluirEliminados`, la baja lógica) — ya trae activos e inactivos por default.
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
  }, [])

  // `areas` trae activas e inactivas (incluirInactivos: true) — el default de un artículo NUEVO tiene que ser la
  // primera ACTIVA, nunca la primera del array tal cual (el servidor ordena por nombre, no por estado).
  // Anotación explícita de tipo: el efecto de defaults tardíos necesita el `''` en el tipo para poder comparar
  // contra él, aunque `Array.prototype.find` ya sea `T | undefined`.
  const primeraAreaActiva = areas.find((a) => a.activo)
  const areaPorDefecto: number | '' = primeraAreaActiva ? primeraAreaActiva.id : ''
  const alicuotaPorDefecto = elegirAlicuotaPorDefecto(alicuotasIva)

  // Altas rápidas de padrones (Categoría/Marca/Grupo/Proveedor habitual) desde el propio formulario de
  // artículo: cada handler solo inserta el item nuevo en la lista ya ordenada — la selección en el formulario y
  // el cierre del modal los resuelve `FormularioArticulo`, que es quien tiene el `valor`/`actualizarFormulario`.
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

  return {
    areas,
    categorias,
    marcas,
    grupos,
    proveedores,
    proveedoresTruncados,
    alicuotasIva,
    empresas,
    listasPrecio,
    erroresCatalogosRequeridos,
    avisoListasPrecio,
    areaPorDefecto,
    alicuotaPorDefecto,
    alCrearCategoria,
    alCrearMarca,
    alCrearGrupo,
    alCrearProveedor,
  }
}
