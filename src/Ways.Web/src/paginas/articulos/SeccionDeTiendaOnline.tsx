import { useContext, useEffect, useRef, useState } from 'react'
import type { ChangeEvent, MouseEvent } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import {
  ACEPTAR_IMAGEN,
  copiaDeFalloDeTienda,
  debeReleerTrasFalloDeTienda,
  publicarEnLaWeb,
  quitarImagenDeArticulo,
  subirImagenDeArticulo,
  urlDeImagenDeArticuloAdmin,
  validarImagenDeArticulo,
} from '../../api/catalogoWeb'
import { puedeGestionarCatalogos } from '../../api/tipos'
import { AuthContext } from '../../auth/AuthContext'
import { ConfirmacionDeBaja } from '../../componentes/ConfirmacionDeBaja'

type Props = {
  idArticulo: number
  /** Estado de la tienda con el que se cargó el artículo; después la sección lleva el suyo. */
  inicial: { publicadoWeb: boolean; tieneImagen: boolean }
  bloqueadoPorPadre: boolean
  alDeEscribir: (enCurso: boolean) => void
  /** La tienda del artículo cambió, o una escritura falló sin decir qué quedó escrito: la grilla muestra su estado y tiene que refrescarse. */
  alCambiar: () => void
}

/**
 * Tienda online del artículo: si se publica y su imagen. Solo la ve quien puede administrar el catálogo (el servidor
 * exige la misma policy en las tres escrituras; esto solo evita ofrecer controles que van a dar 403), y solo con el
 * artículo ya guardado. Cada control escribe por su cuenta, nunca con el "Guardar" del formulario.
 */
export function SeccionDeTiendaOnline(props: Props) {
  const sesion = useContext(AuthContext)
  const usuario = sesion?.usuario ?? null

  if (usuario === null || !puedeGestionarCatalogos(usuario.rolId)) return null

  return <ContenidoDeLaTienda {...props} />
}

/**
 * Las tres escrituras (publicar, subir y quitar la imagen) comparten una única guarda de reentrancia
 * (`ocupadoRef`) y, mientras dura una, `alDeEscribir` deja inerte todo el modal. Por eso la respuesta de una
 * escritura no lleva token propio (`react-async-state` regla 2): la superficie no puede reemplazarse ni cerrarse
 * mientras está en vuelo, lo que escribe es estado de esta instancia (que muere con el artículo que la montó) y no
 * navega. La relectura tras un fallo incierto corre dentro de esa misma ventana inerte y es la única lectura.
 *
 * La puerta de confirmación de "Quitar imagen" es inerte a nivel del modal entero: mientras está abierta se avisa a
 * `alDeEscribir` (igual que durante una escritura) y se suelta al cerrarla o desmontar la sección, así que el resto del
 * formulario queda deshabilitado y no se puede abrir ningún modal apilado (alta rápida) sobre ella. Su Escape en
 * captura es el correcto porque la puerta es la capa de arriba.
 */
function ContenidoDeLaTienda({ idArticulo, inicial, bloqueadoPorPadre, alDeEscribir, alCambiar }: Props) {
  const [publicado, setPublicado] = useState(inicial.publicadoWeb)
  const [tieneImagen, setTieneImagen] = useState(inicial.tieneImagen)
  const [version, setVersion] = useState<number | null>(null)
  // Cada fuente tiene su propio slot (regla 14); `limpiarMensajes` los borra al empezar cualquier acción (regla 16).
  const [errorDePublicacion, setErrorDePublicacion] = useState('')
  const [errorDeImagen, setErrorDeImagen] = useState('')
  const [aviso, setAviso] = useState('')
  const [ocupado, setOcupado] = useState(false)
  const [escribiendo, setEscribiendo] = useState<'publicar' | 'imagen' | null>(null)
  const [puertaAbierta, setPuertaAbierta] = useState(false)
  const [disparador, setDisparador] = useState<HTMLElement | null>(null)
  /** URL que el navegador no pudo mostrar. Va por URL y no por booleano: subir otra imagen cambia la versión y con
   * ella la URL, así que la vista previa vuelve sola. */
  const [urlRota, setUrlRota] = useState<string | null>(null)
  /** Espejo SÍNCRONO de `ocupado` y única guarda de reentrancia: dos disparos del mismo tick leen el mismo render. */
  const ocupadoRef = useRef(false)
  /** Adónde vuelve el foco si "Quitar imagen" desaparece con la confirmación: la sección vive en el portal del
   * `Modal`, donde la puerta no encuentra una `Box` a la que subir. */
  const tituloRef = useRef<HTMLElement>(null)

  // El contador del padre se mantiene balanceado por construcción: el cleanup corre al cerrar la puerta y al desmontar.
  useEffect(() => {
    if (!puertaAbierta) return

    alDeEscribir(true)

    return () => alDeEscribir(false)
  }, [puertaAbierta, alDeEscribir])

  const bloqueado = ocupado || bloqueadoPorPadre || puertaAbierta
  const urlDeLaImagen = urlDeImagenDeArticuloAdmin(idArticulo, version)

  function limpiarMensajes() {
    setErrorDePublicacion('')
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
      setPublicado(leido.publicadoWeb)
      setTieneImagen(leido.tieneImagen)
      setVersion(leido.tieneImagen ? Date.now() : null)
    } catch {
      // El mensaje del fallo ya manda a verificar el estado del artículo.
    }
  }

  async function escribir<T>(
    cual: 'publicar' | 'imagen',
    escritura: () => Promise<T>,
    alTenerExito: (resultado: T) => void,
    encabezadoDelFallo: string,
    ponerError: (texto: string) => void,
  ) {
    if (ocupadoRef.current) return
    ocupadoRef.current = true
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

  async function cambiarPublicacion(quiere: boolean) {
    await escribir(
      'publicar',
      () => publicarEnLaWeb(idArticulo, quiere),
      (guardada) => {
        setPublicado(guardada.publicado)
        setAviso(guardada.publicado ? 'El artículo quedó publicado en la tienda.' : 'El artículo quedó fuera de la tienda.')
      },
      quiere ? 'No se pudo publicar el artículo.' : 'No se pudo despublicar el artículo.',
      setErrorDePublicacion,
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
    <section aria-labelledby="art-tienda-titulo">
      <strong id="art-tienda-titulo" ref={tituloRef} tabIndex={-1} className="text-muted small text-uppercase">
        Tienda online
      </strong>

      <div className="form-check form-switch mt-2">
        <input
          id="art-publicado-web"
          type="checkbox"
          role="switch"
          className="form-check-input"
          checked={publicado}
          onChange={(e) => void cambiarPublicacion(e.target.checked)}
          disabled={bloqueado}
          aria-describedby="art-publicado-web-ayuda"
        />
        <label className="form-check-label" htmlFor="art-publicado-web">
          Publicado en la tienda
        </label>
      </div>
      <div id="art-publicado-web-ayuda" className="form-text">
        Se guarda al instante, sin pasar por "Guardar". Para verse en la tienda también tiene que estar activo y tener
        precio en la lista de la vitrina.
        {escribiendo === 'publicar' && ' Guardando…'}
      </div>
      {errorDePublicacion && (
        <div className="text-danger small mt-1" role="alert">
          {errorDePublicacion}
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
        {errorDeImagen && (
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
        <div className="mt-3">
          <ConfirmacionDeBaja
            titulo="la imagen del artículo"
            pregunta="Quitar"
            nota="Podés volver a subir una cuando quieras."
            etiquetaConfirmar="Confirmar quitar imagen"
            etiquetaEnCurso="Quitando…"
            ocupado={ocupado}
            disparador={disparador}
            regreso={tituloRef}
            onConfirmar={() => void confirmarQuitarImagen()}
            onCancelar={cerrarPuerta}
          />
        </div>
      )}

      {aviso && (
        <div className="text-success small mt-2" role="status">
          {aviso}
        </div>
      )}
    </section>
  )
}
