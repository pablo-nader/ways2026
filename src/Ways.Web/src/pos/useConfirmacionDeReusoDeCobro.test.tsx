import { act, cleanup, renderHook } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import type { CobroMercadoPago } from '../api/cobrosMercadoPago'
import { useConfirmacionDeReusoDeCobro } from './useConfirmacionDeReusoDeCobro'

const cobro: CobroMercadoPago = {
  idCobro: 31,
  estado: 'aprobado',
  qrData: null,
  expiraEn: '2026-11-01T12:10:00Z',
  importe: 100,
  importePagado: 100,
  importeDistinto: false,
  verificandoPago: false,
  pagoInformadoSinDatos: false,
  usadoEnVenta: false,
  tipo: 'qr',
  idTerminal: null,
  enTerminal: false,
  revisarTerminal: false,
}

afterEach(cleanup)

async function estado(promesa: Promise<boolean>): Promise<'pendiente' | boolean> {
  return Promise.race([promesa, Promise.resolve('pendiente' as const)])
}

describe('useConfirmacionDeReusoDeCobro', () => {
  it('una confirmación nueva resuelve la anterior como rechazada antes de reemplazarla', async () => {
    const { result } = renderHook(() => useConfirmacionDeReusoDeCobro())
    let primera!: Promise<boolean>
    let segunda!: Promise<boolean>

    act(() => {
      primera = result.current.confirmar(cobro, '0007-00000500')
    })
    act(() => {
      segunda = result.current.confirmarApartado({ idCobro: 31, importe: 100 }, '0007-00000500')
    })

    await expect(primera).resolves.toBe(false)
    expect(await estado(segunda)).toBe('pendiente')
  })

  it('al desmontar, la confirmación pendiente se resuelve como rechazada', async () => {
    const { result, unmount } = renderHook(() => useConfirmacionDeReusoDeCobro())
    let pendiente!: Promise<boolean>
    act(() => {
      pendiente = result.current.confirmarApartado({ idCobro: 31, importe: 100 }, '0007-00000500')
    })

    unmount()

    await expect(pendiente).resolves.toBe(false)
  })
})
