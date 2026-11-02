import { describe, expect, it, vi, type Mock } from 'vitest'
import { ErrorApi, ErrorDeRed } from '../api/cliente'
import type { clienteDeCobrosMercadoPago, CobroMercadoPago } from '../api/cobrosMercadoPago'
import {
  cobrarConMercadoPago,
  MENSAJE_CON_APARTADOS_SIN_SINCRONIZAR,
  resolverResultadoDelCobro,
  sanarReservaYaVendida,
  type ReservaDeCobroEnPos,
} from './cobrarConMercadoPago'
import type { ResultadoDelCobro } from './ModalDeCobroMercadoPago'
import type { ReservaLeida } from './reservaDeCobro'

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
    usadoEnVenta: false,
    tipo: 'qr',
    idTerminal: null,
    enTerminal: false,
    revisarTerminal: false,
    ...sobrescribir,
  }
}

const aprobado = cobro({ estado: 'aprobado', qrData: null, importePagado: 1500 })

type Crear = Mock<typeof clienteDeCobrosMercadoPago.crear>
type Consultar = Mock<typeof clienteDeCobrosMercadoPago.consultar>
type Cancelar = Mock<typeof clienteDeCobrosMercadoPago.cancelar>

function armar(
  opciones: { previa?: ReservaLeida | null; resultado?: ResultadoDelCobro; consultar?: Consultar; crear?: Crear; cancelar?: Cancelar; confirmar?: boolean; apartadosPendientes?: number } = {},
) {
  const reserva = {
    reservarNumeroParaCobro: vi.fn().mockResolvedValue({ ok: true, numero: 500 }),
    anotarCobroEnCurso: vi.fn().mockResolvedValue(true),
    soltarReservaDeCobro: vi.fn().mockResolvedValue(true),
    consumirReservaDeCobro: vi.fn().mockResolvedValue(true),
    leerReservaDeCobro: vi.fn().mockResolvedValue(opciones.previa ?? null),
    sincronizarApartados: vi.fn().mockResolvedValue(opciones.apartadosPendientes ?? 0),
  } satisfies ReservaDeCobroEnPos
  const clienteDeCobros = {
    crear: opciones.crear ?? vi.fn<typeof clienteDeCobrosMercadoPago.crear>().mockResolvedValue(cobro()),
    consultar: opciones.consultar ?? vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(cobro()),
    cancelar: opciones.cancelar ?? vi.fn<typeof clienteDeCobrosMercadoPago.cancelar>().mockResolvedValue(cobro({ estado: 'cancelado', qrData: null })),
  }
  const esperarCobro = vi.fn().mockResolvedValue(opciones.resultado ?? { tipo: 'aprobado', cobro: aprobado })
  const confirmarReuso = vi.fn().mockResolvedValue(opciones.confirmar ?? true)
  const deps = { enLinea: true, idPuntoVenta: 7, reserva, clienteDeCobros, esperarCobro, confirmarReuso }
  return { deps, reserva, clienteDeCobros, esperarCobro, confirmarReuso }
}

const PAGO = { idMedioPago: 4, importe: 1500 }

const enCurso = (sobrescribir: Partial<ReservaLeida> = {}): ReservaLeida => ({
  idPuntoVenta: 7,
  numero: 500,
  estado: 'en_curso',
  idCobro: 31,
  idMedioPago: 4,
  importe: 1500,
  creadoEn: 'x',
  fueraDeBloque: false,
  ...sobrescribir,
})

describe('cobrarConMercadoPago', () => {
  it('con un cobro apartado sin sincronizar no reserva ni crea nada y explica por qué', async () => {
    const { deps, reserva, clienteDeCobros } = armar({ apartadosPendientes: 1 })

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toEqual({ tipo: 'detenido', mensaje: MENSAJE_CON_APARTADOS_SIN_SINCRONIZAR })
    expect(reserva.sincronizarApartados).toHaveBeenCalledTimes(1)
    expect(reserva.reservarNumeroParaCobro).not.toHaveBeenCalled()
    expect(clienteDeCobros.crear).not.toHaveBeenCalled()
  })

  it('sin apartados pendientes cobra como siempre', async () => {
    const { deps, clienteDeCobros } = armar({ apartadosPendientes: 0 })

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado.tipo).toBe('aprobado')
    expect(clienteDeCobros.crear).toHaveBeenCalledTimes(1)
  })

  it('sin conexión se detiene con el aviso y no reserva ni crea nada', async () => {
    const { deps, reserva, clienteDeCobros } = armar()

    const resultado = await cobrarConMercadoPago({ ...deps, enLinea: false }, PAGO)

    expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('necesita conexión') })
    expect(reserva.reservarNumeroParaCobro).not.toHaveBeenCalled()
    expect(clienteDeCobros.crear).not.toHaveBeenCalled()
  })

  it('aparta el número, crea el cobro con ese número y devuelve aprobado con el MISMO número y lo cobrado', async () => {
    const { deps, reserva, clienteDeCobros, esperarCobro } = armar()

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toEqual({ tipo: 'aprobado', numero: 500, idCobro: 31, importe: 1500 })
    expect(reserva.reservarNumeroParaCobro).toHaveBeenCalledWith(7)
    expect(clienteDeCobros.crear).toHaveBeenCalledWith({ idMedioPago: 4, numeroPreasignado: 500, importe: 1500 })
    expect(reserva.anotarCobroEnCurso).toHaveBeenCalledWith({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 1500 })
    expect(esperarCobro).toHaveBeenCalledWith(expect.objectContaining({ idCobro: 31 }), '0007-00000500')
  })

  it('el orden es reservar, crear, anotar, esperar', async () => {
    const orden: string[] = []
    const { deps, reserva, clienteDeCobros, esperarCobro } = armar()
    reserva.reservarNumeroParaCobro.mockImplementation(async () => (orden.push('reservar'), { ok: true, numero: 500 }))
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

  it('si el servidor rechaza crear el cobro, la reserva se suelta (no se anota) y se muestra el motivo', async () => {
    const crear = vi.fn<typeof clienteDeCobrosMercadoPago.crear>().mockRejectedValue(new ErrorApi(409, 'mercadopago_caja_no_registrada', 'x'))
    const { deps, reserva, esperarCobro } = armar({ crear })

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('caja de Mercado Pago') })
    expect(reserva.soltarReservaDeCobro).toHaveBeenCalledWith(500)
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

  // F2: sin el registro local no se abre el modal y el cobro recién creado se cancela en el servidor.
  it.each([
    ['devuelve false', (r: { anotarCobroEnCurso: Mock }) => r.anotarCobroEnCurso.mockResolvedValue(false)],
    ['lanza', (r: { anotarCobroEnCurso: Mock }) => r.anotarCobroEnCurso.mockRejectedValue(new Error('disco lleno'))],
  ])('si anotar el cobro %s, cancela el cobro creado, suelta la reserva y no abre el modal', async (_caso, fallar) => {
    const { deps, reserva, clienteDeCobros, esperarCobro } = armar()
    fallar(reserva)

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(clienteDeCobros.cancelar).toHaveBeenCalledWith(31)
    expect(reserva.soltarReservaDeCobro).toHaveBeenCalledWith(500)
    expect(esperarCobro).not.toHaveBeenCalled()
    expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('se canceló el cobro') })
  })

  it('si anotar falla y tampoco se puede cancelar, lo dice con el id del cobro y no abre el modal', async () => {
    const cancelar = vi.fn<typeof clienteDeCobrosMercadoPago.cancelar>().mockRejectedValue(new ErrorDeRed(new TypeError('x')))
    const { deps, reserva, esperarCobro } = armar({ cancelar })
    reserva.anotarCobroEnCurso.mockResolvedValue(false)

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('cobro 31') })
    expect(esperarCobro).not.toHaveBeenCalled()
  })

  it('si anotar falla y el cobro ya se había aprobado, lo informa para resolverlo a mano', async () => {
    const cancelar = vi.fn<typeof clienteDeCobrosMercadoPago.cancelar>().mockResolvedValue(aprobado)
    const { deps, reserva } = armar({ cancelar })
    reserva.anotarCobroEnCurso.mockResolvedValue(false)

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('ya lo aprobó') })
  })

  it('un cobro Point rechazado en la terminal vuelve al pago sin consumir el número: el reintento aparta el MISMO número y crea otro cobro', async () => {
    const point = cobro({ tipo: 'point', idTerminal: 'PAX_A910__SMARTPOS1', qrData: null })
    const crear = vi.fn<typeof clienteDeCobrosMercadoPago.crear>().mockResolvedValue(point)
    const { deps, reserva, clienteDeCobros, esperarCobro } = armar({ crear })
    esperarCobro.mockResolvedValueOnce({ tipo: 'sin_aprobar', estado: 'fallido', cobro: cobro({ ...point, estado: 'fallido' }) })

    const primero = await cobrarConMercadoPago(deps, PAGO)
    const segundo = await cobrarConMercadoPago(deps, PAGO)

    expect(primero).toEqual({ tipo: 'detenido', mensaje: 'El pago fue rechazado en la terminal: probá de nuevo u otro medio.' })
    expect(reserva.soltarReservaDeCobro).toHaveBeenCalledWith(500)
    expect(reserva.consumirReservaDeCobro).not.toHaveBeenCalled()
    expect(segundo).toEqual({ tipo: 'aprobado', numero: 500, idCobro: 31, importe: 1500 })
    expect(clienteDeCobros.crear.mock.calls.map(([solicitud]) => solicitud.numeroPreasignado)).toEqual([500, 500])
  })

  it('al retomar tras una recarga un cobro Point fallido libera la reserva y sigue con uno nuevo', async () => {
    const fallido = cobro({ tipo: 'point', idTerminal: 'T', qrData: null, estado: 'fallido' })
    const { deps, reserva, clienteDeCobros } = armar({
      previa: enCurso(),
      consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(fallido),
    })

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(reserva.soltarReservaDeCobro).toHaveBeenCalledWith(500)
    expect(clienteDeCobros.crear).toHaveBeenCalledTimes(1)
    expect(resultado.tipo).toBe('aprobado')
  })

  it('un cobro vencido suelta la reserva y el segundo intento aparta el MISMO número (el bloque no avanzó)', async () => {
    const { deps, reserva, clienteDeCobros, esperarCobro } = armar()
    esperarCobro.mockResolvedValueOnce({ tipo: 'sin_aprobar', estado: 'expirado', cobro: cobro({ estado: 'expirado', qrData: null }) })

    const primero = await cobrarConMercadoPago(deps, PAGO)
    const segundo = await cobrarConMercadoPago(deps, PAGO)

    expect(primero).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('venció') })
    expect(reserva.soltarReservaDeCobro).toHaveBeenCalledWith(500)
    expect(segundo).toEqual({ tipo: 'aprobado', numero: 500, idCobro: 31, importe: 1500 })
    expect(clienteDeCobros.crear.mock.calls.map(([solicitud]) => solicitud.numeroPreasignado)).toEqual([500, 500])
  })

  // F7: el importe distinto no es un callejón sin salida.
  it('con un importe distinto no cierra la venta, el cobro sigue vivo y dice cómo seguir', async () => {
    const distinto = cobro({ estado: 'aprobado', qrData: null, importePagado: 1400, importeDistinto: true })
    const { deps, reserva } = armar({ resultado: { tipo: 'importe_distinto', cobro: distinto } })

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringMatching(/aprobó .*1\.400,00 y la venta pide .*1\.500,00/) })
    expect(resultado).toMatchObject({ mensaje: expect.stringMatching(/ajustá la fila de Mercado Pago a .*1\.400,00.*volvé a cobrar/) })
    expect(reserva.soltarReservaDeCobro).not.toHaveBeenCalled()
  })

  describe('con un cobro vivo de antes (recarga o error tras aprobarse)', () => {
    it('aprobado y con el mismo medio e importe: pide confirmación y, si la dan, se usa sin crear otro cobro ni abrir el modal', async () => {
      const { deps, reserva, clienteDeCobros, esperarCobro, confirmarReuso } = armar({
        previa: enCurso(),
        consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(aprobado),
      })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(confirmarReuso).toHaveBeenCalledWith(aprobado, '0007-00000500')
      expect(resultado).toEqual({ tipo: 'aprobado', numero: 500, idCobro: 31, importe: 1500 })
      expect(clienteDeCobros.crear).not.toHaveBeenCalled()
      expect(reserva.reservarNumeroParaCobro).not.toHaveBeenCalled()
      expect(esperarCobro).not.toHaveBeenCalled()
    })

    it('si el cajero no confirma el reuso, se detiene y el cobro aprobado se conserva', async () => {
      const { deps, reserva } = armar({
        previa: enCurso(),
        consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(aprobado),
        confirmar: false,
      })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('No se usó el cobro aprobado') })
      expect(reserva.soltarReservaDeCobro).not.toHaveBeenCalled()
      expect(reserva.consumirReservaDeCobro).not.toHaveBeenCalled()
    })

    it('un aprobado con importe distinto se reutiliza cuando la fila vale lo realmente cobrado', async () => {
      const distinto = cobro({ estado: 'aprobado', qrData: null, importePagado: 1400, importeDistinto: true })
      const { deps, confirmarReuso } = armar({ previa: enCurso(), consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(distinto) })

      const resultado = await cobrarConMercadoPago(deps, { idMedioPago: 4, importe: 1400 })

      expect(confirmarReuso).toHaveBeenCalledTimes(1)
      expect(resultado).toEqual({ tipo: 'aprobado', numero: 500, idCobro: 31, importe: 1400 })
    })

    it('un aprobado con importe distinto y la fila sin ajustar dice qué importe poner y no pide confirmación', async () => {
      const distinto = cobro({ estado: 'aprobado', qrData: null, importePagado: 1400, importeDistinto: true })
      const { deps, confirmarReuso } = armar({ previa: enCurso(), consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(distinto) })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringMatching(/ajustá la fila de Mercado Pago a .*1\.400,00/) })
      expect(confirmarReuso).not.toHaveBeenCalled()
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

    it('cerrado sin pagarse: suelta la reserva y crea un cobro nuevo con el mismo número', async () => {
      const { deps, reserva, clienteDeCobros } = armar({
        previa: enCurso(),
        consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(cobro({ estado: 'expirado', qrData: null })),
      })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(reserva.soltarReservaDeCobro).toHaveBeenCalledWith(500)
      expect(clienteDeCobros.crear).toHaveBeenCalledTimes(1)
      expect(resultado).toEqual({ tipo: 'aprobado', numero: 500, idCobro: 31, importe: 1500 })
    })

    it('si no se puede consultar el cobro vivo, no crea otro encima', async () => {
      const { deps, clienteDeCobros } = armar({ previa: enCurso(), consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockRejectedValue(new ErrorDeRed(new TypeError('x'))) })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(resultado.tipo).toBe('detenido')
      expect(clienteDeCobros.crear).not.toHaveBeenCalled()
    })

    // F4: una reserva vieja de un cobro que una venta ya usó se descarta sola y deja el número atrás.
    it('si el cobro ya lo usó una venta, consume la reserva (el bloque deja atrás el número) y sigue con un cobro nuevo', async () => {
      const { deps, reserva, clienteDeCobros } = armar({
        previa: enCurso(),
        consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(cobro({ estado: 'aprobado', qrData: null, importePagado: 1500, usadoEnVenta: true })),
      })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(reserva.consumirReservaDeCobro).toHaveBeenCalledWith(500)
      expect(deps.confirmarReuso).not.toHaveBeenCalled()
      expect(clienteDeCobros.crear).toHaveBeenCalledTimes(1)
      expect(resultado).toMatchObject({ tipo: 'aprobado' })
    })

    // F3: una reserva de otro punto de venta no se reutiliza.
    it('de otro punto de venta y pendiente: se cancela en el servidor, se suelta y se sigue con uno nuevo del punto de venta actual', async () => {
      const { deps, reserva, clienteDeCobros, esperarCobro } = armar({ previa: enCurso({ idPuntoVenta: 9 }) })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(clienteDeCobros.cancelar).toHaveBeenCalledWith(31)
      expect(reserva.soltarReservaDeCobro).toHaveBeenCalledWith(500)
      expect(reserva.reservarNumeroParaCobro).toHaveBeenCalledWith(7)
      expect(esperarCobro).toHaveBeenCalledTimes(1)
      expect(resultado).toMatchObject({ tipo: 'aprobado' })
    })

    it('de otro punto de venta y aprobado: se conserva, no se reutiliza ni se pide confirmación y se dice dónde resolverlo', async () => {
      const { deps, reserva, clienteDeCobros, confirmarReuso } = armar({
        previa: enCurso({ idPuntoVenta: 9 }),
        consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(aprobado),
      })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringContaining('otro punto de venta') })
      expect(confirmarReuso).not.toHaveBeenCalled()
      expect(clienteDeCobros.cancelar).not.toHaveBeenCalled()
      expect(reserva.soltarReservaDeCobro).not.toHaveBeenCalled()
      expect(clienteDeCobros.crear).not.toHaveBeenCalled()
    })

    // F1: el bloque cambió y el número ya no es el próximo.
    it('con el número fuera del bloque y pendiente: cancela el cobro y sigue con uno nuevo', async () => {
      const { deps, reserva, clienteDeCobros } = armar({ previa: enCurso({ fueraDeBloque: true }) })

      await cobrarConMercadoPago(deps, PAGO)

      expect(clienteDeCobros.cancelar).toHaveBeenCalledWith(31)
      expect(reserva.soltarReservaDeCobro).toHaveBeenCalledWith(500)
      expect(clienteDeCobros.crear).toHaveBeenCalledTimes(1)
    })

    it('con el número fuera del bloque y aprobado: lo muestra como cobro aprobado sin venta con el id del cobro', async () => {
      const { deps, reserva, confirmarReuso } = armar({
        previa: enCurso({ fueraDeBloque: true }),
        consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(aprobado),
      })

      const resultado = await cobrarConMercadoPago(deps, PAGO)

      expect(resultado).toMatchObject({ tipo: 'detenido', mensaje: expect.stringMatching(/aprobado sin venta.*cobro 31/) })
      expect(confirmarReuso).not.toHaveBeenCalled()
      expect(reserva.soltarReservaDeCobro).not.toHaveBeenCalled()
    })

    it('una reserva sin cobro creado no es un cobro vivo: se crea normalmente', async () => {
      const { deps, clienteDeCobros } = armar({ previa: enCurso({ idCobro: undefined }) })

      await cobrarConMercadoPago(deps, PAGO)

      expect(clienteDeCobros.crear).toHaveBeenCalledTimes(1)
    })
  })
})

describe('resolverResultadoDelCobro', () => {
  it('cancelado también suelta la reserva', async () => {
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

describe('sanarReservaYaVendida', () => {
  it('si el servidor marca el cobro como usado en una venta, consume el número con el dueño de la reserva', async () => {
    const consumir = vi.fn().mockResolvedValue(true)
    const consultar = vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(cobro({ usadoEnVenta: true }))

    const resultado = await sanarReservaYaVendida({ idCobro: 31, numero: 500, dueno: 'abc' }, { consultar, consumirReservaDeCobro: consumir })

    expect(resultado).toBe('sanada')
    expect(consumir).toHaveBeenCalledWith(500, 'abc')
  })

  it('con el cobro todavía sin venta no consume nada y devuelve el cobro vivo', async () => {
    const consumir = vi.fn().mockResolvedValue(true)
    const vivo = cobro()

    const resultado = await sanarReservaYaVendida(
      { idCobro: 31, numero: 500 },
      { consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(vivo), consumirReservaDeCobro: consumir },
    )

    expect(resultado).toBe(vivo)
    expect(consumir).not.toHaveBeenCalled()
  })

  it('si no se puede consultar, devuelve null y no toca la reserva', async () => {
    const consumir = vi.fn().mockResolvedValue(true)

    const resultado = await sanarReservaYaVendida(
      { idCobro: 31, numero: 500 },
      { consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockRejectedValue(new ErrorDeRed(new TypeError('x'))), consumirReservaDeCobro: consumir },
    )

    expect(resultado).toBeNull()
    expect(consumir).not.toHaveBeenCalled()
  })
})

describe('cobros aprobados sin venta ofrecen apartarse', () => {
  it('un aprobado de otro punto de venta se detiene con el cobro apartable y el dueño de la reserva', async () => {
    const { deps } = armar({
      previa: enCurso({ idPuntoVenta: 9, dueno: 'tok' }),
      consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(aprobado),
    })

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toMatchObject({
      tipo: 'detenido',
      apartable: { dueno: 'tok', cobro: { idCobro: 31, numero: 500, idPuntoVenta: 9, idMedioPago: 4, importe: 1500, importePagado: 1500 } },
    })
  })

  it('un aprobado cuyo número ya no es el próximo del bloque se detiene con el cobro apartable', async () => {
    const { deps } = armar({ previa: enCurso({ fueraDeBloque: true }), consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(aprobado) })

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toMatchObject({ tipo: 'detenido', apartable: { cobro: { idCobro: 31 } }, mensaje: expect.stringContaining('Apartalo') })
  })

  it('un aprobado con otro importe se detiene con el cobro apartable', async () => {
    const { deps } = armar({ previa: enCurso(), consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(aprobado) })

    const resultado = await cobrarConMercadoPago(deps, { idMedioPago: 4, importe: 900 })

    expect(resultado).toMatchObject({ tipo: 'detenido', apartable: { cobro: { idCobro: 31, importePagado: 1500 } } })
  })

  it('el importe distinto del modal también deja el cobro apartable', async () => {
    const distinto = cobro({ estado: 'aprobado', qrData: null, importePagado: 1400, importeDistinto: true })

    const resultado = await resolverResultadoDelCobro({ tipo: 'importe_distinto', cobro: distinto }, 500, { soltarReservaDeCobro: vi.fn() }, { idPuntoVenta: 7, dueno: 'tok', idMedioPago: 4 })

    expect(resultado).toMatchObject({ tipo: 'detenido', apartable: { dueno: 'tok', cobro: { idCobro: 31, importe: 1500, importePagado: 1400 } } })
  })

  it('un cobro pendiente que no coincide no es apartable', async () => {
    const { deps } = armar({ previa: enCurso({ importe: 900 }), consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(cobro()) })

    const resultado = await cobrarConMercadoPago(deps, PAGO)

    expect(resultado).toMatchObject({ tipo: 'detenido' })
    expect(resultado).not.toHaveProperty('apartable')
  })

  it('la reserva presenta el dueño de la reserva anterior al soltarla', async () => {
    const { deps, reserva } = armar({
      previa: enCurso({ dueno: 'tok' }),
      consultar: vi.fn<typeof clienteDeCobrosMercadoPago.consultar>().mockResolvedValue(cobro({ estado: 'expirado', qrData: null })),
    })

    await cobrarConMercadoPago(deps, PAGO)

    expect(reserva.soltarReservaDeCobro).toHaveBeenCalledWith(500, 'tok')
  })
})
