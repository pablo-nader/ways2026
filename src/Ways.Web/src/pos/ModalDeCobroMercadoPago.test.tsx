import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi, type Mock } from 'vitest'
import { ErrorApi, ErrorDeRed } from '../api/cliente'
import type { clienteDeCobrosMercadoPago, CobroMercadoPago } from '../api/cobrosMercadoPago'
import { ModalDeCobroMercadoPago, type ResultadoDelCobro } from './ModalDeCobroMercadoPago'

// La consulta real espera 2 s: en el test se acorta para esperar al dato y no al reloj.
vi.mock('./cobroMercadoPago', async (importarOriginal) => ({
  ...(await importarOriginal<typeof import('./cobroMercadoPago')>()),
  esperaHastaLaProximaConsulta: (fallos: number) => (fallos > 0 ? 40 : 10),
}))

afterEach(() => cleanup())

const QR = '00020101021243650016com.mercadolibre0201'

function cobro(sobrescribir: Partial<CobroMercadoPago> = {}): CobroMercadoPago {
  return {
    idCobro: 77,
    estado: 'pendiente',
    qrData: QR,
    expiraEn: new Date(Date.now() + 10 * 60_000).toISOString(),
    importe: 1500,
    importePagado: null,
    importeDistinto: false,
    verificandoPago: false,
    pagoInformadoSinDatos: false,
    ...sobrescribir,
  }
}

const aprobado = (sobrescribir: Partial<CobroMercadoPago> = {}) =>
  cobro({ estado: 'aprobado', qrData: null, importePagado: 1500, ...sobrescribir })

function montar(opciones: {
  inicial?: CobroMercadoPago
  consultar?: Mock<typeof clienteDeCobrosMercadoPago.consultar>
  cancelar?: Mock<typeof clienteDeCobrosMercadoPago.cancelar>
  onImprimirQr?: (qrData: string) => void
}) {
  const consultar = opciones.consultar ?? vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(cobro())
  const cancelar = opciones.cancelar ?? vi.fn<typeof clienteDeCobrosMercadoPago.cancelar>().mockResolvedValue(cobro({ estado: 'cancelado', qrData: null }))
  const onResuelto = vi.fn<(resultado: ResultadoDelCobro) => void>()
  const vista = render(
    <ModalDeCobroMercadoPago
      cobro={opciones.inicial ?? cobro()}
      numeroVisible="0007-00000500"
      onResuelto={onResuelto}
      onImprimirQr={opciones.onImprimirQr}
      clienteDeCobros={{ consultar, cancelar }}
    />,
  )
  return { ...vista, consultar, cancelar, onResuelto }
}

describe('ModalDeCobroMercadoPago', () => {
  it('muestra el importe, el número de venta, el QR y la cuenta regresiva', () => {
    montar({})

    expect(screen.getByText('Venta 0007-00000500')).toBeInTheDocument()
    expect(screen.getByText(/1\.500,00/)).toBeInTheDocument()
    expect(screen.getByRole('img', { name: 'Código QR de Mercado Pago' })).toBeInTheDocument()
    expect(screen.getByText(/Vence en 10:00|Vence en 09:59/)).toBeInTheDocument()
  })

  it('al aprobarse resuelve UNA sola vez, aunque el servidor siga contestando aprobado', async () => {
    const consultar = vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(aprobado())
    const { onResuelto } = montar({ consultar })

    await waitFor(() => expect(onResuelto).toHaveBeenCalledTimes(1))
    expect(onResuelto.mock.calls[0][0]).toMatchObject({ tipo: 'aprobado' })
    await new Promise((r) => setTimeout(r, 80))
    expect(onResuelto).toHaveBeenCalledTimes(1)
    expect(consultar).toHaveBeenCalledTimes(1)
  })

  it('consulta de a una por vez: nunca arranca otra consulta con una en vuelo', async () => {
    let enVuelo = 0
    let maximo = 0
    const consultar = vi.fn(async () => {
      enVuelo += 1
      maximo = Math.max(maximo, enVuelo)
      await new Promise((r) => setTimeout(r, 35))
      enVuelo -= 1
      return cobro()
    })
    montar({ consultar })

    await waitFor(() => expect(consultar.mock.calls.length).toBeGreaterThanOrEqual(3))
    expect(maximo).toBe(1)
  })

  it.each([
    ['expirado', /venció sin pagarse/],
    ['cancelado', /se canceló/],
    ['devuelto', /devolvió el cobro/],
  ] as const)('un cobro %s se resuelve sin aprobar', async (estado, texto) => {
    const consultar = vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(cobro({ estado, qrData: null }))
    const { onResuelto } = montar({ consultar })

    await waitFor(() => expect(onResuelto).toHaveBeenCalledTimes(1))
    expect(onResuelto.mock.calls[0][0]).toMatchObject({ tipo: 'sin_aprobar', estado })
    expect(await screen.findByText(texto)).toBeInTheDocument()
  })

  it('"verificando el pago" no muestra el QR ni se resuelve', async () => {
    montar({ inicial: cobro({ verificandoPago: true }), consultar: vi.fn().mockResolvedValue(cobro({ verificandoPago: true })) })

    expect(screen.getByText('Verificando el pago…')).toBeInTheDocument()
    expect(screen.queryByRole('img', { name: 'Código QR de Mercado Pago' })).not.toBeInTheDocument()
  })

  it('un pago informado sin datos no muestra el QR y no se puede cancelar', async () => {
    const informado = cobro({ pagoInformadoSinDatos: true, verificandoPago: true, qrData: null })
    montar({ inicial: informado, consultar: vi.fn().mockResolvedValue(informado) })

    expect(screen.getByText(/Mercado Pago informa el pago, falta confirmarlo/)).toBeInTheDocument()
    expect(screen.queryByRole('img', { name: 'Código QR de Mercado Pago' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Cancelar cobro' })).toBeDisabled()
  })

  it('un importe distinto no se resuelve solo: bloquea hasta que el cajero lo revisa y deja de consultar', async () => {
    const distinto = aprobado({ importePagado: 1400, importeDistinto: true })
    const consultar = vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(distinto)
    const { onResuelto } = montar({ consultar })

    expect(await screen.findByText(/aprobó \$\s?1\.400,00 y la venta pide \$\s?1\.500,00/)).toBeInTheDocument()
    await new Promise((r) => setTimeout(r, 80))
    expect(onResuelto).not.toHaveBeenCalled()
    expect(screen.queryByRole('button', { name: 'Cancelar cobro' })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Entendido' }))
    expect(onResuelto).toHaveBeenCalledTimes(1)
    expect(onResuelto.mock.calls[0][0]).toMatchObject({ tipo: 'importe_distinto' })
  })

  it('un cobro que ya llega aprobado con otro importe bloquea sin esperar una consulta', () => {
    const { onResuelto, consultar } = montar({ inicial: aprobado({ importePagado: 1400, importeDistinto: true }) })

    expect(screen.getByRole('button', { name: 'Entendido' })).toBeInTheDocument()
    expect(onResuelto).not.toHaveBeenCalled()
    expect(consultar).not.toHaveBeenCalled()
  })

  it('un cobro que ya llega aprobado se resuelve de inmediato', async () => {
    const { onResuelto } = montar({ inicial: aprobado() })

    await waitFor(() => expect(onResuelto).toHaveBeenCalledTimes(1))
    expect(onResuelto.mock.calls[0][0]).toMatchObject({ tipo: 'aprobado' })
  })

  it('cancelar: si el cliente pagó en el medio, la respuesta es aprobado y la venta continúa', async () => {
    const cancelar = vi.fn<typeof clienteDeCobrosMercadoPago.cancelar>().mockResolvedValue(aprobado())
    const { onResuelto } = montar({ cancelar })

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar cobro' }))

    await waitFor(() => expect(onResuelto).toHaveBeenCalledTimes(1))
    expect(onResuelto.mock.calls[0][0]).toMatchObject({ tipo: 'aprobado' })
    expect(cancelar).toHaveBeenCalledWith(77)
  })

  it('cancelar un cobro pendiente lo resuelve como cancelado', async () => {
    const { onResuelto } = montar({})

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar cobro' }))

    await waitFor(() => expect(onResuelto).toHaveBeenCalledTimes(1))
    expect(onResuelto.mock.calls[0][0]).toMatchObject({ tipo: 'sin_aprobar', estado: 'cancelado' })
  })

  it('un doble click en "Cancelar cobro" manda un solo pedido', async () => {
    let resolver: (c: CobroMercadoPago) => void = () => undefined
    const cancelar = vi.fn(() => new Promise<CobroMercadoPago>((r) => (resolver = r)))
    montar({ cancelar })

    const boton = screen.getByRole('button', { name: 'Cancelar cobro' })
    await act(async () => {
      boton.click()
      boton.click()
    })

    expect(cancelar).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: 'Cancelando…' })).toBeDisabled()
    await act(async () => resolver(cobro({ estado: 'cancelado', qrData: null })))
  })

  it('si cancelar falla, el modal sigue abierto con el motivo y se puede reintentar', async () => {
    const cancelar = vi
      .fn<typeof clienteDeCobrosMercadoPago.cancelar>()
      .mockRejectedValueOnce(new ErrorApi(409, 'cobro_mercadopago_pagado_sin_datos', 'x'))
      .mockResolvedValueOnce(cobro({ estado: 'cancelado', qrData: null }))
    const { onResuelto } = montar({ cancelar })

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar cobro' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/falta confirmarlo/)
    expect(onResuelto).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar cobro' }))
    await waitFor(() => expect(onResuelto).toHaveBeenCalledTimes(1))
  })

  it('una respuesta tardía de la consulta después de desmontar se ignora', async () => {
    let resolver: (c: CobroMercadoPago) => void = () => undefined
    const consultar = vi.fn(() => new Promise<CobroMercadoPago>((r) => (resolver = r)))
    const { unmount, onResuelto } = montar({ consultar })

    await waitFor(() => expect(consultar).toHaveBeenCalledTimes(1))
    unmount()
    await act(async () => resolver(aprobado()))

    expect(onResuelto).not.toHaveBeenCalled()
  })

  it('una respuesta tardía del cancelar después de desmontar se ignora', async () => {
    let resolver: (c: CobroMercadoPago) => void = () => undefined
    const cancelar = vi.fn<typeof clienteDeCobrosMercadoPago.cancelar>(() => new Promise<CobroMercadoPago>((r) => (resolver = r)))
    const { unmount, onResuelto } = montar({ cancelar })

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar cobro' }))
    unmount()
    await act(async () => resolver(aprobado()))

    expect(onResuelto).not.toHaveBeenCalled()
  })

  it('una respuesta tardía de un cobro anterior no resuelve al cobro nuevo (misma instancia, otro id)', async () => {
    let resolverViejo: (c: CobroMercadoPago) => void = () => undefined
    const consultar = vi.fn((id: number) => (id === 77 ? new Promise<CobroMercadoPago>((r) => (resolverViejo = r)) : Promise.resolve(cobro({ idCobro: 78 }))))
    const onResuelto = vi.fn()
    const props = { numeroVisible: '0007-00000500', onResuelto, clienteDeCobros: { consultar, cancelar: vi.fn() } }
    const { rerender } = render(<ModalDeCobroMercadoPago cobro={cobro()} {...props} />)
    await waitFor(() => expect(consultar).toHaveBeenCalledWith(77))

    rerender(<ModalDeCobroMercadoPago cobro={cobro({ idCobro: 78 })} {...props} />)
    await act(async () => resolverViejo(aprobado()))

    expect(onResuelto).not.toHaveBeenCalled()
  })

  it('tras un fallo de la consulta sigue consultando con más espera y se recupera', async () => {
    const consultar = vi
      .fn<typeof clienteDeCobrosMercadoPago.consultar>()
      .mockRejectedValueOnce(new ErrorDeRed(new TypeError('x')))
      .mockResolvedValueOnce(aprobado())
    const { onResuelto } = montar({ consultar })

    await waitFor(() => expect(onResuelto).toHaveBeenCalledTimes(1))
    expect(consultar).toHaveBeenCalledTimes(2)
  })

  it('"Imprimir QR" solo se ofrece con una impresora y manda el contenido del QR', async () => {
    const imprimir = vi.fn()
    const { unmount } = montar({ onImprimirQr: imprimir })
    await userEvent.click(screen.getByRole('button', { name: 'Imprimir QR' }))
    expect(imprimir).toHaveBeenCalledWith(QR)
    unmount()

    montar({})
    expect(screen.queryByRole('button', { name: 'Imprimir QR' })).not.toBeInTheDocument()
  })

  it('si la impresora no puede dibujar el QR, avisa y el QR en pantalla sigue', async () => {
    montar({
      onImprimirQr: () => {
        throw new RangeError('no entra')
      },
    })

    await userEvent.click(screen.getByRole('button', { name: 'Imprimir QR' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo imprimir el QR')
    expect(screen.getByRole('img', { name: 'Código QR de Mercado Pago' })).toBeInTheDocument()
  })

  it('no se puede descartar con Escape: cerrarlo dejaría un cobro vivo sin dueño', async () => {
    const { onResuelto } = montar({})

    await userEvent.keyboard('{Escape}')

    expect(onResuelto).not.toHaveBeenCalled()
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
  })
})
