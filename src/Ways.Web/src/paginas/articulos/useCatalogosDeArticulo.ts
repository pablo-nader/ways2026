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

const CONSEJO_DE_LA_PAGINA = 'recargá la página para reintentar'
const CONSEJO_DENTRO_DE_UNA_COMPRA =
  'cerrá el alta y volvé a abrirla para reintentar; no recargues la página: perderías la compra sin guardar'

function conPrimeraMayuscula(texto: string): string {
  return texto.charAt(0).toUpperCase() + texto.slice(1)
}

const clienteAreas = clienteDeCatalogo<AreaListado, AreaAlta>('areas')
const clienteCategorias = clienteDeCatalogo<CategoriaListado, CategoriaAlta>('categorias')
const clienteMarcas = clienteDeCatalogo<MarcaListado, MarcaAlta>('marcas')
const clienteGrupos = clienteDeCatalogo<GrupoListado, GrupoAlta>('grupos')

/**
 * Los catálogos que el formulario de artículo necesita (áreas, categorías, marcas, grupos, proveedores,
 * alícuotas de IVA, empresas y listas de precio), leídos una vez al montar. Los que el formulario exige para
 * guardar dejan su motivo en `erroresCatalogosRequeridos`; el de las listas de precio, en `avisoListasPrecio`.
 */
export function useCatalogosDeArticulo({ dentroDeUnaCompra = false }: { dentroDeUnaCompra?: boolean } = {}) {
  // Dentro de una compra recargar descartaría lo cargado sin guardar: el reintento que sirve es cerrar y reabrir el alta.
  const consejoDeReintento = dentroDeUnaCompra ? CONSEJO_DENTRO_DE_UNA_COMPRA : CONSEJO_DE_LA_PAGINA
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
    // El hook se monta y desmonta con cada apertura del alta desde una compra: lo que llegue después no es de nadie.
    let vigente = true
    const aplicar = <T,>(setter: (valor: T) => void) => (valor: T) => {
      if (vigente) setter(valor)
    }
    // incluirInactivos: true en los cuatro — un artículo existente puede referenciar un área/
    // categoría/marca/grupo ya desactivada (la baja lógica es hoy la salida recomendada cuando la
    // guarda de referencias rechaza el borrado, fix/articulos-form-catalogos-inactivos) y el
    // select de edición necesita esa opción para poder mostrarla y guardarla sin tocarla. El alta
    // solo ofrece las activas: `opcionesConValorActual` filtra en el render, no acá.
    clienteAreas
      .listar(true)
      .then(aplicar(setAreas))
      .catch(() => {
        if (!vigente) return
        setAreas([])
        agregarErrorCatalogoRequerido('No se pudieron cargar las áreas.')
      })
    clienteCategorias.listar(true).then(aplicar(setCategorias)).catch(() => aplicar(setCategorias)([]))
    clienteMarcas.listar(true).then(aplicar(setMarcas)).catch(() => aplicar(setMarcas)([]))
    clienteGrupos.listar(true).then(aplicar(setGrupos)).catch(() => aplicar(setGrupos)([]))
    // tamanio grande a propósito: es un selector de referencia, no un listado paginado. Si el
    // tenant tiene más proveedores que el clamp del servidor, avisamos que la lista quedó
    // truncada en vez de esconder el resto en silencio. Sin `incluirInactivos` acá: a diferencia
    // de los catálogos genéricos, `ServicioDeProveedores.ListarAsync` no filtra por `Activo` (solo
    // por `incluirEliminados`, la baja lógica) — ya trae activos e inactivos por default.
    api
      .get<PaginaDe<ProveedorListado>>('/proveedores?tamanio=200')
      .then((p) => {
        if (!vigente) return
        // Pre-ordenado por la MISMA etiqueta que el select muestra (nombre de fantasía o razón
        // social, nunca la razón social cruda) — el alta rápida inserta manteniendo este orden.
        setProveedores(ordenarProveedoresPorEtiqueta(p.items))
        setProveedoresTruncados(p.total > p.items.length)
      })
      .catch(() => aplicar(setProveedores)([]))
    clienteDeCatalogosFiscales
      .alicuotasIva()
      .then(aplicar(setAlicuotasIva))
      .catch(() => {
        if (!vigente) return
        setAlicuotasIva([])
        agregarErrorCatalogoRequerido('No se pudieron cargar las alícuotas de IVA.')
      })
    clienteDeOrganizacion
      .listarEmpresas()
      .then(aplicar(setEmpresas))
      .catch(() => {
        if (!vigente) return
        setEmpresas([])
        agregarErrorCatalogoRequerido('No se pudieron cargar las empresas.')
      })
    clienteDePrecios
      .listasDePrecio()
      .then(aplicar(setListasPrecio))
      .catch(() => {
        if (!vigente) return
        setListasPrecio([])
        setAvisoListasPrecio(
          `No se pudieron cargar las listas de precio: el editor de precios no está disponible. ${conPrimeraMayuscula(consejoDeReintento)}.`,
        )
      })
    return () => {
      vigente = false
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
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
    consejoDeReintento,
    areaPorDefecto,
    alicuotaPorDefecto,
    alCrearCategoria,
    alCrearMarca,
    alCrearGrupo,
    alCrearProveedor,
  }
}
