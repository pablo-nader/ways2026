import { useContext, useEffect, useRef, useState } from 'react'
import type { ChangeEvent, MouseEvent } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import {
  ACEPTAR_IMAGEN,
  copiaDeFalloDeTienda,
  debeReleerTrasFalloDeTienda,
  cambiarDisponibilidad,
  quitarImagenDeArticulo,
  subirImagenDeArticulo,
  urlDeImagenDeArticuloAdmin,
  validarImagenDeArticulo,
} from '../../api/catalogoWeb'
import { puedeGestionarCatalogos } from '../../api/tipos'
import { AuthContext } from '../../auth/AuthContext'
import { ConfirmacionDeBaja } from '../../componentes/ConfirmacionDeBaja'
import { TituloDeSeccion } from '../../componentes/TituloDeSeccion'

type Props = {
  idArticulo: number
  /** Disponibilidad con la que se cargó el artículo; después la sección lleva la suya. */
  inicial: { disponibleVenta: boolean; disponibleOnline: boolean; tieneImagen: boolean }
  bloqueadoPorPadre: boolean
  alDeEscribir: (enCurso: boolean) => void
  /** La disponibilidad o la imagen del artículo cambió, o una escritura falló sin decir qué quedó escrito: la grilla muestra su estado y tiene que refrescarse. */
  alCambiar: () => void
}

/**
 * Disponibilidad del artículo (a la venta y en la tienda online) y su imagen, bajo el título "Venta y tienda online" porque
 * el formulario ya tiene una sección "Disponibilidad" que es la de las empresas. Solo la ve quien puede administrar el
 * catálogo (el servidor exige la misma policy en las tres escrituras; esto solo evita ofrecer controles que van a dar
 * 403), y solo con el artículo ya guardado. Cada control escribe por su cuenta, nunca con el "Guardar" del formulario.
 */
export function SeccionDeDisponibilidad(props: Props) {
  const sesion = useContext(AuthContext)
  const usuario = sesion?.usuario ?? null

  if (usuario === null || !puedeGestionarCatalogos(usuario.rolId)) return null

  return <ContenidoDeLaDisponibilidad {...props} />
}

/**
 * Las tres escrituras (disponibilidad, subir y quitar la imagen) comparten una única guarda de reentrancia
 * (`ocupadoRef`) y, mientras dura una, `alDeEscribir` deja inerte todo el modal. Por eso la respuesta de una
 * escritura no lleva token propio (`react-async-state` regla 2): la superficie no puede reemplazarse ni cerrarse
 * mientras está en vuelo, lo que escribe es estado de esta instancia (que muere con el artículo que la montó) y no
 * navega. La relectura tras un fallo incierto corre dentro de esa misma ventana inerte y es la única lectura.
 *
 * La puerta de confirmación de "Quitar imagen" es inerte a nivel del modal entero: mientras está abierta se avisa a
 * `alDeEscribir` (igual que durante una escritura) y se suelta al cerrarla o desmontar la sección, así que el resto del
 * formulario queda deshabilitado y no se puede abrir ningún modal apilado (alta rápida) sobre ella. La puerta es un `Modal`
 * apilado sobre el del artículo: Escape y Tab los atiende solo el de arriba, y su rechazo se muestra adentro.
 */
function ContenidoDeLaDisponibilidad({ idArticulo, inicial, bloqueadoPorPadre, alDeEscribir, alCambiar }: Props) {
  const [disponibleVenta, setDisponibleVenta] = useState(inicial.disponibleVenta)
  const [disponibleOnline, setDisponibleOnline] = useState(inicial.disponibleOnline)
  const [tieneImagen, setTieneImagen] = useState(inicial.tieneImagen)
  const [version, setVersion] = useState<number | null>(null)
  // Cada fuente tiene su propio slot (regla 14); `limpiarMensajes` los borra al empezar cualquier acción (regla 16).
  const [errorDeDisponibilidad, setErrorDeDisponibilidad] = useState<{ texto: string; interruptor: 'venta' | 'online' } | null>(null)
  const [errorDeImagen, setErrorDeImagen] = useState('')
  const [aviso, setAviso] = useState('')
  const [ocupado, setOcupado] = useState(false)
  const [escribiendo, setEscribiendo] = useState<'venta' | 'online' | 'imagen' | null>(null)
  const [puertaAbierta, setPuertaAbierta] = useState(false)
  const [disparador, setDisparador] = useState<HTMLElement | null>(null)
  /** URL que el navegador no pudo mostrar. Va por URL y no por booleano: subir otra imagen cambia la versión y con
   * ella la URL, así que la vista previa vuelve sola. */
  const [urlRota, setUrlRota] = useState<string | null>(null)
  /** Espejo SÍNCRONO de `ocupado` y única guarda de reentrancia: dos disparos del mismo tick leen el mismo render. */
  const ocupadoRef = useRef(false)
  /** Adónde vuelve el foco si "Quitar imagen" desaparece con la confirmación: la sección vive en el portal del
   * `Modal`, donde la puerta no encuentra una `Box` a la que subir. */
  const tituloRef = useRef<HTMLHeadingElement>(null)
  const interruptorDeVentaRef = useRef<HTMLInputElement>(null)
  const interruptorOnlineRef = useRef<HTMLInputElement>(null)
  /** Interruptor al que le toca recuperar el foco cuando termine de habilitarse: deshabilitado durante su escritura, el
   * navegador lo pasa a `<body>` (regla 12 de `react-async-state`). Lo asigna `escribir` al empezar, nunca un efecto. */
  const focoPendienteRef = useRef<'venta' | 'online' | null>(null)

  // El contador del padre se mantiene balanceado por construcción: el cleanup corre al cerrar la puerta y al desmontar.
  useEffect(() => {
    if (!puertaAbierta) return

    alDeEscribir(true)

    return () => alDeEscribir(false)
  }, [puertaAbierta, alDeEscribir])

  const bloqueado = ocupado || bloqueadoPorPadre || puertaAbierta
  const urlDeLaImagen = urlDeImagenDeArticuloAdmin(idArticulo, version)

  // Habilitado de nuevo el interruptor que escribió, el foco vuelve a él (un `disabled` en vuelo lo había perdido).
  useEffect(() => {
    if (bloqueado || focoPendienteRef.current === null) return

    const destino = focoPendienteRef.current === 'venta' ? interruptorDeVentaRef.current : interruptorOnlineRef.current
    focoPendienteRef.current = null
    destino?.focus()
  }, [bloqueado])

  function limpiarMensajes() {
    setErrorDeDisponibilidad(null)
    setErrorDeImagen('')
    setAviso('')
  }

  /**
   * Tras un fallo que no dice qué quedó escrito se vuelve a leer el artículo para mostrar el estado real. Si hay
   * imagen, la versión pasa a ser la hora de la relectura: la subida pudo haber reemplazado la imagen sin que lo
   * sepamos, y una URL nueva obliga al navegador a pedirla de nuevo. `urlRota` va por URL, así que no hace falta
   * borrarla: la URL nueva ya no coincide con la que falló.
   */
  async function releer() {
    try {
      const leido = await clienteDeArticulos.obtener(idArticulo)
      setDisponibleVenta(leido.disponibleVenta)
      setDisponibleOnline(leido.disponibleOnline)
      setTieneImagen(leido.tieneImagen)
      setVersion(leido.tieneImagen ? Date.now() : null)
    } catch {
      // El mensaje del fallo ya manda a verificar el estado del artículo.
    }
  }

  async function escribir<T>(
    cual: 'venta' | 'online' | 'imagen',
    escritura: () => Promise<T>,
    alTenerExito: (resultado: T) => void,
    encabezadoDelFallo: string,
    ponerError: (texto: string) => void,
  ) {
    if (ocupadoRef.current) return
    ocupadoRef.current = true
    if (cual !== 'imagen') focoPendienteRef.current = cual
    setOcupado(true)
    setEscribiendo(cual)
    limpiarMensajes()
    alDeEscribir(true)
    try {
      let resultado: T
      try {
        resultado = await escritura()
      } catch (e) {
        ponerError(copiaDeFalloDeTienda(e, encabezadoDelFallo))
        if (debeReleerTrasFalloDeTienda(e)) {
          await releer()
          // El fallo no dijo qué quedó escrito: la grilla también puede haber cambiado, haya salido bien la relectura o no.
          alCambiar()
        }

        return
      }

      alTenerExito(resultado)
      alCambiar()
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
      setEscribiendo(null)
      alDeEscribir(false)
    }
  }

  /** Un único PUT lleva los dos valores: el que se cambió y el otro tal como está en pantalla. */
  async function cambiarLaVenta(quiere: boolean) {
    await escribir(
      'venta',
      () => cambiarDisponibilidad(idArticulo, { disponibleVenta: quiere, disponibleOnline }),
      (guardada) => {
        setDisponibleVenta(guardada.disponibleVenta)
        setDisponibleOnline(guardada.disponibleOnline)
        setAviso(
          guardada.disponibleVenta ? 'El artículo quedó disponible a la venta.' : 'El artículo quedó fuera de la venta.',
        )
      },
      quiere ? 'No se pudo habilitar la venta del artículo.' : 'No se pudo sacar el artículo de la venta.',
      (texto) => setErrorDeDisponibilidad({ texto, interruptor: 'venta' }),
    )
  }

  async function cambiarLoOnline(quiere: boolean) {
    await escribir(
      'online',
      () => cambiarDisponibilidad(idArticulo, { disponibleVenta, disponibleOnline: quiere }),
      (guardada) => {
        setDisponibleVenta(guardada.disponibleVenta)
        setDisponibleOnline(guardada.disponibleOnline)
        setAviso(
          guardada.disponibleOnline
            ? 'El artículo quedó disponible en la tienda online.'
            : 'El artículo quedó fuera de la tienda online.',
        )
      },
      quiere ? 'No se pudo mostrar el artículo en la tienda online.' : 'No se pudo sacar el artículo de la tienda online.',
      (texto) => setErrorDeDisponibilidad({ texto, interruptor: 'online' }),
    )
  }

  async function elegirImagen(evento: ChangeEvent<HTMLInputElement>) {
    const entrada = evento.currentTarget
    const archivo = entrada.files?.[0]
    // Se vacía el control para que elegir de nuevo el mismo archivo vuelva a disparar el cambio.
    entrada.value = ''
    if (!archivo) return

    limpiarMensajes()
    const rechazo = validarImagenDeArticulo(archivo)
    if (rechazo) {
      setErrorDeImagen(rechazo)

      return
    }

    await escribir(
      'imagen',
      () => subirImagenDeArticulo(idArticulo, archivo),
      (guardada) => {
        setTieneImagen(guardada.tieneImagen)
        setVersion(guardada.imagenVersion)
        setAviso('Se subió la imagen.')
      },
      'No se pudo subir la imagen.',
      setErrorDeImagen,
    )
  }

  async function confirmarQuitarImagen() {
    await escribir(
      'imagen',
      () => quitarImagenDeArticulo(idArticulo),
      () => {
        setPuertaAbierta(false)
        setDisparador(null)
        setTieneImagen(false)
        setVersion(null)
        setAviso('Se quitó la imagen.')
      },
      'No se pudo quitar la imagen.',
      setErrorDeImagen,
    )
  }

  function abrirPuerta(evento: MouseEvent<HTMLButtonElement>) {
    limpiarMensajes()
    setDisparador(evento.currentTarget)
    setPuertaAbierta(true)
  }

  function cerrarPuerta() {
    limpiarMensajes()
    setDisparador(null)
    setPuertaAbierta(false)
  }

  return (
    <section aria-labelledby="art-disponibilidad-titulo">
      <TituloDeSeccion id="art-disponibilidad-titulo" ref={tituloRef} tabIndex={-1}>
        Venta y tienda online
      </TituloDeSeccion>

      <div className="form-check form-switch mt-2">
        <input
          ref={interruptorDeVentaRef}
          id="art-disponible-venta"
          type="checkbox"
          role="switch"
          className="form-check-input"
          checked={disponibleVenta}
          onChange={(e) => void cambiarLaVenta(e.target.checked)}
          disabled={bloqueado}
          aria-describedby={
            errorDeDisponibilidad?.interruptor === 'venta'
              ? 'art-disponible-venta-ayuda art-disponibilidad-error'
              : 'art-disponible-venta-ayuda'
          }
        />
        <label className="form-check-label" htmlFor="art-disponible-venta">
          Disponible a la venta
        </label>
      </div>
      <div id="art-disponible-venta-ayuda" className="form-text">
        Se guarda al instante, sin pasar por "Guardar". Desmarcalo para un artículo de uso interno: se compra y se
        stockea, pero no se vende.
        {escribiendo === 'venta' && ' Guardando…'}
      </div>

      <div className="form-check form-switch mt-2">
        <input
          ref={interruptorOnlineRef}
          id="art-disponible-online"
          type="checkbox"
          role="switch"
          className="form-check-input"
          checked={disponibleOnline}
          onChange={(e) => void cambiarLoOnline(e.target.checked)}
          disabled={bloqueado}
          aria-describedby={
            errorDeDisponibilidad?.interruptor === 'online'
              ? 'art-disponible-online-ayuda art-disponibilidad-error'
              : 'art-disponible-online-ayuda'
          }
        />
        <label className="form-check-label" htmlFor="art-disponible-online">
          Disponible online
        </label>
      </div>
      <div id="art-disponible-online-ayuda" className="form-text">
        Desmarcalo para que no aparezca en la tienda online. Para verse en la tienda también tiene que estar disponible a la
        venta, activo y tener precio en la lista de la vitrina.
        {escribiendo === 'online' && ' Guardando…'}
      </div>
      {errorDeDisponibilidad && (
        <div id="art-disponibilidad-error" className="text-danger small mt-1" role="alert">
          {errorDeDisponibilidad.texto}
        </div>
      )}

      <div className="mt-3">
        {tieneImagen ? (
          urlRota === urlDeLaImagen ? (
            <p className="text-muted" role="status">
              No se pudo mostrar la imagen.
            </p>
          ) : (
            <img
              src={urlDeLaImagen}
              alt="Imagen del artículo"
              className="d-block mb-3 border"
              style={{ maxWidth: 240, maxHeight: 160 }}
              onError={() => setUrlRota(urlDeLaImagen)}
            />
          )
        ) : (
          <p className="text-muted">Todavía no hay una imagen cargada.</p>
        )}

        <label className="form-label" htmlFor="art-imagen">
          {tieneImagen ? 'Reemplazar la imagen' : 'Subir una imagen'}
        </label>
        <input
          id="art-imagen"
          type="file"
          className="form-control mb-1"
          style={{ maxWidth: 420 }}
          accept={ACEPTAR_IMAGEN}
          onChange={(e) => void elegirImagen(e)}
          disabled={bloqueado}
          aria-describedby={errorDeImagen ? 'art-imagen-ayuda art-imagen-error' : 'art-imagen-ayuda'}
          aria-invalid={errorDeImagen !== ''}
        />
        <div id="art-imagen-ayuda" className="form-text">
          PNG, JPG o WebP, hasta 1 MB. Al elegir el archivo se sube y reemplaza la imagen actual.
          {escribiendo === 'imagen' && ' Subiendo…'}
        </div>
        {errorDeImagen && !puertaAbierta && (
          <div id="art-imagen-error" className="text-danger small mt-1" role="alert">
            {errorDeImagen}
          </div>
        )}

        {tieneImagen && (
          <button type="button" className="btn btn-outline-danger btn-sm mt-3" onClick={abrirPuerta} disabled={bloqueado}>
            Quitar imagen
          </button>
        )}
      </div>

      {puertaAbierta && (
        <ConfirmacionDeBaja
          titulo="la imagen del artículo"
          pregunta="Quitar"
          nota="Podés volver a subir una cuando quieras."
          etiquetaConfirmar="Confirmar quitar imagen"
          etiquetaEnCurso="Quitando…"
          ocupado={ocupado}
          disparador={disparador}
          regreso={tituloRef}
          error={errorDeImagen}
          onConfirmar={() => void confirmarQuitarImagen()}
          onCancelar={cerrarPuerta}
        />
      )}

      {aviso && (
        <div className="text-success small mt-2" role="status">
          {aviso}
        </div>
      )}
    </section>
  )
}
