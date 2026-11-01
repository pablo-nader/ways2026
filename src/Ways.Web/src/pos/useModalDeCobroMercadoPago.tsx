import { useCallback, useRef, useState, type ReactNode } from 'react'
import type { CobroMercadoPago } from '../api/cobrosMercadoPago'
import { ModalDeCobroMercadoPago, type ResultadoDelCobro } from './ModalDeCobroMercadoPago'

type CobroAbierto = { cobro: CobroMercadoPago; numeroVisible: string }

/**
 * Convierte el modal de cobro en una espera: `esperarCobro(cobro, numero)` lo abre y resuelve cuando el cobro termina, para que
 * `cobrar()` siga su flujo lineal. Una sola espera a la vez; el modal se desmonta al resolverse, así que una respuesta tardía de
 * su consulta ya no encuentra a quién entregarse.
 */
export function useModalDeCobroMercadoPago(onImprimirQr?: (qrData: string, cobro: CobroMercadoPago, numeroVisible: string) => void): {
  modal: ReactNode
  esperarCobro: (cobro: CobroMercadoPago, numeroVisible: string) => Promise<ResultadoDelCobro>
  abierto: boolean
} {
  const [abierto, setAbierto] = useState<CobroAbierto | null>(null)
  const resolverRef = useRef<((resultado: ResultadoDelCobro) => void) | null>(null)

  const esperarCobro = useCallback(
    (cobro: CobroMercadoPago, numeroVisible: string) =>
      new Promise<ResultadoDelCobro>((resolver) => {
        resolverRef.current = resolver
        setAbierto({ cobro, numeroVisible })
      }),
    [],
  )

  function resuelto(resultado: ResultadoDelCobro) {
    const resolver = resolverRef.current
    resolverRef.current = null
    setAbierto(null)
    resolver?.(resultado)
  }

  const modal =
    abierto === null ? null : (
      <ModalDeCobroMercadoPago
        key={abierto.cobro.idCobro}
        cobro={abierto.cobro}
        numeroVisible={abierto.numeroVisible}
        onResuelto={resuelto}
        onImprimirQr={onImprimirQr ? (qrData) => onImprimirQr(qrData, abierto.cobro, abierto.numeroVisible) : undefined}
      />
    )

  return { modal, esperarCobro, abierto: abierto !== null }
}
