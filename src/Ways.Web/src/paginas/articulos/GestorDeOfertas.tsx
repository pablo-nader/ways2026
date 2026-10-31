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
import { ConfirmacionDeBaja } from '../../componentes/ConfirmacionDeBaja'
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
 * Las lecturas llevan generación (la última iniciada gana; cambiar de artículo o desmontar las invalida). Las escrituras
 * comparten una guarda de reentrancia (`ocupadoRef`) y no gatean su respuesta (`react-async-state` regla 2): mientras
 * dura una, el modal de la oferta o la confirmación de baja están abiertos e inertes y `alDeEscribir` deja inerte el
 * modal del artículo, así que nada puede reemplazar lo que la respuesta escribe, y no navega. Mientras el modal o la
 * confirmación están abiertos se avisa a `alDeEscribir` para que el formulario del artículo no se pueda tocar por
 * debajo; por eso las escrituras de acá no miran `bloqueadoPorPadre` (lo habríamos levantado nosotros mismos).
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

  const invalidarLecturas = useCallback(() => {
    generacion.current++
    aperturas.current++
  }, [])

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

  useEffect(() => {
    setCargando(true)
    setError('')
    setOfertas(null)
    void cargar('No se pudieron cargar las ofertas.')
    return invalidarLecturas
  }, [cargar, invalidarLecturas])

  const superficieAbierta = formulario !== null || baja !== null

  useEffect(() => {
    if (!superficieAbierta) return

    alDeEscribir(true)

    return () => alDeEscribir(false)
  }, [superficieAbierta, alDeEscribir])

  const bloqueado = ocupado || bloqueadoPorPadre || superficieAbierta

  async function abrir(obtenerFormulario: () => Promise<FormularioOferta>) {
    if (ocupadoRef.current || bloqueadoPorPadre) return
    ocupadoRef.current = true
    setOcupado(true)
    setError('')
    const mia = ++aperturas.current
    try {
      let propios = catalogos
      if (propios === null) {
        propios = await cargarCatalogosDeOferta()
        if (aperturas.current !== mia) return
        setCatalogos(propios)
      }
      const inicial = await obtenerFormulario()
      if (aperturas.current !== mia) return
      setErrorDeFormulario('')
      setFormulario(inicial)
    } catch (e) {
      if (aperturas.current === mia) setError(e instanceof ErrorApi ? e.message : 'No se pudo abrir la oferta.')
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
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
    try {
      try {
        if (formulario.id === null) await clienteDeOfertas.crear(aAltaOferta(formulario))
        else await clienteDeOfertas.actualizar(formulario.id, aAltaOferta(formulario))
      } catch (e) {
        setErrorDeFormulario(copiaDeFalloAlGuardarOferta(e))
        return
      }
      setFormulario(null)
      await cargar('Se guardó, pero no se pudo actualizar la lista.')
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
      setGuardando(false)
    }
  }

  async function confirmarBaja() {
    if (baja === null || ocupadoRef.current) return
    ocupadoRef.current = true
    setOcupado(true)
    setErrorDeBaja('')
    try {
      try {
        await clienteDeOfertas.eliminar(baja.oferta.id)
      } catch (e) {
        setErrorDeBaja(e instanceof ErrorApi ? e.message : 'No se pudo dar de baja la oferta.')
        return
      }
      setBaja(null)
      await cargar('Se dio de baja, pero no se pudo actualizar la lista.')
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
    }
  }

  function cancelarBaja() {
    if (ocupadoRef.current) return
    setErrorDeBaja('')
    setBaja(null)
  }

  const nombreDeLaFamilia = familia === null ? null : (familia.nombre ?? `#${familia.id}`)

  return (
    <>
      <hr />
      <div ref={contenedorRef} tabIndex={-1} style={{ outline: 'none' }}>
        <strong className="text-muted small text-uppercase">Ofertas</strong>
        {error && (
          <div role="alert" className="alert alert-danger py-1 px-2 small mt-2">
            {error}
          </div>
        )}

        {cargando ? (
          <Cargando texto="Cargando ofertas…" />
        ) : ofertas === null ? null : ofertas.length === 0 ? (
          <p className="text-muted small mt-2 mb-2">Este artículo no tiene ofertas.</p>
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
            error={errorDeBaja || null}
            onConfirmar={() => void confirmarBaja()}
            onCancelar={cancelarBaja}
          />
        )}
      </div>
    </>
  )
}
