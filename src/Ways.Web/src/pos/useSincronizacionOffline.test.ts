import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useSincronizacionOffline } from './useSincronizacionOffline'
import { agregarAOutbox, agregarARechazada, guardarBloque, leerBloque, leerOutbox, leerRechazadas, type BloqueDeNumeracionLocal, type VentaEnCola } from './outboxOffline'
import { guardarInstantaneaLocal, resolverPreciosOffline } from './instantaneaOffline'
import type { AlmacenClaveValor } from './almacenPos'
import { ErrorApi, ErrorDeRed } from '../api/cliente'
import { previaDeLinea } from '../api/ventas'
import type { ArticuloDeInstantanea, EscalonDeCantidad, InstantaneaDePos, SolicitudDeVenta } from '../api/tipos'

const emitirMock = vi.fn()
const reservarNumeracionMock = vi.fn()
const obtenerInstantaneaMock = vi.fn()

// Solo `clienteDeVentas` (el único HTTP que este hook toca) se reemplaza; el resto del módulo
// queda REAL para que el test de acuerdo vista-previa/payload pueda usar la `previaDeLinea` de
// producción en vez de reimplementar su fórmula.
vi.mock('../api/ventas', async (importarOriginal) => ({
  ...(await importarOriginal<typeof import('../api/ventas')>()),
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
      return true
    },
  }
}

/** Igual que `almacenFake`, pero la escritura del outbox SIEMPRE se degrada (`false`) — simula
 * cuota agotada / almacenamiento bloqueado justo al intentar encolar una venta nueva, con la
 * instantánea y el bloque ya persistidos de antes (el escenario real: el dispositivo viene
 * funcionando bien y el almacenamiento se llena recién ahora). */
function almacenFakeConOutboxRoto(): AlmacenClaveValor {
  const datos = new Map<string, unknown>()
  return {
    async leer<T>(clave: string) {
      return (datos.has(clave) ? (datos.get(clave) as T) : null) ?? null
    },
    async escribir<T>(clave: string, valor: T) {
      if (clave === 'outbox') return false
      datos.set(clave, valor)
      return true
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

/** Tramos con valores todos distintos entre sí y del precio plano — ver el mismo criterio en
 * `instantaneaOffline.test.ts`. */
const ESCALON_3: EscalonDeCantidad = { cantidadDesde: 3, precioFinal: 90, descuentoUnitario: 10, aplicadas: [{ idOferta: 31, nombre: '3 o más', descuentoUnitario: 10 }] }
const ESCALON_6: EscalonDeCantidad = { cantidadDesde: 6, precioFinal: 80, descuentoUnitario: 20, aplicadas: [{ idOferta: 61, nombre: '6 o más', descuentoUnitario: 20 }] }

function articuloConEscalonesFixture(sobrescribir: Partial<ArticuloDeInstantanea> = {}): ArticuloDeInstantanea {
  return articuloFixture({ precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [], escalones: [ESCALON_3, ESCALON_6], ...sobrescribir })
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

  // judgment-day ronda 1 (CRITICAL): antes de este fix, un rechazo permanente del ítem más viejo
  // frenaba el drenado ENTERO para siempre — 'b' (sano) quedaba rehén de 'a' (trabado) sin límite.
  it('un rechazo REAL (ErrorApi) del más viejo se archiva como "necesita atención" y el drenado SIGUE con el resto de la cola', async () => {
    const almacen = almacenFake()
    const [a, b] = await outboxConDos(almacen)
    emitirMock.mockImplementation((solicitud: SolicitudDeVenta) =>
      solicitud.numeroPreasignado === a.numeroPreasignado
        ? Promise.reject(new ErrorApi(409, 'numero_preasignado_con_otro_contenido', 'Contenido distinto'))
        : Promise.resolve({ id: 1 }),
    )
    obtenerInstantaneaMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    // 'a' queda archivada como rechazada, visible con su error real — nunca descartada en silencio.
    await waitFor(() => expect(result.current.ventasConError).toHaveLength(1))
    expect(result.current.ventasConError[0]).toMatchObject({ idLocal: a.idLocal, numeroPreasignado: a.numeroPreasignado })
    expect(result.current.ventasConError[0].mensaje).toContain(String(a.numeroPreasignado))

    // 'b' (sano) SÍ se drenó — no quedó rehén de 'a'.
    await waitFor(() => expect(result.current.outboxCount).toBe(0))
    expect(emitirMock.mock.calls.map((c) => (c[0] as SolicitudDeVenta).numeroPreasignado)).toContain(b.numeroPreasignado)
    await expect(leerOutbox(almacen)).resolves.toEqual([])
    await expect(leerRechazadas(almacen)).resolves.toHaveLength(1)
  })

  it('sin señal (ErrorDeRed) en el más viejo, deja el outbox intacto — transitorio, nunca se archiva como rechazada', async () => {
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
    expect(result.current.ventasConError).toEqual([])
  })

  // judgment-day ronda 2 (CRITICAL — regresión de la ronda 1): un 503 (`resultado_incierto`,
  // `ManejadorDeErrores.RespuestaDeFalloTransitorio`) significa "no se sabe si la escritura llegó
  // a pasar", NUNCA un rechazo — antes de este fix, esta rama caía en el `else` genérico y
  // archivaba la venta como rechazada de forma PERMANENTE, perdiendo la recuperación automática.
  it('un 503 (ErrorApi transitorio) en el más viejo NUNCA se archiva — el outbox la conserva y el próximo ciclo la reintenta hasta confirmar', async () => {
    const almacen = almacenFake()
    const [a] = await outboxConDos(almacen)
    let intentosDeA = 0
    emitirMock.mockImplementation((solicitud: SolicitudDeVenta) => {
      if (solicitud.numeroPreasignado !== a.numeroPreasignado) return Promise.resolve({ id: 1 })
      intentosDeA += 1
      return intentosDeA === 1
        ? Promise.reject(new ErrorApi(503, 'resultado_incierto', 'No se pudo confirmar el resultado de la operación: verificá el listado antes de reintentar.'))
        : Promise.resolve({ id: 1 })
    })
    obtenerInstantaneaMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 20 }))

    // Prueba la RETRY real: 'a' necesita un SEGUNDO intento (el primero, transitorio, no cuenta)
    // — leído directo de `intentosDeA` en vez de `outboxCount` (ese estado de React arranca en 0
    // por defecto, así que esperarlo en 0 sería un falso positivo trivial ANTES de que el primer
    // ciclo siquiera corra).
    await waitFor(() => expect(intentosDeA).toBeGreaterThanOrEqual(2), { timeout: 3000 })
    // El outbox termina realmente vacío (releído del almacén, no del estado de React) — si 'a' se
    // hubiera archivado como rechazada en el camino (como antes de este fix), jamás habría un
    // segundo intento y el outbox seguiría con las dos ventas.
    await waitFor(async () => expect(await leerOutbox(almacen)).toEqual([]), { timeout: 3000 })
    expect(result.current.ventasConError).toEqual([])
  })

  // judgment-day ronda 2 (WARNING): "archivar + quitar del outbox" son DOS escrituras — si la
  // segunda se pierde una vez, la venta queda temporalmente en AMBOS stores; el próximo ciclo
  // tiene que converger a un solo store sin duplicar la entrada archivada.
  it('si la salida del outbox falla una vez después de archivar, el próximo ciclo converge sin duplicar y sin dejar una rejection sin manejar', async () => {
    // Sin el `try/catch` alrededor de `quitarDeOutbox` dentro de `drenarOutbox`, el `throw` de
    // `ErrorDePersistenciaOffline` escapa como una promise rejection nunca manejada (el llamador
    // de `ciclo()` es `void ciclo(...)`, fire-and-forget) — la cola sigue convergiendo por el
    // lado (el próximo timer reintenta igual), así que solo esperar la convergencia final NO
    // alcanza para probar esta guarda; hace falta escuchar `unhandledRejection` directamente.
    // `process` no está tipado en `tsconfig.app.json` a propósito (código de navegador, sin
    // `@types/node`) — se accede vía `globalThis` con una forma local mínima en vez de traer los
    // tipos de Node a todo el programa.
    const procesoDeNode = (globalThis as { process?: { on: (evento: string, listener: (razon: unknown) => void) => void; off: (evento: string, listener: (razon: unknown) => void) => void } }).process
    const rejeccionesNoManejadas: unknown[] = []
    const alRejectionNoManejada = (razon: unknown) => rejeccionesNoManejadas.push(razon)
    procesoDeNode?.on('unhandledRejection', alRejectionNoManejada)

    const datos = new Map<string, unknown>()
    let falloLaProximaEscrituraDeOutbox = false
    const almacen: AlmacenClaveValor = {
      async leer<T>(clave: string) {
        return (datos.has(clave) ? (datos.get(clave) as T) : null) ?? null
      },
      async escribir<T>(clave: string, valor: T) {
        if (clave === 'outbox' && falloLaProximaEscrituraDeOutbox) {
          falloLaProximaEscrituraDeOutbox = false
          return false
        }
        datos.set(clave, valor)
        return true
      },
    }
    const [a] = await outboxConDos(almacen)
    emitirMock.mockImplementation((solicitud: SolicitudDeVenta) =>
      solicitud.numeroPreasignado === a.numeroPreasignado
        ? Promise.reject(new ErrorApi(409, 'numero_preasignado_con_otro_contenido', 'Contenido distinto'))
        : Promise.resolve({ id: 1 }),
    )
    obtenerInstantaneaMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))
    // Arma el fallo justo antes del primer drenado: la PRIMERA escritura de 'outbox' que ve el
    // drenado es la salida de 'a' que sigue a archivarla.
    falloLaProximaEscrituraDeOutbox = true

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 20 }))

    // Primer ciclo: se archiva (la escritura de 'ventasRechazadas' no está afectada por el flag),
    // pero la salida del outbox falla una vez — el estado de React todavía no se actualiza. Leído
    // directo del almacén (no de `outboxCount`/`ventasConError`, que arrancan en su valor inicial
    // por defecto y podrían dar un falso positivo trivial antes de que el ciclo corra).
    await waitFor(async () => expect(await leerRechazadas(almacen)).toHaveLength(1))

    // El próximo ciclo reintenta 'a' (sigue en el outbox), re-archiva (idempotente, sin duplicar)
    // y esta vez la salida del outbox confirma — converge a un solo store (releído del almacén).
    await waitFor(async () => expect(await leerOutbox(almacen)).toEqual([]), { timeout: 3000 })
    await expect(leerRechazadas(almacen)).resolves.toHaveLength(1)
    await waitFor(() => expect(result.current.ventasConError).toHaveLength(1))

    procesoDeNode?.off('unhandledRejection', alRejectionNoManejada)
    expect(rejeccionesNoManejadas).toEqual([])
  })
})

describe('useSincronizacionOffline — resolver una venta que necesita atención (judgment-day ronda 2, WARNING)', () => {
  it('reintentarVentaConError la saca de rechazadas y la vuelve a encolar al final del outbox', async () => {
    const almacen = almacenFake()
    await agregarARechazada(almacen, {
      idLocal: 'a',
      numeroPreasignado: 100,
      idPuntoVenta: 7,
      creadoEn: '2026-09-20T09:00:00.000Z',
      solicitud: solicitudFixture({ numeroPreasignado: 100 }),
      mensaje: 'La venta 0007-00000100 no se pudo sincronizar: rechazo del servidor.',
    })
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.ventasConError).toHaveLength(1))

    let exito: boolean | undefined
    await act(async () => {
      exito = await result.current.reintentarVentaConError('a')
    })

    expect(exito).toBe(true)
    expect(result.current.ventasConError).toEqual([])
    await waitFor(() => expect(result.current.outboxCount).toBe(1))
    await expect(leerOutbox(almacen)).resolves.toHaveLength(1)
    await expect(leerRechazadas(almacen)).resolves.toEqual([])
  })

  it('reintentarVentaConError es un no-op idempotente (resuelve true) si la venta ya no está en rechazadas', async () => {
    const almacen = almacenFake()
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await act(async () => {
      await Promise.resolve()
    })

    let exito: boolean | undefined
    await act(async () => {
      exito = await result.current.reintentarVentaConError('no-existe')
    })
    expect(exito).toBe(true)
    expect(result.current.outboxCount).toBe(0)
  })

  it('descartarVentaConError la quita de rechazadas de forma permanente, sin tocar el outbox', async () => {
    const almacen = almacenFake()
    await agregarARechazada(almacen, {
      idLocal: 'a',
      numeroPreasignado: 100,
      idPuntoVenta: 7,
      creadoEn: '2026-09-20T09:00:00.000Z',
      solicitud: solicitudFixture({ numeroPreasignado: 100 }),
      mensaje: 'La venta 0007-00000100 no se pudo sincronizar: rechazo del servidor.',
    })
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.ventasConError).toHaveLength(1))

    let exito: boolean | undefined
    await act(async () => {
      exito = await result.current.descartarVentaConError('a')
    })

    expect(exito).toBe(true)
    expect(result.current.ventasConError).toEqual([])
    await expect(leerRechazadas(almacen)).resolves.toEqual([])
    await expect(leerOutbox(almacen)).resolves.toEqual([])
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

  // judgment-day ronda 1 (SUGGESTION): prueba que `todasLasLineasTienenPrecioOffline` está de
  // verdad ENCHUFADA como el gate (antes, `encolarVentaOffline` reimplementaba la misma
  // precondición inline sin llamar a esa función — ningún test de este archivo ejercitaba
  // `linea_sin_precio` a este nivel).
  it('rechaza con una línea cuyo artículo no está en la instantánea (linea_sin_precio)', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, instantaneaFixture({ articulos: [articuloFixture({ idArticulo: 1 })] }))
    await guardarBloque(almacen, bloqueFixture())
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    const resultado = await result.current.encolarVentaOffline({
      solicitudBase: solicitudFixture({ lineas: [{ idArticulo: 2, cantidad: 1, codigoBarra: null, idLote: null }] }),
      esConsumidorFinal: true,
      pagos: [{ comportamiento: 'Efectivo' }],
    })
    expect(resultado).toEqual({ ok: false, motivo: 'linea_sin_precio' })
  })

  // judgment-day ronda 1 (WARNING): la instantánea CONGELADA que `Pos.tsx` pasa (la que ya
  // resolvió la vista previa en pantalla) manda sobre el estado interno del hook, aunque ese
  // estado ya se haya refrescado en segundo plano — "el precio que se mostró es el que se cobra".
  it('con instantaneaCongelada, usa ESA instantánea para el precio — nunca la más fresca del hook', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, instantaneaFixture({ articulos: [articuloFixture({ precioOriginal: 200, precioFinal: 200, descuentoUnitario: 0 })] }))
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea?.articulos[0].precioOriginal).toBe(200))

    // La instantánea "congelada" (la vieja, la que el cajero vio en pantalla) trae un precio
    // DISTINTO al que el hook tiene ahora — simula un refresco en segundo plano entre la vista
    // previa y el click de "Cobrar".
    const instantaneaVieja = instantaneaFixture({ articulos: [articuloFixture({ precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0 })] })

    let resultado
    await act(async () => {
      resultado = await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture(),
        esConsumidorFinal: true,
        pagos: [{ comportamiento: 'Efectivo' }],
        instantaneaCongelada: instantaneaVieja,
      })
    })

    expect(resultado).toMatchObject({ ok: true })
    const outbox = await leerOutbox(almacen)
    // 100 (la vieja, congelada) — NUNCA 200 (la fresca que el hook tiene ahora en su propio estado).
    expect(outbox[0].solicitud.lineas?.[0]).toMatchObject({ precioUnitario: 100 })
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
    // precioOriginal (bruto), NUNCA precioFinal (ya neto) — el backend resta descuentoUnitario de
    // nuevo, así que mandar el neto lo restaría dos veces (ver el comentario de
    // `enriquecerLineasConPrecioOffline`).
    expect(outbox[0].solicitud.lineas?.[0]).toMatchObject({ precioUnitario: 120, descuentoUnitario: 20 })

    await waitFor(() => expect(result.current.outboxCount).toBe(1))
  })

  // judgment-day ronda 2 (SUGGESTION): la cobertura previa de `encolarVentaOffline` con precio
  // offline solo ejercitaba un carrito de UNA línea — acá con cantidad > 1, `cantidad` viaja tal
  // cual (la multiplicación cantidad×descuento es responsabilidad del servidor,
  // `CalculadorDeTotales.Calcular`, nunca de este hook) junto con el precio/descuento bruto de la
  // instantánea, exactamente igual que con cantidad 1.
  it('con cantidad > 1, encola la línea con esa misma cantidad y el precio/descuento bruto de la instantánea (la multiplicación es del servidor)', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, instantaneaFixture({ articulos: [articuloFixture({ precioOriginal: 50, precioFinal: 40, descuentoUnitario: 10 })] }))
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    let resultado
    await act(async () => {
      resultado = await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture({ lineas: [{ idArticulo: 1, cantidad: 3, codigoBarra: '7790001234567', idLote: null }] }),
        esConsumidorFinal: true,
        pagos: [{ comportamiento: 'Efectivo' }],
      })
    })

    expect(resultado).toMatchObject({ ok: true })
    const outbox = await leerOutbox(almacen)
    expect(outbox[0].solicitud.lineas?.[0]).toMatchObject({ cantidad: 3, precioUnitario: 50, descuentoUnitario: 10 })
  })

  // judgment-day ronda 2 (SUGGESTION): la cobertura previa nunca ejercitó un carrito de DOS
  // líneas con descuentos MIXTOS a este nivel (`enriquecerLineasConPrecioOffline`) — cada línea
  // tiene que traer el precio/descuento de SU PROPIO artículo, nunca el de la otra.
  it('con un carrito de dos líneas y descuentos mixtos, cada línea encola el precio/descuento de SU PROPIO artículo', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(
      almacen,
      instantaneaFixture({
        articulos: [
          articuloFixture({ idArticulo: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0 }),
          articuloFixture({ idArticulo: 2, codigosBarra: ['7790009999999'], precioOriginal: 80, precioFinal: 70, descuentoUnitario: 10 }),
        ],
      }),
    )
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    let resultado
    await act(async () => {
      resultado = await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture({
          lineas: [
            { idArticulo: 1, cantidad: 1, codigoBarra: '7790001234567', idLote: null },
            { idArticulo: 2, cantidad: 2, codigoBarra: '7790009999999', idLote: null },
          ],
        }),
        esConsumidorFinal: true,
        pagos: [{ comportamiento: 'Efectivo' }],
      })
    })

    expect(resultado).toMatchObject({ ok: true })
    const outbox = await leerOutbox(almacen)
    const lineas = outbox[0].solicitud.lineas ?? []
    expect(lineas).toHaveLength(2)
    expect(lineas.find((l) => l.idArticulo === 1)).toMatchObject({ cantidad: 1, precioUnitario: 100, descuentoUnitario: 0 })
    expect(lineas.find((l) => l.idArticulo === 2)).toMatchObject({ cantidad: 2, precioUnitario: 80, descuentoUnitario: 10 })
  })

  // El payload encolado es lo que el servidor cobra LITERAL (`precioUnitario`/`descuentoUnitario`
  // salen de la línea tal cual, el servidor solo registra la discrepancia como auditoría), así que
  // tiene que caer en el MISMO tramo que la vista previa. Cantidad 6 cruza los dos umbrales
  // (3 y 6): el descuento del tramo es 20, el plano 0 y el del primer tramo 10 — los tres
  // distintos, así que elegir mal cualquiera de ellos rompe la aserción.
  it('con una cantidad que cruza un umbral, encola el descuentoUnitario del TRAMO y sigue mandando el precioOriginal bruto como precioUnitario', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, instantaneaFixture({ articulos: [articuloConEscalonesFixture()] }))
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    await act(async () => {
      await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture({ lineas: [{ idArticulo: 1, cantidad: 6, codigoBarra: '7790001234567', idLote: null }] }),
        esConsumidorFinal: true,
        pagos: [{ comportamiento: 'Efectivo' }],
      })
    })

    const outbox = await leerOutbox(almacen)
    expect(outbox[0].solicitud.lineas?.[0]).toMatchObject({ cantidad: 6, precioUnitario: 100, descuentoUnitario: 20 })
  })

  // Misma cláusula por el otro lado: por debajo del primer umbral el payload lleva el descuento
  // PLANO. Un mutante que aplique siempre el primer (o el último) tramo pasa el test de arriba y
  // muere acá.
  it('con la cantidad por debajo del primer umbral, encola el descuento plano — nunca el del primer tramo', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, instantaneaFixture({ articulos: [articuloConEscalonesFixture()] }))
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    await act(async () => {
      await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture({ lineas: [{ idArticulo: 1, cantidad: 2, codigoBarra: '7790001234567', idLote: null }] }),
        esConsumidorFinal: true,
        pagos: [{ comportamiento: 'Efectivo' }],
      })
    })

    const outbox = await leerOutbox(almacen)
    expect(outbox[0].solicitud.lineas?.[0]).toMatchObject({ cantidad: 2, precioUnitario: 100, descuentoUnitario: 0 })
  })

  // El importe que el cajero VE (vista previa, `resolverPreciosOffline` + `previaDeLinea` reales)
  // y el que el servidor va a COBRAR (payload encolado, `cantidad × (precioUnitario −
  // descuentoUnitario)`, fórmula de `CalculadorDeTotales.Calcular`) tienen que coincidir a la misma
  // cantidad. Si los dos caminos eligieran tramos distintos, se cobraría algo que nunca se mostró.
  it.each([
    ['por debajo del primer umbral', 2],
    ['exactamente en el primer umbral', 3],
    ['entre los dos umbrales', 4],
    ['en el segundo umbral', 6],
  ])('la vista previa y el payload encolado cobran el mismo importe con cantidad %s', async (_titulo, cantidad) => {
    const almacen = almacenFake()
    const instantanea = instantaneaFixture({ articulos: [articuloConEscalonesFixture()] })
    await guardarInstantaneaLocal(almacen, instantanea)
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    await act(async () => {
      await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture({ lineas: [{ idArticulo: 1, cantidad, codigoBarra: '7790001234567', idLote: null }] }),
        esConsumidorFinal: true,
        pagos: [{ comportamiento: 'Efectivo' }],
      })
    })

    const lineaCarrito = { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', cantidad }
    const previa = previaDeLinea(lineaCarrito, resolverPreciosOffline([lineaCarrito], instantanea, 5)[1])

    const encolada = (await leerOutbox(almacen))[0].solicitud.lineas?.[0]
    const cobrado = cantidad * ((encolada?.precioUnitario ?? 0) - (encolada?.descuentoUnitario ?? 0))
    expect(cobrado).toBe(previa.total)
  })

  // judgment-day ronda 1 (BLOCKER): antes de este fix, `agregarAOutbox` tragaba CUALQUIER falla
  // de escritura y `encolarVentaOffline` devolvía `ok: true` igual — la venta se mostraba
  // "Guardada sin conexión", el ticket se imprimía, y no había nada guardado.
  it('si el almacén no puede persistir la venta de forma durable, NUNCA devuelve ok — nada que imprimir ni entregar', async () => {
    const almacen = almacenFakeConOutboxRoto()
    await guardarInstantaneaLocal(almacen, instantaneaFixture())
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

    expect(resultado).toEqual({ ok: false, motivo: 'error_al_guardar' })
    // Nunca subió el contador visible — nada que el cajero deba creer que quedó encolado.
    expect(result.current.outboxCount).toBe(0)
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
