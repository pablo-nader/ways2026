import { describe, expect, it } from 'vitest'
import {
  admisibilidadDeVentaOffline,
  agregarAOutbox,
  construirNumeroVisible,
  generarIdLocal,
  guardarBloque,
  leerBloque,
  leerOutbox,
  mensajeDeRechazoOffline,
  necesitaReponerBloque,
  numerosDisponibles,
  quitarDeOutbox,
  tomarProximoNumero,
  UMBRAL_DE_REPOSICION,
  type BloqueDeNumeracionLocal,
  type VentaEnCola,
} from './outboxOffline'
import type { AlmacenClaveValor } from './almacenPos'

/** Fake en memoria del almacén — mismo contrato que el real (`AlmacenClaveValor`), sin
 * IndexedDB: permite testear la lógica de negocio pura de este módulo sin acoplarla a
 * `fake-indexeddb` (ese acoplamiento vive, aparte, en `almacenPos.test.ts`). */
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

function bloqueFixture(sobrescribir: Partial<BloqueDeNumeracionLocal> = {}): BloqueDeNumeracionLocal {
  return { idPuntoVenta: 7, codigoTipoComprobante: 'TX', desde: 100, hasta: 200, proximo: 100, ...sobrescribir }
}

function ventaFixture(sobrescribir: Partial<VentaEnCola> = {}): VentaEnCola {
  return {
    idLocal: 'local-1',
    numeroPreasignado: 100,
    idPuntoVenta: 7,
    creadoEn: '2026-09-20T10:00:00.000Z',
    solicitud: { idPuntoVenta: 7, codigoTipoComprobante: 'TX', idComprobanteAsociado: null, pagos: [], direccionEntrega: null, observaciones: null },
    ...sobrescribir,
  }
}

describe('numerosDisponibles / tomarProximoNumero', () => {
  it('numerosDisponibles cuenta el rango inclusive restante', () => {
    expect(numerosDisponibles(bloqueFixture({ proximo: 100, hasta: 200 }))).toBe(101)
    expect(numerosDisponibles(bloqueFixture({ proximo: 200, hasta: 200 }))).toBe(1)
  })

  it('numerosDisponibles es 0 sin bloque', () => {
    expect(numerosDisponibles(null)).toBe(0)
  })

  it('numerosDisponibles nunca negativo con un bloque ya agotado', () => {
    expect(numerosDisponibles(bloqueFixture({ proximo: 201, hasta: 200 }))).toBe(0)
  })

  it('tomarProximoNumero devuelve el número actual y avanza el puntero, sin tocar desde/hasta', () => {
    const resultado = tomarProximoNumero(bloqueFixture({ proximo: 100, hasta: 200 }))
    expect(resultado).toEqual({ numero: 100, bloqueRestante: bloqueFixture({ proximo: 101, hasta: 200 }) })
  })

  it('tomarProximoNumero es null sin bloque', () => {
    expect(tomarProximoNumero(null)).toBeNull()
  })

  it('tomarProximoNumero es null con el bloque agotado (mutation target: el corte es <= 0, no < 0)', () => {
    expect(tomarProximoNumero(bloqueFixture({ proximo: 201, hasta: 200 }))).toBeNull()
  })
})

describe('necesitaReponerBloque', () => {
  it('true sin bloque', () => {
    expect(necesitaReponerBloque(null)).toBe(true)
  })

  it(`true justo por debajo del umbral (${UMBRAL_DE_REPOSICION})`, () => {
    const bloque = bloqueFixture({ proximo: 100, hasta: 100 + UMBRAL_DE_REPOSICION - 2 }) // quedan UMBRAL - 1
    expect(necesitaReponerBloque(bloque)).toBe(true)
  })

  it('false justo en el umbral (mutation target: el corte es estricto <, no <=)', () => {
    const bloque = bloqueFixture({ proximo: 100, hasta: 100 + UMBRAL_DE_REPOSICION - 1 }) // quedan exactamente UMBRAL
    expect(necesitaReponerBloque(bloque)).toBe(false)
  })
})

describe('construirNumeroVisible — espejo de NumeroDeComprobante.Formatear', () => {
  it('rellena punto de venta a 4 y número a 8, separados por guion', () => {
    expect(construirNumeroVisible(7, 1)).toBe('0007-00000001')
  })

  it('no trunca cuando ya supera el ancho fijo', () => {
    expect(construirNumeroVisible(12345, 123456789)).toBe('12345-123456789')
  })
})

describe('admisibilidadDeVentaOffline — corta en el primer rechazo, orden estable', () => {
  const admitida = {
    esConsumidorFinal: true,
    pagos: [{ comportamiento: 'Efectivo' as const }],
    hayInstantanea: true,
    todasLasLineasConPrecio: true,
    hayNumeroDisponible: true,
  }

  it('null (admitida) cuando las cinco condiciones se cumplen', () => {
    expect(admisibilidadDeVentaOffline(admitida)).toBeNull()
  })

  it('cliente_no_admitido cuando el cliente no es CF, aunque el resto esté OK', () => {
    expect(admisibilidadDeVentaOffline({ ...admitida, esConsumidorFinal: false })).toBe('cliente_no_admitido')
  })

  it('medio_no_admitido cuando algún pago no es efectivo', () => {
    expect(admisibilidadDeVentaOffline({ ...admitida, pagos: [{ comportamiento: 'CuentaCorriente' }] })).toBe('medio_no_admitido')
  })

  it('sin_instantanea sin instantánea local', () => {
    expect(admisibilidadDeVentaOffline({ ...admitida, hayInstantanea: false })).toBe('sin_instantanea')
  })

  it('linea_sin_precio con alguna línea sin precio en la instantánea', () => {
    expect(admisibilidadDeVentaOffline({ ...admitida, todasLasLineasConPrecio: false })).toBe('linea_sin_precio')
  })

  it('sin_numeracion sin números disponibles', () => {
    expect(admisibilidadDeVentaOffline({ ...admitida, hayNumeroDisponible: false })).toBe('sin_numeracion')
  })

  it('orden estable: cliente_no_admitido gana sobre medio_no_admitido cuando ambos fallan (regla D2/E1, mismo criterio que validarPagosLocal)', () => {
    expect(
      admisibilidadDeVentaOffline({ ...admitida, esConsumidorFinal: false, pagos: [{ comportamiento: 'CuentaCorriente' }] }),
    ).toBe('cliente_no_admitido')
  })

  it('mensajeDeRechazoOffline devuelve un mensaje no vacío para cada motivo posible', () => {
    const motivos = ['cliente_no_admitido', 'medio_no_admitido', 'sin_instantanea', 'linea_sin_precio', 'sin_numeracion'] as const
    for (const motivo of motivos) {
      expect(mensajeDeRechazoOffline(motivo).length).toBeGreaterThan(0)
    }
  })
})

describe('generarIdLocal', () => {
  it('genera ids distintos en llamadas sucesivas', () => {
    const a = generarIdLocal()
    const b = generarIdLocal()
    expect(a).not.toBe(b)
  })
})

describe('outbox — leer/agregar/quitar, en orden', () => {
  it('leerOutbox devuelve [] sin nada guardado', async () => {
    await expect(leerOutbox(almacenFake())).resolves.toEqual([])
  })

  it('agregarAOutbox encola AL FINAL — el orden de inserción se preserva para el drenado', async () => {
    const almacen = almacenFake()
    await agregarAOutbox(almacen, ventaFixture({ idLocal: 'a', numeroPreasignado: 100 }))
    const siguiente = await agregarAOutbox(almacen, ventaFixture({ idLocal: 'b', numeroPreasignado: 101 }))
    expect(siguiente.map((v) => v.idLocal)).toEqual(['a', 'b'])
  })

  it('quitarDeOutbox saca solo la venta indicada, preservando el orden de las demás', async () => {
    const almacen = almacenFake()
    await agregarAOutbox(almacen, ventaFixture({ idLocal: 'a' }))
    await agregarAOutbox(almacen, ventaFixture({ idLocal: 'b' }))
    await agregarAOutbox(almacen, ventaFixture({ idLocal: 'c' }))
    const siguiente = await quitarDeOutbox(almacen, 'b')
    expect(siguiente.map((v) => v.idLocal)).toEqual(['a', 'c'])
  })

  it('quitar una venta inexistente es un no-op (nunca lanza)', async () => {
    const almacen = almacenFake()
    await agregarAOutbox(almacen, ventaFixture({ idLocal: 'a' }))
    await expect(quitarDeOutbox(almacen, 'no-existe')).resolves.toEqual(expect.objectContaining({ length: 1 }))
  })

  it('guardarBloque/leerBloque hacen round-trip', async () => {
    const almacen = almacenFake()
    await guardarBloque(almacen, bloqueFixture())
    await expect(leerBloque(almacen)).resolves.toEqual(bloqueFixture())
  })

  it('leerBloque devuelve null sin nada guardado', async () => {
    await expect(leerBloque(almacenFake())).resolves.toBeNull()
  })
})
