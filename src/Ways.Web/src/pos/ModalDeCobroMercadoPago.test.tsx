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
    usadoEnVenta: false,
    tipo: 'qr',
    idTerminal: null,
    enTerminal: false,
    revisarTerminal: false,
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
    ['sin_venta', /se apartó sin venta/],
    ['fallido', /rechazado en la terminal: probá de nuevo u otro medio/],
  ] as const)('un cobro %s se resuelve sin aprobar', async (estado, texto) => {
    const consultar = vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(cobro({ estado, qrData: null }))
    const { onResuelto } = montar({ consultar })

    await waitFor(() => expect(onResuelto).toHaveBeenCalledTimes(1))
    expect(onResuelto.mock.calls[0][0]).toMatchObject({ tipo: 'sin_aprobar', estado })
    expect(await screen.findByText(texto)).toBeInTheDocument()
  })

  describe('cobro con la terminal Point', () => {
    const point = (sobrescribir: Partial<CobroMercadoPago> = {}) =>
      cobro({ tipo: 'point', idTerminal: 'PAX_A910__SMARTPOS1', qrData: null, ...sobrescribir })

    it('muestra "Cobrando en la terminal {id}…", sin QR ni "Imprimir QR", y se puede cancelar mientras la orden no llegó a la terminal', () => {
      montar({ inicial: point(), consultar: vi.fn().mockResolvedValue(point()), onImprimirQr: vi.fn() })

      expect(screen.getByText('Cobrando en la terminal PAX_A910__SMARTPOS1…')).toBeInTheDocument()
      expect(screen.queryByRole('img', { name: 'Código QR de Mercado Pago' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Imprimir QR' })).not.toBeInTheDocument()
      expect(screen.queryByText('Verificando el pago…')).not.toBeInTheDocument()
      expect(screen.getByText(/Vence en/)).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Cancelar cobro' })).toBeEnabled()
    })

    it('con la orden en la terminal el cancelar se apaga y se indica cancelar en la terminal', () => {
      const enTerminal = point({ enTerminal: true })
      montar({ inicial: enTerminal, consultar: vi.fn().mockResolvedValue(enTerminal) })

      expect(screen.getByText('Cobrando en la terminal PAX_A910__SMARTPOS1…')).toBeInTheDocument()
      expect(screen.getByText('Para cancelarlo, hacelo en la terminal.')).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Cancelar cobro' })).toBeDisabled()
    })

    it('"revisar la terminal" muestra el aviso, no se resuelve y no se puede cancelar desde el POS', async () => {
      const revisar = point({ revisarTerminal: true })
      const { onResuelto } = montar({ inicial: revisar, consultar: vi.fn().mockResolvedValue(revisar) })

      expect(screen.getByText('Revisá el resultado en la terminal.')).toBeInTheDocument()
      expect(screen.getByText(/Vence en/)).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Cancelar cobro' })).toBeDisabled()
      await new Promise((r) => setTimeout(r, 80))
      expect(onResuelto).not.toHaveBeenCalled()
    })

    it('«Cerrar y dejarlo en curso» resuelve con en_curso en la terminal y en revisar la terminal, y no cancela nada', async () => {
      const enTerminal = point({ enTerminal: true })
      const a = montar({ inicial: enTerminal, consultar: vi.fn().mockResolvedValue(enTerminal) })
      await userEvent.click(screen.getByRole('button', { name: 'Cerrar y dejarlo en curso' }))
      expect(a.onResuelto).toHaveBeenCalledTimes(1)
      expect(a.onResuelto.mock.calls[0][0]).toMatchObject({ tipo: 'en_curso', cobro: { idCobro: 77 } })
      expect(a.cancelar).not.toHaveBeenCalled()
      cleanup()

      const revisar = point({ idCobro: 78, revisarTerminal: true })
      const r = montar({ inicial: revisar, consultar: vi.fn().mockResolvedValue(revisar) })
      await userEvent.click(screen.getByRole('button', { name: 'Cerrar y dejarlo en curso' }))
      expect(r.onResuelto.mock.calls[0][0]).toMatchObject({ tipo: 'en_curso', cobro: { idCobro: 78 } })
    })

    it('un cobro con QR no ofrece cerrarlo en curso', () => {
      montar({})

      expect(screen.queryByRole('button', { name: 'Cerrar y dejarlo en curso' })).not.toBeInTheDocument()
    })

    it('un cobro Point aprobado se resuelve; uno fallido vuelve al pago con el motivo', async () => {
      const aprobadoPoint = point({ estado: 'aprobado', importePagado: 1500 })
      const a = montar({ inicial: point(), consultar: vi.fn().mockResolvedValue(aprobadoPoint) })
      await waitFor(() => expect(a.onResuelto).toHaveBeenCalledTimes(1))
      expect(a.onResuelto.mock.calls[0][0]).toMatchObject({ tipo: 'aprobado' })
      cleanup()

      const f = montar({ inicial: point({ idCobro: 78 }), consultar: vi.fn().mockResolvedValue(point({ idCobro: 78, estado: 'fallido' })) })
      await waitFor(() => expect(f.onResuelto).toHaveBeenCalledTimes(1))
      expect(f.onResuelto.mock.calls[0][0]).toMatchObject({ tipo: 'sin_aprobar', estado: 'fallido' })
      expect(await screen.findByText(/rechazado en la terminal/)).toBeInTheDocument()
    })

    it('si Mercado Pago contesta que ya está en la terminal, el modal muestra el motivo y sigue abierto', async () => {
      const cancelar = vi
        .fn<typeof clienteDeCobrosMercadoPago.cancelar>()
        .mockRejectedValue(new ErrorApi(409, 'mercadopago_cancelar_en_la_terminal', 'x'))
      const { onResuelto } = montar({ inicial: point(), consultar: vi.fn().mockResolvedValue(point()), cancelar })

      await userEvent.click(screen.getByRole('button', { name: 'Cancelar cobro' }))

      expect(await screen.findByRole('alert')).toHaveTextContent(/cancelalo en la terminal/)
      expect(onResuelto).not.toHaveBeenCalled()
    })
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
