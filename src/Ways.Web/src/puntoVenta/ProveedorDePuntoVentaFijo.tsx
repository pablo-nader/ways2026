import { useMemo } from 'react'
import type { ReactNode } from 'react'
import type { PuntoVentaListado } from '../api/tipos'
import { PuntoVentaContext } from './PuntoVentaContext'
import type { EstadoDePuntoVenta } from './PuntoVentaContext'

type Props = { puntoVenta: PuntoVentaListado; children: ReactNode }

/**
 * Variante de `PuertaDePuntoVenta` para el POS de escritorio (stage-desktop-pos): el punto de
 * venta lo fija el dispositivo vinculado, nunca una elección del cajero — `elegir` es un no-op y
 * `recargar` no repregunta nada, mismo criterio que el `SIN_PUNTOS_VENTA` de `PuertaDePuntoVenta`
 * para un rol que no opera el POS. Es el seam más chico posible: `Pos.tsx`/`CierreDeCaja.tsx` ya
 * consumen `usePuntoVenta()` sin cambios, esta variante solo cambia de dónde sale el valor.
 */
export function ProveedorDePuntoVentaFijo({ puntoVenta, children }: Props) {
  const valor = useMemo<EstadoDePuntoVenta>(
    () => ({
      puntosVenta: [puntoVenta],
      puntoVenta,
      elegir: () => undefined,
      recargar: () => Promise.resolve(),
    }),
    [puntoVenta],
  )

  return <PuntoVentaContext.Provider value={valor}>{children}</PuntoVentaContext.Provider>
}
