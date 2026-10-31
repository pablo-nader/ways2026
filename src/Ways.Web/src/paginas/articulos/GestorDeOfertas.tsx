import { useCallback, useContext, useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import { ErrorApi } from '../../api/cliente'
import {
  aAltaOferta,
  aValoresOferta,
  clienteDeOfertas,
  copiaDeFalloAlGuardarOferta,
  textoDeBeneficio,
  type FormularioOferta,
} from '../../api/ofertas'
import { puedeGestionarCatalogos } from '../../api/tipos'
import type { OfertaListado } from '../../api/tipos'
import { AuthContext } from '../../auth/AuthContext'
import { BotonIcono } from '../../componentes/BotonIcono'
import { Cargando } from '../../componentes/Cargando'
import { ConfirmacionDeBaja, NOTA_DE_BAJA_LOGICA } from '../../componentes/ConfirmacionDeBaja'
import { TituloDeSeccion } from '../../componentes/TituloDeSeccion'
import { cargarCatalogosDeOferta, type CatalogosDeOferta } from '../ofertas/catalogos'
import { ModalDeOferta } from '../ofertas/ModalDeOferta'
import { validarFormulario } from '../ofertas/validarFormulario'
import {
  formularioDeOfertaParaArticulo,
  formularioDeOfertaParaFamilia,
  textoDeAlcanceEnArticulo,
  textoDeCondicion,
  textoDeVigencia,
} from './ofertasDelArticulo'

type Props = {
  idArticulo: number
  nombreArticulo: string
  /** La familia del artículo; `nombre` es `null` mientras no se conoce. */
  familia: { id: number; nombre: string | null } | null
  bloqueadoPorPadre: boolean
  alDeEscribir: (enCurso: boolean) => void
}

/**
 * Ofertas del artículo y de su familia, con alta, edición y baja desde la edición del artículo. Solo la ve quien puede
 * administrar el catálogo (las escrituras de ofertas exigen la misma policy en el servidor) y solo con el artículo ya
 * guardado.
 */
export function GestorDeOfertas(props: Props) {
  const sesion = useContext(AuthContext)
  const usuario = sesion?.usuario ?? null

  if (usuario === null || !puedeGestionarCatalogos(usuario.rolId)) return null

  return <ContenidoDeLasOfertas {...props} />
}

/**
 * Las lecturas llevan generación (la última iniciada gana; cambiar de artículo o de familia, o desmontar, las invalida).
 * Las escrituras comparten una guarda de reentrancia (`ocupadoRef`) y no gatean su respuesta (`react-async-state`
 * regla 2): mientras dura una, el modal de la oferta o la confirmación de baja están abiertos e inertes y
 * `alDeEscribir` deja inerte el modal del artículo, así que nada puede reemplazar lo que la respuesta escribe.
 *
 * `alDeEscribir(true)` se levanta al empezar a abrir el modal (carga de catálogos y detalle) o al abrir la confirmación
 * de baja, y se suelta una sola vez (`elevadaRef` hace cada pareja true/false exacta) cuando: la apertura falla o queda
 * obsoleta, el modal o la confirmación se cierran, termina el guardado o la baja exitosa (después de que aterriza la
 * recarga de la lista) o se desmonta la sección. Un guardado o una baja que fallan dejan el modal abierto y la señal
 * levantada. Por eso las escrituras de acá no miran `bloqueadoPorPadre` una vez levantada la señal (lo habríamos
 * levantado nosotros mismos); la guarda de entrada de `abrir` sí lo mira, y como la señal se levanta antes de
 * cualquier espera, otra sección no puede empezar a escribir mientras la apertura está en vuelo.
 */
function ContenidoDeLasOfertas({ idArticulo, nombreArticulo, familia, bloqueadoPorPadre, alDeEscribir }: Props) {
  const [ofertas, setOfertas] = useState<OfertaListado[] | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [ocupado, setOcupado] = useState(false)
  const [formulario, setFormulario] = useState<FormularioOferta | null>(null)
  const [catalogos, setCatalogos] = useState<CatalogosDeOferta | null>(null)
  const [guardando, setGuardando] = useState(false)
  const [errorDeFormulario, setErrorDeFormulario] = useState('')
  const [rechazosDeValidacion, setRechazosDeValidacion] = useState(0)
  const [baja, setBaja] = useState<{
    oferta: OfertaListado
    disparador: HTMLElement
  } | null>(null)
  const [errorDeBaja, setErrorDeBaja] = useState('')
  const ocupadoRef = useRef(false)
  const generacion = useRef(0)
  // Aperturas del modal: una respuesta tardía de una apertura anterior no puede abrir el modal.
  const aperturas = useRef(0)
  const contenedorRef = useRef<HTMLDivElement>(null)

  // Espejo de si esta sección tiene levantada la señal de `alDeEscribir`: cada true se empareja con un único false.
  const elevadaRef = useRef(false)

  const levantar = useCallback(() => {
    if (elevadaRef.current) return
    elevadaRef.current = true
    alDeEscribir(true)
  }, [alDeEscribir])

  const soltar = useCallback(() => {
    if (!elevadaRef.current) return
    elevadaRef.current = false
    alDeEscribir(false)
  }, [alDeEscribir])

  useEffect(() => soltar, [soltar])

  const invalidarLecturas = useCallback(() => {
    generacion.current++
    aperturas.current++
  }, [])

  const idFamilia = familia?.id ?? null

  const cargar = useCallback(
    async (mensajeDeFallo: string): Promise<void> => {
      const mia = ++generacion.current
      try {
        const lista = await clienteDeOfertas.listar(false, idArticulo)
        if (generacion.current === mia) setOfertas(lista)
      } catch (e) {
        if (generacion.current === mia) setError(e instanceof ErrorApi ? e.message : mensajeDeFallo)
      } finally {
        if (generacion.current === mia) setCargando(false)
      }
    },
    [idArticulo],
  )

  // La familia cambia qué ofertas alcanzan al artículo: el listado se relee también cuando ella cambia.
  useEffect(() => {
    setCargando(true)
    setError('')
    setOfertas(null)
    void cargar('No se pudieron cargar las ofertas.')
    return invalidarLecturas
  }, [cargar, invalidarLecturas, idFamilia])

  const superficieAbierta = formulario !== null || baja !== null
  const bloqueado = ocupado || bloqueadoPorPadre || superficieAbierta

  const nombreDeLaFamilia = familia === null ? null : (familia.nombre ?? `#${familia.id}`)

  function avisoDeFamilia(id: number | '' | null): string | null {
    if (id === null || id === '') return null
    const nombre = id === familia?.id ? nombreDeLaFamilia : `#${id}`

    return `Es una oferta de la familia ${nombre}: el cambio afecta a todos sus artículos.`
  }

  async function abrir(obtenerFormulario: () => Promise<FormularioOferta>) {
    if (ocupadoRef.current || bloqueadoPorPadre) return
    ocupadoRef.current = true
    setOcupado(true)
    setError('')
    levantar()
    const mia = ++aperturas.current
    let abierto = false
    try {
      let propios = catalogos
      // Un resultado con avisos (algún catálogo no cargó) no se reutiliza: la próxima apertura lo reintenta.
      if (propios === null || propios.avisos.length > 0) {
        propios = await cargarCatalogosDeOferta()
        if (aperturas.current !== mia) return
        setCatalogos(propios)
      }
      const inicial = await obtenerFormulario()
      if (aperturas.current !== mia) return
      setErrorDeFormulario('')
      setFormulario(inicial)
      abierto = true
    } catch (e) {
      if (aperturas.current === mia) setError(e instanceof ErrorApi ? e.message : 'No se pudo abrir la oferta.')
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
      if (!abierto) soltar()
    }
  }

  function nuevaParaElArticulo() {
    void abrir(async () => formularioDeOfertaParaArticulo(idArticulo, nombreArticulo))
  }

  function nuevaParaLaFamilia() {
    if (familia === null) return
    void abrir(async () => formularioDeOfertaParaFamilia(familia.id))
  }

  function editar(o: OfertaListado) {
    void abrir(async () => {
      // El listado no trae las listas objetivo: el detalle sí, y sin él se perderían al guardar.
      const detalle = await clienteDeOfertas.obtener(o.id)
      let nombre = ''
      if (detalle.idArticulo === idArticulo) nombre = nombreArticulo
      else if (detalle.idArticulo !== null) nombre = (await clienteDeArticulos.obtener(detalle.idArticulo)).nombre

      return { ...aValoresOferta(detalle), nombreArticulo: nombre }
    })
  }

  function cancelar() {
    if (ocupadoRef.current) return
    setFormulario(null)
    soltar()
  }

  async function guardar() {
    if (ocupadoRef.current || formulario === null) return

    const invalido = validarFormulario(formulario)
    if (invalido) {
      setErrorDeFormulario(invalido)
      setRechazosDeValidacion((n) => n + 1)
      return
    }

    ocupadoRef.current = true
    setOcupado(true)
    setGuardando(true)
    setErrorDeFormulario('')
    let guardado = false
    try {
      try {
        if (formulario.id === null) await clienteDeOfertas.crear(aAltaOferta(formulario))
        else await clienteDeOfertas.actualizar(formulario.id, aAltaOferta(formulario))
      } catch (e) {
        setErrorDeFormulario(copiaDeFalloAlGuardarOferta(e))
        return
      }
      guardado = true
      setFormulario(null)
      await cargar('Se guardó, pero no se pudo actualizar la lista.')
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
      setGuardando(false)
      if (guardado) soltar()
    }
  }

  async function confirmarBaja() {
    if (baja === null || ocupadoRef.current) return
    ocupadoRef.current = true
    setOcupado(true)
    setErrorDeBaja('')
    let eliminada = false
    try {
      try {
        await clienteDeOfertas.eliminar(baja.oferta.id)
      } catch (e) {
        setErrorDeBaja(e instanceof ErrorApi ? e.message : 'No se pudo dar de baja la oferta.')
        return
      }
      eliminada = true
      setBaja(null)
      await cargar('Se dio de baja, pero no se pudo actualizar la lista.')
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
      if (eliminada) soltar()
    }
  }

  function cancelarBaja() {
    if (ocupadoRef.current) return
    setErrorDeBaja('')
    setBaja(null)
    soltar()
  }

  return (
    <>
      <hr />
      <div ref={contenedorRef} tabIndex={-1} style={{ outline: 'none' }}>
        <TituloDeSeccion>Ofertas</TituloDeSeccion>
        {error && (
          <div role="alert" className="alert alert-danger py-1 px-2 small mt-2">
            {error}
          </div>
        )}
        {formulario === null && catalogos !== null && catalogos.avisos.length > 0 && (
          <div className="alert alert-warning py-1 px-2 small mt-2">{catalogos.avisos.join(' ')}</div>
        )}

        {cargando ? (
          <Cargando texto="Cargando ofertas…" />
        ) : ofertas === null ? null : ofertas.length === 0 ? (
          <p className="text-muted small mt-2 mb-2">
            {familia === null ? 'Este artículo no tiene ofertas.' : 'Este artículo ni su familia tienen ofertas.'}
          </p>
        ) : (
          <div className="table-responsive mt-2">
            <table className="table table-sm align-middle mb-2">
              <thead>
                <tr>
                  <th>Nombre</th>
                  <th>Alcance</th>
                  <th>Beneficio</th>
                  <th>Condición</th>
                  <th>Vigencia</th>
                  <th>Estado</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {ofertas.map((o) => (
                  <tr key={o.id}>
                    <td>{o.nombre}</td>
                    <td>{textoDeAlcanceEnArticulo(o, o.idFamilia === familia?.id ? familia.nombre : null)}</td>
                    <td>{textoDeBeneficio(o)}</td>
                    <td>{textoDeCondicion(o)}</td>
                    <td>{textoDeVigencia(o)}</td>
                    <td>
                      <span className={`badge ${o.activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
                        {o.activo ? 'Activa' : 'Inactiva'}
                      </span>
                    </td>
                    <td className="text-end text-nowrap">
                      <BotonIcono
                        icono="editar"
                        etiqueta={`Editar oferta ${o.nombre}`}
                        className="me-1"
                        disabled={bloqueado}
                        onClick={() => editar(o)}
                      />
                      <BotonIcono
                        icono="eliminar"
                        etiqueta={`Eliminar oferta ${o.nombre}`}
                        disabled={bloqueado}
                        onClick={(evento) => {
                          setErrorDeBaja('')
                          levantar()
                          setBaja({
                            oferta: o,
                            disparador: evento.currentTarget,
                          })
                        }}
                      />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        <div className="d-flex flex-wrap gap-2 mt-2">
          <button type="button" className="btn btn-outline-primary" disabled={bloqueado} onClick={nuevaParaElArticulo}>
            Nueva oferta para este artículo
          </button>
          {familia !== null && (
            <button type="button" className="btn btn-outline-primary" disabled={bloqueado} onClick={nuevaParaLaFamilia}>
              Nueva oferta para la familia {nombreDeLaFamilia}
            </button>
          )}
        </div>

        {formulario !== null && catalogos !== null && (
          <ModalDeOferta
            key={formulario.id ?? 'nuevo'}
            valor={formulario}
            grupos={catalogos.grupos}
            categorias={catalogos.categorias}
            familias={catalogos.familias}
            empresas={catalogos.empresas}
            listasPrecio={catalogos.listasPrecio}
            guardando={guardando}
            ocupado={ocupado}
            error={errorDeFormulario}
            avisos={[avisoDeFamilia(formulario.idFamilia), ...catalogos.avisos].filter((a): a is string => a !== null)}
            disparoDelError={rechazosDeValidacion}
            focoDeReserva={contenedorRef}
            onCambio={setFormulario}
            onGuardar={() => void guardar()}
            onCancelar={cancelar}
          />
        )}

        {baja !== null && (
          <ConfirmacionDeBaja
            titulo={`la oferta "${baja.oferta.nombre}"`}
            ocupado={ocupado}
            disparador={baja.disparador}
            regreso={contenedorRef}
            nota={[avisoDeFamilia(baja.oferta.idFamilia), NOTA_DE_BAJA_LOGICA].filter(Boolean).join(' ')}
            error={errorDeBaja || null}
            onConfirmar={() => void confirmarBaja()}
            onCancelar={cancelarBaja}
          />
        )}
      </div>
    </>
  )
}
