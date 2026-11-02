import { Fragment, useId } from 'react'
import {
  importesDifieren,
  lineasDeConfirmacion,
  type CandidatoDeConfirmacion,
  type FilaDeConciliacion,
} from '../../api/conciliacionMercadoPago'
import { Modal } from '../../componentes/Modal'

type Props = {
  fila: FilaDeConciliacion
  candidato: CandidatoDeConfirmacion
  /** La escritura está en vuelo: el diálogo entero queda inerte. */
  ocupado: boolean
  /** Rechazo de la confirmación; el diálogo sigue abierto. */
  error: string
  onConfirmar: () => void
  onCancelar: () => void
}

/** Confirmación de una fila contra un pago de venta sin verificar: pone lado a lado importes y fechas antes de escribir. */
export function DialogoDeConfirmacionDeFila({ fila, candidato, ocupado, error, onConfirmar, onCancelar }: Props) {
  const idDescripcion = useId()
  const lineas = lineasDeConfirmacion(fila, candidato)

  return (
    <Modal
      titulo="Confirmar pago contra el reporte"
      tamano="sm"
      rol="alertdialog"
      idDescripcion={idDescripcion}
      ocupado={ocupado}
      onCerrar={onCancelar}
      pie={
        <>
          <button type="button" className="btn btn-primary" disabled={ocupado} onClick={onConfirmar}>
            {ocupado ? 'Confirmando…' : 'Confirmar'}
          </button>
          <button type="button" className="btn btn-outline-secondary" disabled={ocupado} onClick={onCancelar} autoFocus>
            Cancelar
          </button>
        </>
      }
    >
      <div id={idDescripcion}>
        <p className="mb-2">
          El pago de la venta pasa a <strong>verificado manualmente</strong>. Revisá que sean la misma operación.
        </p>
        <dl className="row mb-2">
          {lineas.map((linea) => (
            <Fragment key={linea.etiqueta}>
              <dt className="col-6">{linea.etiqueta}</dt>
              <dd className="col-6 mb-1">{linea.valor}</dd>
            </Fragment>
          ))}
        </dl>
        {importesDifieren(fila, candidato) && (
          <p className="alert alert-warning py-1 px-2 small mb-0">Los importes no coinciden: el servidor va a rechazar la confirmación.</p>
        )}
      </div>
      {error && (
        <div className="alert alert-danger mt-3 mb-0" role="alert">
          {error}
        </div>
      )}
    </Modal>
  )
}
