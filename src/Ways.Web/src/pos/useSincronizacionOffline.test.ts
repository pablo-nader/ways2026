import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useSincronizacionOffline } from './useSincronizacionOffline'
import { agregarAOutbox, guardarBloque, leerBloque, leerOutbox, type BloqueDeNumeracionLocal, type VentaEnCola } from './outboxOffline'
import { guardarInstantaneaLocal } from './instantaneaOffline'
import type { AlmacenClaveValor } from './almacenPos'
import { ErrorApi, ErrorDeRed } from '../api/cliente'
import type { ArticuloDeInstantanea, InstantaneaDePos, SolicitudDeVenta } from '../api/tipos'

const emitirMock = vi.fn()
const reservarNumeracionMock = vi.fn()
const obtenerInstantaneaMock = vi.fn()

vi.mock('../api/ventas', () => ({
  clienteDeVentas: {
    emitir: (...args: unknown[]) => emitirMock(...args),
    reservarNumeracion: (...args: unknown[]) => reservarNumeracionMock(...args),
  },
}))

vi.mock('../api/pos', () => ({
  clienteDePos: { obtenerInstantanea: (...args: unknown[]) => obtenerInstantaneaMock(...args) },
}))

/** Mismo fake en memoria que `outboxOffline.test.ts` — evita acoplar este test a IndexedDB real. */
function almacenFake(): AlmacenClaveValor {
  const datos = new Map<string, unknown>()
  return {
    async leer<T>(clave: string) {
      return (datos.has(clave) ? (datos.get(clave) as T) : null) ?? null
    },
    async escribir<T>(clave: string, valor: T) {
      datos.set(clave, valor)
    },
  }
}

function articuloFixture(sobrescribir: Partial<ArticuloDeInstantanea> = {}): ArticuloDeInstantanea {
  return {
    idArticulo: 1,
    codigoInterno: 'A0001',
    nombre: 'Coca Cola 1L',
    codigosBarra: ['7790001234567'],
    precioOriginal: 100,
    precioFinal: 100,
    descuentoUnitario: 0,
    aplicadas: [],
    idAlicuotaIva: 1,
    porcentajeIva: 21,
    ...sobrescribir,
  }
}

function instantaneaFixture(sobrescribir: Partial<InstantaneaDePos> = {}): InstantaneaDePos {
  return {
    momento: '2026-09-20T10:00:00.000Z',
    idPuntoVenta: 7,
    articulos: [articuloFixture()],
    mediosDePago: [],
    toleranciaPago: 0,
    ...sobrescribir,
  }
}

function solicitudFixture(sobrescribir: Partial<SolicitudDeVenta> = {}): SolicitudDeVenta {
  return {
    idPuntoVenta: 7,
    idCliente: 1,
    codigoTipoComprobante: 'TX',
    idComprobanteAsociado: null,
    lineas: [{ idArticulo: 1, cantidad: 1, codigoBarra: '7790001234567', idLote: null }],
    pagos: [{ idMedioPago: 1, importe: 100, referencia: null, vuelto: 0 }],
    direccionEntrega: null,
    observaciones: null,
    ...sobrescribir,
  }
}

function bloqueFixture(sobrescribir: Partial<BloqueDeNumeracionLocal> = {}): BloqueDeNumeracionLocal {
  return { idPuntoVenta: 7, codigoTipoComprobante: 'TX', desde: 100, hasta: 200, proximo: 100, ...sobrescribir }
}

beforeEach(() => {
  emitirMock.mockReset()
  reservarNumeracionMock.mockReset()
  obtenerInstantaneaMock.mockReset()
  // default: sin servidor (ErrorDeRed) — cada test que necesite señal la sobrescribe.
  obtenerInstantaneaMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))
  reservarNumeracionMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))
  emitirMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))
})

describe('useSincronizacionOffline — carga inicial (goal A: sobrevive un restart)', () => {
  it('hidrata instantánea/outbox ya persistidos, sin esperar ningún fetch', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, instantaneaFixture())
    await agregarAOutbox(almacen, {
      idLocal: 'a',
      numeroPreasignado: 100,
      idPuntoVenta: 7,
      creadoEn: '2026-09-20T09:00:00.000Z',
      solicitud: solicitudFixture(),
    })

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(result.current.instantanea).not.toBeNull())
    expect(result.current.instantanea?.momento).toBe('2026-09-20T10:00:00.000Z')
    expect(result.current.outboxCount).toBe(1)
  })

  it('sin punto de venta, queda inerte: sin instantánea, sin outbox, sin llamar a la red', async () => {
    const almacen = almacenFake()
    renderHook(() => useSincronizacionOffline({ idPuntoVenta: null, activo: true, almacen, intervaloMs: 60_000 }))

    await act(async () => {
      await Promise.resolve()
    })
    expect(obtenerInstantaneaMock).not.toHaveBeenCalled()
  })
})

describe('useSincronizacionOffline — refresco oportunista de la instantánea', () => {
  it('con señal, refresca y persiste la instantánea nueva', async () => {
    const almacen = almacenFake()
    const fresca = instantaneaFixture({ momento: '2026-09-20T11:00:00.000Z' })
    obtenerInstantaneaMock.mockResolvedValue(fresca)

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(result.current.instantanea?.momento).toBe('2026-09-20T11:00:00.000Z'))
    await expect(almacen.leer('instantanea')).resolves.toEqual(fresca)
    expect(result.current.enLinea).toBe(true)
  })

  it('sin señal (ErrorDeRed), conserva la instantánea local previa en vez de borrarla', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, instantaneaFixture())
    obtenerInstantaneaMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(result.current.instantanea).not.toBeNull())
    expect(result.current.instantanea?.momento).toBe('2026-09-20T10:00:00.000Z')
    expect(result.current.enLinea).toBe(false)
  })

  it('un ErrorApi (el servidor respondió, aunque rechace) NO marca enLinea en false — solo ErrorDeRed lo hace', async () => {
    const almacen = almacenFake()
    obtenerInstantaneaMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'Dispositivo revocado'))

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await act(async () => {
      await Promise.resolve()
      await Promise.resolve()
    })
    expect(result.current.enLinea).toBe(true)
  })
})

describe('useSincronizacionOffline — reposición del bloque de numeración', () => {
  it('con señal y sin bloque local, repone uno nuevo', async () => {
    const almacen = almacenFake()
    obtenerInstantaneaMock.mockResolvedValue(instantaneaFixture())
    reservarNumeracionMock.mockResolvedValue({ desde: 300, hasta: 399, idPuntoVenta: 7, codigoTipoComprobante: 'TX' })

    renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(reservarNumeracionMock).toHaveBeenCalledWith({ idPuntoVenta: 7, codigoTipoComprobante: 'TX', cantidad: 100 }))
  })

  it('repone el bloque Y LO PERSISTE de inmediato (goal C: sobrevive un restart, no solo vive en memoria)', async () => {
    const almacen = almacenFake()
    obtenerInstantaneaMock.mockResolvedValue(instantaneaFixture())
    reservarNumeracionMock.mockResolvedValue({ desde: 300, hasta: 399, idPuntoVenta: 7, codigoTipoComprobante: 'TX' })

    renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(async () => {
      await expect(leerBloque(almacen)).resolves.toEqual({ idPuntoVenta: 7, codigoTipoComprobante: 'TX', desde: 300, hasta: 399, proximo: 300 })
    })
  })

  it('con un bloque local todavía por encima del umbral, NO pide uno nuevo', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, bloqueFixture({ proximo: 100, hasta: 500 }))
    obtenerInstantaneaMock.mockResolvedValue(instantaneaFixture())

    renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(obtenerInstantaneaMock).toHaveBeenCalled())
    await act(async () => {
      await Promise.resolve()
    })
    expect(reservarNumeracionMock).not.toHaveBeenCalled()
  })

  it('sin señal, nunca intenta reponer (la reposición exige haber confirmado conexión en este mismo ciclo)', async () => {
    const almacen = almacenFake()
    obtenerInstantaneaMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))

    renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await act(async () => {
      await Promise.resolve()
      await Promise.resolve()
      await Promise.resolve()
    })
    expect(reservarNumeracionMock).not.toHaveBeenCalled()
  })
})

describe('useSincronizacionOffline — drenado del outbox, EN ORDEN', () => {
  async function outboxConDos(almacen: AlmacenClaveValor) {
    const a: VentaEnCola = {
      idLocal: 'a',
      numeroPreasignado: 100,
      idPuntoVenta: 7,
      creadoEn: '2026-09-20T09:00:00.000Z',
      solicitud: solicitudFixture({ numeroPreasignado: 100 }),
    }
    const b: VentaEnCola = {
      idLocal: 'b',
      numeroPreasignado: 101,
      idPuntoVenta: 7,
      creadoEn: '2026-09-20T09:01:00.000Z',
      solicitud: solicitudFixture({ numeroPreasignado: 101 }),
    }
    await agregarAOutbox(almacen, a)
    await agregarAOutbox(almacen, b)
    return [a, b]
  }

  it('drena todo el outbox cuando el servidor confirma cada una, en el orden de encolado', async () => {
    const almacen = almacenFake()
    const [a, b] = await outboxConDos(almacen)
    emitirMock.mockResolvedValue({ id: 1 })
    obtenerInstantaneaMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(result.current.outboxCount).toBe(0))
    // Se emitió la solicitud de 'a' (numeroPreasignado 100) ANTES que la de 'b' (101) — el mock
    // no impone orden por sí solo, así que esto prueba que `drenarOutbox` de verdad recorre la
    // cola de punta a punta en vez de, por ejemplo, drenar solo la última.
    expect(emitirMock.mock.calls.map((c) => (c[0] as SolicitudDeVenta).numeroPreasignado)).toEqual([a.numeroPreasignado, b.numeroPreasignado])
    await expect(leerOutbox(almacen)).resolves.toEqual([])
  })

  it('un rechazo REAL (ErrorApi) del más viejo detiene el drenado — nunca salta al siguiente', async () => {
    const almacen = almacenFake()
    await outboxConDos(almacen)
    emitirMock.mockRejectedValue(new ErrorApi(409, 'numero_preasignado_con_otro_contenido', 'Contenido distinto'))
    obtenerInstantaneaMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(result.current.errorDeDrenado).not.toBe(''))
    expect(emitirMock).toHaveBeenCalledTimes(1)
    expect(result.current.outboxCount).toBe(2)
  })

  it('sin señal (ErrorDeRed) en el más viejo, deja el outbox intacto y sin marcar error de drenado', async () => {
    const almacen = almacenFake()
    await outboxConDos(almacen)
    emitirMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))
    obtenerInstantaneaMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await act(async () => {
      await Promise.resolve()
      await Promise.resolve()
      await Promise.resolve()
    })
    expect(result.current.outboxCount).toBe(2)
    expect(result.current.errorDeDrenado).toBe('')
  })
})

describe('useSincronizacionOffline — encolarVentaOffline', () => {
  it('rechaza sin instantánea local', async () => {
    const almacen = almacenFake()
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    const resultado = await result.current.encolarVentaOffline({
      solicitudBase: solicitudFixture(),
      esConsumidorFinal: true,
      pagos: [{ comportamiento: 'Efectivo' }],
    })
    expect(resultado).toEqual({ ok: false, motivo: 'sin_instantanea' })
  })

  it('rechaza cliente no-CF, aunque haya instantánea y números disponibles', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, instantaneaFixture())
    await guardarBloque(almacen, bloqueFixture())
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    const resultado = await result.current.encolarVentaOffline({
      solicitudBase: solicitudFixture(),
      esConsumidorFinal: false,
      pagos: [{ comportamiento: 'Efectivo' }],
    })
    expect(resultado).toEqual({ ok: false, motivo: 'cliente_no_admitido' })
  })

  it('rechaza un pago no-efectivo (cuenta corriente), aunque haya instantánea y números disponibles', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, instantaneaFixture())
    await guardarBloque(almacen, bloqueFixture())
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    const resultado = await result.current.encolarVentaOffline({
      solicitudBase: solicitudFixture(),
      esConsumidorFinal: true,
      pagos: [{ comportamiento: 'CuentaCorriente' }],
    })
    expect(resultado).toEqual({ ok: false, motivo: 'medio_no_admitido' })
    // Rechazada: no debe haber tocado el bloque ni el outbox.
    await expect(leerBloque(almacen)).resolves.toEqual(bloqueFixture())
    await expect(leerOutbox(almacen)).resolves.toEqual([])
  })

  it('rechaza sin números disponibles', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, instantaneaFixture())
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    const resultado = await result.current.encolarVentaOffline({
      solicitudBase: solicitudFixture(),
      esConsumidorFinal: true,
      pagos: [{ comportamiento: 'Efectivo' }],
    })
    expect(resultado).toEqual({ ok: false, motivo: 'sin_numeracion' })
  })

  it('éxito: toma el próximo número, persiste el bloque decrementado y encola la venta con precio/número', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, instantaneaFixture({ articulos: [articuloFixture({ precioOriginal: 120, precioFinal: 100, descuentoUnitario: 20 })] }))
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    let resultado
    await act(async () => {
      resultado = await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture(),
        esConsumidorFinal: true,
        pagos: [{ comportamiento: 'Efectivo' }],
      })
    })

    expect(resultado).toEqual({ ok: true, numero: 150, numeroVisible: '0007-00000150' })
    await expect(leerBloque(almacen)).resolves.toEqual(bloqueFixture({ proximo: 151, hasta: 200 }))
    const outbox = await leerOutbox(almacen)
    expect(outbox).toHaveLength(1)
    expect(outbox[0].numeroPreasignado).toBe(150)
    expect(outbox[0].solicitud.numeroPreasignado).toBe(150)
    expect(outbox[0].solicitud.lineas?.[0]).toMatchObject({ precioUnitario: 100, descuentoUnitario: 20 })

    await waitFor(() => expect(result.current.outboxCount).toBe(1))
  })

  it('no admite dos ventas consecutivas con el MISMO número (avanza el puntero cada vez)', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, instantaneaFixture())
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    let primero
    let segundo
    await act(async () => {
      primero = await result.current.encolarVentaOffline({ solicitudBase: solicitudFixture(), esConsumidorFinal: true, pagos: [{ comportamiento: 'Efectivo' }] })
    })
    await act(async () => {
      segundo = await result.current.encolarVentaOffline({ solicitudBase: solicitudFixture(), esConsumidorFinal: true, pagos: [{ comportamiento: 'Efectivo' }] })
    })

    expect(primero).toMatchObject({ ok: true, numero: 150 })
    expect(segundo).toMatchObject({ ok: true, numero: 151 })
    await waitFor(() => expect(result.current.outboxCount).toBe(2))
  })
})
