import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useSincronizacionOffline } from './useSincronizacionOffline'
import { agregarAOutbox, guardarBloque, leerBloque, leerOutbox, type BloqueDeNumeracionLocal } from './outboxOffline'
import { guardarInstantaneaLocal } from './instantaneaOffline'
import { borrarReservaDeCobro, guardarReservaDeCobro, leerReservaDeCobro } from './reservaDeCobro'
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

describe('reservarNumeroParaCobro', () => {
  it('toma el próximo número del bloque, avanza el puntero y deja la reserva libre en el almacén', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)

    let reserva
    await act(async () => {
      reserva = await result.current.reservarNumeroParaCobro(7)
    })

    expect(reserva).toEqual({ ok: true, numero: 500, reutilizado: false })
    expect((await leerBloque(almacen))?.proximo).toBe(501)
    expect(await leerReservaDeCobro(almacen)).toMatchObject({ idPuntoVenta: 7, numero: 500, estado: 'libre' })
  })

  it('reservar de nuevo con la reserva libre devuelve el MISMO número y no consume otro del bloque', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
    })

    let segunda
    await act(async () => {
      segunda = await result.current.reservarNumeroParaCobro(7)
    })

    expect(segunda).toEqual({ ok: true, numero: 500, reutilizado: true })
    expect((await leerBloque(almacen))?.proximo).toBe(501)
  })

  it('con un cobro en curso no reserva otro número', async () => {
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
    expect((await leerBloque(almacen))?.proximo).toBe(501)
  })

  it('sin números en el bloque no reserva nada', async () => {
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

describe('encolarVentaOffline con el número reservado', () => {
  it('encola con ESE número, no toma otro del bloque y da de baja la reserva', async () => {
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

  it('un número reservado que no coincide con la reserva guardada rechaza sin encolar ni consumir', async () => {
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
    expect((await leerBloque(almacen))?.proximo).toBe(501)
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

  it('un número libre (cobro cancelado) lo usa la próxima venta del dispositivo antes de tomar uno nuevo', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.soltarReservaDeCobro(500)
    })

    let encolado
    await act(async () => {
      encolado = await result.current.encolarVentaOffline(ventaEfectivo())
    })

    expect(encolado).toMatchObject({ ok: true, numero: 500 })
    expect((await leerBloque(almacen))?.proximo).toBe(501)
    expect(await leerReservaDeCobro(almacen)).toBeNull()
  })

  it('un número con cobro en curso NO lo toma otra venta: esa toma el siguiente del bloque', async () => {
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

    expect(encolado).toMatchObject({ ok: true, numero: 501 })
    expect(await leerReservaDeCobro(almacen)).toMatchObject({ numero: 500, estado: 'en_curso' })
  })

  it('la venta con número reservado se encola aunque el bloque ya no tenga números', async () => {
    const almacen = await preparar({ bloque: { proximo: 599, hasta: 599 } })
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
    })
    expect((await leerBloque(almacen))?.proximo).toBe(600)

    let encolado
    await act(async () => {
      encolado = await result.current.encolarVentaOffline(ventaMp(599))
    })

    expect(encolado).toMatchObject({ ok: true, numero: 599 })
  })
})

describe('soltar, consumir y leer la reserva', () => {
  it('soltar deja libre el número y conserva su fecha; es idempotente con otro número', async () => {
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

    const libre = await leerReservaDeCobro(almacen)
    expect(libre).toMatchObject({ numero: 500, estado: 'libre' })
    expect(libre).not.toHaveProperty('idCobro')
  })

  it('consumir (venta registrada online) da de baja la reserva y el número no vuelve a ofrecerse', async () => {
    const almacen = await preparar()
    const { result } = await montar(almacen)
    await act(async () => {
      await result.current.reservarNumeroParaCobro(7)
      await result.current.consumirReservaDeCobro(500)
    })

    let reserva
    await act(async () => {
      reserva = await result.current.reservarNumeroParaCobro(7)
    })

    expect(reserva).toEqual({ ok: true, numero: 501, reutilizado: false })
  })

  it('leerReservaDeCobro devuelve la reserva en curso para retomarla tras una recarga', async () => {
    const almacen = await preparar()
    await guardarReservaDeCobro(almacen, { idPuntoVenta: 7, numero: 500, estado: 'en_curso', idCobro: 31, idMedioPago: 4, importe: 100, creadoEn: 'x' })
    const { result } = await montar(almacen)

    let reserva
    await act(async () => {
      reserva = await result.current.leerReservaDeCobro()
    })

    expect(reserva).toMatchObject({ numero: 500, estado: 'en_curso', idCobro: 31 })
  })

  it('si la venta de ese número ya está en la cola, la reserva sobrante se descarta', async () => {
    const almacen = await preparar()
    await guardarReservaDeCobro(almacen, { idPuntoVenta: 7, numero: 500, estado: 'en_curso', idCobro: 31, idMedioPago: 4, importe: 100, creadoEn: 'x' })
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
    const reserva = { idPuntoVenta: 7, numero: 5, estado: 'libre' as const, creadoEn: 'x' }

    await expect(guardarReservaDeCobro(buena, reserva)).resolves.toBe(true)
    await expect(guardarReservaDeCobro(rota, reserva)).resolves.toBe(false)
    await borrarReservaDeCobro(buena)
    await expect(leerReservaDeCobro(buena)).resolves.toBeNull()
  })
})
