import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import type { CobroMercadoPago } from '../api/cobrosMercadoPago'
import { Modal } from '../componentes/Modal'
import { formatearImporte } from '../formato/importes'

type Pendiente =
  | { tipo: 'reuso'; cobro: CobroMercadoPago; numeroVisible: string }
  | { tipo: 'apartar'; idCobro: number; importe: number; numeroVisible: string }

/**
 * Confirmaciones explícitas sobre un cobro de Mercado Pago ya aprobado: usarlo en una venta nueva (`confirmar`) o apartarlo en la lista
 * de cobros sin venta para liberar la caja (`confirmarApartado`). Nunca actúan en silencio. Cada una resuelve `true` si el cajero acepta
 * y `false` si no. Una confirmación nueva resuelve la anterior como rechazada antes de reemplazarla, y al desmontar la pendiente
 * también se rechaza: ninguna promesa queda colgada.
 */
export function useConfirmacionDeReusoDeCobro(): {
  modal: ReactNode
  confirmar: (cobro: CobroMercadoPago, numeroVisible: string) => Promise<boolean>
  confirmarApartado: (cobro: { idCobro: number; importe: number }, numeroVisible: string) => Promise<boolean>
} {
  const [pendiente, setPendiente] = useState<Pendiente | null>(null)
  const resolverRef = useRef<((aceptado: boolean) => void) | null>(null)

  const abrir = useCallback(
    (nueva: Pendiente) =>
      new Promise<boolean>((resolver) => {
        resolverRef.current?.(false)
        resolverRef.current = resolver
        setPendiente(nueva)
      }),
    [],
  )

  useEffect(
    () => () => {
      const resolver = resolverRef.current
      resolverRef.current = null
      resolver?.(false)
    },
    [],
  )

  const confirmar = useCallback((cobro: CobroMercadoPago, numeroVisible: string) => abrir({ tipo: 'reuso', cobro, numeroVisible }), [abrir])
  const confirmarApartado = useCallback(
    (cobro: { idCobro: number; importe: number }, numeroVisible: string) =>
      abrir({ tipo: 'apartar', idCobro: cobro.idCobro, importe: cobro.importe, numeroVisible }),
    [abrir],
  )

  function responder(aceptado: boolean) {
    const resolver = resolverRef.current
    resolverRef.current = null
    setPendiente(null)
    resolver?.(aceptado)
  }

  let modal: ReactNode = null
  if (pendiente?.tipo === 'reuso') {
    const importe = pendiente.cobro.importePagado ?? pendiente.cobro.importe
    modal = (
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
  } else if (pendiente?.tipo === 'apartar') {
    modal = (
      <Modal
        titulo="Apartar el cobro y liberar la caja"
        noDescartable
        rol="alertdialog"
        restaurarFoco={false}
        pie={
          <>
            <button type="button" className="btn btn-outline-secondary" autoFocus onClick={() => responder(false)}>
              Volver
            </button>
            <button type="button" className="btn btn-danger" onClick={() => responder(true)}>
              Apartar cobro
            </button>
          </>
        }
      >
        <p className="mb-0">
          El cobro {pendiente.idCobro} de Mercado Pago por {formatearImporte(pendiente.importe, { simbolo: true })} (venta {pendiente.numeroVisible}) queda
          guardado en la lista de cobros sin venta y la caja vuelve a vender. Esa venta no se registra: lo resuelve un supervisor desde la conciliación de Mercado
          Pago. ¿Apartarlo?
        </p>
      </Modal>
    )
  }

  return { modal, confirmar, confirmarApartado }
}
