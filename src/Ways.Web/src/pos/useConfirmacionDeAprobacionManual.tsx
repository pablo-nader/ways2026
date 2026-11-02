import { useCallback, useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { Modal } from '../componentes/Modal'
import { formatearImporte } from '../formato/importes'

type Pendiente = { importe: number; nombreDelMedio: string }

/** Lo que el cajero confirmó: la referencia es el número de operación que tipeó, o `null` si no lo tiene. */
export type AprobacionManualConfirmada = { referencia: string | null }

/**
 * Confirmación de "Aprobar a mano (sin verificar)" de un pago de Mercado Pago que no se pudo cobrar. Nunca actúa en silencio: dice que el
 * pago no está verificado y que se controla en la conciliación, y pide el número de operación (opcional). Resuelve la confirmación o
 * `null` si el cajero se arrepiente. Una confirmación nueva resuelve la anterior como rechazada antes de reemplazarla, y al desmontar la
 * pendiente también se rechaza: ninguna promesa queda colgada.
 */
export function useConfirmacionDeAprobacionManual(): {
  modal: ReactNode
  pedir: (pago: Pendiente) => Promise<AprobacionManualConfirmada | null>
} {
  const [pendiente, setPendiente] = useState<Pendiente | null>(null)
  const [referencia, setReferencia] = useState('')
  const resolverRef = useRef<((confirmada: AprobacionManualConfirmada | null) => void) | null>(null)
  const idDelCampo = useId()

  const pedir = useCallback(
    (pago: Pendiente) =>
      new Promise<AprobacionManualConfirmada | null>((resolver) => {
        resolverRef.current?.(null)
        resolverRef.current = resolver
        setReferencia('')
        setPendiente(pago)
      }),
    [],
  )

  useEffect(
    () => () => {
      const resolver = resolverRef.current
      resolverRef.current = null
      resolver?.(null)
    },
    [],
  )

  function responder(confirmada: AprobacionManualConfirmada | null) {
    const resolver = resolverRef.current
    resolverRef.current = null
    setPendiente(null)
    resolver?.(confirmada)
  }

  const modal =
    pendiente === null ? null : (
      <Modal
        titulo="Aprobar a mano (sin verificar)"
        noDescartable
        rol="alertdialog"
        restaurarFoco={false}
        pie={
          <>
            <button type="button" className="btn btn-outline-secondary" onClick={() => responder(null)}>
              Volver
            </button>
            <button
              type="button"
              className="btn btn-danger"
              onClick={() => responder({ referencia: referencia.trim() === '' ? null : referencia.trim() })}
            >
              Aprobar a mano
            </button>
          </>
        }
      >
        <p>
          Vas a dar por pagados {formatearImporte(pendiente.importe, { simbolo: true })} con {pendiente.nombreDelMedio} sin que Mercado Pago lo confirme. El pago queda
          marcado como <strong>sin verificar</strong> y se controla en la conciliación de Mercado Pago. Aprobalo solo si el cliente te mostró que ya pagó.
        </p>
        <label htmlFor={idDelCampo} className="form-label">
          Número de operación (opcional)
        </label>
        <input
          id={idDelCampo}
          type="text"
          className="form-control"
          autoFocus
          autoComplete="off"
          maxLength={60}
          value={referencia}
          onChange={(evento) => setReferencia(evento.target.value)}
        />
      </Modal>
    )

  return { modal, pedir }
}
