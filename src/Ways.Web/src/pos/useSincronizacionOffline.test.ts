import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  INTERVALO_DE_RENDICION_MS,
  LIMITE_DE_SINCRONIZACION_INICIAL_CON_COPIA_LOCAL_MS,
  LIMITE_DE_SINCRONIZACION_INICIAL_MS,
  TIEMPO_LIMITE_DE_VERIFICACION_DE_CREDITO_MS,
  useSincronizacionOffline,
} from './useSincronizacionOffline'
import {
  agregarAOutbox,
  agregarARechazada,
  guardarBloque,
  leerBloque,
  leerOutbox,
  leerRechazadas,
  type BloqueDeNumeracionLocal,
  type VentaEnCola,
} from './outboxOffline'
import { TIEMPO_LIMITE_DE_RED_MS } from './tiempoLimite'
import { guardarInstantaneaLocal, leerInstantaneaLocal, purgarInstantaneaLocal, resolverPreciosOffline } from './instantaneaOffline'
import type { AlmacenClaveValor } from './almacenPos'
import { ErrorApi, ErrorDeRed } from '../api/cliente'
import { previaDeLinea } from '../api/ventas'
import type { ArticuloDeInstantanea, ClienteDeInstantanea, EscalonDeCantidad, InstantaneaDePos, PrecioDeListaDeInstantanea, SolicitudDeVenta } from '../api/tipos'

const emitirMock = vi.fn()
const reservarNumeracionMock = vi.fn()
const obtenerInstantaneaMock = vi.fn()
const rendirColaMock = vi.fn()
const obtenerClienteMock = vi.fn()

vi.mock('../api/clientes', () => ({
  clienteDeClientes: { obtener: (...args: unknown[]) => obtenerClienteMock(...args) },
}))

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

/** `obtenerInstantaneaMock` puede devolver directamente una instantánea (se adapta acá a un `200`
 * con etiqueta `"etag-<momento>"`) o una `RespuestaCondicional` ya armada, como el `304` de
 * `SIN_CAMBIOS`. Un rechazo pasa tal cual. */
vi.mock('../api/pos', () => ({
  clienteDePos: {
    obtenerInstantanea: (...args: unknown[]) =>
      Promise.resolve(obtenerInstantaneaMock(...args)).then((r: unknown) =>
        typeof r === 'object' && r !== null && 'modificada' in r
          ? r
          : { modificada: true, cuerpo: r, etag: `"etag-${(r as InstantaneaDePos).momento}"` },
      ),
    rendirCola: (...args: unknown[]) => rendirColaMock(...args),
  },
}))

const SIN_CAMBIOS = { modificada: false } as const

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

/** Lista del Consumidor Final en estos tests: la venta local se cobra con el precio de esa lista. */
const LISTA_CF = 1

/** Artículo con UN precio, en `LISTA_CF` salvo que se pida otra: los campos de precio se pasan
 * planos para que cada caso se lea igual que el payload que produce. */
type ArticuloConPrecio = Omit<ArticuloDeInstantanea, 'preciosPorLista'> & Omit<PrecioDeListaDeInstantanea, 'idListaPrecio'> & { idListaPrecio: number }

function articuloFixture(sobrescribir: Partial<ArticuloConPrecio> = {}): ArticuloDeInstantanea {
  const { precioOriginal = 100, precioFinal = 100, descuentoUnitario = 0, aplicadas = [], escalones, idListaPrecio = LISTA_CF, ...resto } = sobrescribir
  return {
    idArticulo: 1,
    codigoInterno: 'A0001',
    nombre: 'Coca Cola 1L',
    codigosBarra: ['7790001234567'],
    idAlicuotaIva: 1,
    porcentajeIva: 21,
    ...resto,
    preciosPorLista: [{ idListaPrecio, precioOriginal, precioFinal, descuentoUnitario, aplicadas, escalones }],
  }
}

/** Persiste una instantánea como lo hace el hook, verificada en su propio `momento`. */
function guardarLocal(almacen: AlmacenClaveValor, instantanea: InstantaneaDePos, etag: string | null = null) {
  return guardarInstantaneaLocal(almacen, { instantanea, etag, verificadaEn: instantanea.momento })
}

/** Tramos con valores todos distintos entre sí y del precio plano — ver el mismo criterio en
 * `instantaneaOffline.test.ts`. */
const ESCALON_3: EscalonDeCantidad = { cantidadDesde: 3, precioFinal: 90, descuentoUnitario: 10, aplicadas: [{ idOferta: 31, nombre: '3 o más', descuentoUnitario: 10 }] }
const ESCALON_6: EscalonDeCantidad = { cantidadDesde: 6, precioFinal: 80, descuentoUnitario: 20, aplicadas: [{ idOferta: 61, nombre: '6 o más', descuentoUnitario: 20 }] }

function articuloConEscalonesFixture(sobrescribir: Partial<ArticuloConPrecio> = {}): ArticuloDeInstantanea {
  return articuloFixture({ precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [], escalones: [ESCALON_3, ESCALON_6], ...sobrescribir })
}

function instantaneaFixture(sobrescribir: Partial<InstantaneaDePos> = {}): InstantaneaDePos {
  return {
    momento: '2026-09-20T10:00:00.000Z',
    idPuntoVenta: 7,
    articulos: [articuloFixture()],
    clientes: [],
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
  rendirColaMock.mockReset()
  obtenerClienteMock.mockReset()
  // default: la rendición llega bien (204) — cada test que necesite el fallo la sobrescribe.
  rendirColaMock.mockResolvedValue(undefined)
  // default: sin servidor (ErrorDeRed) — cada test que necesite señal la sobrescribe.
  obtenerInstantaneaMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))
  reservarNumeracionMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))
  emitirMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))
})

describe('useSincronizacionOffline — carga inicial (goal A: sobrevive un restart)', () => {
  it('hidrata instantánea/outbox ya persistidos, sin esperar ningún fetch', async () => {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture())
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
    await waitFor(async () => expect((await leerInstantaneaLocal(almacen))?.instantanea).toEqual(fresca))
    expect((await leerInstantaneaLocal(almacen))?.etag).toBe('"etag-2026-09-20T11:00:00.000Z"')
    expect(result.current.enLinea).toBe(true)
  })

  it('pide el refresco con la etiqueta de la copia local y, con un 304, conserva el contenido y renueva solo la verificación', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      vi.setSystemTime(new Date('2026-09-20T15:00:00.000Z'))
      const almacen = almacenFake()
      const local = instantaneaFixture({ momento: '2026-09-20T10:00:00.000Z' })
      await guardarLocal(almacen, local, '"etiqueta-local"')
      obtenerInstantaneaMock.mockResolvedValue(SIN_CAMBIOS)

      const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

      await waitFor(() => expect(result.current.verificadaEn).toBe('2026-09-20T15:00:00.000Z'))
      expect(obtenerInstantaneaMock).toHaveBeenCalledWith('"etiqueta-local"')
      expect(result.current.instantanea).toEqual(local)
      expect(result.current.enLinea).toBe(true)
      // Persistida con la hora nueva: un reinicio muestra la vejez de la verificación, no la del contenido.
      await waitFor(async () => expect((await leerInstantaneaLocal(almacen))?.verificadaEn).toBe('2026-09-20T15:00:00.000Z'))
      expect((await leerInstantaneaLocal(almacen))?.instantanea).toEqual(local)
      expect((await leerInstantaneaLocal(almacen))?.etag).toBe('"etiqueta-local"')
    } finally {
      vi.useRealTimers()
    }
  })

  it('sin copia local pide la instantánea sin etiqueta', async () => {
    const almacen = almacenFake()
    obtenerInstantaneaMock.mockResolvedValue(instantaneaFixture())

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(result.current.instantanea).not.toBeNull())
    expect(obtenerInstantaneaMock).toHaveBeenCalledWith(null)
  })

  it('sin red la vejez sigue siendo la de la última verificación persistida', async () => {
    const almacen = almacenFake()
    await guardarInstantaneaLocal(almacen, { instantanea: instantaneaFixture({ momento: '2026-09-20T10:00:00.000Z' }), etag: null, verificadaEn: '2026-09-20T14:30:00.000Z' })

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(result.current.verificadaEn).toBe('2026-09-20T14:30:00.000Z'))
    await waitFor(() => expect(result.current.enLinea).toBe(false))
    expect(result.current.verificadaEn).toBe('2026-09-20T14:30:00.000Z')
  })

  // Un servidor anterior a los precios por lista ignora `?version=2` y responde la forma vieja.
  it('una respuesta con la forma anterior no reemplaza la copia local ni se persiste', async () => {
    const almacen = almacenFake()
    const local = instantaneaFixture()
    await guardarLocal(almacen, local)
    const formaVieja = { momento: '2026-09-20T12:00:00.000Z', idPuntoVenta: 7, articulos: [{ idArticulo: 1, precioOriginal: 1 }], mediosDePago: [], toleranciaPago: 0 }
    obtenerInstantaneaMock.mockResolvedValue({ modificada: true, cuerpo: formaVieja, etag: null })

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(result.current.sincronizacionInicialPendiente).toBe(false))
    expect(result.current.instantanea).toEqual(local)
    expect((await leerInstantaneaLocal(almacen))?.instantanea).toEqual(local)
    expect(result.current.enLinea).toBe(true)
  })

  it('sin señal (ErrorDeRed), conserva la instantánea local previa en vez de borrarla', async () => {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture())
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

    await waitFor(() => expect(reservarNumeracionMock).toHaveBeenCalledWith({ idPuntoVenta: 7, codigoTipoComprobante: 'TX', cantidad: 500 }, expect.any(AbortSignal)))
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
      esConsumidorFinal: true, idListaPrecio: LISTA_CF,
      pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
    })
    expect(resultado).toEqual({ ok: false, motivo: 'sin_instantanea' })
  })

  // judgment-day ronda 1 (SUGGESTION): prueba que `todasLasLineasTienenPrecioOffline` está de
  // verdad ENCHUFADA como el gate (antes, `encolarVentaOffline` reimplementaba la misma
  // precondición inline sin llamar a esa función — ningún test de este archivo ejercitaba
  // `linea_sin_precio` a este nivel).
  it('rechaza con una línea cuyo artículo no está en la instantánea (linea_sin_precio)', async () => {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture({ articulos: [articuloFixture({ idArticulo: 1 })] }))
    await guardarBloque(almacen, bloqueFixture())
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    const resultado = await result.current.encolarVentaOffline({
      solicitudBase: solicitudFixture({ lineas: [{ idArticulo: 2, cantidad: 1, codigoBarra: null, idLote: null }] }),
      esConsumidorFinal: true, idListaPrecio: LISTA_CF,
      pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
    })
    expect(resultado).toEqual({ ok: false, motivo: 'linea_sin_precio' })
  })

  // judgment-day ronda 1 (WARNING): la instantánea CONGELADA que `Pos.tsx` pasa (la que ya
  // resolvió la vista previa en pantalla) manda sobre el estado interno del hook, aunque ese
  // estado ya se haya refrescado en segundo plano — "el precio que se mostró es el que se cobra".
  it('con instantaneaCongelada, usa ESA instantánea para el precio — nunca la más fresca del hook', async () => {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture({ articulos: [articuloFixture({ precioOriginal: 200, precioFinal: 200, descuentoUnitario: 0 })] }))
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea?.articulos[0].preciosPorLista[0].precioOriginal).toBe(200))

    // La instantánea "congelada" (la vieja, la que el cajero vio en pantalla) trae un precio
    // DISTINTO al que el hook tiene ahora — simula un refresco en segundo plano entre la vista
    // previa y el click de "Cobrar".
    const instantaneaVieja = instantaneaFixture({ articulos: [articuloFixture({ precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0 })] })

    let resultado
    await act(async () => {
      resultado = await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture(),
        esConsumidorFinal: true, idListaPrecio: LISTA_CF,
        pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
        instantaneaCongelada: instantaneaVieja,
      })
    })

    expect(resultado).toMatchObject({ ok: true })
    const outbox = await leerOutbox(almacen)
    // 100 (la vieja, congelada) — NUNCA 200 (la fresca que el hook tiene ahora en su propio estado).
    expect(outbox[0].solicitud.lineas?.[0]).toMatchObject({ precioUnitario: 100 })
  })

  it('rechaza un cliente identificado que no está en la instantánea, aunque haya números disponibles', async () => {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture())
    await guardarBloque(almacen, bloqueFixture())
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    const resultado = await result.current.encolarVentaOffline({
      solicitudBase: solicitudFixture(),
      esConsumidorFinal: false, idListaPrecio: LISTA_CF,
      pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
    })
    expect(resultado).toEqual({ ok: false, motivo: 'cliente_no_admitido' })
  })

  it('rechaza la cuenta corriente del Consumidor Final, aunque haya instantánea y números disponibles', async () => {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture())
    await guardarBloque(almacen, bloqueFixture())
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    const resultado = await result.current.encolarVentaOffline({
      solicitudBase: solicitudFixture(),
      esConsumidorFinal: true, idListaPrecio: LISTA_CF,
      pagos: [{ comportamiento: 'CuentaCorriente', importe: 100 }],
    })
    expect(resultado).toEqual({ ok: false, motivo: 'medio_no_admitido' })
    expect(obtenerClienteMock).not.toHaveBeenCalled()
    // Rechazada: no debe haber tocado el bloque ni el outbox.
    await expect(leerBloque(almacen)).resolves.toEqual(bloqueFixture())
    await expect(leerOutbox(almacen)).resolves.toEqual([])
  })

  it('rechaza sin números disponibles', async () => {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture())
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    const resultado = await result.current.encolarVentaOffline({
      solicitudBase: solicitudFixture(),
      esConsumidorFinal: true, idListaPrecio: LISTA_CF,
      pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
    })
    expect(resultado).toEqual({ ok: false, motivo: 'sin_numeracion' })
  })

  it('éxito: toma el próximo número, persiste el bloque decrementado y encola la venta con precio/número', async () => {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture({ articulos: [articuloFixture({ precioOriginal: 120, precioFinal: 100, descuentoUnitario: 20 })] }))
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    let resultado
    await act(async () => {
      resultado = await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture(),
        esConsumidorFinal: true, idListaPrecio: LISTA_CF,
        pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
      })
    })

    expect(resultado).toEqual({ ok: true, numero: 150, numeroVisible: '0007-00000150', limiteDeCreditoNoValidado: false })
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

  it('encola con el precio de la lista pedida, y rechaza si el artículo no tiene precio en esa lista', async () => {
    const almacen = almacenFake()
    const articulo: ArticuloDeInstantanea = {
      ...articuloFixture(),
      preciosPorLista: [
        { idListaPrecio: LISTA_CF, precioOriginal: 120, precioFinal: 100, descuentoUnitario: 20, aplicadas: [] },
        { idListaPrecio: 8, precioOriginal: 90, precioFinal: 81, descuentoUnitario: 9, aplicadas: [] },
      ],
    }
    await guardarLocal(almacen, instantaneaFixture({ articulos: [articulo] }))
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    let enOtraLista
    let sinPrecio
    await act(async () => {
      enOtraLista = await result.current.encolarVentaOffline({ solicitudBase: solicitudFixture(), esConsumidorFinal: true, idListaPrecio: 8, pagos: [{ comportamiento: 'Efectivo', importe: 100 }] })
      sinPrecio = await result.current.encolarVentaOffline({ solicitudBase: solicitudFixture(), esConsumidorFinal: true, idListaPrecio: 99, pagos: [{ comportamiento: 'Efectivo', importe: 100 }] })
    })

    expect(enOtraLista).toMatchObject({ ok: true })
    expect((await leerOutbox(almacen))[0].solicitud.lineas?.[0]).toMatchObject({ precioUnitario: 90, descuentoUnitario: 9 })
    expect(sinPrecio).toEqual({ ok: false, motivo: 'linea_sin_precio' })
    expect(await leerOutbox(almacen)).toHaveLength(1)
  })

  // judgment-day ronda 2 (SUGGESTION): la cobertura previa de `encolarVentaOffline` con precio
  // offline solo ejercitaba un carrito de UNA línea — acá con cantidad > 1, `cantidad` viaja tal
  // cual (la multiplicación cantidad×descuento es responsabilidad del servidor,
  // `CalculadorDeTotales.Calcular`, nunca de este hook) junto con el precio/descuento bruto de la
  // instantánea, exactamente igual que con cantidad 1.
  it('con cantidad > 1, encola la línea con esa misma cantidad y el precio/descuento bruto de la instantánea (la multiplicación es del servidor)', async () => {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture({ articulos: [articuloFixture({ precioOriginal: 50, precioFinal: 40, descuentoUnitario: 10 })] }))
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    let resultado
    await act(async () => {
      resultado = await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture({ lineas: [{ idArticulo: 1, cantidad: 3, codigoBarra: '7790001234567', idLote: null }] }),
        esConsumidorFinal: true, idListaPrecio: LISTA_CF,
        pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
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
    await guardarLocal(
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
        esConsumidorFinal: true, idListaPrecio: LISTA_CF,
        pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
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
    await guardarLocal(almacen, instantaneaFixture({ articulos: [articuloConEscalonesFixture()] }))
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    await act(async () => {
      await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture({ lineas: [{ idArticulo: 1, cantidad: 6, codigoBarra: '7790001234567', idLote: null }] }),
        esConsumidorFinal: true, idListaPrecio: LISTA_CF,
        pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
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
    await guardarLocal(almacen, instantaneaFixture({ articulos: [articuloConEscalonesFixture()] }))
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    await act(async () => {
      await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture({ lineas: [{ idArticulo: 1, cantidad: 2, codigoBarra: '7790001234567', idLote: null }] }),
        esConsumidorFinal: true, idListaPrecio: LISTA_CF,
        pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
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
    await guardarLocal(almacen, instantanea)
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    await act(async () => {
      await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture({ lineas: [{ idArticulo: 1, cantidad, codigoBarra: '7790001234567', idLote: null }] }),
        esConsumidorFinal: true, idListaPrecio: LISTA_CF,
        pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
      })
    })

    const lineaCarrito = { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', cantidad }
    const previa = previaDeLinea(lineaCarrito, resolverPreciosOffline([lineaCarrito], instantanea, LISTA_CF)[1])

    const encolada = (await leerOutbox(almacen))[0].solicitud.lineas?.[0]
    const cobrado = cantidad * ((encolada?.precioUnitario ?? 0) - (encolada?.descuentoUnitario ?? 0))
    expect(cobrado).toBe(previa.total)
  })

  // judgment-day ronda 1 (BLOCKER): antes de este fix, `agregarAOutbox` tragaba CUALQUIER falla
  // de escritura y `encolarVentaOffline` devolvía `ok: true` igual — la venta se mostraba
  // "Guardada sin conexión", el ticket se imprimía, y no había nada guardado.
  it('si el almacén no puede persistir la venta de forma durable, NUNCA devuelve ok — nada que imprimir ni entregar', async () => {
    const almacen = almacenFakeConOutboxRoto()
    await guardarLocal(almacen, instantaneaFixture())
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    let resultado
    await act(async () => {
      resultado = await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture(),
        esConsumidorFinal: true, idListaPrecio: LISTA_CF,
        pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
      })
    })

    expect(resultado).toEqual({ ok: false, motivo: 'error_al_guardar' })
    // Nunca subió el contador visible — nada que el cajero deba creer que quedó encolado.
    expect(result.current.outboxCount).toBe(0)
  })

  it('no admite dos ventas consecutivas con el MISMO número (avanza el puntero cada vez)', async () => {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture())
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    let primero
    let segundo
    await act(async () => {
      primero = await result.current.encolarVentaOffline({ solicitudBase: solicitudFixture(), esConsumidorFinal: true, idListaPrecio: LISTA_CF, pagos: [{ comportamiento: 'Efectivo', importe: 100 }] })
    })
    await act(async () => {
      segundo = await result.current.encolarVentaOffline({ solicitudBase: solicitudFixture(), esConsumidorFinal: true, idListaPrecio: LISTA_CF, pagos: [{ comportamiento: 'Efectivo', importe: 100 }] })
    })

    expect(primero).toMatchObject({ ok: true, numero: 150 })
    expect(segundo).toMatchObject({ ok: true, numero: 151 })
    await waitFor(() => expect(result.current.outboxCount).toBe(2))
  })
})

// Rendición de la cola local (`POST /api/pos/rendicion-de-cola`): el cierre de turno del servidor
// verifica este reporte contra `comprobantes_venta` en vez de creerle al IndexedDB de UNA máquina.
describe('useSincronizacionOffline — rendición de la cola local', () => {
  function ventaEnCola(idLocal: string, numeroPreasignado: number): VentaEnCola {
    return {
      idLocal,
      numeroPreasignado,
      idPuntoVenta: 7,
      creadoEn: '2026-09-20T09:00:00.000Z',
      solicitud: solicitudFixture({ numeroPreasignado }),
    }
  }

  /** Espera a que el ciclo haya llegado AL MENOS hasta el refresco de instantánea (paso 2 de los 4
   * de `ciclo`) y después drena las microtareas pendientes con un `setTimeout(0)`. NO observa
   * ninguna señal del paso de rendición (paso 4): un test que afirme que NO se rindió necesita
   * además su propio control positivo de que ese paso corre de verdad — ver el test de "sin bloque
   * local", que lo consigue con una segunda fase. */
  async function esperarHastaElRefrescoDeInstantanea() {
    await waitFor(() => expect(obtenerInstantaneaMock).toHaveBeenCalled())
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0))
    })
  }

  /**
   * Cláusula bajo prueba: `entregadoHasta = bloque.proximo - 1` (el puntero local apunta al
   * PRÓXIMO número a repartir, así que el más alto ya entregado es el anterior) y
   * `pendientes = outbox + rechazadas` (una venta rechazada también tiene su ticket en la mano de
   * un cliente). Los tres valores son distintos entre sí a propósito: 2 en el outbox, 1 rechazada,
   * 3 en total — ninguna suma parcial coincide con el total por casualidad.
   */
  it('rinde entregadoHasta = proximo - 1 y pendientes = outbox + rechazadas, con el tipo de comprobante del bloque', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, bloqueFixture({ desde: 100, hasta: 200, proximo: 105 }))
    await agregarAOutbox(almacen, ventaEnCola('a', 100))
    await agregarAOutbox(almacen, ventaEnCola('b', 101))
    await agregarARechazada(almacen, { ...ventaEnCola('c', 102), mensaje: 'La venta 102 no se pudo sincronizar.' })
    // Sin señal para el checkout: el outbox no drena, así que los pendientes siguen siendo 3.
    emitirMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))

    renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() =>
      expect(rendirColaMock).toHaveBeenCalledWith({ codigoTipoComprobante: 'TX', entregadoHasta: 104, pendientes: 3 }, expect.any(AbortSignal)),
    )
  })

  it('rinde DESPUÉS del drenado: lo que declara refleja el outbox ya vacío, nunca el de antes del ciclo', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, bloqueFixture({ desde: 100, hasta: 200, proximo: 103 }))
    await agregarAOutbox(almacen, ventaEnCola('a', 100))
    await agregarAOutbox(almacen, ventaEnCola('b', 101))
    emitirMock.mockResolvedValue({ id: 1 })

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(result.current.outboxCount).toBe(0))
    await waitFor(() => expect(rendirColaMock).toHaveBeenCalled())
    // Ninguna rendición de este ciclo declaró las dos ventas que el drenado ya había entregado.
    expect(rendirColaMock.mock.calls.map((c) => c[0])).toEqual([
      { codigoTipoComprobante: 'TX', entregadoHasta: 102, pendientes: 0 },
    ])
  })

  /**
   * Cláusula bajo prueba: `if (bloque === null) return null` de `rendirColaLocal` — un dispositivo
   * que perdió su bloque no puede dar fe de su cola, así que no rinde nada (el servidor bloquea el
   * cierre por sí mismo ante un reporte ausente).
   *
   * La afirmación negativa necesita un control POSITIVO en el mismo test: el paso de rendición no
   * emite ninguna señal observable cuando saltea, así que esperar "un ciclo" dejaría el assert verde
   * incluso con el `await rendirColaLocal()` borrado entero de `ciclo`. La segunda fase es ese
   * control: apenas hay señal, el ciclo repone el bloque (`reponerBloque`) y ese mismo
   * paso SÍ rinde — recién ahí queda probado que el paso corre, y por lo tanto que en la primera
   * fase corrió y eligió no rendir.
   */
  it('sin bloque local no rinde nada: no hay cola de la que dar fe', async () => {
    const almacen = almacenFake()
    await agregarAOutbox(almacen, ventaEnCola('a', 100))
    emitirMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))
    // Sin señal (default del `beforeEach`): `reponerBloque` ni se intenta, así que el
    // bloque sigue ausente durante toda la primera fase.

    renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await esperarHastaElRefrescoDeInstantanea()
    expect(rendirColaMock).not.toHaveBeenCalled()

    // Control positivo: con señal, el ciclo repone el bloque y el paso de rendición del MISMO ciclo
    // declara la cola. `entregadoHasta` (99) y `pendientes` (1) son valores distintos entre sí y
    // distintos de `desde` (100), así que ninguno puede coincidir por casualidad.
    obtenerInstantaneaMock.mockResolvedValue(instantaneaFixture())
    reservarNumeracionMock.mockResolvedValue({ desde: 100, hasta: 199, idPuntoVenta: 7, codigoTipoComprobante: 'TX' })
    await act(async () => {
      window.dispatchEvent(new Event('online'))
      await new Promise((resolve) => setTimeout(resolve, 0))
    })

    await waitFor(() =>
      expect(rendirColaMock).toHaveBeenCalledWith({ codigoTipoComprobante: 'TX', entregadoHasta: 99, pendientes: 1 }, expect.any(AbortSignal)),
    )
  })

  it('una rendición que falla no rompe el drenado del mismo ciclo ni el ciclo siguiente, y no deja una rejection sin manejar', async () => {
    // Sin el `try/catch` alrededor de `clienteDePos.rendirCola`, el rechazo escapa como una promise
    // rejection nunca manejada (el llamador de `ciclo()` es `void ciclo(...)`, fire-and-forget) —
    // los ciclos siguen corriendo igual por el timer, así que esperar la convergencia NO alcanza
    // para probar esta guarda; hace falta escuchar `unhandledRejection`, mismo criterio (y misma
    // forma local mínima de `process`) que el test del `try/catch` de `quitarDeOutbox`.
    const procesoDeNode = (globalThis as { process?: { on: (evento: string, listener: (razon: unknown) => void) => void; off: (evento: string, listener: (razon: unknown) => void) => void } }).process
    const rejeccionesNoManejadas: unknown[] = []
    const alRejectionNoManejada = (razon: unknown) => rejeccionesNoManejadas.push(razon)
    procesoDeNode?.on('unhandledRejection', alRejectionNoManejada)

    const almacen = almacenFake()
    await guardarBloque(almacen, bloqueFixture({ desde: 100, hasta: 200, proximo: 101 }))
    await agregarAOutbox(almacen, ventaEnCola('a', 100))
    emitirMock.mockResolvedValue({ id: 1 })
    rendirColaMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    // El drenado del MISMO ciclo llegó a completarse (la rendición corre después, y falla sola).
    await waitFor(() => expect(result.current.outboxCount).toBe(0))
    await expect(leerOutbox(almacen)).resolves.toEqual([])
    await waitFor(() => expect(rendirColaMock).toHaveBeenCalledTimes(1))

    // Y el ciclo siguiente corre completo igual: drena la venta nueva y vuelve a rendir.
    await agregarAOutbox(almacen, ventaEnCola('b', 101))
    await act(async () => {
      window.dispatchEvent(new Event('online'))
      await new Promise((resolve) => setTimeout(resolve, 0))
    })

    await waitFor(() => expect(rendirColaMock).toHaveBeenCalledTimes(2))
    expect(emitirMock.mock.calls.map((c) => (c[0] as SolicitudDeVenta).numeroPreasignado)).toEqual([100, 101])
    await expect(leerOutbox(almacen)).resolves.toEqual([])

    procesoDeNode?.off('unhandledRejection', alRejectionNoManejada)
    expect(rejeccionesNoManejadas).toEqual([])
  })
})

/** Promesa controlable desde el test — el ciclo queda en vuelo hasta que se la resuelve. */
function promesaControlada<T>() {
  let resolver!: (valor: T) => void
  const promesa = new Promise<T>((r) => {
    resolver = r
  })
  return { promesa, resolver }
}

function ventaEnColaFixture(numero: number, idLocal = `v-${numero}`): VentaEnCola {
  return {
    idLocal,
    numeroPreasignado: numero,
    idPuntoVenta: 7,
    creadoEn: '2026-09-20T09:00:00.000Z',
    solicitud: solicitudFixture({ numeroPreasignado: numero }),
  }
}

describe('useSincronizacionOffline — ciclo de arranque y sincronización a pedido', () => {
  it('sincronizacionInicialPendiente queda en true hasta que termina el primer ciclo', async () => {
    const almacen = almacenFake()
    const instantaneaPendiente = promesaControlada<InstantaneaDePos>()
    obtenerInstantaneaMock.mockReturnValue(instantaneaPendiente.promesa)

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(obtenerInstantaneaMock).toHaveBeenCalledTimes(1))
    expect(result.current.sincronizacionInicialPendiente).toBe(true)
    expect(result.current.sincronizando).toBe(true)

    await act(async () => {
      instantaneaPendiente.resolver(instantaneaFixture())
    })

    await waitFor(() => expect(result.current.sincronizacionInicialPendiente).toBe(false))
    expect(result.current.sincronizando).toBe(false)
    expect(result.current.instantanea).toEqual(instantaneaFixture())
  })

  it('sin red, el ciclo de arranque termina enseguida y libera la venta con la instantánea local', async () => {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture())

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(result.current.sincronizacionInicialPendiente).toBe(false))
    expect(result.current.instantanea).toEqual(instantaneaFixture())
    expect(result.current.enLinea).toBe(false)
  })

  it('con una red que cuelga, libera la venta al vencer el tope aunque el ciclo siga en vuelo', async () => {
    vi.useFakeTimers()
    try {
      const almacen = almacenFake()
      obtenerInstantaneaMock.mockReturnValue(new Promise(() => {}))

      const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
      await act(async () => {
        await vi.advanceTimersByTimeAsync(LIMITE_DE_SINCRONIZACION_INICIAL_MS - 1)
      })
      expect(result.current.sincronizacionInicialPendiente).toBe(true)

      await act(async () => {
        await vi.advanceTimersByTimeAsync(1)
      })
      expect(result.current.sincronizacionInicialPendiente).toBe(false)
      expect(result.current.sincronizando).toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })

  // Cláusula: con una instantánea local de ESTE punto de venta, el tope baja a
  // LIMITE_DE_SINCRONIZACION_INICIAL_CON_COPIA_LOCAL_MS.
  it('con una red que cuelga y una copia local, libera la venta al vencer el tope corto', async () => {
    vi.useFakeTimers()
    try {
      const almacen = almacenFake()
      await guardarLocal(almacen, instantaneaFixture())
      obtenerInstantaneaMock.mockReturnValue(new Promise(() => {}))

      const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0)
      })
      expect(result.current.instantanea).not.toBeNull()
      await act(async () => {
        await vi.advanceTimersByTimeAsync(LIMITE_DE_SINCRONIZACION_INICIAL_CON_COPIA_LOCAL_MS - 1)
      })
      expect(result.current.sincronizacionInicialPendiente).toBe(true)

      await act(async () => {
        await vi.advanceTimersByTimeAsync(1)
      })
      expect(result.current.sincronizacionInicialPendiente).toBe(false)
      expect(result.current.sincronizando).toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })

  it('una copia local de OTRO punto de venta no acorta el tope del arranque', async () => {
    vi.useFakeTimers()
    try {
      const almacen = almacenFake()
      await guardarLocal(almacen, instantaneaFixture({ idPuntoVenta: 99 }))
      obtenerInstantaneaMock.mockReturnValue(new Promise(() => {}))

      const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
      await act(async () => {
        await vi.advanceTimersByTimeAsync(LIMITE_DE_SINCRONIZACION_INICIAL_CON_COPIA_LOCAL_MS + 1_000)
      })
      expect(result.current.sincronizacionInicialPendiente).toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })

  it('sincronizarAhora con un ciclo en curso no arranca otro: dos pedidos seguidos comparten el mismo ciclo', async () => {
    const almacen = almacenFake()
    const arranque = promesaControlada<InstantaneaDePos>()
    obtenerInstantaneaMock.mockReturnValueOnce(arranque.promesa)

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(obtenerInstantaneaMock).toHaveBeenCalledTimes(1))

    let primero: Promise<void> = Promise.resolve()
    let segundo: Promise<void> = Promise.resolve()
    act(() => {
      primero = result.current.sincronizarAhora()
      segundo = result.current.sincronizarAhora()
    })
    expect(obtenerInstantaneaMock).toHaveBeenCalledTimes(1)

    await act(async () => {
      arranque.resolver(instantaneaFixture())
      await primero
      await segundo
    })
    expect(obtenerInstantaneaMock).toHaveBeenCalledTimes(1)

    // Terminado el ciclo, un pedido nuevo sí corre uno nuevo.
    obtenerInstantaneaMock.mockResolvedValue(instantaneaFixture())
    await act(async () => {
      await result.current.sincronizarAhora()
    })
    expect(obtenerInstantaneaMock).toHaveBeenCalledTimes(2)
  })

  it('cambiar el intervalo no dispara un ciclo nuevo, solo reprograma el periódico', async () => {
    const almacen = almacenFake()
    const { result, rerender } = renderHook(
      ({ intervaloMs }) => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs }),
      { initialProps: { intervaloMs: 60_000 } },
    )
    await waitFor(() => expect(result.current.sincronizacionInicialPendiente).toBe(false))
    expect(obtenerInstantaneaMock).toHaveBeenCalledTimes(1)

    rerender({ intervaloMs: 120_000 })
    await act(async () => {
      await Promise.resolve()
    })
    expect(obtenerInstantaneaMock).toHaveBeenCalledTimes(1)
  })
})

describe('useSincronizacionOffline — el drenado nunca bloquea el encolado de una venta nueva', () => {
  it('con el envío de la venta más vieja colgado en la red, una venta nueva se encola igual', async () => {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture())
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    await agregarAOutbox(almacen, ventaEnColaFixture(149))
    emitirMock.mockReturnValue(new Promise(() => {}))

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(emitirMock).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())

    let resultado
    await act(async () => {
      resultado = await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture(),
        esConsumidorFinal: true, idListaPrecio: LISTA_CF,
        pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
      })
    })

    expect(resultado).toEqual({ ok: true, numero: 150, numeroVisible: '0007-00000150', limiteDeCreditoNoValidado: false })
    expect((await leerOutbox(almacen)).map((v) => v.numeroPreasignado)).toEqual([149, 150])
  })

  it('si la salida del outbox de una venta ya enviada no se puede confirmar, NUNCA la archiva como rechazada', async () => {
    const datos = new Map<string, unknown>()
    let escriturasDeOutbox = 0
    const almacen: AlmacenClaveValor = {
      async leer<T>(clave: string) {
        return (datos.has(clave) ? (datos.get(clave) as T) : null) ?? null
      },
      async escribir<T>(clave: string, valor: T) {
        // La primera escritura del outbox es la de este propio fixture; la segunda, la salida
        // de la venta después de enviarla — esa es la que se pierde.
        if (clave === 'outbox' && ++escriturasDeOutbox === 2) return true
        datos.set(clave, valor)
        return true
      },
    }
    await agregarAOutbox(almacen, ventaEnColaFixture(100))
    emitirMock.mockResolvedValue({ id: 1 })

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.sincronizacionInicialPendiente).toBe(false))

    expect(emitirMock).toHaveBeenCalledTimes(1)
    await expect(leerRechazadas(almacen)).resolves.toEqual([])
    expect((await leerOutbox(almacen)).map((v) => v.numeroPreasignado)).toEqual([100])
  })
})

describe('useSincronizacionOffline — drenarAhora', () => {
  it('drena el outbox, devuelve lo que queda según el almacén y rinde la cola', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, bloqueFixture({ proximo: 101, hasta: 200 }))
    await agregarAOutbox(almacen, ventaEnColaFixture(100))
    await agregarARechazada(almacen, { ...ventaEnColaFixture(99), mensaje: 'rechazada' })

    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.sincronizacionInicialPendiente).toBe(false))
    await waitFor(() => expect(result.current.outboxCount).toBe(1))

    emitirMock.mockResolvedValue({ id: 1 })
    rendirColaMock.mockClear()
    let estado
    await act(async () => {
      estado = await result.current.drenarAhora()
    })

    expect(estado).toEqual({ pendientes: 0, conError: 1 })
    expect(result.current.outboxCount).toBe(0)
    expect(rendirColaMock).toHaveBeenCalledWith({ codigoTipoComprobante: 'TX', entregadoHasta: 100, pendientes: 1 }, expect.any(AbortSignal))
  })

  it('sin señal deja la venta en el outbox y lo informa', async () => {
    const almacen = almacenFake()
    await agregarAOutbox(almacen, ventaEnColaFixture(100))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.sincronizacionInicialPendiente).toBe(false))

    let estado
    await act(async () => {
      estado = await result.current.drenarAhora()
    })
    expect(estado).toEqual({ pendientes: 1, conError: 0 })
  })
})

const BLOQUE_RESERVADO = { idPuntoVenta: 7, codigoTipoComprobante: 'TX', desde: 300, hasta: 399 }
const BLOQUE_BAJO = bloqueFixture({ desde: 100, proximo: 195, hasta: 200 })

async function encolarUna(result: { current: ReturnType<typeof useSincronizacionOffline> }) {
  let resultado: Awaited<ReturnType<ReturnType<typeof useSincronizacionOffline>['encolarVentaOffline']>> | undefined
  await act(async () => {
    resultado = await result.current.encolarVentaOffline({ solicitudBase: solicitudFixture(), esConsumidorFinal: true, idListaPrecio: LISTA_CF, pagos: [{ comportamiento: 'Efectivo', importe: 100 }] })
  })
  return resultado
}

async function montarListo(almacen: AlmacenClaveValor) {
  await guardarLocal(almacen, instantaneaFixture())
  const hook = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
  await waitFor(() => expect(hook.result.current.sincronizacionInicialPendiente).toBe(false))
  await waitFor(() => expect(hook.result.current.instantanea).not.toBeNull())
  return hook
}

describe('useSincronizacionOffline — reposición del bloque en segundo plano', () => {
  it('bajo el umbral, repone apenas drena y el bloque nuevo reemplaza al que estaba en uso', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, BLOQUE_BAJO)
    const { result } = await montarListo(almacen)
    // El ciclo de arranque corrió sin señal (ErrorDeRed por defecto): nunca intentó reservar.
    expect(reservarNumeracionMock).not.toHaveBeenCalled()

    reservarNumeracionMock.mockResolvedValue(BLOQUE_RESERVADO)
    await act(async () => {
      await result.current.drenarAhora()
    })

    expect(reservarNumeracionMock).toHaveBeenCalledTimes(1)
    await expect(leerBloque(almacen)).resolves.toEqual({ ...BLOQUE_RESERVADO, proximo: 300 })
    const r = await encolarUna(result)
    expect(r).toEqual({ ok: true, numero: 300, numeroVisible: '0007-00000300', limiteDeCreditoNoValidado: false })
  })

  it('rinde el bloque vivo con pendientes = 0 ANTES de reservar (su rendición queda congelada al abandonarlo)', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, BLOQUE_BAJO)
    const { result } = await montarListo(almacen)
    rendirColaMock.mockClear()

    reservarNumeracionMock.mockResolvedValue(BLOQUE_RESERVADO)
    await act(async () => {
      await result.current.drenarAhora()
    })

    expect(rendirColaMock.mock.calls[0][0]).toEqual({ codigoTipoComprobante: 'TX', entregadoHasta: 194, pendientes: 0 })
    expect(rendirColaMock.mock.invocationCallOrder[0]).toBeLessThan(reservarNumeracionMock.mock.invocationCallOrder[0])
    // Después de rotar, la rendición del ciclo es sobre el bloque vivo NUEVO, todavía sin usar.
    expect(rendirColaMock.mock.calls.at(-1)?.[0]).toEqual({ codigoTipoComprobante: 'TX', entregadoHasta: 299, pendientes: 0 })
  })

  it('con ventas pendientes en la cola local no rota el bloque: congelaría pendientes > 0 en el abandonado', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, BLOQUE_BAJO)
    await agregarAOutbox(almacen, ventaEnColaFixture(194))
    const { result } = await montarListo(almacen)

    reservarNumeracionMock.mockResolvedValue(BLOQUE_RESERVADO)
    await act(async () => {
      await result.current.drenarAhora()
    })

    expect(reservarNumeracionMock).not.toHaveBeenCalled()
    await expect(leerBloque(almacen)).resolves.toEqual(BLOQUE_BAJO)
  })

  it('si el servidor ya no tiene vivo el bloque local (reserva anterior perdida), reserva igual', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, BLOQUE_BAJO)
    const { result } = await montarListo(almacen)

    rendirColaMock.mockRejectedValueOnce(new ErrorApi(409, 'rendicion_de_bloque_reemplazado', 'El bloque fue reemplazado.'))
    reservarNumeracionMock.mockResolvedValue(BLOQUE_RESERVADO)
    await act(async () => {
      await result.current.drenarAhora()
    })

    expect(reservarNumeracionMock).toHaveBeenCalledTimes(1)
  })

  it('si la rendición previa falla por red, no reserva (el bloque abandonado quedaría con un reporte viejo)', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, BLOQUE_BAJO)
    const { result } = await montarListo(almacen)

    rendirColaMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))
    reservarNumeracionMock.mockResolvedValue(BLOQUE_RESERVADO)
    await act(async () => {
      await result.current.drenarAhora()
    })

    expect(reservarNumeracionMock).not.toHaveBeenCalled()
  })

  it('una reserva colgada en la red nunca demora el encolado mientras el bloque en uso tenga números', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, BLOQUE_BAJO)
    const { result } = await montarListo(almacen)

    reservarNumeracionMock.mockReturnValue(new Promise(() => {}))
    act(() => {
      void result.current.drenarAhora()
    })
    await waitFor(() => expect(reservarNumeracionMock).toHaveBeenCalledTimes(1))

    const r = await encolarUna(result)
    expect(r).toEqual({ ok: true, numero: 195, numeroVisible: '0007-00000195', limiteDeCreditoNoValidado: false })
  })
})

describe('useSincronizacionOffline — topes de red', () => {
  it('un envío colgado vence en el tope y se trata como incierto: la venta queda en el outbox, nunca rechazada', async () => {
    vi.useFakeTimers()
    try {
      const almacen = almacenFake()
      await agregarAOutbox(almacen, ventaEnColaFixture(100))
      emitirMock.mockReturnValue(new Promise(() => {}))

      const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
      await act(async () => {
        await vi.advanceTimersByTimeAsync(TIEMPO_LIMITE_DE_RED_MS - 1)
      })
      expect(result.current.sincronizacionInicialPendiente).toBe(true)

      await act(async () => {
        await vi.advanceTimersByTimeAsync(1)
      })
      expect(result.current.sincronizacionInicialPendiente).toBe(false)
      expect(emitirMock).toHaveBeenCalledTimes(1)
      expect((await leerOutbox(almacen)).map((v) => v.numeroPreasignado)).toEqual([100])
      await expect(leerRechazadas(almacen)).resolves.toEqual([])
      expect(result.current.ventasConError).toEqual([])
    } finally {
      vi.useRealTimers()
    }
  })

  it('con los dos bloques agotados, el encolado espera la reserva y la usa', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, bloqueFixture({ desde: 100, proximo: 201, hasta: 200 }))
    const { result } = await montarListo(almacen)

    reservarNumeracionMock.mockResolvedValue(BLOQUE_RESERVADO)
    const r = await encolarUna(result)

    expect(r).toEqual({ ok: true, numero: 300, numeroVisible: '0007-00000300', limiteDeCreditoNoValidado: false })
  })

  it('con los dos bloques agotados y la reserva colgada, el encolado espera solo hasta el tope y rechaza sin_numeracion', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, bloqueFixture({ desde: 100, proximo: 201, hasta: 200 }))
    const { result } = await montarListo(almacen)

    vi.useFakeTimers()
    try {
      reservarNumeracionMock.mockReturnValue(new Promise(() => {}))
      let resultado: unknown
      const encolado = result.current
        .encolarVentaOffline({ solicitudBase: solicitudFixture(), esConsumidorFinal: true, idListaPrecio: LISTA_CF, pagos: [{ comportamiento: 'Efectivo', importe: 100 }] })
        .then((r) => {
          resultado = r
        })

      await act(async () => {
        await vi.advanceTimersByTimeAsync(TIEMPO_LIMITE_DE_RED_MS - 1)
      })
      expect(resultado).toBeUndefined()

      await act(async () => {
        await vi.advanceTimersByTimeAsync(1)
        await encolado
      })
      expect(resultado).toEqual({ ok: false, motivo: 'sin_numeracion' })
    } finally {
      vi.useRealTimers()
    }
  })

  it('una venta que no se admitiría igual (otro cliente) nunca espera una reserva', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, bloqueFixture({ desde: 100, proximo: 201, hasta: 200 }))
    const { result } = await montarListo(almacen)

    let resultado
    await act(async () => {
      resultado = await result.current.encolarVentaOffline({ solicitudBase: solicitudFixture(), esConsumidorFinal: false, idListaPrecio: LISTA_CF, pagos: [{ comportamiento: 'Efectivo', importe: 100 }] })
    })

    expect(resultado).toEqual({ ok: false, motivo: 'cliente_no_admitido' })
    expect(reservarNumeracionMock).not.toHaveBeenCalled()
  })
})

describe('useSincronizacionOffline — rendición periódica, independiente del intervalo de sincronización', () => {
  it('con un intervalo de 60 minutos, rinde igual cada 2 minutos sin correr el ciclo completo', async () => {
    vi.useFakeTimers()
    try {
      const almacen = almacenFake()
      await guardarBloque(almacen, bloqueFixture({ proximo: 150 }))
      renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60 * 60_000 }))
      await act(async () => {
        await vi.advanceTimersByTimeAsync(1_000)
      })
      const instantaneasDelArranque = obtenerInstantaneaMock.mock.calls.length
      rendirColaMock.mockClear()

      await act(async () => {
        await vi.advanceTimersByTimeAsync(INTERVALO_DE_RENDICION_MS)
      })

      expect(rendirColaMock).toHaveBeenCalledWith({ codigoTipoComprobante: 'TX', entregadoHasta: 149, pendientes: 0 }, expect.any(AbortSignal))
      expect(obtenerInstantaneaMock.mock.calls.length).toBe(instantaneasDelArranque)
    } finally {
      vi.useRealTimers()
    }
  })

  it('drenarAhora sin ningún bloque local no intenta reservar (la primera reserva es del ciclo)', async () => {
    const almacen = almacenFake()
    const { result } = await montarListo(almacen)
    reservarNumeracionMock.mockResolvedValue(BLOQUE_RESERVADO)

    await act(async () => {
      await result.current.drenarAhora()
    })

    expect(reservarNumeracionMock).not.toHaveBeenCalled()
  })
})

describe('useSincronizacionOffline — revisión: el ciclo nunca queda retenido', () => {
  it('una descarga de instantánea colgada vence a los 30 s y libera el ciclo único', async () => {
    vi.useFakeTimers()
    try {
      const almacen = almacenFake()
      obtenerInstantaneaMock.mockReturnValue(new Promise(() => {}))
      const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60 * 60_000 }))

      await act(async () => {
        await vi.advanceTimersByTimeAsync(29_999)
      })
      expect(result.current.sincronizando).toBe(true)

      await act(async () => {
        await vi.advanceTimersByTimeAsync(1)
      })
      expect(result.current.sincronizando).toBe(false)

      obtenerInstantaneaMock.mockResolvedValue(instantaneaFixture())
      await act(async () => {
        await result.current.sincronizarAhora()
      })
      expect(obtenerInstantaneaMock).toHaveBeenCalledTimes(2)
    } finally {
      vi.useRealTimers()
    }
  })

  it('un paso del ciclo que tira no deja el ciclo retenido: el próximo pedido corre uno nuevo', async () => {
    const datos = new Map<string, unknown>()
    // La primera lectura del outbox es la carga inicial; la segunda, la del drenado del ciclo de
    // arranque — esa es la que falla.
    let lecturasDeOutbox = 0
    const almacen: AlmacenClaveValor = {
      async leer<T>(clave: string) {
        if (clave === 'outbox' && ++lecturasDeOutbox === 2) throw new Error('almacén caído')
        return (datos.has(clave) ? (datos.get(clave) as T) : null) ?? null
      },
      async escribir<T>(clave: string, valor: T) {
        datos.set(clave, valor)
        return true
      },
    }
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60 * 60_000 }))
    await waitFor(() => expect(result.current.sincronizacionInicialPendiente).toBe(false))
    expect(result.current.sincronizando).toBe(false)
    expect(obtenerInstantaneaMock).not.toHaveBeenCalled()

    await act(async () => {
      await result.current.sincronizarAhora()
    })
    expect(obtenerInstantaneaMock).toHaveBeenCalledTimes(1)
  })

  it('con sincronizarAntesDeVender = false la venta no espera el ciclo de arranque, que corre igual', async () => {
    const almacen = almacenFake()
    obtenerInstantaneaMock.mockReturnValue(new Promise(() => {}))
    const { result } = renderHook(() =>
      useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000, sincronizarAntesDeVender: false }),
    )

    expect(result.current.sincronizacionInicialPendiente).toBe(false)
    await waitFor(() => expect(obtenerInstantaneaMock).toHaveBeenCalledTimes(1))
    expect(result.current.sincronizacionInicialPendiente).toBe(false)
  })
})

describe('useSincronizacionOffline — revisión: nunca numerar con un bloque de otro punto de venta', () => {
  it('un bloque persistido de otro punto de venta se descarta al cargar: se repone uno propio', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, bloqueFixture({ idPuntoVenta: 99, proximo: 100, hasta: 200 }))
    obtenerInstantaneaMock.mockResolvedValue(instantaneaFixture())
    reservarNumeracionMock.mockResolvedValue(BLOQUE_RESERVADO)

    renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))

    await waitFor(() => expect(reservarNumeracionMock).toHaveBeenCalledTimes(1))
    expect((reservarNumeracionMock.mock.calls[0][0] as { idPuntoVenta: number }).idPuntoVenta).toBe(7)
  })

  it('una reposición del punto de venta anterior que aterriza después del cambio nunca numera ventas del nuevo', async () => {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture())
    await guardarBloque(almacen, BLOQUE_BAJO)
    const reserva = promesaControlada<typeof BLOQUE_RESERVADO>()
    reservarNumeracionMock.mockReturnValue(reserva.promesa)

    const { result, rerender } = renderHook(
      ({ idPuntoVenta }) => useSincronizacionOffline({ idPuntoVenta, activo: true, almacen, intervaloMs: 60_000 }),
      { initialProps: { idPuntoVenta: 7 } },
    )
    await waitFor(() => expect(result.current.sincronizacionInicialPendiente).toBe(false))
    act(() => {
      void result.current.drenarAhora()
    })
    await waitFor(() => expect(reservarNumeracionMock).toHaveBeenCalledTimes(1))

    rerender({ idPuntoVenta: 8 })
    await act(async () => {
      reserva.resolver(BLOQUE_RESERVADO)
      await Promise.resolve()
    })
    await waitFor(async () => expect(await leerBloque(almacen)).toEqual({ ...BLOQUE_RESERVADO, proximo: 300 }))

    let resultado
    await act(async () => {
      resultado = await result.current.encolarVentaOffline({
        solicitudBase: solicitudFixture({ idPuntoVenta: 8 }),
        esConsumidorFinal: true, idListaPrecio: LISTA_CF,
        pagos: [{ comportamiento: 'Efectivo', importe: 100 }],
        instantaneaCongelada: instantaneaFixture(),
      })
    })

    expect(resultado).toEqual({ ok: false, motivo: 'sin_numeracion' })
    await expect(leerBloque(almacen)).resolves.toEqual({ ...BLOQUE_RESERVADO, proximo: 300 })
  })
})

describe('useSincronizacionOffline — fin de sesión', () => {
  it('una instantánea que llega después de desmontar no se vuelve a guardar', async () => {
    const almacen = almacenFake()
    let resolver: (i: InstantaneaDePos) => void = () => {}
    obtenerInstantaneaMock.mockReturnValue(new Promise<InstantaneaDePos>((r) => (resolver = r)))

    const { unmount } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(obtenerInstantaneaMock).toHaveBeenCalled())
    unmount()

    await act(async () => {
      resolver(instantaneaFixture())
    })

    await expect(leerInstantaneaLocal(almacen)).resolves.toBeNull()
  })
})

describe('useSincronizacionOffline — sesión terminada con una sincronización en vuelo', () => {
  it('una instantánea que llega después de la purga no se vuelve a guardar, aunque el hook siga montado', async () => {
    const datos = new Map<string, unknown>()
    const almacen: AlmacenClaveValor & { eliminar(clave: string): Promise<boolean> } = {
      async leer<T>(clave: string) {
        return (datos.get(clave) as T) ?? null
      },
      async escribir<T>(clave: string, valor: T) {
        datos.set(clave, valor)
        return true
      },
      async eliminar(clave: string) {
        datos.delete(clave)
        return true
      },
    }
    let resolver: (i: InstantaneaDePos) => void = () => {}
    obtenerInstantaneaMock.mockReturnValue(new Promise<InstantaneaDePos>((r) => (resolver = r)))

    renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(obtenerInstantaneaMock).toHaveBeenCalled())

    await act(async () => {
      await purgarInstantaneaLocal(almacen)
      resolver(instantaneaFixture())
    })

    await expect(leerInstantaneaLocal(almacen)).resolves.toBeNull()
  })
})

describe('useSincronizacionOffline — venta local a un cliente identificado', () => {
  const ID_CLIENTE = 42
  const LISTA_CLIENTE = 3

  function clienteDeInstantanea(sobrescribir: Partial<ClienteDeInstantanea> = {}): ClienteDeInstantanea {
    return {
      idCliente: ID_CLIENTE,
      numero: 42,
      nombre: 'Cliente con cuenta',
      apellido: null,
      razonSocial: null,
      tipoDocumento: null,
      numeroDocumento: null,
      idCondicionFiscal: 1,
      idEmpresa: null,
      idListaPrecio: LISTA_CLIENTE,
      esConsumidorFinal: false,
      saldo: 0,
      limiteCredito: 1000,
      creditoIlimitado: false,
      ...sobrescribir,
    }
  }

  /** Un artículo con precio distinto en la lista del Consumidor Final y en la del cliente: el
   * payload delata cuál de las dos se usó. */
  const articuloEnDosListas: ArticuloDeInstantanea = {
    ...articuloFixture(),
    preciosPorLista: [
      { idListaPrecio: LISTA_CF, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] },
      { idListaPrecio: LISTA_CLIENTE, precioOriginal: 250, precioFinal: 230, descuentoUnitario: 20, aplicadas: [] },
    ],
  }

  async function montar(cliente: ClienteDeInstantanea = clienteDeInstantanea()) {
    const almacen = almacenFake()
    await guardarLocal(almacen, instantaneaFixture({ articulos: [articuloEnDosListas], clientes: [cliente] }))
    await guardarBloque(almacen, bloqueFixture({ proximo: 150, hasta: 200 }))
    const { result } = renderHook(() => useSincronizacionOffline({ idPuntoVenta: 7, activo: true, almacen, intervaloMs: 60_000 }))
    await waitFor(() => expect(result.current.instantanea).not.toBeNull())
    return { almacen, result }
  }

  function encolar(
    result: { current: ReturnType<typeof useSincronizacionOffline> },
    pagos: { comportamiento: 'Efectivo' | 'Electronico' | 'CuentaCorriente'; importe: number }[],
  ) {
    return result.current.encolarVentaOffline({
      solicitudBase: solicitudFixture({ idCliente: ID_CLIENTE }),
      esConsumidorFinal: false,
      idListaPrecio: LISTA_CLIENTE,
      pagos,
    })
  }

  it('encola con los precios de la lista del cliente, sin consultar el límite si no paga con cuenta corriente', async () => {
    const { almacen, result } = await montar()

    let resultado
    await act(async () => {
      resultado = await encolar(result, [{ comportamiento: 'Efectivo', importe: 230 }])
    })

    expect(resultado).toEqual({ ok: true, numero: 150, numeroVisible: '0007-00000150', limiteDeCreditoNoValidado: false })
    const [venta] = await leerOutbox(almacen)
    expect(venta.solicitud.idCliente).toBe(ID_CLIENTE)
    expect(venta.solicitud.lineas?.[0]).toMatchObject({ precioUnitario: 250, descuentoUnitario: 20 })
    expect(venta.solicitud).not.toHaveProperty('limiteDeCreditoNoValidado')
    expect(obtenerClienteMock).not.toHaveBeenCalled()
    expect(emitirMock).not.toHaveBeenCalled()
  })

  it('con cuenta corriente y el servidor diciendo que supera el límite, no encola nada ni consume número', async () => {
    const { almacen, result } = await montar()
    obtenerClienteMock.mockResolvedValue({ saldo: 900, limiteCredito: 1000, creditoIlimitado: false })

    let resultado
    await act(async () => {
      resultado = await encolar(result, [{ comportamiento: 'CuentaCorriente', importe: 230 }])
    })

    expect(resultado).toEqual({ ok: false, motivo: 'limite_credito_excedido' })
    expect(obtenerClienteMock).toHaveBeenCalledWith(ID_CLIENTE)
    await expect(leerOutbox(almacen)).resolves.toEqual([])
    await expect(leerBloque(almacen)).resolves.toEqual(bloqueFixture({ proximo: 150, hasta: 200 }))
    expect(emitirMock).not.toHaveBeenCalled()
  })

  it('con cuenta corriente y el servidor confirmando que cabe, encola con limiteDeCreditoNoValidado en false aunque la instantánea diga que supera', async () => {
    // La instantánea dice saldo 5000 (supera); el servidor, saldo 100 (cabe): manda el servidor.
    const { almacen, result } = await montar(clienteDeInstantanea({ saldo: 5000 }))
    obtenerClienteMock.mockResolvedValue({ saldo: 100, limiteCredito: 1000, creditoIlimitado: false })

    let resultado
    await act(async () => {
      resultado = await encolar(result, [{ comportamiento: 'CuentaCorriente', importe: 230 }])
    })

    expect(resultado).toEqual({ ok: true, numero: 150, numeroVisible: '0007-00000150', limiteDeCreditoNoValidado: false })
    const [venta] = await leerOutbox(almacen)
    expect(venta.solicitud.limiteDeCreditoNoValidado).toBe(false)
    expect(venta.solicitud.numeroPreasignado).toBe(150)
  })

  it('con cuenta corriente y sin red para consultar, encola igual con limiteDeCreditoNoValidado en true', async () => {
    const { almacen, result } = await montar()
    obtenerClienteMock.mockRejectedValue(new ErrorDeRed(new TypeError('Failed to fetch')))

    let resultado
    await act(async () => {
      resultado = await encolar(result, [{ comportamiento: 'CuentaCorriente', importe: 230 }])
    })

    expect(resultado).toEqual({ ok: true, numero: 150, numeroVisible: '0007-00000150', limiteDeCreditoNoValidado: true })
    const [venta] = await leerOutbox(almacen)
    expect(venta.solicitud.limiteDeCreditoNoValidado).toBe(true)
    expect(emitirMock).not.toHaveBeenCalled()
  })

  it('con cuenta corriente y un 5xx del servidor, encola igual con limiteDeCreditoNoValidado en true', async () => {
    const { almacen, result } = await montar()
    obtenerClienteMock.mockRejectedValue(new ErrorApi(503, 'servicio_no_disponible', 'No disponible'))

    let resultado
    await act(async () => {
      resultado = await encolar(result, [{ comportamiento: 'CuentaCorriente', importe: 230 }])
    })

    expect(resultado).toMatchObject({ ok: true, limiteDeCreditoNoValidado: true })
    expect((await leerOutbox(almacen))[0].solicitud.limiteDeCreditoNoValidado).toBe(true)
  })

  it('con cuenta corriente y un 4xx del servidor, no encola nada (el servidor contestó)', async () => {
    const { almacen, result } = await montar()
    obtenerClienteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el cliente'))

    let resultado
    await act(async () => {
      resultado = await encolar(result, [{ comportamiento: 'CuentaCorriente', importe: 230 }])
    })

    expect(resultado).toEqual({ ok: false, motivo: 'cuenta_corriente_no_verificada' })
    await expect(leerOutbox(almacen)).resolves.toEqual([])
    await expect(leerBloque(almacen)).resolves.toEqual(bloqueFixture({ proximo: 150, hasta: 200 }))
  })

  it('con cuenta corriente y un servidor que no contesta, encola con limiteDeCreditoNoValidado en true al vencer el tope, nunca antes', async () => {
    const { almacen, result } = await montar()
    obtenerClienteMock.mockReturnValue(new Promise(() => {}))

    vi.useFakeTimers()
    try {
      let resultado: unknown = 'pendiente'
      let promesa!: Promise<unknown>
      await act(async () => {
        promesa = encolar(result, [{ comportamiento: 'CuentaCorriente', importe: 230 }]).then((r) => (resultado = r))
        await vi.advanceTimersByTimeAsync(TIEMPO_LIMITE_DE_VERIFICACION_DE_CREDITO_MS - 1)
      })
      expect(resultado).toBe('pendiente')
      expect(await leerOutbox(almacen)).toEqual([])

      await act(async () => {
        await vi.advanceTimersByTimeAsync(1)
        await promesa
      })
      expect(resultado).toMatchObject({ ok: true, limiteDeCreditoNoValidado: true })
      expect((await leerOutbox(almacen))[0].solicitud.limiteDeCreditoNoValidado).toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })
})
