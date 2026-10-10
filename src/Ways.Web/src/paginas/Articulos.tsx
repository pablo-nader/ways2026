import { useCallback, useEffect, useRef, useState } from 'react'
import { NavigationType, useLocation, useNavigate, useNavigationType } from 'react-router'
import { clienteDeArticulos } from '../api/articulos'
import { clienteDeCatalogo, clienteDeCatalogosFiscales } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeFamilias } from '../api/familias'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDePrecios } from '../api/precios'
import type {
  AlcanceDeFamilia,
  AlicuotaIvaListado,
  AreaAlta,
  AreaListado,
  CategoriaAlta,
  CategoriaListado,
  EmpresaListado,
  FilaDeGrillaDeArticulos,
  GrupoAlta,
  GrupoListado,
  ListaPrecioListado,
  MarcaAlta,
  MarcaListado,
  PaginaDe,
  ProveedorListado,
} from '../api/tipos'
import { Box } from '../componentes/Box'
import { Modal } from '../componentes/Modal'
import {
  AVISO_DE_CAMPOS_COMPARTIDOS_CAMBIARON,
  AVISO_DE_FAMILIA_CAMBIO,
  avisoDeValoresDistintos,
  camposCompartidosModificados,
  camposNombradosEnMensaje,
  CODIGOS_DE_CONFLICTO_DE_ALTA,
  contextoDeAlcance,
  familiaDelArticulo,
  mensajeDeAlta,
  mensajeDeEdicion,
  motivoDeFamiliaNoElegible,
  rechazoDeLaFamiliaNuevaEnElAlta,
  valoresDeFamiliaAFormulario,
  type AccionesDeFamiliaDelFormulario,
  type EstadoDeUnionAFamilia,
} from './articulos/familia'
import { aAlta, aEdicion, aFormulario, formularioVacio, type Formulario } from './articulos/FormularioArticulo'
import { GrillaDeArticulos } from './articulos/GrillaDeArticulos'
import { elegirAlicuotaPorDefecto, etiquetaDeProveedor, insertarOrdenadoPor, ordenarProveedoresPorEtiqueta } from './articulos/helpers'
import { desplazamientoHaciaLaAnterior, HISTORIAL_SIN_OBSERVAR, registrarEntrada } from './articulos/historialObservado'
import { ModalDeArticulo } from './articulos/ModalDeArticulo'
import { PreguntaDeAlcance } from './articulos/PreguntaDeAlcance'
import { analizarRutaModal, type ModoModalDeArticulo } from './articulos/rutaModal'
import { useFamiliasDelFormulario } from './articulos/useFamiliasDelFormulario'
import { mensajeDeFalloDeEscritura } from './familias/mensajes'
import { usePrevisualizacion } from './familias/usePrevisualizacion'
import { BotonIcono } from '../componentes/BotonIcono'

const clienteAreas = clienteDeCatalogo<AreaListado, AreaAlta>('areas')
const clienteCategorias = clienteDeCatalogo<CategoriaListado, CategoriaAlta>('categorias')
const clienteMarcas = clienteDeCatalogo<MarcaListado, MarcaAlta>('marcas')
const clienteGrupos = clienteDeCatalogo<GrupoListado, GrupoAlta>('grupos')

const MENSAJE_ID_INVALIDO = 'No se especificó un artículo válido.'
const MENSAJE_CONFIRMAR_DESCARTE = 'Hay cambios sin guardar en el artículo. ¿Descartarlos?'

/** Identidad del modal actualmente comprometido en pantalla: `null` (cerrado), `'nuevo'` (alta) o
 * el id numérico de la edición en curso — nunca el `modo`/`idParam` crudos de la URL, que pueden
 * apuntar a un id inválido sin formulario cargado. Se usa para distinguir "la URL cambió pero
 * seguimos en el mismo modal" (p. ej. tras cancelar un intento de salida) de una salida real. */
type DestinoModal = 'nuevo' | number | 'invalido' | null

/** La pregunta de alcance abierta al guardar la edición de un miembro de una familia (doc 10 §3): `contexto` es
 * con lo que se abre (la familia conocida o el texto del servidor) y `campos`, las etiquetas de los campos
 * compartidos que cambian. Se abre antes de escribir lo que pregunta; al elegir un alcance sigue abierta, con sus
 * respuestas inertes, mientras dura el PUT, y la respuesta del PUT la cierra mientras el modal siga siendo el que preguntó. */
type DecisionDeAlcance = { contexto: string; campos: string[] }

function destinoDeRuta(modo: ModoModalDeArticulo | null, idParam: string | null): DestinoModal {
  if (modo === 'crear') return 'nuevo'
  if (modo === 'editar') return idParam !== null && /^\d+$/.test(idParam) ? Number(idParam) : 'invalido'
  return null
}

function rutaDeDestino(destino: DestinoModal): string {
  if (destino === 'nuevo') return '/articulos/create'
  if (typeof destino === 'number') return `/articulos/edit/${destino}`
  return '/articulos'
}

/**
 * ABM dedicado de artículos (design decision 1: no la máquina genérica de catálogos) — la
 * pantalla más pesada a la fecha (identificación + códigos de barra + clasificación + costos +
 * disponibilidad por empresa + precios por lista). El código de barras y el editor de precios
 * solo se habilitan una vez que el artículo tiene `id` persistido: ambos endpoints cuelgan de
 * `/api/articulos/{id}/...`, no existen antes del alta.
 *
 * articulos-en-modal: la grilla es lo único que se ve en `/articulos`; el alta/edición vive en un
 * `Modal` gobernado por la URL (`/articulos/create`, `/articulos/edit/:id`). Montado en
 * `/articulos/*` (App.tsx) como una única entrada de ruta — el modo del modal se deriva de
 * `useLocation().pathname` con `analizarRutaModal` en vez de con `<Routes>` anidadas, para que
 * React Router nunca remonte esta página (y con ella, la grilla) al abrir/cerrar el modal: React
 * Router remonta al cambiar de ENTRADA de ruta, no al cambiar solo un param de la misma entrada.
 */
export function Articulos() {
  const location = useLocation()
  const navigate = useNavigate()
  const navigationType = useNavigationType()
  const { modo, idParam } = analizarRutaModal(location.pathname)

  const [areas, setAreas] = useState<AreaListado[]>([])
  const [categorias, setCategorias] = useState<CategoriaListado[]>([])
  const [marcas, setMarcas] = useState<MarcaListado[]>([])
  const [grupos, setGrupos] = useState<GrupoListado[]>([])
  const [proveedores, setProveedores] = useState<ProveedorListado[]>([])
  const [proveedoresTruncados, setProveedoresTruncados] = useState(false)
  const [alicuotasIva, setAlicuotasIva] = useState<AlicuotaIvaListado[]>([])
  const [empresas, setEmpresas] = useState<EmpresaListado[]>([])
  const [listasPrecio, setListasPrecio] = useState<ListaPrecioListado[]>([])
  // Banners de la Baja únicamente (articulos-grilla-web: el error de CARGA del listado ahora vive
  // dentro de `GrillaDeArticulos`, con su propio banner — regla 14 de react-async-state, un slot
  // de estado por fuente).
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [erroresCatalogosRequeridos, setErroresCatalogosRequeridos] = useState<string[]>([])
  const [avisoListasPrecio, setAvisoListasPrecio] = useState('')
  const [eliminando, setEliminando] = useState(false)
  // Bump tras guardar/dar de baja: le pide a la grilla un refresco manteniendo sus propios
  // filtros/página — la página no espera ni conoce el resultado de ese refresco (ver el
  // doc-comment de `GrillaDeArticulos`).
  const [pedidoDeRefresco, setPedidoDeRefresco] = useState(0)

  // ---- estado del modal de alta/edición ----------------------------------------------------------
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [claveFormulario, setClaveFormulario] = useState<number | 'nuevo'>('nuevo')
  const [cargandoDetalle, setCargandoDetalle] = useState(false)
  const [errorDetalle, setErrorDetalle] = useState('')
  const [guardando, setGuardando] = useState(false)
  const [avisoGuardado, setAvisoGuardado] = useState('')
  const [errorGuardado, setErrorGuardado] = useState('')
  const [escriturasHijas, setEscriturasHijas] = useState(0)
  // Familias (doc 10 §3): las que se ofrecen en un alta y el detalle de la familia del formulario.
  const familias = useFamiliasDelFormulario()
  const [decisionDeAlcance, setDecisionDeAlcance] = useState<DecisionDeAlcance | null>(null)
  const [saliendoDeFamilia, setSaliendoDeFamilia] = useState(false)
  // Unir un artículo existente a una familia (elegir, previsualizar, confirmar) y crearle una: la elección todavía no escribe nada.
  const [unionElegida, setUnionElegida] = useState<number | ''>('')
  const [errorDeUnion, setErrorDeUnion] = useState('')
  const [escribiendoFamilia, setEscribiendoFamilia] = useState(false)
  const previsualizacionDeUnion = usePrevisualizacion()
  // El rechazo del servidor a la familia nueva de un alta: slot propio, junto a la sección de la familia (react-async-state regla 14).
  const [errorDeFamiliaNueva, setErrorDeFamiliaNueva] = useState('')
  // Única guarda de reentrancia de `guardar`, `sacarDeLaFamilia` y `confirmarUnion` (react-async-state regla 11): es un `ref` porque el
  // estado (`guardando`, `saliendoDeFamilia`) recién cambia en el próximo render, después de los dos clics de un mismo tick.
  const escrituraEnCursoRef = useRef(false)
  const tokenEdicionRef = useRef(0)
  // Snapshot del formulario tal como quedó cargado/guardado por última vez — la base contra la que
  // se compara para saber si hay cambios sin guardar al intentar cerrar (regla: confirmar antes de
  // descartar, igual criterio que el `confirm()` de la Baja).
  const formularioOriginalRef = useRef<Formulario | null>(null)
  // Identidad del modal ya comprometida (ver `DestinoModal`) — la compuerta de confirmación del
  // efecto de apertura la compara contra el destino que la URL pide ahora, para distinguir "salir
  // de verdad" de "la URL volvió sola al mismo modal" (p. ej. tras cancelar esa misma salida).
  // `destinoMostrado` es su espejo en estado y es lo ÚNICO que decide si el modal se renderiza: el
  // pathname en vivo puede adelantarse a la decisión (Atrás/Adelante commitean la URL antes de que
  // el efecto pregunte), y renderizar desde él desmontaría el modal — y con él el estado propio de
  // los hijos (código de barras tipeado, alta rápida abierta) — aunque después se cancele la salida.
  // Ref y estado se actualizan siempre juntos, solo cuando la salida o apertura ya quedó decidida.
  const destinoModalRef = useRef<DestinoModal>(null)
  const [destinoMostrado, setDestinoMostrado] = useState<DestinoModal>(null)
  // Posición relativa de las entradas del historial vistas, para deshacer un Atrás/Adelante
  // rechazado volviendo a la entrada del modal (ver `historialObservado`).
  const historialRef = useRef(HISTORIAL_SIN_OBSERVAR)
  const refBotonNuevo = useRef<HTMLButtonElement>(null)
  const ocupado = guardando || eliminando || escriturasHijas > 0 || saliendoDeFamilia || escribiendoFamilia

  // Token del fetch de edición en curso: protege contra la staleness del fetch de detalle (abrir
  // otra edición, o cerrar, mientras el detalle anterior sigue en vuelo) y contra que la propia
  // respuesta del guardado se aplique si mientras tanto se invalidó. El "supersede" de una edición
  // por otra acción durante un guardado ya no depende del token — mientras `ocupado` es true, el
  // modal queda inerte (no se puede cerrar) y Nuevo/Editar/Baja quedan deshabilitados en la grilla.
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

  // `areas` trae activas e inactivas (incluirInactivos: true, fix/articulos-form-catalogos-
  // inactivos) — el default de un artículo NUEVO tiene que ser la primera ACTIVA, nunca la primera
  // del array tal cual (el servidor ordena por nombre, no por estado, así que una inactiva puede
  // quedar primera alfabéticamente). Anotación explícita de tipo: sin `noUncheckedIndexedAccess`,
  // el efecto de defaults tardíos de más abajo (M5) necesita el `''` en el tipo para poder comparar
  // contra él, aunque `Array.prototype.find` ya sea `T | undefined`.
  const primeraAreaActiva = areas.find((a) => a.activo)
  const areaPorDefecto: number | '' = primeraAreaActiva ? primeraAreaActiva.id : ''
  const alicuotaPorDefecto = elegirAlicuotaPorDefecto(alicuotasIva)

  // Altas rápidas de padrones (Categoría/Marca/Grupo/Proveedor habitual) desde el propio
  // formulario de artículo: cada handler solo inserta el item nuevo en la lista ya ordenada — la
  // selección en el formulario y el cierre del modal los resuelve `FormularioArticulo`, que es
  // quien tiene el `valor`/`actualizarFormulario` del artículo en edición.
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

  function actualizarFormulario(actualizar: (previo: Formulario) => Formulario) {
    setFormulario((prev) => (prev ? actualizar(prev) : prev))
  }

  /** Suelta la familia que el formulario tenía a la vista —la elegida en un alta o la propuesta para unir un artículo
   * existente— e invalida todo lo que estaba en vuelo sobre ella: lecturas, previsualización y avisos. */
  function soltarFamiliaDelFormulario() {
    familias.descartarDetalle()
    previsualizacionDeUnion.invalidar()
    setUnionElegida('')
    setErrorDeUnion('')
    setErrorDeFamiliaNueva('')
  }

  /** La propuesta de unión previsualiza lo que cambiaría con los valores GUARDADOS del artículo: si se guardó el artículo o uno de
   * sus precios, esa foto ya no vale y se descarta (el selector vuelve a "Sin familia"). Solo actúa si hay una propuesta abierta:
   * `soltarFamiliaDelFormulario` también descarta el detalle de la familia de un miembro. */
  function descartarPropuestaDeUnion() {
    if (unionElegida !== '') soltarFamiliaDelFormulario()
  }

  /** `avisoInicial` es el error que el formulario recién cargado muestra: lo usa la recarga que sigue a un
   * `familia_cambio`, que de otro modo perdería su explicación al limpiar los avisos. */
  async function abrirEdicion(idNumerico: number, avisoInicial = '') {
    setErrorDetalle('')
    setCargandoDetalle(true)
    setFormulario(null)
    setDecisionDeAlcance(null)
    soltarFamiliaDelFormulario()
    const token = invalidarEdicionEnCurso()
    setClaveFormulario(idNumerico)
    try {
      // El listado no completa idsEmpresas (evita el N+1) — el detalle sí.
      //
      // El servidor es la autoridad sobre qué referencia es válida — nunca se clasifica acá
      // contra el estado (cliente) de los catálogos, que puede estar cargando, haber fallado en
      // silencio, o venir truncado (proveedores). Cada id viaja tal cual al formulario; si ya no
      // existe, el guardado sin tocar lo reenvía intacto y el servidor lo rechaza con 400
      // `referencia_invalida` (`ServicioDeArticulos`), que este modal ya muestra vía
      // `ErrorApi.message`.
      const detalle = await clienteDeArticulos.obtener(idNumerico)
      if (tokenEdicionRef.current !== token) return
      // La familia del miembro (nombre y cantidad para el rótulo y para la pregunta de alcance) se espera antes
      // de mostrar el formulario. Su fallo no lo impide: el hook deja el motivo y el artículo sigue siendo
      // miembro, solo que sin nombre; el servidor frena un cambio sin alcance con `alcance_requerido`.
      if (detalle.idFamilia !== null) {
        await familias.cargarDetalle(detalle.idFamilia)
        if (tokenEdicionRef.current !== token) return
      }
      const cargado = aFormulario(detalle)
      setFormulario(cargado)
      formularioOriginalRef.current = cargado
      setGuardando(false)
      setAvisoGuardado('')
      setErrorGuardado(avisoInicial)
    } catch (e) {
      if (tokenEdicionRef.current !== token) return
      setFormulario(null)
      formularioOriginalRef.current = null
      setErrorDetalle(e instanceof ErrorApi ? e.message : 'No se pudo abrir el artículo.')
    } finally {
      if (tokenEdicionRef.current === token) setCargandoDetalle(false)
    }
  }

  // Vuelve al estado "sin modal": compromete el destino nulo (desmonta el modal) y limpia todo el
  // estado del formulario para no arrastrar restos a la próxima apertura. Lo usan el efecto de
  // apertura (salida aceptada por URL) y `cerrarModal` (salida aceptada por click) — en este último
  // caso, comprometerlo ANTES de navegar es lo que evita que el efecto vuelva a preguntar.
  function descartarModal() {
    destinoModalRef.current = null
    setDestinoMostrado(null)
    invalidarEdicionEnCurso()
    soltarFamiliaDelFormulario()
    setDecisionDeAlcance(null)
    setGuardando(false)
    setFormulario(null)
    formularioOriginalRef.current = null
    setErrorDetalle('')
    setAvisoGuardado('')
    setErrorGuardado('')
    setCargandoDetalle(false)
  }

  // Declarado antes del efecto de apertura a propósito: React corre los efectos en orden de
  // declaración, así que cuando ese efecto decide una salida la entrada nueva ya quedó registrada.
  useEffect(() => {
    historialRef.current = registrarEntrada(historialRef.current, location.key, navigationType)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [location.key])

  // Efecto de apertura: reacciona a la URL, no a clicks — así una edición abierta desde la grilla,
  // desde una URL tipeada a mano o desde "atrás/adelante" del navegador pasan siempre por el mismo
  // camino. Cuando `modo` pasa a null (URL vuelve a /articulos) se limpia todo el estado del modal
  // para no arrastrar restos a la próxima apertura.
  useEffect(() => {
    const destino = destinoDeRuta(modo, idParam)

    // Ya estamos en este destino: tras cancelar un intento de salir (unas líneas más abajo se navegó
    // de vuelta a esta misma URL), o tras un `cerrarModal` que ya comprometió el cierre antes de
    // navegar. No hay nada que resetear ni que preguntar — evita reaplicar el bloque de abajo (que
    // en 'crear' pisaría el borrador con un formulario en blanco nuevo).
    if (destino === destinoModalRef.current) return

    // La URL se fue de un modal con cambios sin guardar sin pasar por `cerrarModal` (Atrás/Adelante
    // del navegador, o un link a otra edición): la URL nueva ya está commiteada, pero el modal sigue
    // montado porque se renderiza desde `destinoMostrado`, que todavía no cambió. Si cancela, se
    // vuelve a la URL del modal actual y las mismas instancias siguen vivas (formulario e hijos
    // intactos); si acepta, se sigue de largo y el bloque de abajo compromete el destino nuevo.
    if (destinoModalRef.current !== null && haySinGuardar()) {
      if (!confirm(MENSAJE_CONFIRMAR_DESCARTE)) {
        // Un POP se deshace MOVIÉNDOSE a la entrada anterior, la del modal: un `replace` pisaría la
        // entrada a la que llegó el POP (p. ej. la de la grilla) y Atrás ya no la encontraría. Si
        // esa entrada es anterior al montaje de esta pantalla (p. ej. tras recargar), no tiene
        // posición conocida y se reemplaza igual. Un PUSH/REPLACE sí se deshace con `replace`:
        // reescribe solo la entrada que esa misma navegación acaba de crear o de pisar.
        const desplazamiento = navigationType === NavigationType.Pop ? desplazamientoHaciaLaAnterior(historialRef.current) : null
        if (desplazamiento === null) navigate(rutaDeDestino(destinoModalRef.current), { replace: true })
        else navigate(desplazamiento)
        return
      }
    }

    if (destino === null) {
      descartarModal()
      return
    }

    destinoModalRef.current = destino
    setDestinoMostrado(destino)

    if (modo === 'crear') {
      invalidarEdicionEnCurso()
      soltarFamiliaDelFormulario()
      setDecisionDeAlcance(null)
      setGuardando(false)
      setErrorDetalle('')
      setAvisoGuardado('')
      setErrorGuardado('')
      setCargandoDetalle(false)
      const nuevo = { ...formularioVacio(), idArea: areaPorDefecto, idAlicuotaIva: alicuotaPorDefecto }
      setFormulario(nuevo)
      formularioOriginalRef.current = nuevo
      setClaveFormulario('nuevo')
      return
    }

    if (modo === 'editar') {
      const idNumerico = idParam !== null && /^\d+$/.test(idParam) ? Number(idParam) : null
      if (idNumerico === null) {
        invalidarEdicionEnCurso()
        setDecisionDeAlcance(null)
        setFormulario(null)
        formularioOriginalRef.current = null
        setErrorDetalle(MENSAJE_ID_INVALIDO)
        setCargandoDetalle(false)
        return
      }
      // Ya cargado: pasa exactamente cuando `guardar()` acaba de crear este mismo artículo y
      // reemplazó la URL a /articulos/edit/{id} — evita un refetch redundante que además pisaría
      // el aviso de éxito recién puesto.
      if (formulario?.id === idNumerico) return
      void abrirEdicion(idNumerico)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [modo, idParam])

  // Los defaults de Área/Alícuota de IVA de un alta se calculan en el efecto de arriba, pero ese
  // efecto solo corre al ENTRAR a 'crear' — una navegación directa a /articulos/create antes de que
  // esos catálogos resuelvan deja ambos campos en '' para siempre (el efecto no vuelve a correr
  // cuando las listas llegan tarde). Este efecto completa esos dos campos SOLO si siguen en '' en
  // el momento en que el catálogo respectivo llega — nunca pisa una elección ya hecha por el
  // usuario — y aplica a `formularioOriginalRef` SOLO los campos que completó, para que el
  // auto-completado no dispare un falso "hay cambios sin guardar" (M2/M3) sin convertir en
  // "guardado" lo que el usuario ya hubiera tipeado en otros campos antes de que llegaran.
  useEffect(() => {
    if (modo !== 'crear' || formulario === null || formulario.id !== null) return
    const completados: Partial<Pick<Formulario, 'idArea' | 'idAlicuotaIva'>> = {}
    if (formulario.idArea === '' && areaPorDefecto !== '') completados.idArea = areaPorDefecto
    if (formulario.idAlicuotaIva === '' && alicuotaPorDefecto !== '') completados.idAlicuotaIva = alicuotaPorDefecto
    if (Object.keys(completados).length === 0) return
    setFormulario({ ...formulario, ...completados })
    if (formularioOriginalRef.current) formularioOriginalRef.current = { ...formularioOriginalRef.current, ...completados }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [modo, areaPorDefecto, alicuotaPorDefecto])

  /** Qué hace "Guardar": la edición de un miembro de una familia que cambia un campo compartido no escribe todavía,
   * pregunta el alcance (doc 10 §3). Todo lo demás —el alta, la edición de quien no es miembro y la que solo cambia
   * campos propios, que no tiene nada que replicar— guarda directo. */
  function pedirGuardado() {
    if (!formulario) return

    const original = formularioOriginalRef.current
    if (formulario.id !== null && formulario.idFamilia !== '' && original !== null) {
      const campos = camposCompartidosModificados(original, formulario)
      if (campos.length > 0) {
        setErrorGuardado('')
        setAvisoGuardado('')
        setDecisionDeAlcance({
          contexto: contextoDeAlcance(familiaDelArticulo(formulario.idFamilia, familias.detalle)),
          campos: campos.map((campo) => campo.etiqueta),
        })
        return
      }
    }

    void guardar()
  }

  // Las dos salidas de la pregunta ("Cancelar" y el cierre del modal) ya están inertes mientras hay un guardado en vuelo.
  function cancelarAlcance() {
    setDecisionDeAlcance(null)
  }

  function elegirAlcance(alcance: AlcanceDeFamilia) {
    void guardar(alcance)
  }

  async function guardar(alcance?: AlcanceDeFamilia) {
    if (escrituraEnCursoRef.current) return
    if (!formulario) return
    escrituraEnCursoRef.current = true

    const token = invalidarEdicionEnCurso()
    setGuardando(true)
    setErrorGuardado('')
    setAvisoGuardado('')
    setErrorDeFamiliaNueva('')
    // Lo que sigue a un rechazo que hay que resolver DESPUÉS de soltar el guardado (recargar el artículo o la
    // familia): ejecutado dentro del `catch`, `abrirEdicion` invalidaría el token y el `finally` dejaría el
    // formulario ocupado para siempre.
    let seguimiento: (() => void) | null = null

    try {
      if (formulario.id === null) {
        const creado = await clienteDeArticulos.crear(aAlta(formulario))
        // La familia tiene un miembro más: las que se ofrecen en un alta se vuelven a leer, también si el modal ya cambió de artículo.
        if (creado.idFamilia !== null) void familias.recargarOpciones()
        if (tokenEdicionRef.current === token) {
          const cargado = aFormulario(creado)
          setAvisoGuardado(
            mensajeDeAlta(
              formulario.nombre,
              creado.codigoInterno,
              familiaDelArticulo(creado.idFamilia ?? '', familias.detalle),
              formulario.nombreFamiliaNueva.trim(),
            ),
          )
          setFormulario(cargado)
          formularioOriginalRef.current = cargado
          // El rótulo de la familia cuenta al artículo recién nacido: se vuelve a leer su detalle.
          if (creado.idFamilia !== null) void familias.cargarDetalle(creado.idFamilia)
          // History replace (no push): /articulos/create nunca queda alcanzable con "atrás" una
          // vez que el alta se concretó — el modal sigue montado (misma entrada de ruta,
          // `/articulos/*`), así que el aviso y el foco sobreviven al cambio de URL.
          navigate(`/articulos/edit/${creado.id}`, { replace: true })
        }
      } else {
        const actualizado = await clienteDeArticulos.actualizar(formulario.id, aEdicion(formulario, alcance))
        // El artículo salió de su familia: se vuelven a leer las que se ofrecen en un alta.
        if (alcance === 'SoloEste') void familias.recargarOpciones()
        if (tokenEdicionRef.current === token) {
          const cargado = aFormulario(actualizado)
          setAvisoGuardado(
            mensajeDeEdicion(formulario.nombre, alcance, familiaDelArticulo(formulario.idFamilia, familias.detalle)?.nombre ?? null),
          )
          setFormulario(cargado)
          formularioOriginalRef.current = cargado
          setDecisionDeAlcance(null)
          descartarPropuestaDeUnion()
        }
      }

      // El refresco de la grilla no pertenece al token de edición: la fila afectada debe quedar
      // al día sin importar si el formulario abierto ahora es otro. El guardado ya tuvo éxito
      // acá — un bump de `pedidoDeRefresco` solo PIDE el refresco, `GrillaDeArticulos` es dueña
      // de ejecutarlo y de reportar su propio fallo con su propio banner (react-async-state
      // regla 6/14: el guardado ya confirmado nunca se reporta como fallido por un refresco de
      // vista ajeno).
      setPedidoDeRefresco((n) => n + 1)
    } catch (e) {
      if (tokenEdicionRef.current === token) {
        setDecisionDeAlcance(null)
        seguimiento = tratarFalloDeGuardado(e, formulario, token)
      }
    } finally {
      // Sin gate de token: la reentrancia se destraba siempre (react-async-state regla 11).
      escrituraEnCursoRef.current = false
      if (tokenEdicionRef.current === token) setGuardando(false)
    }

    seguimiento?.()
  }

  /** El rechazo de un guardado. Los conflictos de familia (doc 10 §3) tienen su propio camino: la pregunta de
   * alcance que el cliente no hizo, la pertenencia que cambió, la familia que ya no admite al artículo. Devuelve
   * lo que queda por hacer una vez soltado el guardado, o `null` si el rechazo ya quedó resuelto. */
  function tratarFalloDeGuardado(e: unknown, enviado: Formulario, token: number): (() => void) | null {
    if (e instanceof ErrorApi) {
      if (enviado.id === null && enviado.idFamilia === '' && enviado.nombreFamiliaNueva.trim() !== '') {
        const rechazo = rechazoDeLaFamiliaNuevaEnElAlta(e.estado, e.codigo, enviado.nombre)
        if (rechazo !== null) {
          // El nombre en uso puede ser de una familia que la lista no tenía: se vuelve a leer para que la validación lo conozca.
          if (e.codigo === 'familia_nombre_duplicado') void familias.recargarOpciones()
          setErrorDeFamiliaNueva(rechazo)
          return null
        }
      }

      if (enviado.id === null && enviado.idFamilia !== '') {
        const idFamilia = enviado.idFamilia
        const rechazoDeLaFamilia = e.estado === 409 && (CODIGOS_DE_CONFLICTO_DE_ALTA as readonly string[]).includes(e.codigo)
        if (rechazoDeLaFamilia || e.estado === 404) return () => void resolverAltaRechazadaPorLaFamilia(e, idFamilia, token)
      }

      if (enviado.id !== null && e.estado === 409) {
        const idArticulo = enviado.id
        if (e.codigo === 'alcance_requerido') {
          // El artículo es miembro de una familia que la pantalla no conocía: se pregunta ahora, con el texto del
          // servidor, que nombra la familia y cuántos artículos tiene. Si la pantalla no ve ningún campo compartido
          // cambiado, lo que el servidor ve distinto es lo que ella tiene desactualizado: preguntar ofrecería escribir
          // esos valores viejos en toda la familia, así que se recarga el artículo.
          const campos = camposCompartidosModificados(formularioOriginalRef.current ?? enviado, enviado)
          if (campos.length === 0) return () => recargarArticuloDesactualizado(idArticulo, AVISO_DE_CAMPOS_COMPARTIDOS_CAMBIARON)
          setDecisionDeAlcance({ contexto: e.message, campos: campos.map((campo) => campo.etiqueta) })
          return null
        }
        if (e.codigo === 'familia_cambio') return () => recargarArticuloDesactualizado(idArticulo)
      }
    }

    setErrorGuardado(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
    return null
  }

  /** Un alta dentro de una familia que el servidor rechazó. `familia_valores_distintos`: la familia cambió desde
   * que se la eligió, así que se la vuelve a leer y sus valores vuelven al formulario. Inactiva, sin artículos
   * vivos o inexistente: ya no admite artículos, sale de la selección y se actualiza la lista de familias. */
  async function resolverAltaRechazadaPorLaFamilia(e: ErrorApi, idFamilia: number, token: number) {
    void familias.recargarOpciones()
    let mensaje = e.message

    if (e.codigo === 'familia_valores_distintos') {
      const detalle = await familias.cargarDetalle(idFamilia)
      if (tokenEdicionRef.current !== token) return

      if (detalle === null) {
        // La carga falló: el motivo lo muestra la sección de la familia, y el rechazo del servidor sigue en pie.
        setErrorGuardado(e.message)
        return
      }

      if (detalle.activo && detalle.valores !== null) {
        const valores = valoresDeFamiliaAFormulario(detalle.valores)
        actualizarFormulario((previo) => ({ ...previo, ...valores }))
        setErrorGuardado(avisoDeValoresDistintos(camposNombradosEnMensaje(e.message)))
        return
      }

      mensaje = detalle.activo
        ? `La familia "${detalle.nombre}" no tiene artículos vivos: no hay valores de referencia ni precios que copiar.`
        : `La familia "${detalle.nombre}" está inactiva: no se le pueden agregar artículos.`
    }

    familias.descartarDetalle()
    actualizarFormulario((previo) => ({ ...previo, idFamilia: '' }))
    setErrorGuardado(`${mensaje} Elegí otra familia o creá el artículo sin familia.`)
  }

  /** Lo que la pantalla muestra del artículo ya no es lo que hay en el servidor (`familia_cambio`: su pertenencia; o
   * `alcance_requerido` sin cambios compartidos a la vista: sus campos compartidos). Se vuelve a leer el artículo con su
   * familia y el aviso explica por qué se perdió lo tipeado. Solo si el modal sigue en ese artículo: la llamada puede
   * venir del editor de precios de uno que ya se cerró. */
  function recargarArticuloDesactualizado(idArticulo: number, aviso = AVISO_DE_FAMILIA_CAMBIO) {
    if (destinoModalRef.current !== idArticulo) return
    void abrirEdicion(idArticulo, aviso)
  }

  /** El artículo cambió de familia en el servidor (salió de ella o se creó una con él como referencia): el formulario y su
   * base de comparación lo reflejan, así que no cuenta como un cambio sin guardar. */
  function aplicarPertenencia(idArticulo: number, idFamilia: number | '') {
    actualizarFormulario((previo) => (previo.id === idArticulo ? { ...previo, idFamilia } : previo))
    if (formularioOriginalRef.current?.id === idArticulo) {
      formularioOriginalRef.current = { ...formularioOriginalRef.current, idFamilia }
    }
  }

  /** El editor de precios escribió con "solo este": el precio quedó únicamente en este artículo, que salió. Las familias
   * que se ofrecen en un alta se vuelven a leer aunque el modal ya muestre otro artículo: la salida ocurrió en el servidor. */
  function alSalirDeLaFamiliaPorUnPrecio(idArticulo: number | null, nombre: string | null) {
    void familias.recargarOpciones()
    if (idArticulo === null || destinoModalRef.current !== idArticulo) return
    aplicarPertenencia(idArticulo, '')
    setAvisoGuardado(`El artículo salió de la familia${nombre === null ? '' : ` "${nombre}"`}: el precio se guardó solo en él.`)
  }

  /** "Sacar de la familia": una escritura propia (DELETE), con su ventana inerte y su token. Resuelve `false`
   * si no llegó a empezar. */
  async function sacarDeLaFamilia(): Promise<boolean> {
    if (escrituraEnCursoRef.current) return false
    if (!formulario || formulario.id === null || formulario.idFamilia === '') return false
    const idArticulo = formulario.id
    const idFamilia = formulario.idFamilia
    const nombre = formulario.nombre
    const nombreDeLaFamilia = familiaDelArticulo(idFamilia, familias.detalle).nombre
    escrituraEnCursoRef.current = true

    const token = invalidarEdicionEnCurso()
    setSaliendoDeFamilia(true)
    setErrorGuardado('')
    setAvisoGuardado('')

    try {
      await clienteDeFamilias.sacarArticulo(idFamilia, idArticulo)
      // La familia tiene un miembro menos, y si era el último deja de ofrecerse en un alta.
      void familias.recargarOpciones()
      if (tokenEdicionRef.current === token) {
        aplicarPertenencia(idArticulo, '')
        setAvisoGuardado(
          `El artículo "${nombre}" salió de la familia${nombreDeLaFamilia === null ? '' : ` "${nombreDeLaFamilia}"`}: conserva todos sus valores y sus precios.`,
        )
      }
    } catch (e) {
      if (tokenEdicionRef.current === token) {
        // La familia ya no existe o el artículo ya no es de ella: lo que la pantalla muestra dejó de ser cierto.
        if (e instanceof ErrorApi && ((e.estado === 409 && e.codigo === 'familia_cambio') || e.estado === 404)) {
          recargarArticuloDesactualizado(idArticulo)
        } else {
          setErrorGuardado(e instanceof ErrorApi ? e.message : 'No se pudo sacar el artículo de la familia.')
        }
      }
    } finally {
      // Sin gate de token, a diferencia de `guardar`: esta bandera es solo de esta escritura, así que la recarga que
      // acaba de arrancar `abrirEdicion` no la afecta.
      escrituraEnCursoRef.current = false
      setSaliendoDeFamilia(false)
    }

    return true
  }

  /** Elegir una familia en un alta: el artículo toma sus trece campos compartidos, que quedan bloqueados. Dejarla
   * en "Sin familia" los desbloquea y conserva lo que tenían. Elegir una existente descarta la familia nueva anotada.
   * En un artículo existente que no es miembro no escribe: propone la unión (ver `proponerUnion`). */
  function elegirFamilia(idFamilia: number | '') {
    soltarFamiliaDelFormulario()
    setErrorGuardado('')
    if (formulario !== null && formulario.id !== null) {
      if (idFamilia !== '') void proponerUnion(formulario.id, idFamilia)
      return
    }

    // Elegir otra familia supera todo lo que estaba en vuelo sobre la anterior, incluido el seguimiento de un
    // guardado rechazado que todavía está releyendo su familia.
    invalidarEdicionEnCurso()
    actualizarFormulario((previo) => ({ ...previo, idFamilia, nombreFamiliaNueva: '' }))
    if (idFamilia !== '') void cargarFamiliaDelAlta(idFamilia)
  }

  /** Lee la familia elegida y, si admite al artículo, pide qué cambiaría al unirlo con su referencia (el miembro vivo de menor
   * id). Cada lectura lleva la generación de su hook: elegir otra familia o cancelar descarta la respuesta de la anterior. */
  async function proponerUnion(idArticulo: number, idFamilia: number) {
    setUnionElegida(idFamilia)
    const detalle = await familias.cargarDetalle(idFamilia)
    // `null`: la carga falló (la sección muestra el motivo) o la superó otra elección, la cancelación o el cierre del modal.
    if (detalle === null) return

    const motivo = motivoDeFamiliaNoElegible(detalle)
    if (motivo !== '') {
      familias.descartarDetalle()
      setUnionElegida('')
      setErrorDeUnion(motivo)
      void familias.recargarOpciones()
      return
    }

    void previsualizacionDeUnion.previsualizar({ idArticuloReferencia: detalle.articulos[0].id, idsArticulos: [idArticulo] })
  }

  async function cargarFamiliaDelAlta(idFamilia: number) {
    const detalle = await familias.cargarDetalle(idFamilia)
    // `null`: la carga falló (la sección muestra el motivo) o la superó otra elección o el cierre del modal. Una
    // respuesta que no es `null` es la de la familia elegida ahora: la generación del hook descarta las demás.
    if (detalle === null || detalle.valores === null) return

    const valores = valoresDeFamiliaAFormulario(detalle.valores)
    actualizarFormulario((previo) => ({ ...previo, ...valores }))
  }

  function reintentarFamilia() {
    if (!formulario) return
    setErrorGuardado('')

    if (formulario.id !== null) {
      if (unionElegida === '') return
      setErrorDeUnion('')
      previsualizacionDeUnion.invalidar()
      void proponerUnion(formulario.id, unionElegida)
      return
    }

    if (formulario.idFamilia === '') return
    void cargarFamiliaDelAlta(formulario.idFamilia)
  }

  /** "+" del selector de familia. En un alta solo anota el nombre: el servidor crea la familia al guardar el artículo. En un
   * artículo existente la crea ya, con él como referencia. Resuelve el motivo del rechazo, o `''` si salió bien. */
  async function crearFamilia(nombre: string): Promise<string> {
    const idArticulo = formulario?.id ?? null
    if (idArticulo !== null) return crearFamiliaDelArticulo(idArticulo, nombre)

    invalidarEdicionEnCurso()
    soltarFamiliaDelFormulario()
    setErrorGuardado('')
    actualizarFormulario((previo) => ({ ...previo, idFamilia: '', nombreFamiliaNueva: nombre }))
    return ''
  }

  function quitarFamiliaNueva() {
    setErrorDeFamiliaNueva('')
    actualizarFormulario((previo) => ({ ...previo, nombreFamiliaNueva: '' }))
  }

  /** Crea una familia con el artículo existente como referencia (una sola escritura atómica en el servidor): el artículo
   * conserva sus valores y sus precios. Una escritura propia, con su ventana inerte y su token. Su reentrancia la guarda el
   * modal que la dispara (`AltaRapidaFamilia`); el ref solo le avisa a las demás escrituras que hay una en curso. */
  async function crearFamiliaDelArticulo(idArticulo: number, nombre: string): Promise<string> {
    escrituraEnCursoRef.current = true

    const token = invalidarEdicionEnCurso()
    setEscribiendoFamilia(true)
    setErrorGuardado('')
    setAvisoGuardado('')
    setErrorDeUnion('')
    let rechazo = ''
    let seguimiento: (() => void) | null = null

    try {
      const creada = await clienteDeFamilias.crear({ nombre, idArticuloReferencia: idArticulo, idsArticulos: [] })
      void familias.recargarOpciones()
      setPedidoDeRefresco((n) => n + 1)
      if (tokenEdicionRef.current === token) {
        soltarFamiliaDelFormulario()
        aplicarPertenencia(idArticulo, creada.idFamilia)
        void familias.cargarDetalle(creada.idFamilia)
        setAvisoGuardado(`Se creó la familia "${creada.nombre}": este artículo es su referencia y conserva sus valores y sus precios.`)
      }
    } catch (e) {
      if (tokenEdicionRef.current === token) {
        if (e instanceof ErrorApi && e.codigo === 'articulo_en_otra_familia') {
          // La pertenencia del artículo cambió desde que se abrió: lo que la pantalla muestra dejó de ser cierto.
          void familias.recargarOpciones()
          seguimiento = () => recargarArticuloDesactualizado(idArticulo)
        } else {
          // Un nombre en uso puede ser de una familia que la lista no tenía: se vuelve a leer para que la validación lo conozca.
          if (e instanceof ErrorApi && e.codigo === 'familia_nombre_duplicado') void familias.recargarOpciones()
          rechazo = mensajeDeFalloDeEscritura(e, 'crear la familia')
        }
      }
    } finally {
      escrituraEnCursoRef.current = false
      setEscribiendoFamilia(false)
    }

    seguimiento?.()
    return rechazo
  }

  /** "Confirmar y unir a la familia": suma al artículo a la familia elegida, que lo alinea con su referencia (reemplaza sus
   * trece valores compartidos y sus precios de lista fija). Después se vuelve a leer el artículo. */
  async function confirmarUnion() {
    if (escrituraEnCursoRef.current) return
    if (!formulario || formulario.id === null || unionElegida === '') return
    const idArticulo = formulario.id
    const idFamilia = unionElegida
    const nombreDelArticulo = formulario.nombre
    const nombreDeLaFamilia = familiaDelArticulo(idFamilia, familias.detalle).nombre
    escrituraEnCursoRef.current = true

    const token = invalidarEdicionEnCurso()
    setEscribiendoFamilia(true)
    setErrorGuardado('')
    setAvisoGuardado('')
    setErrorDeUnion('')
    let seguimiento: (() => void) | null = null

    try {
      await clienteDeFamilias.agregarArticulos(idFamilia, { idsArticulos: [idArticulo] })
      void familias.recargarOpciones()
      setPedidoDeRefresco((n) => n + 1)
      const aviso = `El artículo "${nombreDelArticulo}" se unió a la familia${nombreDeLaFamilia === null ? '' : ` "${nombreDeLaFamilia}"`}: tomó sus valores compartidos y sus precios.`
      seguimiento = () => void recargarTrasUnirse(idArticulo, aviso)
    } catch (e) {
      if (tokenEdicionRef.current === token) {
        if (e instanceof ErrorApi && e.codigo === 'articulo_en_otra_familia') {
          void familias.recargarOpciones()
          seguimiento = () => recargarArticuloDesactualizado(idArticulo)
        } else {
          // La previsualización es una foto: lo que el servidor rechaza ya no la respalda. Se vuelve a "Sin familia" con el motivo,
          // y si la familia dejó de admitir artículos se actualiza la lista que se ofrece.
          soltarFamiliaDelFormulario()
          setErrorDeUnion(mensajeDeFalloDeEscritura(e, 'unir el artículo a la familia'))
          if (e instanceof ErrorApi && (e.estado === 404 || e.codigo === 'familia_inactiva' || e.codigo === 'familia_sin_articulos')) {
            void familias.recargarOpciones()
          }
        }
      }
    } finally {
      // Sin gate de token: esta bandera es solo de esta escritura.
      escrituraEnCursoRef.current = false
      setEscribiendoFamilia(false)
    }

    seguimiento?.()
  }

  /** El artículo se unió a una familia: se lo vuelve a leer para que el formulario muestre sus valores alineados y su pertenencia. */
  async function recargarTrasUnirse(idArticulo: number, aviso: string) {
    if (destinoModalRef.current !== idArticulo) return
    await abrirEdicion(idArticulo)
    if (destinoModalRef.current === idArticulo) setAvisoGuardado(aviso)
  }

  // `idsEmpresas` es el único campo cuya REPRESENTACIÓN puede cambiar sin que haya un cambio real:
  // destildar y volver a tildar una empresa lo reordena (filter + append al final), así que dos
  // formularios con el mismo conjunto de empresas pueden serializar distinto — comparar ordenado
  // evita el falso positivo de "cambios sin guardar".
  function normalizarParaComparar(f: Formulario) {
    return { ...f, idsEmpresas: [...f.idsEmpresas].sort((a, b) => a - b) }
  }

  function haySinGuardar(): boolean {
    return (
      formulario !== null &&
      formularioOriginalRef.current !== null &&
      JSON.stringify(normalizarParaComparar(formulario)) !== JSON.stringify(normalizarParaComparar(formularioOriginalRef.current))
    )
  }

  // Recarga/cierre de PESTAÑA (no navegación SPA — esa la cubren `cerrarModal` y el efecto de apertura):
  // el listener se registra/desregistra según haya o no cambios sin guardar, nunca queda pegado.
  // `formulario` alcanza como dependencia: `formularioOriginalRef.current` siempre se asigna en el
  // mismo tick que `setFormulario` (nunca solo), así que `haySinGuardar()` ya lee el par correcto
  // en cada corrida de este efecto.
  useEffect(() => {
    if (!haySinGuardar()) return
    function alIntentarSalir(evento: BeforeUnloadEvent) {
      evento.preventDefault()
      evento.returnValue = ''
    }
    window.addEventListener('beforeunload', alIntentarSalir)
    return () => window.removeEventListener('beforeunload', alIntentarSalir)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [formulario])

  // Único punto de cierre: el botón "Cancelar" del formulario, el × del header, Escape y el click
  // en el backdrop de `Modal` llegan todos acá (nunca `history.back()` — una pestaña nueva no tiene
  // historial previo). `Modal` ya bloquea estos tres últimos mientras `ocupado`; el guard de acá
  // cubre además el botón "Cancelar" del propio formulario. Con cambios sin guardar se pregunta
  // ACÁ, antes de navegar: si cancela no se toca ni la URL ni el modal (los hijos conservan su
  // estado); si acepta, `descartarModal()` compromete el cierre antes del `navigate`, así el efecto
  // de apertura encuentra el destino nulo ya aplicado y no vuelve a preguntar.
  function cerrarModal() {
    if (ocupado) return
    if (haySinGuardar() && !confirm(MENSAJE_CONFIRMAR_DESCARTE)) return
    descartarModal()
    navigate('/articulos', { replace: true })
  }

  function irACrear() {
    if (ocupado) return
    navigate('/articulos/create')
  }

  async function eliminar(a: FilaDeGrillaDeArticulos) {
    if (ocupado) return
    if (!confirm(`¿Dar de baja el artículo "${a.nombre}"?`)) return

    setError('')
    setAviso('')
    setEliminando(true)
    try {
      await clienteDeArticulos.eliminar(a.id)
      setAviso(`Artículo "${a.nombre}" dado de baja.`)
      // Igual criterio que `guardar()`: la baja ya se confirmó acá, el refresco de la grilla es
      // un pedido aparte que `GrillaDeArticulos` resuelve (y reporta) por su cuenta.
      setPedidoDeRefresco((n) => n + 1)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo dar de baja.')
    } finally {
      setEliminando(false)
    }
  }

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <BotonIcono
        icono="agregar"
        ref={refBotonNuevo}
        className="text-nowrap"
        disabled={ocupado}
        onClick={irACrear}
      />
    </nav>
  )

  // Las acciones del formulario sobre su familia atan al artículo que se está mostrando: una que llegue tarde,
  // desde el editor de precios de uno que ya se cerró, no puede tocar al que se abrió después.
  const idDelFormulario = formulario?.id ?? null
  const union: EstadoDeUnionAFamilia = {
    idElegida: unionElegida,
    previsualizacion: previsualizacionDeUnion.previsualizacion,
    previsualizando: previsualizacionDeUnion.previsualizando,
    errorDePrevisualizacion: previsualizacionDeUnion.error,
    error: errorDeUnion,
    escribiendo: escribiendoFamilia,
  }
  const accionesDeFamilia: AccionesDeFamiliaDelFormulario = {
    elegir: elegirFamilia,
    confirmarUnion,
    crearFamilia,
    quitarFamiliaNueva,
    reintentar: reintentarFamilia,
    sacar: sacarDeLaFamilia,
    alSalirDeLaFamilia: (nombre) => alSalirDeLaFamiliaPorUnPrecio(idDelFormulario, nombre),
    alEscribirPrecio: () => {
      if (idDelFormulario !== null && destinoModalRef.current === idDelFormulario) descartarPropuestaDeUnion()
    },
    alCambiarLaFamilia: () => {
      if (idDelFormulario !== null) recargarArticuloDesactualizado(idDelFormulario)
    },
  }

  return (
    <div className="container-fluid py-4">
      <Box titulo="Artículos" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger">{error}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}
        {erroresCatalogosRequeridos.length > 0 && (
          <div className="alert alert-warning">
            {erroresCatalogosRequeridos.join(' ')} El guardado (alta o edición) de artículos va a quedar bloqueado
            hasta que se puedan cargar — recargá la página para reintentar.
          </div>
        )}
        {avisoListasPrecio && <div className="alert alert-warning">{avisoListasPrecio}</div>}

        <GrillaDeArticulos proveedores={proveedores} ocupado={ocupado} pedidoDeRefresco={pedidoDeRefresco} onEliminar={eliminar} />
      </Box>

      {destinoMostrado !== null && (
        <ModalDeArticulo
          clave={claveFormulario}
          formulario={formulario}
          cargandoDetalle={cargandoDetalle}
          errorDetalle={errorDetalle}
          guardando={guardando}
          ocupado={ocupado}
          avisoGuardado={avisoGuardado}
          errorGuardado={errorGuardado}
          bloqueadoPorCatalogos={erroresCatalogosRequeridos.length > 0}
          erroresCatalogosRequeridos={erroresCatalogosRequeridos}
          avisoListasPrecio={avisoListasPrecio}
          areas={areas}
          categorias={categorias}
          marcas={marcas}
          grupos={grupos}
          proveedores={proveedores}
          proveedoresTruncados={proveedoresTruncados}
          alicuotasIva={alicuotasIva}
          empresas={empresas}
          listasPrecio={listasPrecio}
          familia={familias}
          accionesDeFamilia={accionesDeFamilia}
          union={union}
          errorDeFamiliaNueva={errorDeFamiliaNueva}
          sinGuardar={haySinGuardar()}
          preguntaDeAlcanceAbierta={decisionDeAlcance !== null}
          focoDeReserva={refBotonNuevo}
          onCambio={setFormulario}
          actualizarFormulario={actualizarFormulario}
          onGuardar={pedirGuardado}
          onCerrar={cerrarModal}
          alDeEscribir={alDeEscribir}
          onCategoriaCreada={alCrearCategoria}
          onMarcaCreada={alCrearMarca}
          onGrupoCreada={alCrearGrupo}
          onProveedorCreado={alCrearProveedor}
        />
      )}

      {/* La pregunta de alcance es hermana del modal de artículo y no hija: React propaga los eventos por el
          árbol de componentes, y el Tab de este diálogo no tiene que pasar por la trampa de foco del de abajo. */}
      {decisionDeAlcance !== null && (
        <Modal titulo="Cambio en una familia" ocupado={ocupado} onCerrar={cancelarAlcance}>
          <PreguntaDeAlcance
            contexto={decisionDeAlcance.contexto}
            cambios={decisionDeAlcance.campos}
            ocupado={ocupado}
            onElegir={elegirAlcance}
            onCancelar={cancelarAlcance}
          />
        </Modal>
      )}
    </div>
  )
}
