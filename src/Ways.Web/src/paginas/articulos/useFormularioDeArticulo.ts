import { useCallback, useEffect, useRef, useState } from 'react'
import type { RefObject } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import { ErrorApi } from '../../api/cliente'
import { clienteDeFamilias } from '../../api/familias'
import type { AlcanceDeFamilia, ArticuloListado } from '../../api/tipos'
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
  valoresDeFamiliaAFormulario,
  type AccionesDeFamiliaDelFormulario,
} from './familia'
import { aAlta, aEdicion, aFormulario, formularioVacio, type Formulario } from './FormularioArticulo'
import type { PropsDeModalDeArticulo } from './ModalDeArticulo'
import { useCatalogosDeArticulo } from './useCatalogosDeArticulo'
import { useFamiliasDelFormulario } from './useFamiliasDelFormulario'

export const MENSAJE_CONFIRMAR_DESCARTE = 'Hay cambios sin guardar en el artículo. ¿Descartarlos?'

/** La pregunta de alcance abierta al guardar la edición de un miembro de una familia (doc 10 §3): `contexto` es
 * con lo que se abre (la familia conocida o el texto del servidor) y `campos`, las etiquetas de los campos
 * compartidos que cambian. Se abre antes de escribir lo que pregunta; al elegir un alcance sigue abierta, con sus
 * respuestas inertes, mientras dura el PUT, y la respuesta del PUT la cierra mientras el modal siga siendo el que preguntó. */
export type DecisionDeAlcance = { contexto: string; campos: string[] }

/** Lo que un alta precarga: campos del formulario y, aparte, el código con el que el proveedor habitual conoce al
 * artículo (que no es un campo del artículo y por eso viaja por su cuenta). */
export type PrecargaDeAlta = {
  campos?: Partial<Formulario>
  /** `null`/ausente: el formulario no ofrece el campo del código del proveedor. */
  codigoProveedor?: string | null
}

type Opciones = {
  /** El alta se concretó y su respuesta sigue vigente. `codigoProveedor` es el que viajó (o `null`). */
  alCrear: (creado: ArticuloListado, codigoProveedor: string | null) => void
  /** Un guardado (alta o edición) tuvo éxito, aunque el modal ya muestre otro artículo. */
  alGuardar?: () => void
  /** Abre ya con un alta precargada, sin pasar por un primer render sin formulario (quien lo aloja en un modal no
   * quiere mostrar "Cargando artículo…" para algo que ya tiene). Solo se lee al montar. */
  precargaInicial?: PrecargaDeAlta
  /** Presente solo cuando el formulario vive dentro de una compra: ahí no se puede recargar la página para reintentar
   * y el código del proveedor es el de `idProveedor`, el proveedor de la compra. */
  compra?: { idProveedor: number | null }
}

/** El código que viaja con un alta: solo si el formulario lo ofrece, hay proveedor habitual y se tipeó algo. Con
 * `idProveedorDeLaCompra` (el alta desde una compra), solo si el proveedor habitual es el de la compra: el código es de
 * ese proveedor y no se le puede asociar a otro. */
export function codigoDeProveedorDeAlta(
  formulario: Formulario,
  codigoProveedor: string | null,
  idProveedorDeLaCompra?: number | null,
): string | null {
  if (codigoProveedor === null || formulario.idProveedorHabitual === '') return null
  if (idProveedorDeLaCompra !== undefined && formulario.idProveedorHabitual !== idProveedorDeLaCompra) return null
  const limpio = codigoProveedor.trim()
  return limpio === '' ? null : limpio
}

/**
 * Estado, validación y guardado del formulario de artículo (alta y edición), sin saber de dónde se abre: la
 * pantalla de artículos lo gobierna con la URL y la compra lo abre en un modal propio. Quien lo usa decide
 * cuándo abrir (`abrirCrear`, `abrirEdicion`) y cerrar (`descartar`), y qué pasa tras un alta (`alCrear`).
 *
 * Todas las escrituras comparten una única guarda de reentrancia (`escrituraEnCursoRef`) y un token
 * (`tokenEdicionRef`) que abrir, cerrar y guardar invalidan: la respuesta de una escritura superada no se aplica.
 */
export function useFormularioDeArticulo({ alCrear, alGuardar, precargaInicial, compra }: Opciones) {
  const catalogos = useCatalogosDeArticulo({ dentroDeUnaCompra: compra !== undefined })
  const { areaPorDefecto, alicuotaPorDefecto } = catalogos

  // El área y la alícuota por defecto de una alta precargada las completa el efecto de más abajo cuando llegan los catálogos.
  const [inicial] = useState<Formulario | null>(() =>
    precargaInicial === undefined ? null : { ...formularioVacio(), ...precargaInicial.campos },
  )
  const [formulario, setFormulario] = useState<Formulario | null>(inicial)
  const [claveFormulario, setClaveFormulario] = useState<number | 'nuevo'>('nuevo')
  const [cargandoDetalle, setCargandoDetalle] = useState(false)
  const [errorDetalle, setErrorDetalle] = useState('')
  const [guardando, setGuardando] = useState(false)
  const [avisoGuardado, setAvisoGuardado] = useState('')
  const [errorGuardado, setErrorGuardado] = useState('')
  const [escriturasHijas, setEscriturasHijas] = useState(0)
  // `null`: el formulario no ofrece el código del proveedor (siempre en la pantalla de artículos).
  const [codigoProveedor, setCodigoProveedor] = useState<string | null>(precargaInicial?.codigoProveedor ?? null)
  // Familias (doc 10 §3): las que se ofrecen en un alta y el detalle de la familia del formulario.
  const familias = useFamiliasDelFormulario()
  const [decisionDeAlcance, setDecisionDeAlcance] = useState<DecisionDeAlcance | null>(null)
  const [saliendoDeFamilia, setSaliendoDeFamilia] = useState(false)
  // Única guarda de reentrancia de `guardar` y de `sacarDeLaFamilia` (react-async-state regla 11): es un `ref` porque el
  // estado (`guardando`, `saliendoDeFamilia`) recién cambia en el próximo render, después de los dos clics de un mismo tick.
  const escrituraEnCursoRef = useRef(false)
  const tokenEdicionRef = useRef(0)
  // Snapshot del formulario tal como quedó cargado/guardado por última vez — la base contra la que
  // se compara para saber si hay cambios sin guardar al intentar cerrar.
  const formularioOriginalRef = useRef<Formulario | null>(inicial)
  // Lo mismo para el código del proveedor, que no es un campo del formulario.
  const codigoOriginalRef = useRef<string | null>(precargaInicial?.codigoProveedor ?? null)
  // El artículo que el modal muestra ahora: `'nuevo'` (alta), el id de la edición en curso o `null` (nada). Lo usan
  // las acciones que pueden llegar tarde (el editor de precios de uno que ya se cerró) para no tocar a otro.
  const articuloEnModalRef = useRef<number | 'nuevo' | null>(inicial === null ? null : 'nuevo')
  const ocupado = guardando || escriturasHijas > 0 || saliendoDeFamilia

  // Token del fetch de edición en curso: protege contra la staleness del fetch de detalle (abrir
  // otra edición, o cerrar, mientras el detalle anterior sigue en vuelo) y contra que la propia
  // respuesta del guardado se aplique si mientras tanto se invalidó. El "supersede" de una edición
  // por otra acción durante un guardado ya no depende del token — mientras `ocupado` es true, el
  // modal queda inerte (no se puede cerrar) y Nuevo/Editar/Baja quedan deshabilitados.
  function invalidarEdicionEnCurso(): number {
    return (tokenEdicionRef.current += 1)
  }

  // Cuenta escrituras hijas en vuelo (códigos de barra, precios): mientras haya al menos una,
  // `ocupado` se mantiene true para que no se pueda borrar o cambiar de artículo en medio de un POST
  // de un componente hijo.
  const alDeEscribir = useCallback((enCurso: boolean) => {
    setEscriturasHijas((n) => (enCurso ? n + 1 : Math.max(0, n - 1)))
  }, [])

  function actualizarFormulario(actualizar: (previo: Formulario) => Formulario) {
    setFormulario((prev) => (prev ? actualizar(prev) : prev))
  }

  /** `avisoInicial` es el error que el formulario recién cargado muestra: lo usa la recarga que sigue a un
   * `familia_cambio`, que de otro modo perdería su explicación al limpiar los avisos. */
  async function abrirEdicion(idNumerico: number, avisoInicial = '') {
    setErrorDetalle('')
    setCargandoDetalle(true)
    setFormulario(null)
    setCodigoProveedor(null)
    setDecisionDeAlcance(null)
    familias.descartarDetalle()
    const token = invalidarEdicionEnCurso()
    articuloEnModalRef.current = idNumerico
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

  /** Abre un alta en blanco, con el área y la alícuota por defecto y lo que `precarga` traiga. Lo precargado es parte
   * de lo "original": no cuenta como un cambio sin guardar. */
  function abrirCrear(precarga: PrecargaDeAlta = {}) {
    invalidarEdicionEnCurso()
    familias.descartarDetalle()
    setDecisionDeAlcance(null)
    setGuardando(false)
    setErrorDetalle('')
    setAvisoGuardado('')
    setErrorGuardado('')
    setCargandoDetalle(false)
    const nuevo = { ...formularioVacio(), idArea: areaPorDefecto, idAlicuotaIva: alicuotaPorDefecto, ...precarga.campos }
    articuloEnModalRef.current = 'nuevo'
    setCodigoProveedor(precarga.codigoProveedor ?? null)
    codigoOriginalRef.current = precarga.codigoProveedor ?? null
    setFormulario(nuevo)
    formularioOriginalRef.current = nuevo
    setClaveFormulario('nuevo')
  }

  /** Una edición que no se puede abrir (el id de la URL no es un número): no hay formulario, solo el motivo. */
  function rechazarEdicion(mensaje: string) {
    invalidarEdicionEnCurso()
    setDecisionDeAlcance(null)
    setFormulario(null)
    formularioOriginalRef.current = null
    articuloEnModalRef.current = null
    setErrorDetalle(mensaje)
    setCargandoDetalle(false)
  }

  // Vuelve al estado "sin modal": limpia todo el estado del formulario para no arrastrar restos a la próxima
  // apertura e invalida lo que haya en vuelo.
  function descartar() {
    invalidarEdicionEnCurso()
    articuloEnModalRef.current = null
    familias.descartarDetalle()
    setDecisionDeAlcance(null)
    setGuardando(false)
    setFormulario(null)
    setCodigoProveedor(null)
    formularioOriginalRef.current = null
    setErrorDetalle('')
    setAvisoGuardado('')
    setErrorGuardado('')
    setCargandoDetalle(false)
  }

  // Los defaults de Área/Alícuota de IVA de un alta se calculan al abrirla, pero esos catálogos pueden llegar
  // después — una alta abierta antes de que resuelvan dejaría ambos campos en '' para siempre. Este efecto completa
  // esos dos campos SOLO si siguen en '' en el momento en que el catálogo respectivo llega — nunca pisa una
  // elección ya hecha por el usuario — y aplica a `formularioOriginalRef` SOLO los campos que completó, para que el
  // auto-completado no dispare un falso "hay cambios sin guardar" sin convertir en "guardado" lo que el usuario ya
  // hubiera tipeado en otros campos antes de que llegaran.
  const enAlta = formulario !== null && formulario.id === null
  useEffect(() => {
    if (!enAlta || formulario === null) return
    const completados: Partial<Pick<Formulario, 'idArea' | 'idAlicuotaIva'>> = {}
    if (formulario.idArea === '' && areaPorDefecto !== '') completados.idArea = areaPorDefecto
    if (formulario.idAlicuotaIva === '' && alicuotaPorDefecto !== '') completados.idAlicuotaIva = alicuotaPorDefecto
    if (Object.keys(completados).length === 0) return
    setFormulario({ ...formulario, ...completados })
    if (formularioOriginalRef.current) formularioOriginalRef.current = { ...formularioOriginalRef.current, ...completados }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enAlta, areaPorDefecto, alicuotaPorDefecto])

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
    // Lo que sigue a un rechazo que hay que resolver DESPUÉS de soltar el guardado (recargar el artículo o la
    // familia): ejecutado dentro del `catch`, `abrirEdicion` invalidaría el token y el `finally` dejaría el
    // formulario ocupado para siempre.
    let seguimiento: (() => void) | null = null

    try {
      if (formulario.id === null) {
        const codigoEnviado = codigoDeProveedorDeAlta(formulario, codigoProveedor, compra?.idProveedor)
        const alta = aAlta(formulario)
        const creado = await clienteDeArticulos.crear(codigoProveedor === null ? alta : { ...alta, codigoProveedor: codigoEnviado })
        // La familia tiene un miembro más: las que se ofrecen en un alta se vuelven a leer, también si el modal ya cambió de artículo.
        if (creado.idFamilia !== null) void familias.recargarOpciones()
        if (tokenEdicionRef.current === token) {
          const cargado = aFormulario(creado)
          setAvisoGuardado(
            mensajeDeAlta(formulario.nombre, creado.codigoInterno, familiaDelArticulo(creado.idFamilia ?? '', familias.detalle)),
          )
          setFormulario(cargado)
          formularioOriginalRef.current = cargado
          articuloEnModalRef.current = creado.id
          // El rótulo de la familia cuenta al artículo recién nacido: se vuelve a leer su detalle.
          if (creado.idFamilia !== null) void familias.cargarDetalle(creado.idFamilia)
          alCrear(creado, codigoEnviado)
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
        }
      }

      // El refresco de la grilla no pertenece al token de edición: la fila afectada debe quedar
      // al día sin importar si el formulario abierto ahora es otro. El guardado ya tuvo éxito
      // acá — `alGuardar` solo PIDE el refresco, quien lo usa es dueño de ejecutarlo y de reportar su propio
      // fallo (react-async-state regla 6/14: el guardado ya confirmado nunca se reporta como fallido por un
      // refresco de vista ajeno).
      alGuardar?.()
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
    if (articuloEnModalRef.current !== idArticulo) return
    void abrirEdicion(idArticulo, aviso)
  }

  /** El artículo ya no es miembro: el formulario y su base de comparación dejan de tener familia, así que sacarlo
   * no cuenta como un cambio sin guardar. */
  function aplicarSalidaDeFamilia(idArticulo: number) {
    actualizarFormulario((previo) => (previo.id === idArticulo ? { ...previo, idFamilia: '' } : previo))
    if (formularioOriginalRef.current?.id === idArticulo) {
      formularioOriginalRef.current = { ...formularioOriginalRef.current, idFamilia: '' }
    }
  }

  /** El editor de precios escribió con "solo este": el precio quedó únicamente en este artículo, que salió. Las familias
   * que se ofrecen en un alta se vuelven a leer aunque el modal ya muestre otro artículo: la salida ocurrió en el servidor. */
  function alSalirDeLaFamiliaPorUnPrecio(idArticulo: number | null, nombre: string | null) {
    void familias.recargarOpciones()
    if (idArticulo === null || articuloEnModalRef.current !== idArticulo) return
    aplicarSalidaDeFamilia(idArticulo)
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
        aplicarSalidaDeFamilia(idArticulo)
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

  /** Elegir una familia en un alta: el artículo toma sus quince campos compartidos, que quedan bloqueados. Dejarla
   * en "Sin familia" los desbloquea y conserva lo que tenían. */
  function elegirFamilia(idFamilia: number | '') {
    // Elegir otra familia supera todo lo que estaba en vuelo sobre la anterior, incluido el seguimiento de un
    // guardado rechazado que todavía está releyendo su familia.
    invalidarEdicionEnCurso()
    setErrorGuardado('')
    familias.descartarDetalle()
    actualizarFormulario((previo) => ({ ...previo, idFamilia }))
    if (idFamilia !== '') void cargarFamiliaDelAlta(idFamilia)
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
    if (!formulario || formulario.id !== null || formulario.idFamilia === '') return
    setErrorGuardado('')
    void cargarFamiliaDelAlta(formulario.idFamilia)
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
      (JSON.stringify(normalizarParaComparar(formulario)) !== JSON.stringify(normalizarParaComparar(formularioOriginalRef.current)) ||
        codigoProveedor !== codigoOriginalRef.current)
    )
  }

  // Recarga/cierre de PESTAÑA (no navegación SPA): el listener se registra/desregistra según haya o no cambios sin
  // guardar, nunca queda pegado. Depende de `formulario` y de `codigoProveedor`, los dos estados que `haySinGuardar()`
  // compara contra su original: `formularioOriginalRef.current` siempre se asigna en el mismo tick que `setFormulario`
  // (nunca solo), así que el par que lee en cada corrida es el correcto.
  useEffect(() => {
    if (!haySinGuardar()) return
    function alIntentarSalir(evento: BeforeUnloadEvent) {
      evento.preventDefault()
      evento.returnValue = ''
    }
    window.addEventListener('beforeunload', alIntentarSalir)
    return () => window.removeEventListener('beforeunload', alIntentarSalir)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [formulario, codigoProveedor])

  // Las acciones del formulario sobre su familia atan al artículo que se está mostrando: una que llegue tarde,
  // desde el editor de precios de uno que ya se cerró, no puede tocar al que se abrió después.
  const idDelFormulario = formulario?.id ?? null
  const accionesDeFamilia: AccionesDeFamiliaDelFormulario = {
    elegir: elegirFamilia,
    reintentar: reintentarFamilia,
    sacar: sacarDeLaFamilia,
    alSalirDeLaFamilia: (nombre) => alSalirDeLaFamiliaPorUnPrecio(idDelFormulario, nombre),
    alCambiarLaFamilia: () => {
      if (idDelFormulario !== null) recargarArticuloDesactualizado(idDelFormulario)
    },
  }

  // El código de la fila es del proveedor de la compra: con otro proveedor habitual no se envía (la fila lo conserva y
  // el servidor lo asocia al proveedor de la compra al confirmar).
  const codigoEsDeOtroProveedor =
    compra !== undefined &&
    formulario !== null &&
    formulario.idProveedorHabitual !== '' &&
    formulario.idProveedorHabitual !== compra.idProveedor

  /** Las props del modal de artículo; quien lo muestra aporta lo que depende de dónde vive. */
  function propsDelModal(propias: {
    ocupado: boolean
    focoDeReserva: RefObject<HTMLElement | null>
    onCerrar: () => void
  }): PropsDeModalDeArticulo {
    return {
      ...propias,
      clave: claveFormulario,
      formulario,
      cargandoDetalle,
      errorDetalle,
      guardando,
      avisoGuardado,
      errorGuardado,
      bloqueadoPorCatalogos: catalogos.erroresCatalogosRequeridos.length > 0,
      erroresCatalogosRequeridos: catalogos.erroresCatalogosRequeridos,
      avisoListasPrecio: catalogos.avisoListasPrecio,
      consejoDeReintento: catalogos.consejoDeReintento,
      areas: catalogos.areas,
      categorias: catalogos.categorias,
      marcas: catalogos.marcas,
      grupos: catalogos.grupos,
      proveedores: catalogos.proveedores,
      proveedoresTruncados: catalogos.proveedoresTruncados,
      alicuotasIva: catalogos.alicuotasIva,
      empresas: catalogos.empresas,
      listasPrecio: catalogos.listasPrecio,
      familia: familias,
      accionesDeFamilia,
      preguntaDeAlcanceAbierta: decisionDeAlcance !== null,
      codigoProveedor:
        codigoProveedor === null
          ? undefined
          : { valor: codigoProveedor, onCambio: setCodigoProveedor, deOtroProveedor: codigoEsDeOtroProveedor },
      onCambio: setFormulario,
      actualizarFormulario,
      onGuardar: pedirGuardado,
      alDeEscribir,
      onCategoriaCreada: catalogos.alCrearCategoria,
      onMarcaCreada: catalogos.alCrearMarca,
      onGrupoCreada: catalogos.alCrearGrupo,
      onProveedorCreado: catalogos.alCrearProveedor,
    }
  }

  return {
    catalogos,
    formulario,
    ocupado,
    decisionDeAlcance,
    abrirCrear,
    abrirEdicion,
    rechazarEdicion,
    descartar,
    haySinGuardar,
    cancelarAlcance,
    elegirAlcance,
    propsDelModal,
  }
}
