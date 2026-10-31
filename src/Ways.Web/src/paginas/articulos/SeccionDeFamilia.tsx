import { useRef, useState } from 'react'
import type { FamiliaDetalle, ListaPrecioListado } from '../../api/tipos'
import { Cargando } from '../../componentes/Cargando'
import { ConfirmacionDeBaja } from '../../componentes/ConfirmacionDeBaja'
import { Modal } from '../../componentes/Modal'
import { TituloDeSeccion } from '../../componentes/TituloDeSeccion'
import { VistaDePrevisualizacion } from '../familias/VistaDePrevisualizacion'
import type { NombresDeCatalogo } from '../familias/valoresCompartidos'
import { AltaRapidaFamilia } from './AltaRapidaFamilia'
import {
  advertenciaDeUnion,
  AVISO_DE_CAMBIOS_SIN_GUARDAR_EN_FAMILIA,
  AVISO_DE_UNION_DESACTUALIZADA,
  CAMPOS_PROPIOS,
  describirEstadoDePrecios,
  etiquetaDeFamilia,
  familiaDelArticulo,
  opcionesDeFamilia,
  previsualizacionDeUnionVigente,
  type AccionesDeFamiliaDelFormulario,
  type EstadoDeFamiliaDelFormulario,
  type EstadoDeUnionAFamilia,
} from './familia'

/** Lo que el selector muestra como elegido cuando hay una familia nueva anotada en un alta: no es un id de familia. */
const VALOR_FAMILIA_NUEVA = 'nueva'

type Props = {
  esNuevo: boolean
  idFamilia: number | ''
  /** En un alta, el nombre de la familia que se crea junto con el artículo; `''` si no se pidió ninguna. */
  nombreFamiliaNueva: string
  idDelArticulo: number | null
  codigoInterno: string
  nombreDelArticulo: string
  ocupado: boolean
  listasPrecio: ListaPrecioListado[]
  familia: EstadoDeFamiliaDelFormulario
  acciones: AccionesDeFamiliaDelFormulario
  union: EstadoDeUnionAFamilia
  errorDeFamiliaNueva: string
  /** El artículo existente tiene cambios sin guardar: el servidor lee los valores guardados, así que no se actúa sobre su familia. */
  sinGuardar: boolean
  nombresDeCatalogo: NombresDeCatalogo
  nombresDeListas: ReadonlyMap<number, string>
  /** La salida de la familia espera confirmación. Lo decide `FormularioArticulo`, que es quien deja inerte
   * el resto del formulario mientras la confirmación está abierta. */
  confirmandoSalida: boolean
  /** Motivo con el que el servidor rechazó la salida: se muestra dentro de la confirmación, que sigue abierta. */
  errorDeSalida: string
  onPedirSalida: (disparador: HTMLElement) => void
  onCancelarSalida: () => void
  onConfirmarSalida: () => void
}

/**
 * La familia del artículo en el formulario (doc 10 §3). El selector de familias está siempre: en un alta elige la familia de la
 * que toma sus campos o, con "+", anota una nueva que el servidor crea al guardar; en un artículo existente que no es miembro,
 * elegir una previsualiza lo que cambiaría y pide confirmar antes de unirlo, y "+" la crea con el artículo como referencia. Al
 * editar un miembro, su pertenencia y la salida de la familia. Si falló la carga de las familias el aviso se muestra igual, para
 * que el motivo no se esconda.
 */
export function SeccionDeFamilia({
  esNuevo,
  idFamilia,
  nombreFamiliaNueva,
  idDelArticulo,
  codigoInterno,
  nombreDelArticulo,
  ocupado,
  listasPrecio,
  familia,
  acciones,
  union,
  errorDeFamiliaNueva,
  sinGuardar,
  nombresDeCatalogo,
  nombresDeListas,
  confirmandoSalida,
  errorDeSalida,
  onPedirSalida,
  onCancelarSalida,
  onConfirmarSalida,
}: Props) {
  if (esNuevo || idFamilia === '') {
    return (
      <EleccionDeFamilia
        esNuevo={esNuevo}
        idFamilia={idFamilia}
        nombreFamiliaNueva={nombreFamiliaNueva}
        idDelArticulo={idDelArticulo}
        codigoInterno={codigoInterno}
        nombreDelArticulo={nombreDelArticulo}
        ocupado={ocupado}
        listasPrecio={listasPrecio}
        familia={familia}
        acciones={acciones}
        union={union}
        errorDeFamiliaNueva={errorDeFamiliaNueva}
        sinGuardar={sinGuardar}
        nombresDeCatalogo={nombresDeCatalogo}
        nombresDeListas={nombresDeListas}
      />
    )
  }

  return (
    <PertenenciaAFamilia
      idFamilia={idFamilia}
      nombreDelArticulo={nombreDelArticulo}
      ocupado={ocupado}
      familia={familia}
      confirmandoSalida={confirmandoSalida}
      errorDeSalida={errorDeSalida}
      onPedirSalida={onPedirSalida}
      onCancelarSalida={onCancelarSalida}
      onConfirmarSalida={onConfirmarSalida}
    />
  )
}

type PropsDeEleccion = Pick<
  Props,
  | 'esNuevo'
  | 'idFamilia'
  | 'nombreFamiliaNueva'
  | 'idDelArticulo'
  | 'codigoInterno'
  | 'nombreDelArticulo'
  | 'ocupado'
  | 'listasPrecio'
  | 'familia'
  | 'acciones'
  | 'union'
  | 'errorDeFamiliaNueva'
  | 'sinGuardar'
  | 'nombresDeCatalogo'
  | 'nombresDeListas'
>

function EleccionDeFamilia({
  esNuevo,
  idFamilia,
  nombreFamiliaNueva,
  idDelArticulo,
  codigoInterno,
  nombreDelArticulo,
  ocupado,
  listasPrecio,
  familia,
  acciones,
  union,
  errorDeFamiliaNueva,
  sinGuardar,
  nombresDeCatalogo,
  nombresDeListas,
}: PropsDeEleccion) {
  const [altaAbierta, setAltaAbierta] = useState(false)
  const refSelect = useRef<HTMLSelectElement>(null)
  const opciones = opcionesDeFamilia(familia.opciones)
  const conCambiosSinGuardar = !esNuevo && sinGuardar
  const elegida = esNuevo ? idFamilia : union.idElegida
  const valorDelSelector = elegida !== '' ? elegida : nombreFamiliaNueva !== '' ? VALOR_FAMILIA_NUEVA : ''

  function alElegir(valor: string) {
    if (valor === VALOR_FAMILIA_NUEVA) return
    acciones.elegir(valor === '' ? '' : Number(valor))
  }

  // Se enfoca el selector antes de montar el modal para que `Modal` lo capture como el foco previo y se lo devuelva al cerrar.
  function abrirAlta() {
    refSelect.current?.focus()
    setAltaAbierta(true)
  }

  return (
    <div className="mb-3">
      <TituloDeSeccion>Familia</TituloDeSeccion>
      {familia.errorOpciones && <div className="alert alert-warning py-1 px-2 small mt-2">{familia.errorOpciones}</div>}

      <div className="row g-3 mt-0">
        <div className="col-md-6">
          <label className="form-label" htmlFor="art-familia">
            Familia (opcional)
          </label>
          <div className="input-group">
            <select
              id="art-familia"
              ref={refSelect}
              className="form-select"
              aria-describedby="art-familia-ayuda"
              value={valorDelSelector}
              disabled={ocupado || conCambiosSinGuardar}
              onChange={(e) => alElegir(e.target.value)}
            >
              <option value="">Sin familia</option>
              {nombreFamiliaNueva !== '' && <option value={VALOR_FAMILIA_NUEVA}>Nueva: {nombreFamiliaNueva}</option>}
              {opciones.map((o) => (
                <option key={o.valor} value={o.valor}>
                  {o.etiqueta}
                </option>
              ))}
            </select>
            <button
              type="button"
              className="btn btn-outline-secondary"
              aria-label="Nueva familia"
              disabled={ocupado || conCambiosSinGuardar}
              onClick={abrirAlta}
            >
              +
            </button>
          </div>
          <div id="art-familia-ayuda" className="form-text">
            {esNuevo
              ? 'Con una familia, el artículo toma sus campos compartidos y los precios de cada lista.'
              : 'Elegir una familia muestra qué cambia en el artículo y pide confirmar antes de unirlo.'}
          </div>
          {conCambiosSinGuardar && <div className="form-text text-warning-emphasis">{AVISO_DE_CAMBIOS_SIN_GUARDAR_EN_FAMILIA}</div>}
        </div>
      </div>

      {esNuevo && nombreFamiliaNueva !== '' && (
        <div className="alert alert-info py-2 px-2 small mt-2 d-flex flex-wrap align-items-center gap-2">
          <span>{`Familia nueva: ${nombreFamiliaNueva}`}</span>
          <span>Se crea al guardar el artículo, que será su referencia.</span>
          <button type="button" className="btn btn-sm btn-outline-secondary" disabled={ocupado} onClick={abrirAlta}>
            Cambiar nombre
          </button>
          <button type="button" className="btn btn-sm btn-outline-secondary" disabled={ocupado} onClick={acciones.quitarFamiliaNueva}>
            Quitar
          </button>
        </div>
      )}
      {esNuevo && errorDeFamiliaNueva && (
        <div className="alert alert-danger py-2 px-2 small mt-2" role="alert">
          {errorDeFamiliaNueva}
        </div>
      )}

      {!esNuevo && union.error && !hayConfirmacionDeUnion(familia, union, union.idElegida) && (
        <div className="alert alert-danger py-2 px-2 small mt-2" role="alert">
          {union.error}
        </div>
      )}

      {idFamilia !== '' && <FamiliaElegida idFamilia={idFamilia} listasPrecio={listasPrecio} familia={familia} acciones={acciones} />}
      {!esNuevo && union.idElegida !== '' && idDelArticulo !== null && (
        <PropuestaDeUnion
          idDelArticulo={idDelArticulo}
          codigoInterno={codigoInterno}
          nombreDelArticulo={nombreDelArticulo}
          ocupado={ocupado}
          sinGuardar={sinGuardar}
          familia={familia}
          acciones={acciones}
          union={union}
          idElegida={union.idElegida}
          nombresDeCatalogo={nombresDeCatalogo}
          nombresDeListas={nombresDeListas}
        />
      )}

      {altaAbierta && (
        <AltaRapidaFamilia
          familias={familia.opciones}
          nombreInicial={nombreFamiliaNueva}
          onConfirmar={acciones.crearFamilia}
          onCerrar={() => setAltaAbierta(false)}
        />
      )}
    </div>
  )
}

/** La confirmación de la unión (el modal de `PropuestaDeUnion`) está abierta: ya hay una previsualización de la familia elegida. */
function hayConfirmacionDeUnion(familia: EstadoDeFamiliaDelFormulario, union: EstadoDeUnionAFamilia, idElegida: number | ''): boolean {
  return (
    idElegida !== '' &&
    !familia.cargando &&
    !familia.error &&
    familia.detalle?.id === idElegida &&
    !union.previsualizando &&
    !union.errorDePrevisualizacion &&
    union.previsualizacion !== null
  )
}

/** La unión de un artículo existente a la familia elegida: carga, previsualización y confirmación. Nada se escribe hasta confirmar. */
function PropuestaDeUnion({
  idDelArticulo,
  codigoInterno,
  nombreDelArticulo,
  ocupado,
  sinGuardar,
  familia,
  acciones,
  union,
  idElegida,
  nombresDeCatalogo,
  nombresDeListas,
}: Pick<
  Props,
  'codigoInterno' | 'nombreDelArticulo' | 'ocupado' | 'sinGuardar' | 'familia' | 'acciones' | 'union' | 'nombresDeCatalogo' | 'nombresDeListas'
> & { idDelArticulo: number; idElegida: number }) {
  const detalle = familia.detalle

  if (familia.cargando) return <Cargando texto="Cargando la familia…" />

  if (familia.error) {
    return (
      <div className="alert alert-danger py-2 px-2 small mt-2 d-flex flex-wrap align-items-center gap-2">
        <span>{familia.error}</span>
        <button type="button" className="btn btn-sm btn-outline-danger" onClick={acciones.reintentar}>
          Reintentar
        </button>
        <button type="button" className="btn btn-sm btn-outline-secondary" onClick={() => acciones.elegir('')}>
          Cancelar
        </button>
      </div>
    )
  }

  if (detalle === null || detalle.id !== idElegida) return null
  if (union.previsualizando) return <Cargando texto="Calculando qué cambia…" />

  if (union.errorDePrevisualizacion) {
    return (
      <div className="alert alert-danger py-2 px-2 small mt-2 d-flex flex-wrap align-items-center gap-2">
        <span>{union.errorDePrevisualizacion}</span>
        <button type="button" className="btn btn-sm btn-outline-danger" onClick={acciones.reintentar}>
          Reintentar
        </button>
        <button type="button" className="btn btn-sm btn-outline-secondary" onClick={() => acciones.elegir('')}>
          Cancelar
        </button>
      </div>
    )
  }

  const previsualizacion = union.previsualizacion
  if (previsualizacion === null) return null

  const vigente = previsualizacionDeUnionVigente(previsualizacion, idElegida)
  const conProblemas = previsualizacion.problemas.length > 0
  const articulos = new Map([[idDelArticulo, { id: idDelArticulo, codigoInterno, nombre: nombreDelArticulo }]])

  return (
    <Modal
      titulo="Unir el artículo a la familia"
      tamano="lg"
      desplazable
      ocupado={ocupado}
      onCerrar={() => acciones.elegir('')}
      pie={
        <>
          <button
            type="button"
            className="btn btn-success"
            disabled={ocupado || conProblemas || !vigente || sinGuardar}
            onClick={() => void acciones.confirmarUnion()}
          >
            {union.escribiendo ? 'Uniendo…' : 'Confirmar y unir a la familia'}
          </button>
          <button type="button" className="btn btn-outline-secondary" disabled={ocupado} onClick={() => acciones.elegir('')}>
            Cancelar
          </button>
        </>
      }
    >
      <p className="small mb-2">{advertenciaDeUnion(detalle.nombre)}</p>
      <VistaDePrevisualizacion previsualizacion={previsualizacion} articulos={articulos} nombres={nombresDeCatalogo} listas={nombresDeListas} />
      {!vigente && <div className="alert alert-warning py-1 px-2 small">{AVISO_DE_UNION_DESACTUALIZADA}</div>}
      {union.error && (
        <div className="alert alert-danger py-2 px-2 small mt-2 mb-0" role="alert">
          {union.error}
        </div>
      )}
    </Modal>
  )
}

function FamiliaElegida({
  idFamilia,
  listasPrecio,
  familia,
  acciones,
}: Pick<Props, 'idFamilia' | 'listasPrecio' | 'familia' | 'acciones'>) {
  if (familia.cargando) return <Cargando texto="Cargando la familia…" />

  if (familia.error) {
    return (
      <div className="alert alert-danger py-2 px-2 small mt-2 d-flex flex-wrap align-items-center gap-2">
        <span>
          {familia.error} El artículo no se puede guardar con esta familia hasta que cargue: reintentá o elegí "Sin familia".
        </span>
        <button type="button" className="btn btn-sm btn-outline-danger" onClick={acciones.reintentar}>
          Reintentar
        </button>
      </div>
    )
  }

  const detalle = familia.detalle
  if (detalle === null || detalle.id !== idFamilia) return null

  if (detalle.valores === null) {
    return (
      <div className="alert alert-warning py-2 px-2 small mt-2">
        La familia "{detalle.nombre}" no tiene artículos vivos: no hay valores ni precios que copiar. El artículo no se puede guardar en ella.
      </div>
    )
  }

  return (
    <div className="mt-2">
      <p className="small mb-2">
        El artículo toma de la familia "{detalle.nombre}" los campos marcados "(de la familia)" y los precios de cada lista; no se pueden
        cambiar acá. Son propios del artículo: {CAMPOS_PROPIOS}.
      </p>
      <PreciosDeLaFamilia detalle={detalle} listasPrecio={listasPrecio} />
    </div>
  )
}

function PreciosDeLaFamilia({ detalle, listasPrecio }: { detalle: FamiliaDetalle; listasPrecio: ListaPrecioListado[] }) {
  return (
    <div className="table-responsive">
      <table className="table table-sm table-bordered align-middle mb-0">
        <caption className="caption-top small text-body-secondary">Precios que se copian de la familia</caption>
        <thead>
          <tr>
            <th>Lista</th>
            <th>Precio</th>
          </tr>
        </thead>
        <tbody>
          {detalle.precios.map((precio) => (
            <tr key={precio.idListaPrecio}>
              <td>{listasPrecio.find((l) => l.id === precio.idListaPrecio)?.nombre ?? `Lista ${precio.idListaPrecio}`}</td>
              <td>{describirEstadoDePrecios(precio.estado)}</td>
            </tr>
          ))}
          {detalle.precios.length === 0 && (
            <tr>
              <td colSpan={2} className="text-center text-muted">
                No hay listas de precio fijas.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}

function PertenenciaAFamilia({
  idFamilia,
  nombreDelArticulo,
  ocupado,
  familia,
  confirmandoSalida,
  errorDeSalida,
  onPedirSalida,
  onCancelarSalida,
  onConfirmarSalida,
}: Pick<
  Props,
  'nombreDelArticulo' | 'ocupado' | 'familia' | 'confirmandoSalida' | 'errorDeSalida' | 'onPedirSalida' | 'onCancelarSalida' | 'onConfirmarSalida'
> & {
  idFamilia: number
}) {
  const pertenencia = familiaDelArticulo(idFamilia, familia.detalle)

  return (
    <div className="mb-3">
      <TituloDeSeccion>Familia</TituloDeSeccion>
      <div className="d-flex flex-wrap align-items-center gap-2 mt-2">
        <span className="badge text-bg-info">{etiquetaDeFamilia(pertenencia)}</span>
        <button
          type="button"
          className="btn btn-sm btn-outline-secondary"
          disabled={ocupado || confirmandoSalida}
          onClick={(evento) => onPedirSalida(evento.currentTarget)}
        >
          Sacar de la familia
        </button>
      </div>
      {familia.error && <div className="alert alert-warning py-1 px-2 small mt-2">{familia.error}</div>}
      <div className="form-text">
        Los campos compartidos y los precios son iguales en toda la familia: al cambiarlos se pregunta si el cambio llega a todos los
        artículos o solo a este.
      </div>

      {confirmandoSalida && (
        <ConfirmacionDeBaja
          titulo={`${nombreDelArticulo.trim() ? `"${nombreDelArticulo.trim()}"` : 'este artículo'} de ${
            pertenencia !== null && pertenencia.nombre !== null ? `la familia "${pertenencia.nombre}"` : 'su familia'
          }`}
          pregunta="Sacar"
          nota="El artículo queda sin familia y conserva todos sus valores y sus precios; los demás artículos de la familia no cambian."
          etiquetaConfirmar="Confirmar salida"
          etiquetaEnCurso="Sacando…"
          ocupado={ocupado}
          error={errorDeSalida || null}
          onConfirmar={onConfirmarSalida}
          onCancelar={onCancelarSalida}
        />
      )}
    </div>
  )
}
