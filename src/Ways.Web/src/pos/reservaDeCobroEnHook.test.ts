import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clienteDePos } from '../api/pos'
import { clienteDeVentas } from '../api/ventas'
import { useSincronizacionOffline } from './useSincronizacionOffline'
import { agregarAOutbox, guardarBloque, leerBloque, leerOutbox, type BloqueDeNumeracionLocal } from './outboxOffline'
import { guardarInstantaneaLocal } from './instantaneaOffline'
import { borrarReservaDeCobro, guardarReservaDeCobro, leerCobrosSinVenta, leerReservaDeCobro } from './reservaDeCobro'
import { reiniciarLotesDeFacturacionParaTests } from './loteDeFacturacion'
import type { AlmacenClaveValor } from './almacenPos'
import type { InstantaneaDePos, SolicitudDeVenta } from '../api/tipos'

vi.mock('../api/facturacionFiscal', () => ({ clienteDeFacturacionFiscal: { procesarLote: vi.fn().mockResolvedValue({}) } }))
vi.mock('../api/clientes', () => ({ clienteDeClientes: { obtener: vi.fn() } }))
vi.mock('../api/ventas', async (importarOriginal) => ({
  ...(await importarOriginal<typeof import('../api/ventas')>()),
  clienteDeVentas: {
    emitir: vi.fn().mockRejectedValue(new Error('sin red')),
    reservarNumeracion: vi.fn().mockRejectedValue(new Error('sin red')),
  },
}))
vi.mock('../api/pos', () => ({
  clienteDePos: {
    obtenerInstantanea: vi.fn().mockRejectedValue(new Error('sin red')),
    rendirCola: vi.fn().mockResolvedValue(undefined),
  },
}))

function almacenFake(opciones: { fallarReserva?: boolean } = {}): AlmacenClaveValor {
  const datos = new Map<string, unknown>()
  return {
    async leer<T>(clave: string) {
      return (datos.has(clave) ? (datos.get(clave) as T) : null) ?? null
    },
    async escribir<T>(clave: string, valor: T) {
      if (opciones.fallarReserva && clave === 'reservaDeCobroMercadoPago') return false
      datos.set(clave, valor)
      return true
    },
  }
}

const instantanea: InstantaneaDePos = {
  momento: '2026-09-20T10:00:00.000Z',
  idPuntoVenta: 7,
  articulos: [
    {
      idArticulo: 1,
      codigoInterno: 'A0001',
      nombre: 'Coca Cola 1L',
      codigosBarra: ['7790001234567'],
      idAlicuotaIva: 1,
      porcentajeIva: 21,
      preciosPorLista: [{ idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] }],
    },
  ],
  clientes: [],
  mediosDePago: [],
  toleranciaPago: 0,
  listas: [{ idListaPrecio: 1, modo: 'Fija', porcentaje: null }],
}

const bloque = (sobrescribir: Partial<BloqueDeNumeracionLocal> = {}): BloqueDeNumeracionLocal => ({
  idPuntoVenta: 7,
  codigoTipoComprobante: 'TX',
  desde: 500,
  hasta: 599,
  proximo: 500,
  ...sobrescribir,
})

const solicitud = (sobrescribir: Partial<SolicitudDeVenta> = {}): SolicitudDeVenta => ({
  idPuntoVenta: 7,
  idCliente: 1,
  codigoTipoComprobante: 'TX',
  idComprobanteAsociado: null,
  lineas: [{ idArticulo: 1, cantidad: 1, codigoBarra: '7790001234567', idLote: null }],
  pagos: [{ idMedioPago: 4, importe: 100, referencia: null, vuelto: 0, idCobroMercadoPago: 31 }],
  direccionEntrega: null,
  observaciones: null,
  ...sobrescribir,
})

const ventaEfectivo = () => ({
  solicitudBase: solicitud({ pagos: [{ idMedioPago: 1, importe: 100, referencia: null, vuelto: 0 }] }),
  esConsumidorFinal: true,
  idListaPrecio: 1,
  pagos: [{ comportamiento: 'Efectivo' as const, importe: 100 }],
})

const ventaMp = (numeroReservado?: number) => ({
  solicitudBase: solicitud(),
  esConsumidorFinal: true,
  idListaPrecio: 1,
  pagos: [{ comportamiento: 'Electronico' as const, importe: 100 }],
  ...(numeroReservado !== undefined ? { numeroReservado } : {}),
})

async function montar(almacen: AlmacenClaveValor) {
  const vista = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
  await waitFor(() => expect(vista.result.current.instantanea).not.toBeNull())
  return vista
}

async function preparar(opciones: { bloque?: Partial<BloqueDeNumeracionLocal>; fallarReserva?: boolean } = {}) {
  const almacen = almacenFake({ fallarReserva: opciones.fallarReserva })
  await guardarInstantaneaLocal(almacen, { instantanea, etag: null, verificadaEn: instantanea.momento })
  await guardarBloque(almacen, bloque(opciones.bloque))
  return almacen
}

beforeEach(() => {
  reiniciarLotesDeFacturacionParaTests()
})

const reservaEnCurso = { idPuntoVenta: 7, numero: 500, estado: 'en_curso' as const, idCobro: 31, idMedioPago: 4, importe: 100, creadoEn: 'x' }

describe('reservarNumeroParaCobro (aparta el próximo número SIN consumirlo)', () => {
  it('aparta el próximo del bloque, deja el puntero donde estaba y guarda la reserva', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)

    let reserva
    await act(async () => {
      reserva = await result.current.reservarNumeroParaCobro(7)
    })

    expect(reserva).toMatchObject({ ok: true, numero: 500 })
    expect((await leerBloque(almacen))?.proximo).toBe(500)
    expect(await leerReservaDeCobro(almacen)).toMatchObject({ idPuntoVenta: 7, numero: 500, estado: 'en_curso' })
  })

  it('apartar de nuevo un intento que no llegó a crear el cobro devuelve el MISMO número', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
    })

    let segunda
    await act(async () => {
      segunda = await result.current.reservarNumeroParaCobro(7)
    })

    expect(segunda).toMatchObject({ ok: true, numero: 500 })
    expect((await leerBloque(almacen))?.proximo).toBe(500)
  })

  it('con un cobro en curso no aparta otro número', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.anotarCobroEnCurso({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 100 })
    })

    let intento
    await act(async () => {
      intento = await result.current.reservarNumeroParaCobro(7)
    })

    expect(intento).toEqual({ ok: false, motivo: 'cobro_en_curso' })
    expect((await leerBloque(almacen))?.proximo).toBe(500)
  })

  it('sin números en el bloque no aparta nada', async () => {
    const almacen = await preparar({ bloque: { proximo: 600 } })
    const { result } = await montar(almacen)

    let reserva
    await act(async () => {
      reserva = await result.current.reservarNumeroParaCobro(7)
    })

    expect(reserva).toEqual({ ok: false, motivo: 'sin_numeracion' })
    expect(await leerReservaDeCobro(almacen)).toBeNull()
  })

  it('si la reserva no se puede escribir, no devuelve un número que podría perderse', async () => {
    const almacen = await preparar({ fallarReserva: true })
    const { result } = await montar(almacen)

    let reserva
    await act(async () => {
      reserva = await result.current.reservarNumeroParaCobro(7)
    })

    expect(reserva).toEqual({ ok: false, motivo: 'error_al_guardar' })
  })
})

describe('sin huecos: un número apartado y nunca vendido no se consume', () => {
  it('cobro cancelado y después una venta común: la venta lleva el MISMO número y lo declarado coincide con lo vendido', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.anotarCobroEnCurso({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 100 })
      await result.current.soltarReservaDeCobro(500)
    })

    let encolado
    await act(async () => {
      encolado = await result.current.encolarVentaOffline(ventaEfectivo())
    })

    expect(encolado).toMatchObject({ ok: true, numero: 500 })
    const bloqueFinal = await leerBloque(almacen)
    const vendidos = (await leerOutbox(almacen)).map((v) => v.numeroPreasignado)
    expect(vendidos).toEqual([500])
    // `entregadoHasta` de la rendición es `proximo - 1`: tiene que ser el número más alto realmente vendido.
    expect((bloqueFinal?.proximo ?? 0) - 1).toBe(Math.max(...vendidos))
  })

  it('cobro cancelado sin ninguna venta posterior: el puntero no se movió, no hay número entregado que no exista', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.soltarReservaDeCobro(500)
    })

    expect((await leerBloque(almacen))?.proximo).toBe(500)
    expect(await leerReservaDeCobro(almacen)).toBeNull()
  })

  it('con un bloque a punto de agotarse y una reserva viva, no se repone (el número apartado quedaría fuera del bloque)', async () => {
    const almacen = await preparar({ bloque: { proximo: 595 } })
    await guardarReservaDeCobro(almacen, { ...reservaEnCurso, numero: 595 })
    vi.mocked(clienteDeVentas.reservarNumeracion).mockClear()
    vi.mocked(clienteDePos.obtenerInstantanea).mockResolvedValue({ modificada: false } as never)

    renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(clienteDePos.obtenerInstantanea).toHaveBeenCalled())
    await act(async () => {
      await new Promise((r) => setTimeout(r, 30))
    })

    expect(clienteDeVentas.reservarNumeracion).not.toHaveBeenCalled()
    vi.mocked(clienteDePos.obtenerInstantanea).mockRejectedValue(new Error('sin red'))
  })
})

describe('encolarVentaOffline con el número apartado', () => {
  it('encola con ESE número, avanza el puntero una sola vez y da de baja la reserva', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
    })

    let encolado
    await act(async () => {
      encolado = await result.current.encolarVentaOffline(ventaMp(500))
    })

    expect(encolado).toMatchObject({ ok: true, numero: 500, numeroVisible: '0007-00000500' })
    expect((await leerBloque(almacen))?.proximo).toBe(501)
    const outbox = await leerOutbox(almacen)
    expect(outbox).toHaveLength(1)
    expect(outbox[0]).toMatchObject({ numeroPreasignado: 500 })
    expect(outbox[0].solicitud).toMatchObject({ numeroPreasignado: 500 })
    expect(outbox[0].solicitud.pagos?.[0]).toMatchObject({ idCobroMercadoPago: 31 })
    expect(await leerReservaDeCobro(almacen)).toBeNull()
  })

  it('un número que no coincide con la reserva guardada rechaza sin encolar ni consumir', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
    })

    let encolado
    await act(async () => {
      encolado = await result.current.encolarVentaOffline(ventaMp(777))
    })

    expect(encolado).toEqual({ ok: false, motivo: 'reserva_de_cobro_perdida' })
    expect(await leerOutbox(almacen)).toEqual([])
    expect((await leerBloque(almacen))?.proximo).toBe(500)
    expect(await leerReservaDeCobro(almacen)).toMatchObject({ numero: 500 })
  })

  it('sin reserva guardada rechaza aunque el bloque tenga números', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)

    let encolado
    await act(async () => {
      encolado = await result.current.encolarVentaOffline(ventaMp(500))
    })

    expect(encolado).toEqual({ ok: false, motivo: 'reserva_de_cobro_perdida' })
    expect((await leerBloque(almacen))?.proximo).toBe(500)
  })

  it('si el bloque cambió y el número ya no es el próximo, no encola con un número ajeno al bloque', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.anotarCobroEnCurso({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 100 })
    })
    // Un bloque nuevo (otro rango) reemplaza al vivo: la reserva ya no apunta a un número de él.
    await act(async () => {
      await guardarBloque(almacen, bloque({ desde: 900, hasta: 999, proximo: 900 }))
    })
    const { result: rehecho } = await montar(almacen)

    let encolado
    await act(async () => {
      encolado = await rehecho.current.encolarVentaOffline(ventaMp(500))
    })

    expect(encolado).toEqual({ ok: false, motivo: 'reserva_de_cobro_perdida' })
    expect(await leerOutbox(almacen)).toEqual([])
    expect((await leerBloque(almacen))?.proximo).toBe(900)
  })

  it('mientras hay un cobro apartado ninguna otra venta corre: no toma ese número ni lo deja atrás', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.anotarCobroEnCurso({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 100 })
    })

    let encolado
    await act(async () => {
      encolado = await result.current.encolarVentaOffline(ventaEfectivo())
    })

    expect(encolado).toEqual({ ok: false, motivo: 'cobro_en_curso' })
    expect(await leerOutbox(almacen)).toEqual([])
    expect((await leerBloque(almacen))?.proximo).toBe(500)
    expect(await leerReservaDeCobro(almacen)).toMatchObject({ numero: 500, estado: 'en_curso' })
  })

  it('la venta con número apartado se encola aunque sea el último del bloque', async () => {
    const almacen = await preparar({ bloque: { proximo: 599, hasta: 599 } })
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
    })
    expect((await leerBloque(almacen))?.proximo).toBe(599)

    let encolado
    await act(async () => {
      encolado = await result.current.encolarVentaOffline(ventaMp(599))
    })

    expect(encolado).toMatchObject({ ok: true, numero: 599 })
    expect((await leerBloque(almacen))?.proximo).toBe(600)
  })
})

describe('soltar, consumir y leer la reserva', () => {
  it('soltar da de baja la reserva solo si el número coincide; el puntero no se toca', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.anotarCobroEnCurso({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 100 })
      await result.current.soltarReservaDeCobro(999)
    })
    expect(await leerReservaDeCobro(almacen)).toMatchObject({ estado: 'en_curso' })

    await act(async () => {
      await result.current.soltarReservaDeCobro(500)
    })

    expect(await leerReservaDeCobro(almacen)).toBeNull()
    expect((await leerBloque(almacen))?.proximo).toBe(500)
  })

  it('consumir (venta emitida online con ese número) avanza el puntero más allá del número y da de baja la reserva', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.consumirReservaDeCobro(500)
    })

    expect((await leerBloque(almacen))?.proximo).toBe(501)
    expect(await leerReservaDeCobro(almacen)).toBeNull()
    let siguiente
    await act(async () => {
      siguiente = await result.current.reservarNumeroParaCobro(7)
    })
    expect(siguiente).toMatchObject({ ok: true, numero: 501 })
  })

  it('leerReservaDeCobro devuelve la reserva en curso para retomarla tras una recarga', async () => {
    const almacen = await preparar()
    await guardarReservaDeCobro(almacen, reservaEnCurso)
    const { result } = await montar(almacen)

    let reserva
    await act(async () => {
      reserva = await result.current.leerReservaDeCobro()
    })

    expect(reserva).toMatchObject({ numero: 500, estado: 'en_curso', idCobro: 31, fueraDeBloque: false })
  })

  it('avisa fueraDeBloque cuando el número ya no es el próximo del bloque', async () => {
    const almacen = await preparar({ bloque: { proximo: 501 } })
    await guardarReservaDeCobro(almacen, reservaEnCurso)
    const { result } = await montar(almacen)

    let reserva
    await act(async () => {
      reserva = await result.current.leerReservaDeCobro()
    })

    expect(reserva).toMatchObject({ numero: 500, fueraDeBloque: true })
  })

  it('una reserva sin cobro creado (un intento que se cortó) se descarta al leer', async () => {
    const almacen = await preparar()
    await guardarReservaDeCobro(almacen, { idPuntoVenta: 7, numero: 500, estado: 'en_curso', creadoEn: 'x' })
    const { result } = await montar(almacen)

    let reserva
    await act(async () => {
      reserva = await result.current.leerReservaDeCobro()
    })

    expect(reserva).toBeNull()
    expect(await leerReservaDeCobro(almacen)).toBeNull()
  })

  it('si la venta de ese número ya está en la cola, la reserva sobrante se descarta', async () => {
    const almacen = await preparar()
    await guardarReservaDeCobro(almacen, reservaEnCurso)
    await agregarAOutbox(almacen, { idLocal: 'v', numeroPreasignado: 500, idPuntoVenta: 7, creadoEn: 'x', solicitud: solicitud({ numeroPreasignado: 500 }) })
    const { result } = await montar(almacen)

    let reserva
    await act(async () => {
      reserva = await result.current.leerReservaDeCobro()
    })

    expect(reserva).toBeNull()
    expect(await leerReservaDeCobro(almacen)).toBeNull()
  })
})

describe('reservaDeCobro (almacén)', () => {
  it('guardar confirma releyendo; una escritura degradada no cuenta como guardada', async () => {
    const buena = almacenFake()
    const rota = almacenFake({ fallarReserva: true })
    const reserva = { idPuntoVenta: 7, numero: 5, estado: 'en_curso' as const, creadoEn: 'x' }

    await expect(guardarReservaDeCobro(buena, reserva)).resolves.toBe(true)
    await expect(guardarReservaDeCobro(rota, reserva)).resolves.toBe(false)
    await borrarReservaDeCobro(buena)
    await expect(leerReservaDeCobro(buena)).resolves.toBeNull()
  })

  it('una reserva `libre` de la versión anterior ya no cuenta como reserva', async () => {
    const almacen = almacenFake()
    await almacen.escribir('reservaDeCobroMercadoPago', { idPuntoVenta: 7, numero: 5, estado: 'libre', creadoEn: 'x' })

    await expect(leerReservaDeCobro(almacen)).resolves.toBeNull()
  })
})

const cobroAprobadoSinVenta = (idCobro = 31, numero = 500) => ({ idCobro, numero, idPuntoVenta: 7, idMedioPago: 4, importe: 100, importePagado: 100 })

describe('apartarCobroSinVenta (R1b: la salida de un cobro aprobado sin venta)', () => {
  it('mueve el cobro a la lista durable, suelta la reserva sin consumir el número y la próxima venta lo lleva (sin hueco)', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.anotarCobroEnCurso({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 100 })
    })

    let apartado
    await act(async () => {
      apartado = await result.current.apartarCobroSinVenta({ numero: 500, cobro: cobroAprobadoSinVenta() })
    })

    expect(apartado).toBe(true)
    expect(await leerReservaDeCobro(almacen)).toBeNull()
    expect((await leerBloque(almacen))?.proximo).toBe(500)
    expect(await leerCobrosSinVenta(almacen)).toMatchObject([cobroAprobadoSinVenta()])
    let encolado
    await act(async () => {
      encolado = await result.current.encolarVentaOffline(ventaEfectivo())
    })
    expect(encolado).toMatchObject({ ok: true, numero: 500 })
    expect((await leerOutbox(almacen)).map((v) => v.numeroPreasignado)).toEqual([500])
  })

  it('la lista persiste: otra instancia del hook sobre el mismo almacén la lee', async () => {
    const almacen = await preparar()
    const primera = await montar(almacen)
    await act(async () => {
      await primera.result.current.reservarNumeroParaCobro(7)
      await primera.result.current.anotarCobroEnCurso({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 100 })
      await primera.result.current.apartarCobroSinVenta({ numero: 500, cobro: cobroAprobadoSinVenta() })
    })
    primera.unmount()

    const segunda = await montar(almacen)
    let lista
    await act(async () => {
      lista = await segunda.result.current.leerCobrosSinVenta()
    })

    expect(lista).toMatchObject([{ idCobro: 31, importePagado: 100, apartadoEn: expect.any(String) }])
  })

  it('es de solo altas: apartar otro cobro conserva el anterior y apartar el mismo no lo duplica', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.anotarCobroEnCurso({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 100 })
      await result.current.apartarCobroSinVenta({ numero: 500, cobro: cobroAprobadoSinVenta() })
      await result.current.reservarNumeroParaCobro(7)
      await result.current.anotarCobroEnCurso({ numero: 500, idCobro: 32, idMedioPago: 4, importe: 100 })
      await result.current.apartarCobroSinVenta({ numero: 500, cobro: cobroAprobadoSinVenta(32) })
      await result.current.apartarCobroSinVenta({ numero: 500, cobro: cobroAprobadoSinVenta(32) })
    })

    expect((await leerCobrosSinVenta(almacen)).map((c) => c.idCobro)).toEqual([31, 32])
  })

  it('con la reserva de OTRO cobro no suelta nada ni agrega nada a la lista', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.anotarCobroEnCurso({ numero: 500, idCobro: 77, idMedioPago: 4, importe: 100 })
    })

    let apartado
    await act(async () => {
      apartado = await result.current.apartarCobroSinVenta({ numero: 500, cobro: cobroAprobadoSinVenta(31) })
    })

    expect(apartado).toBe(false)
    expect(await leerReservaDeCobro(almacen)).toMatchObject({ idCobro: 77 })
    expect(await leerCobrosSinVenta(almacen)).toEqual([])
  })

  it('si la lista no se puede escribir, la reserva se conserva', async () => {
    const almacen = await preparar()
    const original = almacen.escribir.bind(almacen)
    almacen.escribir = async (clave, valor) => (clave === 'cobrosMercadoPagoSinVenta' ? false : original(clave, valor))
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.anotarCobroEnCurso({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 100 })
    })

    let apartado
    await act(async () => {
      apartado = await result.current.apartarCobroSinVenta({ numero: 500, cobro: cobroAprobadoSinVenta() })
    })

    expect(apartado).toBe(false)
    expect(await leerReservaDeCobro(almacen)).toMatchObject({ idCobro: 31 })
  })

  it('un cobro de la reserva que otra pestaña ya soltó igual queda en la lista (el cobro aprobado no se pierde)', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)

    let apartado
    await act(async () => {
      apartado = await result.current.apartarCobroSinVenta({ numero: 500, cobro: cobroAprobadoSinVenta() })
    })

    expect(apartado).toBe(true)
    expect(await leerCobrosSinVenta(almacen)).toHaveLength(1)
  })
})

describe('R3: dueño de la reserva (compare-and-set entre pestañas)', () => {
  it('la reserva guarda un dueño y reservar lo devuelve', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)

    let reserva
    await act(async () => {
      reserva = await result.current.reservarNumeroParaCobro(7)
    })

    const guardada = await leerReservaDeCobro(almacen)
    expect(guardada?.dueno).toEqual(expect.any(String))
    expect(reserva).toMatchObject({ ok: true, dueno: guardada?.dueno })
  })

  it('otra pestaña (otra instancia, sin el dueño) no suelta, no consume, no anota ni aparta la reserva ajena', async () => {
    const almacen = await preparar()
    const a = await montar(almacen)
    const b = await montar(almacen)
    await act(async () => {
      await a.result.current.reservarNumeroParaCobro(7)
      await a.result.current.anotarCobroEnCurso({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 100 })
    })

    let anotado
    let apartado
    await act(async () => {
      await b.result.current.soltarReservaDeCobro(500)
      await b.result.current.consumirReservaDeCobro(500)
      anotado = await b.result.current.anotarCobroEnCurso({ numero: 500, idCobro: 99, idMedioPago: 4, importe: 5 })
      apartado = await b.result.current.apartarCobroSinVenta({ numero: 500, cobro: cobroAprobadoSinVenta() })
    })

    expect(anotado).toBe(false)
    expect(apartado).toBe(false)
    expect(await leerReservaDeCobro(almacen)).toMatchObject({ numero: 500, idCobro: 31 })
    expect((await leerBloque(almacen))?.proximo).toBe(500)
    await act(async () => {
      await a.result.current.soltarReservaDeCobro(500)
    })
    expect(await leerReservaDeCobro(almacen)).toBeNull()
  })

  it('un soltar con el dueño de una reserva anterior no toca la nueva reserva del MISMO número', async () => {
    const almacen = await preparar()
    const a = await montar(almacen)
    const b = await montar(almacen)
    await act(async () => {
      await a.result.current.reservarNumeroParaCobro(7)
      await a.result.current.anotarCobroEnCurso({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 100 })
    })
    let vista
    await act(async () => {
      vista = await b.result.current.leerReservaDeCobro()
    })
    const duenoViejo = (vista as unknown as { dueno: string }).dueno
    await act(async () => {
      await a.result.current.soltarReservaDeCobro(500)
      await a.result.current.reservarNumeroParaCobro(7)
      await a.result.current.anotarCobroEnCurso({ numero: 500, idCobro: 32, idMedioPago: 4, importe: 100 })
    })

    await act(async () => {
      await b.result.current.soltarReservaDeCobro(500, duenoViejo)
      await b.result.current.consumirReservaDeCobro(500, duenoViejo)
    })

    expect(await leerReservaDeCobro(almacen)).toMatchObject({ numero: 500, idCobro: 32 })
    expect((await leerBloque(almacen))?.proximo).toBe(500)
  })

  it('una reserva guardada antes de los dueños (sin dueño) se sigue pudiendo soltar', async () => {
    const almacen = await preparar()
    await guardarReservaDeCobro(almacen, reservaEnCurso)
    const { result } = await montar(almacen)

    await act(async () => {
      await result.current.soltarReservaDeCobro(500)
    })

    expect(await leerReservaDeCobro(almacen)).toBeNull()
  })

  it('la venta del propio cobro decide contra el bloque PERSISTIDO: si otra pestaña lo movió, se rechaza sin encolar', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.anotarCobroEnCurso({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 100 })
    })
    await guardarBloque(almacen, bloque({ proximo: 501 }))

    let encolado
    await act(async () => {
      encolado = await result.current.encolarVentaOffline(ventaMp(500))
    })

    expect(encolado).toEqual({ ok: false, motivo: 'reserva_de_cobro_perdida' })
    expect(await leerOutbox(almacen)).toEqual([])
  })

  it('reservar toma el próximo número del bloque persistido, no el que esta pestaña tenía en memoria', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await guardarBloque(almacen, bloque({ proximo: 510 }))

    let reserva
    await act(async () => {
      reserva = await result.current.reservarNumeroParaCobro(7)
    })

    expect(reserva).toMatchObject({ ok: true, numero: 510 })
  })
})

describe('R3: Web Locks serializa las operaciones de la reserva entre pestañas', () => {
  const originalLocks = Object.getOwnPropertyDescriptor(window.navigator, 'locks')
  afterEach(() => {
    if (originalLocks) Object.defineProperty(window.navigator, 'locks', originalLocks)
    else Reflect.deleteProperty(window.navigator, 'locks')
  })

  function instalarCandados() {
    const nombres: string[] = []
    let dentro = 0
    let maximoDentro = 0
    let cola: Promise<unknown> = Promise.resolve()
    const request = vi.fn((nombre: string, callback: () => Promise<unknown>) => {
      nombres.push(nombre)
      const ejecucion = cola.then(async () => {
        dentro += 1
        maximoDentro = Math.max(maximoDentro, dentro)
        try {
          return await callback()
        } finally {
          dentro -= 1
        }
      })
      cola = ejecucion.catch(() => undefined)
      return ejecucion
    })
    Object.defineProperty(window.navigator, 'locks', { configurable: true, value: { request } })
    return { request, nombres, maximoDentro: () => maximoDentro, dentro: () => dentro }
  }

  it('reservar, anotar, soltar, consumir, apartar y leer toman el candado "ways-pos-reserva-cobro" y acceden a la reserva solo adentro', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    const candados = instalarCandados()
    const accesosFueraDelCandado: string[] = []
    const leerOriginal = almacen.leer.bind(almacen)
    almacen.leer = async <T,>(clave: string) => {
      if (clave === 'reservaDeCobroMercadoPago' && candados.dentro() === 0) accesosFueraDelCandado.push(clave)
      return leerOriginal<T>(clave)
    }

    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.anotarCobroEnCurso({ numero: 500, idCobro: 31, idMedioPago: 4, importe: 100 })
      await result.current.leerReservaDeCobro()
      await result.current.soltarReservaDeCobro(500)
      await result.current.reservarNumeroParaCobro(7)
      await result.current.consumirReservaDeCobro(500)
      await result.current.reservarNumeroParaCobro(7)
      await result.current.anotarCobroEnCurso({ numero: 501, idCobro: 32, idMedioPago: 4, importe: 100 })
      await result.current.apartarCobroSinVenta({ numero: 501, cobro: cobroAprobadoSinVenta(32, 501) })
      await result.current.encolarVentaOffline(ventaEfectivo())
    })

    expect(new Set(candados.nombres)).toEqual(new Set(['ways-pos-reserva-cobro']))
    expect(candados.request.mock.calls.length).toBeGreaterThanOrEqual(10)
    expect(accesosFueraDelCandado).toEqual([])
  })

  it('dos pestañas operando a la vez nunca están dentro del candado al mismo tiempo', async () => {
    const almacen = await preparar()
    const a = await montar(almacen)
    const b = await montar(almacen)
    const candados = instalarCandados()

    await act(async () => {
      await Promise.all([
        a.result.current.reservarNumeroParaCobro(7),
        b.result.current.reservarNumeroParaCobro(7),
        a.result.current.leerReservaDeCobro(),
        b.result.current.leerReservaDeCobro(),
      ])
    })

    expect(candados.request.mock.calls.length).toBeGreaterThanOrEqual(4)
    expect(candados.maximoDentro()).toBe(1)
  })

  it('sin navigator.locks las operaciones siguen funcionando', async () => {
    Reflect.deleteProperty(window.navigator, 'locks')
    const almacen = await preparar()
    const { result } = await montar(almacen)

    let reserva
    await act(async () => {
      reserva = await result.current.reservarNumeroParaCobro(7)
    })

    expect(reserva).toMatchObject({ ok: true, numero: 500 })
  })
})
