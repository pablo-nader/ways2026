import { useCallback, useEffect, useRef, useState } from 'react'
import { NavigationType, useLocation, useNavigate, useNavigationType } from 'react-router'
import { clienteDeArticulos } from '../api/articulos'
import { clienteDeCatalogo, clienteDeCatalogosFiscales } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDePrecios } from '../api/precios'
import type {
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
import { aAlta, aEdicion, aFormulario, formularioVacio, type Formulario } from './articulos/FormularioArticulo'
import { GrillaDeArticulos } from './articulos/GrillaDeArticulos'
import { elegirAlicuotaPorDefecto, etiquetaDeProveedor, insertarOrdenadoPor, ordenarProveedoresPorEtiqueta } from './articulos/helpers'
import { desplazamientoHaciaLaAnterior, HISTORIAL_SIN_OBSERVAR, registrarEntrada } from './articulos/historialObservado'
import { ModalDeArticulo } from './articulos/ModalDeArticulo'
import { analizarRutaModal, type ModoModalDeArticulo } from './articulos/rutaModal'

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
  const ocupado = guardando || eliminando || escriturasHijas > 0

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

  async function abrirEdicion(idNumerico: number) {
    setErrorDetalle('')
    setCargandoDetalle(true)
    setFormulario(null)
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
      const cargado = aFormulario(detalle)
      setFormulario(cargado)
      formularioOriginalRef.current = cargado
      setGuardando(false)
      setAvisoGuardado('')
      setErrorGuardado('')
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

  async function guardar() {
    if (ocupado) return
    if (!formulario) return

    const token = invalidarEdicionEnCurso()
    setGuardando(true)
    setErrorGuardado('')
    setAvisoGuardado('')

    try {
      if (formulario.id === null) {
        const creado = await clienteDeArticulos.crear(aAlta(formulario))
        if (tokenEdicionRef.current === token) {
          const cargado = aFormulario(creado)
          setAvisoGuardado(`Artículo "${formulario.nombre}" creado con código interno ${creado.codigoInterno}.`)
          setFormulario(cargado)
          formularioOriginalRef.current = cargado
          // History replace (no push): /articulos/create nunca queda alcanzable con "atrás" una
          // vez que el alta se concretó — el modal sigue montado (misma entrada de ruta,
          // `/articulos/*`), así que el aviso y el foco sobreviven al cambio de URL.
          navigate(`/articulos/edit/${creado.id}`, { replace: true })
        }
      } else {
        const actualizado = await clienteDeArticulos.actualizar(formulario.id, aEdicion(formulario))
        if (tokenEdicionRef.current === token) {
          const cargado = aFormulario(actualizado)
          setAvisoGuardado(`Artículo "${formulario.nombre}" actualizado.`)
          setFormulario(cargado)
          formularioOriginalRef.current = cargado
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
        setErrorGuardado(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
      }
    } finally {
      if (tokenEdicionRef.current === token) setGuardando(false)
    }
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
      <button
        ref={refBotonNuevo}
        type="button"
        className="btn btn-sm btn-success rounded-0 text-nowrap"
        disabled={ocupado}
        onClick={irACrear}
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
          focoDeReserva={refBotonNuevo}
          onCambio={setFormulario}
          actualizarFormulario={actualizarFormulario}
          onGuardar={guardar}
          onCerrar={cerrarModal}
          alDeEscribir={alDeEscribir}
          onCategoriaCreada={alCrearCategoria}
          onMarcaCreada={alCrearMarca}
          onGrupoCreada={alCrearGrupo}
          onProveedorCreado={alCrearProveedor}
        />
      )}
    </div>
  )
}
