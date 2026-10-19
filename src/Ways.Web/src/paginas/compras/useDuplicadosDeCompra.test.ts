import { act, cleanup, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { CompraDuplicada } from '../../api/tipos'
import { ESPERA_DE_DUPLICADOS_MS, pedidoDeDuplicados, useDuplicadosDeCompra } from './useDuplicadosDeCompra'

const buscarDuplicadosMock = vi.hoisted(() => vi.fn())

vi.mock('../../api/compras', async (importarOriginal) => {
  const real = await importarOriginal<typeof import('../../api/compras')>()
  return { ...real, clienteDeCompras: { ...real.clienteDeCompras, buscarDuplicados: buscarDuplicadosMock } }
})

function duplicado(id: number, sobrescribir: Partial<CompraDuplicada> = {}): CompraDuplicada {
  return { id, estado: 'Borrador', numeroExterno: '0003-00012345', fechaComprobante: '2026-08-01', origen: 'Manual', ...sobrescribir }
}

function entradaBase(sobrescribir: Partial<Parameters<typeof pedidoDeDuplicados>[0]> = {}) {
  return {
    idProveedor: 1 as number | '',
    idTipoComprobante: 5 as number | '',
    numeroExterno: '00003-00012345',
    excluirId: null as number | null,
    activo: true,
    ...sobrescribir,
  }
}

function diferido<T>() {
  let resolver!: (valor: T) => void
  const promesa = new Promise<T>((r) => {
    resolver = r
  })
  return { promesa, resolver }
}

async function pasarElTiempo(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms)
  })
}

describe('pedidoDeDuplicados', () => {
  it('con proveedor, tipo y un número completo arma el pedido con el número recortado', () => {
    expect(pedidoDeDuplicados(entradaBase({ numeroExterno: '  00003-00012345 ', excluirId: 7 }))).toEqual({
      idProveedor: 1,
      idTipoComprobante: 5,
      numeroExterno: '00003-00012345',
      excluirId: 7,
    })
  })

  it('un número sin completar a ceros también se consulta: el servidor lo normaliza', () => {
    expect(pedidoDeDuplicados(entradaBase({ numeroExterno: '3-12345' }))?.numeroExterno).toBe('3-12345')
  })

  it.each([
    ['sin proveedor', { idProveedor: '' as const }],
    ['sin tipo', { idTipoComprobante: '' as const }],
    ['pantalla inactiva', { activo: false }],
    ['número vacío', { numeroExterno: '' }],
    ['número en blanco', { numeroExterno: '   ' }],
    ['solo el punto de venta', { numeroExterno: '00003-' }],
    ['solo el número', { numeroExterno: '-00012345' }],
  ])('no consulta %s', (_caso, cambios) => {
    expect(pedidoDeDuplicados(entradaBase(cambios))).toBeNull()
  })
})

describe('useDuplicadosDeCompra', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    buscarDuplicadosMock.mockReset()
  })

  afterEach(() => {
    cleanup()
    vi.useRealTimers()
  })

  it('consulta recién después de la espera y devuelve las compras que ya tienen el número', async () => {
    buscarDuplicadosMock.mockResolvedValue([duplicado(11), duplicado(12, { estado: 'Confirmada' })])
    const { result } = renderHook(() => useDuplicadosDeCompra(entradaBase()))

    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS - 1)
    expect(buscarDuplicadosMock).not.toHaveBeenCalled()
    expect(result.current).toEqual([])

    await pasarElTiempo(1)
    expect(buscarDuplicadosMock).toHaveBeenCalledTimes(1)
    expect(result.current.map((d) => d.id)).toEqual([11, 12])
  })

  it('manda excluirId cuando se edita una compra existente', async () => {
    buscarDuplicadosMock.mockResolvedValue([])
    renderHook(() => useDuplicadosDeCompra(entradaBase({ excluirId: 42 })))

    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS)

    expect(buscarDuplicadosMock).toHaveBeenCalledWith({
      idProveedor: 1,
      idTipoComprobante: 5,
      numeroExterno: '00003-00012345',
      excluirId: 42,
    })
  })

  it('sin duplicados devuelve una lista vacía', async () => {
    buscarDuplicadosMock.mockResolvedValue([])
    const { result } = renderHook(() => useDuplicadosDeCompra(entradaBase()))

    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS)

    expect(buscarDuplicadosMock).toHaveBeenCalledTimes(1)
    expect(result.current).toEqual([])
  })

  it('si el número cambia antes de que venza la espera, solo se consulta el último', async () => {
    buscarDuplicadosMock.mockResolvedValue([])
    const { rerender } = renderHook((props) => useDuplicadosDeCompra(props), { initialProps: entradaBase({ numeroExterno: '3-1' }) })

    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS - 1)
    rerender(entradaBase({ numeroExterno: '3-12' }))
    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS - 1)
    rerender(entradaBase({ numeroExterno: '3-123' }))
    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS)

    expect(buscarDuplicadosMock).toHaveBeenCalledTimes(1)
    expect(buscarDuplicadosMock.mock.calls[0][0].numeroExterno).toBe('3-123')
  })

  it('el aviso de un número anterior desaparece al cambiar el número, sin esperar la consulta nueva', async () => {
    const nueva = diferido<CompraDuplicada[]>()
    buscarDuplicadosMock.mockResolvedValueOnce([duplicado(11)]).mockReturnValueOnce(nueva.promesa)
    const { result, rerender } = renderHook((props) => useDuplicadosDeCompra(props), {
      initialProps: entradaBase({ numeroExterno: '3-12345' }),
    })
    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS)
    expect(result.current.map((d) => d.id)).toEqual([11])

    rerender(entradaBase({ numeroExterno: '3-99999' }))

    expect(result.current).toEqual([])
    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS)
    expect(buscarDuplicadosMock).toHaveBeenCalledTimes(2)
    expect(result.current).toEqual([])

    await act(async () => nueva.resolver([duplicado(21)]))
    expect(result.current.map((d) => d.id)).toEqual([21])
  })

  it('una respuesta tardía de un número anterior no pisa el aviso del número vigente', async () => {
    const vieja = diferido<CompraDuplicada[]>()
    const nueva = diferido<CompraDuplicada[]>()
    buscarDuplicadosMock.mockReturnValueOnce(vieja.promesa).mockReturnValueOnce(nueva.promesa)
    const { result, rerender } = renderHook((props) => useDuplicadosDeCompra(props), {
      initialProps: entradaBase({ numeroExterno: '3-12345' }),
    })
    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS)
    rerender(entradaBase({ numeroExterno: '3-99999' }))
    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS)
    expect(buscarDuplicadosMock).toHaveBeenCalledTimes(2)

    await act(async () => vieja.resolver([duplicado(11)]))
    expect(result.current).toEqual([])

    await act(async () => nueva.resolver([]))
    expect(result.current).toEqual([])
  })

  it('un número que deja de ser consultable (queda a medio escribir) limpia el aviso', async () => {
    buscarDuplicadosMock.mockResolvedValue([duplicado(11)])
    const { result, rerender } = renderHook((props) => useDuplicadosDeCompra(props), { initialProps: entradaBase() })
    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS)
    expect(result.current).toHaveLength(1)

    rerender(entradaBase({ numeroExterno: '00003-' }))
    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS)

    expect(result.current).toEqual([])
    expect(buscarDuplicadosMock).toHaveBeenCalledTimes(1)
  })

  it('una consulta que falla no muestra nada ni rompe', async () => {
    buscarDuplicadosMock.mockRejectedValue(new Error('sin conexión'))
    const { result } = renderHook(() => useDuplicadosDeCompra(entradaBase()))

    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS)

    expect(buscarDuplicadosMock).toHaveBeenCalledTimes(1)
    expect(result.current).toEqual([])
  })

  it('desmontar antes de que venza la espera cancela la consulta', async () => {
    const { unmount } = renderHook(() => useDuplicadosDeCompra(entradaBase()))

    unmount()
    await pasarElTiempo(ESPERA_DE_DUPLICADOS_MS * 2)

    expect(buscarDuplicadosMock).not.toHaveBeenCalled()
  })
})
