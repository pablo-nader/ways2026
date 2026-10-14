import { afterEach, describe, expect, it, vi } from 'vitest'
import { clienteDeFacturacionFiscal } from './facturacionFiscal'

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('clienteDeFacturacionFiscal.procesarLote', () => {
  it('hace POST a la ruta del lote con solo el punto de venta en el cuerpo y la señal recibida', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)
    const controlador = new AbortController()

    await clienteDeFacturacionFiscal.procesarLote(7, controlador.signal)

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain('/api/fiscal/facturaciones/procesar')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ idPuntoVenta: 7 })
    expect(init.signal).toBe(controlador.signal)
  })
})
