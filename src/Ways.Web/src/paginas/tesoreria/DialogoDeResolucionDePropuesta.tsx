import { useId } from 'react'
import {
  etiquetaDeTipoDePropuesta,
  textoDeEfecto,
  type AccionDePropuesta,
  type PropuestaDeTesoreria,
} from '../../api/conciliacionMercadoPago'
import { Modal } from '../../componentes/Modal'

type Props = {
  propuesta: PropuestaDeTesoreria
  accion: AccionDePropuesta
  /** Nombre de la cuenta que recibe un retiro, elegido en la lista. */
  nombreDeDestino: string | null
  /** Nombre de la cuenta de la que sale un retiro cuando el Admin la eligió; si el origen es fijo, viene en la propuesta. */
  nombreDeOrigen: string | null
  /** La escritura está en vuelo: el diálogo entero queda inerte. */
  ocupado: boolean
  /** Rechazo de la resolución; el diálogo sigue abierto. */
  error: string
  onConfirmar: () => void
  onCancelar: () => void
}

/** Confirmación de aplicar o descartar una propuesta de tesorería: dice qué se va a escribir antes de hacerlo. */
export function DialogoDeResolucionDePropuesta({
  propuesta,
  accion,
  nombreDeDestino,
  nombreDeOrigen,
  ocupado,
  error,
  onConfirmar,
  onCancelar,
}: Props) {
  const idDescripcion = useId()
  const aplicar = accion === 'aplicar'
  const efecto = textoDeEfecto({
    ...propuesta,
    nombreCuentaOrigen: propuesta.nombreCuentaOrigen ?? nombreDeOrigen,
    nombreCuentaDestino: nombreDeDestino,
  })

  return (
    <Modal
      titulo={aplicar ? 'Aplicar movimiento a tesorería' : 'Descartar propuesta'}
      tamano="sm"
      rol="alertdialog"
      idDescripcion={idDescripcion}
      ocupado={ocupado}
      onCerrar={onCancelar}
      pie={
        <>
          <button type="button" className="btn btn-primary" disabled={ocupado} onClick={onConfirmar}>
            {ocupado ? (aplicar ? 'Aplicando…' : 'Descartando…') : aplicar ? 'Aplicar' : 'Descartar'}
          </button>
          <button type="button" className="btn btn-outline-secondary" disabled={ocupado} onClick={onCancelar} autoFocus>
            Cancelar
          </button>
        </>
      }
    >
      <div id={idDescripcion}>
        <p className="mb-2">
          <strong>{etiquetaDeTipoDePropuesta(propuesta.tipo)}</strong>: {efecto}.
        </p>
        <p className="mb-0">
          {aplicar
            ? 'Se escribe en el libro de las cuentas y no se puede deshacer desde acá.'
            : 'No se escribe nada en la tesorería y la propuesta no se vuelve a ofrecer.'}
        </p>
      </div>
      {error && (
        <div className="alert alert-danger mt-3 mb-0" role="alert">
          {error}
        </div>
      )}
    </Modal>
  )
}
