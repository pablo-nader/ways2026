import { useEffect, useRef } from 'react'
import type { FamiliaDetalle, ListaPrecioListado } from '../../api/tipos'
import { Cargando } from '../../componentes/Cargando'
import {
  CAMPOS_PROPIOS,
  describirEstadoDePrecios,
  etiquetaDeFamilia,
  familiaDelArticulo,
  opcionesDeFamilia,
  type AccionesDeFamiliaDelFormulario,
  type EstadoDeFamiliaDelFormulario,
} from './familia'

type Props = {
  esNuevo: boolean
  idFamilia: number | ''
  nombreDelArticulo: string
  ocupado: boolean
  listasPrecio: ListaPrecioListado[]
  familia: EstadoDeFamiliaDelFormulario
  acciones: AccionesDeFamiliaDelFormulario
  /** La salida de la familia espera confirmación. Lo decide `FormularioArticulo`, que es quien deja inerte
   * el resto del formulario mientras la confirmación está abierta. */
  confirmandoSalida: boolean
  onPedirSalida: (disparador: HTMLElement) => void
  onCancelarSalida: () => void
  onConfirmarSalida: () => void
}

/**
 * La familia del artículo en el formulario (doc 10 §3). En un alta, el selector opcional de la familia a la
 * que se suma el artículo, con lo que toma de ella; al editar un miembro, su pertenencia y la salida de la
 * familia. El selector solo se ofrece cuando hay familias para elegir (o cuando falló su carga, para que el
 * motivo no se esconda).
 */
export function SeccionDeFamilia({
  esNuevo,
  idFamilia,
  nombreDelArticulo,
  ocupado,
  listasPrecio,
  familia,
  acciones,
  confirmandoSalida,
  onPedirSalida,
  onCancelarSalida,
  onConfirmarSalida,
}: Props) {
  if (esNuevo) {
    return <EleccionDeFamilia idFamilia={idFamilia} ocupado={ocupado} listasPrecio={listasPrecio} familia={familia} acciones={acciones} />
  }

  if (idFamilia === '') return null

  return (
    <PertenenciaAFamilia
      idFamilia={idFamilia}
      nombreDelArticulo={nombreDelArticulo}
      ocupado={ocupado}
      familia={familia}
      confirmandoSalida={confirmandoSalida}
      onPedirSalida={onPedirSalida}
      onCancelarSalida={onCancelarSalida}
      onConfirmarSalida={onConfirmarSalida}
    />
  )
}

function EleccionDeFamilia({
  idFamilia,
  ocupado,
  listasPrecio,
  familia,
  acciones,
}: Pick<Props, 'idFamilia' | 'ocupado' | 'listasPrecio' | 'familia' | 'acciones'>) {
  const opciones = opcionesDeFamilia(familia.opciones)

  if (opciones.length === 0 && familia.errorOpciones === '' && idFamilia === '') return null

  return (
    <div className="mb-3">
      <strong className="text-muted small text-uppercase">Familia</strong>
      {familia.errorOpciones && <div className="alert alert-warning py-1 px-2 small mt-2">{familia.errorOpciones}</div>}

      {opciones.length > 0 && (
        <div className="row g-3 mt-0">
          <div className="col-md-6">
            <label className="form-label" htmlFor="art-familia">
              Familia (opcional)
            </label>
            <select
              id="art-familia"
              className="form-select"
              aria-describedby="art-familia-ayuda"
              value={idFamilia}
              disabled={ocupado}
              onChange={(e) => acciones.elegir(e.target.value === '' ? '' : Number(e.target.value))}
            >
              <option value="">Sin familia</option>
              {opciones.map((o) => (
                <option key={o.valor} value={o.valor}>
                  {o.etiqueta}
                </option>
              ))}
            </select>
            <div id="art-familia-ayuda" className="form-text">
              Con una familia, el artículo toma sus campos compartidos y los precios de cada lista.
            </div>
          </div>
        </div>
      )}

      {idFamilia !== '' && <FamiliaElegida idFamilia={idFamilia} listasPrecio={listasPrecio} familia={familia} acciones={acciones} />}
    </div>
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
  onPedirSalida,
  onCancelarSalida,
  onConfirmarSalida,
}: Pick<Props, 'nombreDelArticulo' | 'ocupado' | 'familia' | 'confirmandoSalida' | 'onPedirSalida' | 'onCancelarSalida' | 'onConfirmarSalida'> & {
  idFamilia: number
}) {
  const cancelarRef = useRef<HTMLButtonElement>(null)
  const pertenencia = familiaDelArticulo(idFamilia, familia.detalle)

  // Al abrirse la confirmación el foco va a "Cancelar", la respuesta que no escribe nada.
  useEffect(() => {
    if (confirmandoSalida) cancelarRef.current?.focus()
  }, [confirmandoSalida])

  return (
    <div className="mb-3">
      <strong className="text-muted small text-uppercase">Familia</strong>
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
        <div className="alert alert-warning mt-2 mb-0" role="group" aria-label="Confirmar salida de la familia">
          <p className="mb-2">
            <strong>
              ¿Sacar {nombreDelArticulo.trim() ? `"${nombreDelArticulo.trim()}"` : 'este artículo'} de{' '}
              {pertenencia !== null && pertenencia.nombre !== null ? `la familia "${pertenencia.nombre}"` : 'su familia'}?
            </strong>
          </p>
          <p className="mb-3">
            El artículo queda sin familia y conserva todos sus valores y sus precios; los demás artículos de la familia no cambian.
          </p>
          <div className="d-flex gap-2">
            <button type="button" className="btn btn-warning" disabled={ocupado} onClick={onConfirmarSalida}>
              {ocupado ? 'Sacando…' : 'Confirmar salida'}
            </button>
            <button ref={cancelarRef} type="button" className="btn btn-outline-secondary" disabled={ocupado} onClick={onCancelarSalida}>
              Cancelar
            </button>
          </div>
        </div>
      )}
    </div>
  )
}
