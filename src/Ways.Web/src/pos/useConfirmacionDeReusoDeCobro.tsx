import { useCallback, useRef, useState, type ReactNode } from 'react'
import type { CobroMercadoPago } from '../api/cobrosMercadoPago'
import { Modal } from '../componentes/Modal'
import { formatearImporte } from '../formato/importes'

type ReusoPendiente = { cobro: CobroMercadoPago; numeroVisible: string }

/**
 * Confirmación explícita antes de usar un cobro de Mercado Pago ya aprobado en una venta nueva: muestra lo que se cobró y nunca
 * lo reutiliza en silencio. `confirmar(cobro, numero)` resuelve `true` si el cajero lo usa y `false` si no.
 */
export function useConfirmacionDeReusoDeCobro(): {
  modal: ReactNode
  confirmar: (cobro: CobroMercadoPago, numeroVisible: string) => Promise<boolean>
} {
  const [pendiente, setPendiente] = useState<ReusoPendiente | null>(null)
  const resolverRef = useRef<((usar: boolean) => void) | null>(null)

  const confirmar = useCallback(
    (cobro: CobroMercadoPago, numeroVisible: string) =>
      new Promise<boolean>((resolver) => {
        resolverRef.current = resolver
        setPendiente({ cobro, numeroVisible })
      }),
    [],
  )

  function responder(usar: boolean) {
    const resolver = resolverRef.current
    resolverRef.current = null
    setPendiente(null)
    resolver?.(usar)
  }

  const importe = pendiente === null ? 0 : (pendiente.cobro.importePagado ?? pendiente.cobro.importe)
  const modal =
    pendiente === null ? null : (
      <Modal
        titulo="Cobro de Mercado Pago aprobado"
        noDescartable
        rol="alertdialog"
        restaurarFoco={false}
        pie={
          <>
            <button type="button" className="btn btn-outline-secondary" onClick={() => responder(false)}>
              No usarlo
            </button>
            <button type="button" className="btn btn-primary" autoFocus onClick={() => responder(true)}>
              Usar este cobro
            </button>
          </>
        }
      >
        <p className="mb-0">
          Mercado Pago ya aprobó {formatearImporte(importe, { simbolo: true })} (venta {pendiente.numeroVisible}) y la venta todavía no se registró. ¿Usar ese cobro para
          esta venta?
        </p>
      </Modal>
    )

  return { modal, confirmar }
}
