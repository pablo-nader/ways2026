import { useId, useRef, useState } from 'react'
import { Modal } from '../../componentes/Modal'

type Props = {
  /** Cambia la advertencia: con artículos se revierte el stock que entró. */
  tieneArticulos: boolean
  /** `true` mientras la anulación está en vuelo: el modal entero queda inerte. */
  anulando: boolean
  /** Cualquier otra escritura de la pantalla en vuelo: bloquea el botón que confirma. */
  bloqueado: boolean
  /** Motivo del rechazo; se muestra dentro del modal, que sigue abierto. */
  error: string
  /** Solo se invoca con la casilla tildada: el botón que la dispara está deshabilitado sin ella. */
  onConfirmar: () => void
  onCancelar: () => void
}

export function ModalDeAnulacionDeCompra({ tieneArticulos, anulando, bloqueado, error, onConfirmar, onCancelar }: Props) {
  const [confirmado, setConfirmado] = useState(false)
  const confirmandoRef = useRef(false)
  const idDescripcion = useId()

  function alConfirmar() {
    if (confirmandoRef.current) return
    confirmandoRef.current = true
    queueMicrotask(() => {
      confirmandoRef.current = false
    })
    onConfirmar()
  }

  return (
    <Modal
      titulo="Anular compra"
      tamano="sm"
      rol="alertdialog"
      idDescripcion={idDescripcion}
      ocupado={anulando}
      onCerrar={onCancelar}
      pie={
        <>
          <button type="button" className="btn btn-danger" disabled={!confirmado || bloqueado || anulando} onClick={alConfirmar}>
            {anulando ? 'Anulando…' : 'Anular'}
          </button>
          <button type="button" className="btn btn-outline-secondary" disabled={anulando} onClick={onCancelar}>
            Cancelar
          </button>
        </>
      }
    >
      <div id={idDescripcion} className="form-check">
        <input
          id="compra-confirmacion-anular"
          type="checkbox"
          className="form-check-input"
          checked={confirmado}
          disabled={anulando}
          autoFocus
          onChange={(e) => setConfirmado(e.target.checked)}
        />
        <label className="form-check-label" htmlFor="compra-confirmacion-anular">
          {tieneArticulos
            ? 'Confirmo que quiero anular esta compra. Es irreversible: se revierte el stock que entró, el costo del artículo NO se corrige solo (se edita aparte).'
            : 'Confirmo que quiero anular esta compra. Es irreversible.'}
        </label>
      </div>
      {error && (
        <div className="alert alert-danger mt-3 mb-0" role="alert">
          {error}
        </div>
      )}
    </Modal>
  )
}
