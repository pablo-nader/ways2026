import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { ErrorApi } from '../../api/cliente'
import { clienteDeFamilias } from '../../api/familias'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import { CAMPOS_COMPARTIDOS, CAMPOS_PROPIOS, CLAVES_DE_COMPOSICION } from '../articulos/familia'
import { avisoDeCreacion, LIMITE_DE_ARTICULOS } from './agrupacion'
import { AYUDA_DE_DESGLOSAR_COSTO, ETIQUETA_DE_DESGLOSAR_COSTO } from './edicion'
import { ListaDeElegidos } from './ListaDeElegidos'
import { mensajeDeFalloDeEscritura } from './mensajes'
import { SelectorDeArticulos, type ArticuloElegido } from './SelectorDeArticulos'
import { useCatalogosDeFamilias } from './useCatalogosDeFamilias'
import { useMontado } from './useMontado'
import { usePrevisualizacion } from './usePrevisualizacion'
import { VistaDePrevisualizacion } from './VistaDePrevisualizacion'

const ID_FORMULARIO_NUEVA_FAMILIA = 'formulario-nueva-familia'

/** Los rechazos que hablan del nombre y no de lo elegido: lo previsualizado sigue valiendo. */
const CODIGOS_DEL_NOMBRE = ['familia_nombre_duplicado', 'nombre_requerido', 'nombre_muy_largo']

const AYUDA_DE_REFERENCIA_CON_FAMILIA =
  'El artículo de referencia ya es miembro de una familia: agrupar no mueve a nadie de la que tiene. Para sumarle artículos usá el detalle de su familia.'

/** `Alícuota de IVA` → `alícuota de IVA`: baja solo la inicial, para no tocar una sigla. */
function etiquetaEnMinuscula(etiqueta: string): string {
  return etiqueta.charAt(0).toLowerCase() + etiqueta.slice(1)
}

/** Lo que hace falta para crear: una previsualización sin problemas, un nombre y una referencia. Es un type guard sobre
 * la referencia: quien lo pregunta ya la tiene, y no tiene que volver a preguntar por ella. */
function listaParaCrear(referencia: ArticuloElegido | null, sinProblemas: boolean, nombre: string): referencia is ArticuloElegido {
  return sinProblemas && referencia !== null && nombre.trim() !== ''
}

type Props = {
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCancelar: () => void
}

/**
 * Nueva familia (doc 10 §3, "Gestión de familias") en un modal: el nombre, el artículo de referencia y los demás
 * artículos. Todos toman de la referencia los dieciséis campos compartidos y el precio de cada lista fija. Antes de crear
 * se previsualiza qué cambia en cada uno; los problemas de la previsualización bloquean "Crear familia", y cualquier
 * cambio de la referencia o de los artículos elegidos descarta lo previsualizado. Crear es todo o nada, en el servidor.
 * Un modal cerrado descarta todo lo tipeado y elegido: el estado vive acá, no en el listado. Al crear navega al detalle
 * de la familia, así que la respuesta consulta `useMontado` antes (el modal no se puede cerrar con el POST en vuelo, pero
 * el usuario puede irse por el menú o el Atrás).
 */
export function ModalDeNuevaFamilia({ focoDeReserva, onCancelar }: Props) {
  const navegar = useNavigate()
  const catalogos = useCatalogosDeFamilias()
  const [nombre, setNombre] = useState('')
  const [permiteDesglosarCosto, setPermiteDesglosarCosto] = useState(false)
  const [referencia, setReferencia] = useState<ArticuloElegido | null>(null)
  const [elegidos, setElegidos] = useState<ArticuloElegido[]>([])
  const [selector, setSelector] = useState<'referencia' | 'otros' | null>(null)
  const [creando, setCreando] = useState(false)
  const [errorDeCreacion, setErrorDeCreacion] = useState('')
  const { previsualizacion, previsualizando, error: errorDePrevisualizacion, previsualizar, invalidar } = usePrevisualizacion()
  const creandoRef = useRef(false)
  const errorRef = useRef<HTMLDivElement>(null)
  const montado = useMontado()

  // El botón que se envió queda deshabilitado y el foco caería en <body>: ante un rechazo pasa al mensaje.
  useEffect(() => {
    if (errorDeCreacion) errorRef.current?.focus()
  }, [errorDeCreacion])

  const bloqueado = previsualizando || creando
  const excedido = elegidos.length > LIMITE_DE_ARTICULOS
  // Si la referencia ya tiene familia, la previsualización lo sabe (`idFamilia`): agrupar sería sumar a ESA familia,
  // que es lo que hace el detalle de la familia, no este modal.
  const idFamiliaDeLaReferencia = previsualizacion?.idFamilia ?? null
  const referenciaConFamilia = idFamiliaDeLaReferencia !== null
  const sinProblemas = previsualizacion !== null && previsualizacion.problemas.length === 0 && !referenciaConFamilia
  const listoParaCrear = listaParaCrear(referencia, sinProblemas, nombre)

  const camposAlineados = CAMPOS_COMPARTIDOS.filter((c) => !(permiteDesglosarCosto && CLAVES_DE_COMPOSICION.includes(c.clave)))
  // En un alta apagada y ausente son lo mismo: la marca viaja solo cuando está encendida.
  const conMarca = permiteDesglosarCosto ? { permiteDesglosarCosto: true } : {}
  const articulosPorId = new Map<number, ArticuloElegido>(elegidos.map((a) => [a.id, a]))

  function elegirReferencia(elegidosEnElSelector: ArticuloElegido[]) {
    setReferencia(elegidosEnElSelector[0] ?? null)
    invalidar()
    setErrorDeCreacion('')
    setSelector(null)
  }

  function elegirOtros(nuevos: ArticuloElegido[]) {
    setElegidos(nuevos)
    invalidar()
    setErrorDeCreacion('')
    setSelector(null)
  }

  /** La marca cambia qué campos se alinean con la referencia: lo previsualizado ya no habla de lo elegido. */
  function cambiarDesglose(permite: boolean) {
    setPermiteDesglosarCosto(permite)
    invalidar()
    setErrorDeCreacion('')
  }

  function quitarReferencia() {
    setReferencia(null)
    invalidar()
    setErrorDeCreacion('')
  }

  function quitar(idArticulo: number) {
    setElegidos((previos) => previos.filter((a) => a.id !== idArticulo))
    invalidar()
    setErrorDeCreacion('')
  }

  // Sin referencia el botón "Previsualizar" está deshabilitado y no tiene qué hacer.
  const pedirPrevisualizacion =
    referencia === null
      ? undefined
      : () => {
          setErrorDeCreacion('')
          void previsualizar({ idArticuloReferencia: referencia.id, idsArticulos: elegidos.map((a) => a.id), ...conMarca })
        }

  async function crear() {
    if (creandoRef.current || !listaParaCrear(referencia, sinProblemas, nombre)) return

    creandoRef.current = true
    setCreando(true)
    setErrorDeCreacion('')
    try {
      const resultado = await clienteDeFamilias.crear({
        nombre: nombre.trim(),
        idArticuloReferencia: referencia.id,
        idsArticulos: elegidos.map((a) => a.id),
        ...conMarca,
      })
      // Quien se fue mientras tanto (el menú, el Atrás) no vuelve a una pantalla que no pidió.
      if (!montado.current) return
      navegar(`/familias/${resultado.idFamilia}`, { state: { aviso: avisoDeCreacion(resultado) } })
    } catch (e) {
      setErrorDeCreacion(mensajeDeFalloDeEscritura(e, 'crear la familia'))
      // La foto quedó vieja (otro escritor cambió algo): hay que volver a previsualizar. Un rechazo del nombre no
      // habla de lo previsualizado, así que lo conserva.
      if (!(e instanceof ErrorApi && CODIGOS_DEL_NOMBRE.includes(e.codigo))) invalidar()
    } finally {
      creandoRef.current = false
      setCreando(false)
    }
  }

  const pie = (
    <>
      {previsualizacion === null && (
        <span className="me-auto small text-body-secondary">Previsualizá antes de crear: así ves qué cambia en cada artículo.</span>
      )}
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={creando}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO_NUEVA_FAMILIA} className="btn btn-success" disabled={bloqueado || !listoParaCrear}>
        {creando ? 'Creando…' : 'Crear familia'}
      </button>
    </>
  )

  return (
    <Modal titulo="Nueva familia" tamano="xl" desplazable ocupado={creando} focoDeReserva={focoDeReserva} pie={pie} onCerrar={onCancelar}>
      <div className="alert alert-info">
        Todos los artículos de la familia toman del artículo de referencia sus campos compartidos (
        {camposAlineados.map((c) => etiquetaEnMinuscula(c.etiqueta)).join(', ')}) y el precio de cada lista de precios fija: los que ya
        tengan otros valores o precios pasan a tener los de la referencia. Son propios de cada artículo: {CAMPOS_PROPIOS}
        {permiteDesglosarCosto && ', y la composición del costo (costo de lista, su base, descuento e impuesto interno)'}.
      </div>

      {catalogos.aviso && <div className="alert alert-warning">{catalogos.aviso}</div>}
      <div ref={errorRef} tabIndex={-1}>
        <ErrorDeFormulario mensaje={errorDeCreacion} />
      </div>

      <div className="mb-3">
        <div className="form-check">
          <input
            id="nf-desglosar-costo"
            type="checkbox"
            className="form-check-input"
            checked={permiteDesglosarCosto}
            disabled={bloqueado}
            aria-describedby="nf-desglosar-costo-ayuda"
            onChange={(e) => cambiarDesglose(e.target.checked)}
          />
          <label className="form-check-label" htmlFor="nf-desglosar-costo">
            {ETIQUETA_DE_DESGLOSAR_COSTO}
          </label>
        </div>
        <div id="nf-desglosar-costo-ayuda" className="form-text">
          {AYUDA_DE_DESGLOSAR_COSTO}
        </div>
      </div>

      <form
        id={ID_FORMULARIO_NUEVA_FAMILIA}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          void crear()
        }}
      >
        <fieldset disabled={bloqueado} className="border-0 p-0 m-0">
          <div className="row g-3 mb-3">
            <div className="col-md-6">
              <label className="form-label" htmlFor="nf-nombre">
                Nombre de la familia
              </label>
              <input
                id="nf-nombre"
                className="form-control"
                maxLength={150}
                value={nombre}
                onChange={(e) => setNombre(e.target.value)}
                required
              />
            </div>
          </div>

          <h6>Artículo de referencia</h6>
          {referencia === null ? (
            <p className="text-muted small mb-2">Todavía no elegiste el artículo de referencia.</p>
          ) : (
            <p className="mb-2">
              {referencia.codigoInterno} — {referencia.nombre}
            </p>
          )}
          <div className="d-flex gap-2 mb-3">
            <button type="button" className="btn btn-outline-primary" onClick={() => setSelector('referencia')}>
              {referencia === null ? 'Elegir artículo de referencia' : 'Cambiar artículo de referencia'}
            </button>
            {referencia !== null && (
              <button type="button" className="btn btn-outline-secondary" onClick={quitarReferencia}>
                Quitar referencia
              </button>
            )}
          </div>

          <h6>Otros artículos de la familia</h6>
          <ListaDeElegidos elegidos={elegidos} bloqueado={bloqueado} vacio="Todavía no elegiste otros artículos." onQuitar={quitar} />
          {excedido && (
            <div className="alert alert-warning py-1 px-2 small">
              Se pueden agrupar hasta {LIMITE_DE_ARTICULOS} artículos por vez además de la referencia: quitá{' '}
              {elegidos.length - LIMITE_DE_ARTICULOS} para poder previsualizar.
            </div>
          )}
          <div className="d-flex flex-wrap gap-2 mb-3">
            <button type="button" className="btn btn-outline-primary" onClick={() => setSelector('otros')}>
              Agregar artículos
            </button>
            <button type="button" className="btn btn-outline-secondary" disabled={referencia === null || excedido} onClick={pedirPrevisualizacion}>
              {previsualizando ? 'Previsualizando…' : 'Previsualizar'}
            </button>
          </div>

          {errorDePrevisualizacion && <div className="alert alert-danger">{errorDePrevisualizacion}</div>}

          {previsualizacion && (
            <div className="mb-3">
              {idFamiliaDeLaReferencia !== null && (
                <div className="alert alert-danger" role="alert">
                  {AYUDA_DE_REFERENCIA_CON_FAMILIA}{' '}
                  <Link to={`/familias/${idFamiliaDeLaReferencia}`} className="alert-link">
                    Ver el detalle de su familia
                  </Link>
                </div>
              )}
              <VistaDePrevisualizacion
                previsualizacion={previsualizacion}
                articulos={articulosPorId}
                nombres={catalogos.nombres}
                listas={catalogos.listas}
              />
            </div>
          )}
        </fieldset>
      </form>

      {selector === 'referencia' && (
        <SelectorDeArticulos
          titulo="Elegir el artículo de referencia"
          multiple={false}
          elegidos={referencia === null ? [] : [referencia]}
          excluidos={new Map(elegidos.map((a) => [a.id, 'Ya está elegido como otro artículo de la familia']))}
          onListo={elegirReferencia}
          onCerrar={() => setSelector(null)}
        />
      )}
      {selector === 'otros' && (
        <SelectorDeArticulos
          titulo="Elegir otros artículos de la familia"
          multiple
          elegidos={elegidos}
          excluidos={referencia === null ? undefined : new Map([[referencia.id, 'Ya es el artículo de referencia']])}
          onListo={elegirOtros}
          onCerrar={() => setSelector(null)}
        />
      )}
    </Modal>
  )
}
