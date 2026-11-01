import { describe, expect, it, vi, type Mock } from 'vitest'
import { ErrorApi, ErrorDeRed } from '../api/cliente'
import type { clienteDeCobrosMercadoPago, CobroMercadoPago } from '../api/cobrosMercadoPago'
import { cobrarConMercadoPago, resolverResultadoDelCobro, type ReservaDeCobroEnPos } from './cobrarConMercadoPago'
import type { ResultadoDelCobro } from './ModalDeCobroMercadoPago'
import type { ReservaDeCobro } from './reservaDeCobro'

function cobro(sobrescribir: Partial<CobroMercadoPago> = {}): CobroMercadoPago {
  return {
    idCobro: 31,
    estado: 'pendiente',
    qrData: '0002',
    expiraEn: '2026-11-01T12:10:00Z',
    importe: 1500,
    importePagado: null,
    importeDistinto: false,
    verificandoPago: false,
    pagoInformadoSinDatos: false,
    ...sobrescribir,
  }
}

const aprobado = cobro({ estado: 'aprobado', qrData: null, importePagado: 1500 })

type Crear = Mock<typeof clienteDeCobrosMercadoPago.crear>
type Consultar = Mock<typeof clienteDeCobrosMercadoPago.consultar>

function armar(opciones: { previa?: ReservaDeCobro | null; resultado?: ResultadoDelCobro; consultar?: Consultar; crear?: Crear } = {}) {
  const reserva = {
    reservarNumeroParaCobro: vi.fn().mockResolvedValue({ ok: true, numero: 500, reutilizado: false }),
    anotarCobroEnCurso: vi.fn().mockResolvedValue(true),
    soltarReservaDeCobro: vi.fn().mockResolvedValue(true),
    leerReservaDeCobro: vi.fn().mockResolvedValue(opciones.previa ?? null),
  } satisfies ReservaDeCobroEnPos
  const clienteDeCobros = {
    crear: opciones.crear ?? vi.fn<typeof clienteDeCobrosMercadoPago.crear>().mockResolvedValue(cobro()),
    consultar: opciones.consultar ?? vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(cobro()),
  }
  const esperarCobro = vi.fn().mockResolvedValue(opciones.resultado ?? { tipo: 'aprobado', cobro: aprobado })
  const deps = { enLinea: true, idPuntoVenta: 7, reserva, clienteDeCobros, esperarCobro }
  return { deps, reserva, clienteDeCobros, esperarCobro }
}

const PAGO = { idMedioPago: 4, importe: 1500 }

const enCurso = (sobrescribir: Partial<ReservaDeCobro> = {}): ReservaDeCobro => ({
  idPuntoVenta: 7,
  numero: 500,
  estado: 'en_curso',
  idCobro: 31,
  idMedioPago: 4,
  importe: 1500,
  creadoEn: 'x',
  ...sobrescribir,
})

describe('cobrarConMercadoPago', () => {
  it('sin conexión se detiene con el aviso y no reserva ni crea nada', async () => {
    const { deps, reserva, clienteDeCobros } = armar()

    const resultado = await cobrarConMercadoPago({ ...deps, enLinea: false }, PAGO)

    expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('necesita conexión') })
    expect(reserva.reservarNumeroParaCobro).not.toHaveBeenCalled()
    expect(clienteDeCobros.crear).not.toHaveBeenCalled()
  })

  it('reserva el número, crea el cobro con ese número y devuelve aprobado con el MISMO número', async () => {
    const { deps, reserva, clienteDeCobros, esperarCobro } = armar()

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toEqual({ tipo: 'aprobado', numero: 500, idCobro: 31 })
    expect(reserva.reservarNumeroParaCobro).toHaveBeenCalledWith(7)
    expect(clienteDeCobros.crear).toHaveBeenCalledWith({ idMedioPago: 4, numeroPreasignado: 500, importe: 1500 })
    expect(reserva.anotarCobroEnCurso).toHaveBeenCalledWith({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 1500 })
    expect(esperarCobro).toHaveBeenCalledWith(expect.objectContaining({ idCobro: 31 }), '0007-00000500')
  })

  it('el orden es reservar, crear, anotar, esperar', async () => {
    const orden: string[] = []
    const { deps, reserva, clienteDeCobros, esperarCobro } = armar()
    reserva.reservarNumeroParaCobro.mockImplementation(async () => (orden.push('reservar'), { ok: true, numero: 500, reutilizado: false }))
    clienteDeCobros.crear.mockImplementation(async () => (orden.push('crear'), cobro()))
    reserva.anotarCobroEnCurso.mockImplementation(async () => (orden.push('anotar'), true))
    esperarCobro.mockImplementation(async () => (orden.push('esperar'), { tipo: 'aprobado', cobro: aprobado }))

    await cobrarConMercadoPago(deps, PAGO)

    expect(orden).toEqual(['reservar', 'crear', 'anotar', 'esperar'])
  })

  it.each([
    ['sin_numeracion', /No quedan números/],
    ['error_al_guardar', /No se pudo reservar/],
    ['cobro_en_curso', /sin cerrar/],
  ] as const)('si no se puede reservar (%s) no crea el cobro', async (motivo, texto) => {
    const { deps, reserva, clienteDeCobros } = armar()
    reserva.reservarNumeroParaCobro.mockResolvedValue({ ok: false, motivo })

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringMatching(texto) })
    expect(clienteDeCobros.crear).not.toHaveBeenCalled()
  })

  it('si el servidor rechaza crear el cobro, el número queda libre (no se anota) y se muestra el motivo', async () => {
    const crear = vi.fn<typeof clienteDeCobrosMercadoPago.crear>().mockRejectedValue(new ErrorApi(409, 'mercadopago_caja_no_registrada', 'x'))
    const { deps, reserva, esperarCobro } = armar({ crear })

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('caja de Mercado Pago') })
    expect(reserva.anotarCobroEnCurso).not.toHaveBeenCalled()
    expect(esperarCobro).not.toHaveBeenCalled()
  })

  it('sin respuesta al crear informa la falta de conexión, sin anotar un cobro que no se conoce', async () => {
    const crear = vi.fn<typeof clienteDeCobrosMercadoPago.crear>().mockRejectedValue(new ErrorDeRed(new TypeError('x')))
    const { deps, reserva } = armar({ crear })

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('Sin conexión') })
    expect(reserva.anotarCobroEnCurso).not.toHaveBeenCalled()
  })

  it('un cobro vencido deja el número libre y el segundo intento reutiliza el MISMO número', async () => {
    const { deps, reserva, clienteDeCobros, esperarCobro } = armar()
    esperarCobro.mockResolvedValueOnce({ tipo: 'sin_aprobar', estado: 'expirado', cobro: cobro({ estado: 'expirado', qrData: null }) })
    reserva.reservarNumeroParaCobro
      .mockResolvedValueOnce({ ok: true, numero: 500, reutilizado: false })
      .mockResolvedValueOnce({ ok: true, numero: 500, reutilizado: true })

    const primero = await cobrarConMercadoPago(deps, PAGO)
    const segundo = await cobrarConMercadoPago(deps, PAGO)

    expect(primero).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('venció') })
    expect(reserva.soltarReservaDeCobro).toHaveBeenCalledWith(500)
    expect(segundo).toEqual({ tipo: 'aprobado', numero: 500, idCobro: 31 })
    expect(clienteDeCobros.crear.mock.calls.map(([solicitud]) => solicitud.numeroPreasignado)).toEqual([500, 500])
  })

  it('con un importe distinto no cierra la venta y el cobro sigue vivo (no se suelta el número)', async () => {
    const distinto = cobro({ estado: 'aprobado', qrData: null, importePagado: 1400, importeDistinto: true })
    const { deps, reserva } = armar({ resultado: { tipo: 'importe_distinto', cobro: distinto } })

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringMatching(/aprobó .*1\.400,00 y la venta pide .*1\.500,00/) })
    expect(reserva.soltarReservaDeCobro).not.toHaveBeenCalled()
  })

  describe('con un cobro vivo de antes (recarga o error tras aprobarse)', () => {
    it('aprobado y con el mismo medio e importe: se usa tal cual, sin crear otro cobro ni reservar otro número', async () => {
      const { deps, reserva, clienteDeCobros, esperarCobro } = armar({
        previa: enCurso(),
        consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(aprobado),
      })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(resultado).toEqual({ tipo: 'aprobado', numero: 500, idCobro: 31 })
      expect(clienteDeCobros.crear).not.toHaveBeenCalled()
      expect(reserva.reservarNumeroParaCobro).not.toHaveBeenCalled()
      expect(esperarCobro).toHaveBeenCalledTimes(1)
    })

    it('pendiente y con el mismo medio e importe: vuelve a mostrar el modal con ese cobro', async () => {
      const { deps, clienteDeCobros, esperarCobro } = armar({ previa: enCurso(), consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(cobro()) })

      await cobrarConMercadoPago(deps, PAGO)

      expect(clienteDeCobros.crear).not.toHaveBeenCalled()
      expect(esperarCobro).toHaveBeenCalledWith(expect.objectContaining({ idCobro: 31 }), '0007-00000500')
    })

    it('con otro importe se detiene y pide resolverlo, sin tocar nada', async () => {
      const { deps, reserva, clienteDeCobros, esperarCobro } = armar({ previa: enCurso({ importe: 900 }), consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(cobro()) })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('Resolvelo antes') })
      expect(clienteDeCobros.crear).not.toHaveBeenCalled()
      expect(reserva.reservarNumeroParaCobro).not.toHaveBeenCalled()
      expect(esperarCobro).not.toHaveBeenCalled()
    })

    it('con otro medio se detiene', async () => {
      const { deps } = armar({ previa: enCurso({ idMedioPago: 9 }), consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(aprobado) })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(resultado.tipo).toBe('detenido')
    })

    it('cerrado sin pagarse: suelta el número y crea un cobro nuevo con él', async () => {
      const { deps, reserva, clienteDeCobros } = armar({
        previa: enCurso(),
        consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(cobro({ estado: 'expirado', qrData: null })),
      })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(reserva.soltarReservaDeCobro).toHaveBeenCalledWith(500)
      expect(clienteDeCobros.crear).toHaveBeenCalledTimes(1)
      expect(resultado).toEqual({ tipo: 'aprobado', numero: 500, idCobro: 31 })
    })

    it('si no se puede consultar el cobro vivo, no crea otro encima', async () => {
      const { deps, clienteDeCobros } = armar({ previa: enCurso(), consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockRejectedValue(new ErrorDeRed(new TypeError('x'))) })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(resultado.tipo).toBe('detenido')
      expect(clienteDeCobros.crear).not.toHaveBeenCalled()
    })

    it('una reserva libre no es un cobro vivo: se crea normalmente', async () => {
      const { deps, clienteDeCobros } = armar({ previa: { idPuntoVenta: 7, numero: 500, estado: 'libre', creadoEn: 'x' } })

      await cobrarConMercadoPago(deps, PAGO)

      expect(clienteDeCobros.crear).toHaveBeenCalledTimes(1)
    })
  })
})

describe('resolverResultadoDelCobro', () => {
  it('cancelado también libera el número', async () => {
    const soltar = vi.fn().mockResolvedValue(true)

    const resultado = await resolverResultadoDelCobro(
      { tipo: 'sin_aprobar', estado: 'cancelado', cobro: cobro({ estado: 'cancelado', qrData: null }) },
      500,
      { soltarReservaDeCobro: soltar },
    )

    expect(soltar).toHaveBeenCalledWith(500)
    expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('se canceló') })
  })
})
