import { useCallback, useEffect, useRef, useState } from 'react'
import type { ChangeEvent, FormEvent, MouseEvent } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDePublico } from '../api/publico'
import type { EmpresaListado, SitioPublico, VitrinaAdmin } from '../api/tipos'
import {
  ACEPTAR_LOGO,
  COLOR_DE_MARCA,
  LARGO_MAXIMO_DE_LA_DESCRIPCION,
  LARGO_MAXIMO_DEL_TAGLINE,
  aBorrador,
  aEdicion,
  colorParaElSelector,
  copiaDeFalloDeVitrina,
  debeReleerTrasFallo,
  eliminarVitrina,
  esColorHex,
  guardarVitrina,
  obtenerVitrina,
  opcionesDeEmpresaParaVitrina,
  quitarLogo,
  subirLogo,
  urlDelLogoAdmin,
  validarLogo,
} from '../api/vitrinas'
import type { BorradorDeVitrina } from '../api/vitrinas'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const COPIA_COLOR_INVALIDO = 'Usá el formato #rrggbb, por ejemplo #f7991c.'

type Rechazo = { codigo: string | null; texto: string }
type Puerta = 'logo' | 'vitrina'

/**
 * Vitrina de una empresa: lo que muestra la landing de su dominio (tagline, descripción, color,
 * tema, logo y si está publicada). Admin del tenant y plataforma. Se elige la empresa (si hay una
 * sola queda elegida) y se edita su vitrina; la selección vive en el estado y no en la URL, así que
 * Atrás no la cambia por debajo de una escritura.
 *
 * Lo guardado (`vitrina`) y lo que se está editando (`borrador`) son estados separados: subir o
 * quitar el logo es otra escritura y no pisa lo escrito en el formulario sin guardar. Solo elegir
 * empresa, guardar y eliminar reinician el borrador.
 *
 * Contrato de invalidación (`react-async-state` reglas 2-4): tres lecturas, cada una con su
 * generación. `generacionEmpresas` y `generacionSitio` las acuña el montaje. `generacionVitrina` la
 * acuña toda acción que cambia lo que la pantalla muestra de la vitrina —elegir empresa y cada
 * relectura posterior a una escritura— ANTES de tocar el estado, y cada aplicación posterior a un
 * `await` la vuelve a chequear.
 *
 * Las escrituras no se gatean por generación y no hace falta: mientras una está en vuelo, su
 * relectura incluida, la pantalla queda inerte (`bloqueado`: nada puede cambiar de empresa, abrir la
 * puerta de confirmación ni lanzar otra escritura) y la respuesta no navega. Lo que sí se gatea son
 * las lecturas.
 */
export function Vitrina() {
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [errorDeEmpresas, setErrorDeEmpresas] = useState('')
  const [sitio, setSitio] = useState<SitioPublico | null>(null)

  const [idEmpresa, setIdEmpresa] = useState('')
  const [vitrina, setVitrina] = useState<VitrinaAdmin | null>(null)
  const [borrador, setBorrador] = useState<BorradorDeVitrina | null>(null)
  const [cargandoVitrina, setCargandoVitrina] = useState(false)

  // Cada slot tiene un solo dueño (regla 14). `limpiarMensajes` borra los de las escrituras y el
  // aviso; el de lectura solo lo escriben `elegirEmpresa` y `cargarVitrina`.
  const [errorDeLectura, setErrorDeLectura] = useState('')
  const [rechazoDeGuardado, setRechazoDeGuardado] = useState<Rechazo | null>(null)
  const [errorDeLogo, setErrorDeLogo] = useState('')
  /** Rechazo de lo que se confirma en la puerta (quitar el logo o eliminar la vitrina). */
  const [errorDeAccion, setErrorDeAccion] = useState('')
  const [aviso, setAviso] = useState('')

  const [ocupado, setOcupado] = useState(false)
  /** Solo para el texto de los botones: la inercia la gobierna `ocupado`. */
  const [escribiendo, setEscribiendo] = useState<'guardar' | 'logo' | null>(null)
  /** Confirmación pendiente. La puerta es modal: ver `bloqueado`. */
  const [puerta, setPuerta] = useState<Puerta | null>(null)
  /** Control que abrió la puerta, capturado en su `onClick` (ver `ConfirmacionDeBaja.tsx`). */
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)

  const generacionEmpresas = useRef(0)
  const generacionSitio = useRef(0)
  const generacionVitrina = useRef(0)
  /** Espejo SÍNCRONO de `ocupado` y única guarda de re-entrancia: dos disparos del mismo tick leen
   * el mismo render. Se apaga en el `finally` de cada escritura, sin gate. */
  const ocupadoRef = useRef(false)

  const cargarVitrina = useCallback(
    async (id: number, token: number, opciones: { propagar?: boolean; reiniciarBorrador: boolean }) => {
      setCargandoVitrina(true)
      try {
        const leida = await obtenerVitrina(id)
        if (generacionVitrina.current !== token) return
        setVitrina(leida)
        if (opciones.reiniciarBorrador) setBorrador(aBorrador(leida))
        setErrorDeLectura('')
      } catch (e) {
        if (generacionVitrina.current !== token) return
        if (opciones.propagar) throw e
        setErrorDeLectura(copiaDeFalloDeVitrina(e, 'No se pudo cargar la vitrina.'))
      } finally {
        if (generacionVitrina.current === token) setCargandoVitrina(false)
      }
    },
    [],
  )

  function limpiarMensajes() {
    setRechazoDeGuardado(null)
    setErrorDeLogo('')
    setErrorDeAccion('')
    setAviso('')
  }

  const elegirEmpresa = useCallback(
    (valor: string) => {
      const token = ++generacionVitrina.current
      setRechazoDeGuardado(null)
      setErrorDeLogo('')
      setErrorDeAccion('')
      setAviso('')
      setErrorDeLectura('')
      setIdEmpresa(valor)
      setVitrina(null)
      setBorrador(null)
      setCargandoVitrina(false)

      if (valor !== '') void cargarVitrina(Number(valor), token, { reiniciarBorrador: true })
    },
    [cargarVitrina],
  )

  useEffect(() => {
    const token = ++generacionEmpresas.current

    clienteDeOrganizacion
      .listarEmpresas()
      .then((filas) => {
        if (generacionEmpresas.current !== token) return
        setEmpresas(filas)
        if (filas.length === 1) elegirEmpresa(String(filas[0].id))
      })
      .catch((e) => {
        if (generacionEmpresas.current === token) {
          setErrorDeEmpresas(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las empresas.')
          setEmpresas([])
        }
      })
  }, [elegirEmpresa])

  useEffect(() => {
    const token = ++generacionSitio.current

    clienteDePublico
      .obtenerSitio()
      .then((conocido) => {
        if (generacionSitio.current === token) setSitio(conocido)
      })
      .catch(() => {
        // Sin el sitio conocido no se ofrece el enlace: la ayuda genérica de abajo sigue siendo cierta.
      })
  }, [])

  /** La relectura va fuera del try/catch de la escritura: una escritura que ya commiteó nunca se
   * reporta como fallida (regla 6). */
  async function refrescarTrasEscribir(id: number, mensajeOk: string, reiniciarBorrador: boolean) {
    setAviso(mensajeOk)
    try {
      await cargarVitrina(id, ++generacionVitrina.current, { propagar: true, reiniciarBorrador })
    } catch {
      setAviso(`${mensajeOk} ${AVISO_REFRESCO_FALLIDO}`)
    }
  }

  /** Tras un fallo que no dice qué quedó escrito se vuelve a leer para mostrar el estado real. No
   * toca los mensajes (el del fallo sigue visible) ni lo que se está editando. */
  async function releerTrasFallo(id: number) {
    await cargarVitrina(id, ++generacionVitrina.current, { reiniciarBorrador: false })
  }

  function editar(cambio: Partial<BorradorDeVitrina>) {
    setBorrador((previo) => (previo ? { ...previo, ...cambio } : previo))
    // Un rechazo habla de lo que se mandó: editar el formulario lo deja desactualizado.
    setRechazoDeGuardado(null)
  }

  async function guardar(evento: FormEvent, id: number, datos: BorradorDeVitrina) {
    evento.preventDefault()
    if (ocupadoRef.current) return

    ocupadoRef.current = true
    setOcupado(true)
    setEscribiendo('guardar')
    limpiarMensajes()
    try {
      let guardada: VitrinaAdmin
      try {
        guardada = await guardarVitrina(id, aEdicion(datos))
      } catch (e) {
        setRechazoDeGuardado({
          codigo: e instanceof ErrorApi ? e.codigo : null,
          texto: copiaDeFalloDeVitrina(e, 'No se pudo guardar la vitrina.'),
        })
        if (debeReleerTrasFallo(e)) await releerTrasFallo(id)

        return
      }

      setVitrina(guardada)
      setBorrador(aBorrador(guardada))
      setAviso('Se guardó la vitrina.')
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
      setEscribiendo(null)
    }
  }

  async function elegirLogo(evento: ChangeEvent<HTMLInputElement>, id: number) {
    const entrada = evento.currentTarget
    const archivo = entrada.files?.[0]
    // Se vacía el control para que elegir de nuevo el mismo archivo vuelva a disparar el cambio.
    entrada.value = ''
    if (!archivo || ocupadoRef.current) return

    limpiarMensajes()
    const rechazo = validarLogo(archivo)
    if (rechazo) {
      setErrorDeLogo(rechazo)

      return
    }

    ocupadoRef.current = true
    setOcupado(true)
    setEscribiendo('logo')
    try {
      let actualizada: VitrinaAdmin
      try {
        actualizada = await subirLogo(id, archivo)
      } catch (e) {
        setErrorDeLogo(copiaDeFalloDeVitrina(e, 'No se pudo subir el logo.'))
        if (debeReleerTrasFallo(e)) await releerTrasFallo(id)

        return
      }

      // El servidor contesta la vitrina entera con el `logoVersion` nuevo: la vista previa se
      // refresca con eso. El borrador no se toca.
      setVitrina(actualizada)
      setAviso('Se subió el logo.')
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
      setEscribiendo(null)
    }
  }

  async function confirmarQuitarLogo(id: number) {
    if (ocupadoRef.current) return

    ocupadoRef.current = true
    setOcupado(true)
    limpiarMensajes()
    try {
      try {
        await quitarLogo(id)
      } catch (e) {
        // La puerta queda abierta con el motivo al lado.
        setErrorDeAccion(copiaDeFalloDeVitrina(e, 'No se pudo quitar el logo.'))
        if (debeReleerTrasFallo(e)) await releerTrasFallo(id)

        return
      }

      setPuerta(null)
      await refrescarTrasEscribir(id, 'Se quitó el logo.', false)
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
    }
  }

  async function confirmarEliminarVitrina(id: number) {
    if (ocupadoRef.current) return

    ocupadoRef.current = true
    setOcupado(true)
    limpiarMensajes()
    try {
      try {
        await eliminarVitrina(id)
      } catch (e) {
        setErrorDeAccion(copiaDeFalloDeVitrina(e, 'No se pudo eliminar la vitrina.'))
        if (debeReleerTrasFallo(e)) await releerTrasFallo(id)

        return
      }

      setPuerta(null)
      await refrescarTrasEscribir(id, 'Se eliminó la vitrina.', true)
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
    }
  }

  function abrirPuerta(cual: Puerta, evento: MouseEvent<HTMLButtonElement>) {
    limpiarMensajes()
    setDisparadorDeLaPuerta(evento.currentTarget)
    setPuerta(cual)
  }

  function cerrarPuerta() {
    limpiarMensajes()
    setDisparadorDeLaPuerta(null)
    setPuerta(null)
  }

  /** La puerta abierta bloquea la pantalla entera, no solo la escritura en vuelo. */
  const bloqueado = ocupado || puerta !== null
  /** Mientras la vitrina se lee no se escribe sobre ella; elegir otra empresa sí está permitido. */
  const inerte = bloqueado || cargandoVitrina

  const opciones = opcionesDeEmpresaParaVitrina(empresas ?? [])
  const nombreElegido = opciones.find((o) => o.valor === idEmpresa)?.etiqueta ?? ''
  const idElegido = Number(idEmpresa)

  return (
    <div className="container-fluid py-4">
      <Box titulo="Vitrina" variante="inverse">
        {errorDeEmpresas && <div className="alert alert-danger">{errorDeEmpresas}</div>}
        {errorDeLectura && (
          <div className="alert alert-danger d-flex align-items-center justify-content-between gap-2">
            <span>{errorDeLectura}</span>
            {vitrina === null && idEmpresa !== '' && (
              <button
                type="button"
                className="btn btn-sm btn-outline-danger"
                onClick={() => elegirEmpresa(idEmpresa)}
                disabled={bloqueado}
              >
                Reintentar
              </button>
            )}
          </div>
        )}
        {errorDeAccion && <div className="alert alert-danger">{errorDeAccion}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}

        {puerta === 'logo' && (
          <ConfirmacionDeBaja
            titulo="el logo"
            pregunta="Quitar"
            nota="Podés volver a subir uno cuando quieras."
            etiquetaConfirmar="Confirmar quitar logo"
            etiquetaEnCurso="Quitando…"
            ocupado={ocupado}
            disparador={disparadorDeLaPuerta}
            onConfirmar={() => void confirmarQuitarLogo(idElegido)}
            onCancelar={cerrarPuerta}
          />
        )}
        {puerta === 'vitrina' && (
          <ConfirmacionDeBaja
            titulo={`la vitrina de "${nombreElegido}"`}
            pregunta="Eliminar"
            nota="La página deja de verse y los textos, el color y el logo cargados dejan de estar disponibles. Podés armar otra vitrina desde cero."
            etiquetaConfirmar="Confirmar eliminación"
            etiquetaEnCurso="Eliminando…"
            ocupado={ocupado}
            disparador={disparadorDeLaPuerta}
            onConfirmar={() => void confirmarEliminarVitrina(idElegido)}
            onCancelar={cerrarPuerta}
          />
        )}

        {empresas === null ? (
          <Cargando />
        ) : (
          <>
            <div className="col-md-4 mb-3">
              <label className="form-label" htmlFor="vit-empresa">
                Empresa
              </label>
              <select
                id="vit-empresa"
                className="form-select"
                value={idEmpresa}
                onChange={(e) => elegirEmpresa(e.target.value)}
                disabled={bloqueado}
              >
                <option value="">Elegir una empresa…</option>
                {opciones.map((o) => (
                  <option key={o.valor} value={o.valor}>
                    {o.etiqueta}
                  </option>
                ))}
              </select>
            </div>

            {idEmpresa !== '' &&
              (vitrina === null || borrador === null ? (
                cargandoVitrina && <Cargando />
              ) : (
                <>
                  <FormularioDeVitrina
                    borrador={borrador}
                    rechazo={rechazoDeGuardado}
                    inerte={inerte}
                    guardando={escribiendo === 'guardar'}
                    puedeEliminar={vitrina.existe}
                    onEditar={editar}
                    onGuardar={(evento) => void guardar(evento, idElegido, borrador)}
                    onEliminar={(evento) => abrirPuerta('vitrina', evento)}
                  />

                  <BloqueDeLogo
                    idEmpresa={idElegido}
                    vitrina={vitrina}
                    error={errorDeLogo}
                    inerte={inerte}
                    subiendo={escribiendo === 'logo'}
                    onElegir={(evento) => void elegirLogo(evento, idElegido)}
                    onQuitar={(evento) => abrirPuerta('logo', evento)}
                  />

                  <EnlaceALaPagina sitio={sitio} publicada={vitrina.existe && vitrina.publicada} />
                </>
              ))}
          </>
        )}
      </Box>
    </div>
  )
}

type PropsDelFormulario = {
  borrador: BorradorDeVitrina
  rechazo: Rechazo | null
  inerte: boolean
  guardando: boolean
  puedeEliminar: boolean
  onEditar: (cambio: Partial<BorradorDeVitrina>) => void
  onGuardar: (evento: FormEvent) => void
  onEliminar: (evento: MouseEvent<HTMLButtonElement>) => void
}

function FormularioDeVitrina({
  borrador,
  rechazo,
  inerte,
  guardando,
  puedeEliminar,
  onEditar,
  onGuardar,
  onEliminar,
}: PropsDelFormulario) {
  const color = borrador.colorAcento.trim()
  const colorInvalido = color !== '' && !esColorHex(color)
  const errorDeColor = colorInvalido ? COPIA_COLOR_INVALIDO : rechazo?.codigo === 'color_invalido' ? rechazo.texto : ''
  const errorDeTema = rechazo?.codigo === 'tema_invalido' ? rechazo.texto : ''
  /** Todo rechazo que no tiene un campo propio queda junto a las acciones. */
  const errorGeneral = rechazo && !errorDeColor && !errorDeTema ? rechazo.texto : ''

  return (
    <form className="row g-3 border p-3 mb-4 bg-body" onSubmit={onGuardar}>
      <div className="col-12">
        <div className="form-check form-switch">
          <input
            id="vit-publicada"
            type="checkbox"
            role="switch"
            className="form-check-input"
            checked={borrador.publicada}
            onChange={(e) => onEditar({ publicada: e.target.checked })}
            disabled={inerte}
            aria-describedby="vit-publicada-ayuda"
          />
          <label className="form-check-label" htmlFor="vit-publicada">
            Publicada
          </label>
        </div>
        <div id="vit-publicada-ayuda" className="form-text">
          Solo una empresa del comercio puede tener su vitrina publicada. Para despublicarla, desmarcá esta opción y guardá.
        </div>
      </div>

      <div className="col-md-8">
        <label className="form-label" htmlFor="vit-tagline">
          Slogan
        </label>
        <input
          id="vit-tagline"
          className="form-control"
          maxLength={LARGO_MAXIMO_DEL_TAGLINE}
          value={borrador.tagline}
          onChange={(e) => onEditar({ tagline: e.target.value })}
          disabled={inerte}
          aria-describedby="vit-tagline-contador"
        />
        <div id="vit-tagline-contador" className="form-text">
          {borrador.tagline.length}/{LARGO_MAXIMO_DEL_TAGLINE} caracteres
        </div>
      </div>

      <div className="col-12">
        <label className="form-label" htmlFor="vit-descripcion">
          Descripción
        </label>
        <textarea
          id="vit-descripcion"
          className="form-control"
          rows={5}
          maxLength={LARGO_MAXIMO_DE_LA_DESCRIPCION}
          value={borrador.descripcion}
          onChange={(e) => onEditar({ descripcion: e.target.value })}
          disabled={inerte}
          aria-describedby="vit-descripcion-contador"
        />
        <div id="vit-descripcion-contador" className="form-text">
          {borrador.descripcion.length}/{LARGO_MAXIMO_DE_LA_DESCRIPCION} caracteres
        </div>
      </div>

      <div className="col-md-5">
        <label className="form-label" htmlFor="vit-color">
          Color de acento
        </label>
        <div className="d-flex gap-2">
          <input
            type="color"
            className="form-control form-control-color"
            aria-label="Elegir color de acento"
            value={colorParaElSelector(borrador.colorAcento)}
            onChange={(e) => onEditar({ colorAcento: e.target.value })}
            disabled={inerte}
          />
          <input
            id="vit-color"
            className="form-control"
            placeholder={COLOR_DE_MARCA}
            maxLength={7}
            value={borrador.colorAcento}
            onChange={(e) => onEditar({ colorAcento: e.target.value })}
            disabled={inerte}
            aria-invalid={errorDeColor !== ''}
            aria-describedby={errorDeColor ? 'vit-color-ayuda vit-color-error' : 'vit-color-ayuda'}
          />
        </div>
        <div id="vit-color-ayuda" className="form-text">
          Vacío usa el naranja de Ways ({COLOR_DE_MARCA.toUpperCase()}).
        </div>
        {errorDeColor && (
          <div id="vit-color-error" className="text-danger small mt-1" role="alert">
            {errorDeColor}
          </div>
        )}
      </div>

      <fieldset className="col-md-4">
        <legend className="form-label fs-6">Tema</legend>
        <div className="form-check">
          <input
            id="vit-tema-oscuro"
            type="radio"
            name="vit-tema"
            className="form-check-input"
            checked={borrador.tema === 'oscuro'}
            onChange={() => onEditar({ tema: 'oscuro' })}
            disabled={inerte}
          />
          <label className="form-check-label" htmlFor="vit-tema-oscuro">
            Oscuro
          </label>
        </div>
        <div className="form-check">
          <input
            id="vit-tema-claro"
            type="radio"
            name="vit-tema"
            className="form-check-input"
            checked={borrador.tema === 'claro'}
            onChange={() => onEditar({ tema: 'claro' })}
            disabled={inerte}
          />
          <label className="form-check-label" htmlFor="vit-tema-claro">
            Claro
          </label>
        </div>
        {errorDeTema && (
          <div className="text-danger small mt-1" role="alert">
            {errorDeTema}
          </div>
        )}
      </fieldset>

      {errorGeneral && (
        <div className="col-12">
          <div className="alert alert-danger mb-0" role="alert">
            {errorGeneral}
          </div>
        </div>
      )}

      <div className="col-12 d-flex gap-2">
        <button type="submit" className="btn btn-success" disabled={inerte || colorInvalido}>
          {guardando ? 'Guardando…' : 'Guardar'}
        </button>
        {puedeEliminar && (
          <button type="button" className="btn btn-outline-danger" onClick={onEliminar} disabled={inerte}>
            Eliminar vitrina
          </button>
        )}
      </div>
    </form>
  )
}

type PropsDelLogo = {
  idEmpresa: number
  vitrina: VitrinaAdmin
  error: string
  inerte: boolean
  subiendo: boolean
  onElegir: (evento: ChangeEvent<HTMLInputElement>) => void
  onQuitar: (evento: MouseEvent<HTMLButtonElement>) => void
}

/** El logo se sube y se quita aparte del formulario: cada uno es su propia escritura. */
function BloqueDeLogo({ idEmpresa, vitrina, error, inerte, subiendo, onElegir, onQuitar }: PropsDelLogo) {
  return (
    <section className="border p-3 mb-4 bg-body" aria-labelledby="vit-logo-titulo">
      <h6 id="vit-logo-titulo">Logo</h6>

      {vitrina.tieneLogo ? (
        <img
          src={urlDelLogoAdmin(idEmpresa, vitrina.logoVersion)}
          alt="Logo actual"
          className="d-block mb-3 border"
          style={{ maxWidth: 240, maxHeight: 160 }}
        />
      ) : (
        <p className="text-muted">Todavía no hay un logo cargado.</p>
      )}

      <label className="form-label" htmlFor="vit-logo">
        {vitrina.tieneLogo ? 'Reemplazar el logo' : 'Subir un logo'}
      </label>
      <input
        id="vit-logo"
        type="file"
        className="form-control mb-1"
        accept={ACEPTAR_LOGO}
        onChange={onElegir}
        disabled={inerte}
        aria-describedby={error ? 'vit-logo-ayuda vit-logo-error' : 'vit-logo-ayuda'}
        aria-invalid={error !== ''}
      />
      <div id="vit-logo-ayuda" className="form-text">
        PNG, JPG o WebP, hasta 1 MB. Al elegir el archivo se sube y reemplaza el logo actual.
        {subiendo && ' Subiendo…'}
      </div>
      {error && (
        <div id="vit-logo-error" className="text-danger small mt-1" role="alert">
          {error}
        </div>
      )}

      {vitrina.tieneLogo && (
        <button type="button" className="btn btn-outline-danger btn-sm mt-3" onClick={onQuitar} disabled={inerte}>
          Quitar logo
        </button>
      )}
    </section>
  )
}

/**
 * La página vive en `/` del dominio del comercio. Solo bajo ese dominio, y con la vitrina ya
 * publicada (la guardada, no el borrador), el enlace lleva a una página que existe.
 */
function EnlaceALaPagina({ sitio, publicada }: { sitio: SitioPublico | null; publicada: boolean }) {
  if (sitio === null || sitio.esPlataforma) {
    return (
      <p className="text-muted small mb-0">
        La página de la vitrina se ve bajo el dominio propio del comercio, no desde esta dirección.
      </p>
    )
  }

  if (!publicada) {
    return <p className="text-muted small mb-0">La página todavía no está publicada: publicala y guardá para poder verla.</p>
  }

  return (
    <a href="/" target="_blank" rel="noopener noreferrer">
      Ver la página
    </a>
  )
}
