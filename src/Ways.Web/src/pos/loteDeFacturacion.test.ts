import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  PEOR_CASO_DEL_LOTE_EN_EL_SERVIDOR_MS,
  TIEMPO_LIMITE_DEL_LOTE_DE_FACTURACION_MS,
  crearDisparadorDeLote,
  reiniciarLotesDeFacturacionParaTests,
} from './loteDeFacturacion'

/** Una llamada que el test resuelve o rechaza cuando quiere. */
function llamadaControlable() {
  const llamadas: { idPuntoVenta: number; senal: AbortSignal; resolver: () => void; rechazar: (e: unknown) => void }[] = []
  const procesar = vi.fn(
    (idPuntoVenta: number, senal: AbortSignal) =>
      new Promise<unknown>((resolver, rechazar) => {
        llamadas.push({ idPuntoVenta, senal, resolver: () => resolver({}), rechazar })
      }),
  )
  return { procesar, llamadas }
}

const vaciarMicrotareas = () => new Promise<void>((r) => setTimeout(r, 0))

beforeEach(() => {
  reiniciarLotesDeFacturacionParaTests()
})

describe('crearDisparadorDeLote', () => {
  it('llama una vez con el punto de venta recibido', () => {
    const { procesar } = llamadaControlable()
    const disparar = crearDisparadorDeLote({ procesar, estaEnLinea: () => true })

    disparar(7)

    expect(procesar).toHaveBeenCalledTimes(1)
    expect(procesar.mock.calls[0][0]).toBe(7)
  })

  it('sin conexión no llama', () => {
    const { procesar } = llamadaControlable()
    const disparar = crearDisparadorDeLote({ procesar, estaEnLinea: () => false })

    disparar(7)

    expect(procesar).not.toHaveBeenCalled()
  })

  it('sin punto de venta no llama', () => {
    const { procesar } = llamadaControlable()
    const disparar = crearDisparadorDeLote({ procesar, estaEnLinea: () => true })

    disparar(null)

    expect(procesar).not.toHaveBeenCalled()
  })

  it('con una llamada en vuelo no arranca otra, y los pedidos de ese lapso se juntan en una sola vuelta', async () => {
    const { procesar, llamadas } = llamadaControlable()
    const disparar = crearDisparadorDeLote({ procesar, estaEnLinea: () => true })

    disparar(7)
    disparar(7)
    disparar(7)
    expect(procesar).toHaveBeenCalledTimes(1)

    llamadas[0].resolver()
    await vaciarMicrotareas()
    expect(procesar).toHaveBeenCalledTimes(2)

    llamadas[1].resolver()
    await vaciarMicrotareas()
    expect(procesar).toHaveBeenCalledTimes(2)
  })

  it('sin pedidos durante la llamada no hay vuelta extra', async () => {
    const { procesar, llamadas } = llamadaControlable()
    const disparar = crearDisparadorDeLote({ procesar, estaEnLinea: () => true })

    disparar(7)
    llamadas[0].resolver()
    await vaciarMicrotareas()

    expect(procesar).toHaveBeenCalledTimes(1)
  })

  it('termina la llamada y se puede volver a disparar', async () => {
    const { procesar, llamadas } = llamadaControlable()
    const disparar = crearDisparadorDeLote({ procesar, estaEnLinea: () => true })

    disparar(7)
    llamadas[0].resolver()
    await vaciarMicrotareas()
    disparar(7)

    expect(procesar).toHaveBeenCalledTimes(2)
  })

  it('la vuelta pendiente no sale si para entonces se perdió la conexión', async () => {
    const { procesar, llamadas } = llamadaControlable()
    let enLinea = true
    const disparar = crearDisparadorDeLote({ procesar, estaEnLinea: () => enLinea })

    disparar(7)
    disparar(7)
    enLinea = false
    llamadas[0].resolver()
    await vaciarMicrotareas()

    expect(procesar).toHaveBeenCalledTimes(1)
  })

  it('un rechazo se descarta (Vitest falla la corrida ante un rechazo sin manejar) y no impide la siguiente llamada', async () => {
    const { procesar, llamadas } = llamadaControlable()
    const disparar = crearDisparadorDeLote({ procesar, estaEnLinea: () => true })

    disparar(7)
    llamadas[0].rechazar(new Error('boom'))
    await vaciarMicrotareas()
    disparar(7)
    await vaciarMicrotareas()

    expect(procesar).toHaveBeenCalledTimes(2)
  })

  it('una llamada que no responde se corta por tiempo y libera el disparador', async () => {
    vi.useFakeTimers()
    try {
      const { procesar, llamadas } = llamadaControlable()
      const disparar = crearDisparadorDeLote({ procesar, estaEnLinea: () => true, tiempoLimiteMs: 30_000 })

      disparar(7)
      await vi.advanceTimersByTimeAsync(30_000)

      expect(llamadas[0].senal.aborted).toBe(true)
      disparar(7)
      expect(procesar).toHaveBeenCalledTimes(2)
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('crearDisparadorDeLote — estado compartido por punto de venta entre instancias (remontajes)', () => {
  it('dos instancias del mismo punto de venta nunca tienen dos llamadas en vuelo, y sus pedidos se juntan en una sola vuelta', async () => {
    const { procesar, llamadas } = llamadaControlable()
    const primera = crearDisparadorDeLote({ procesar, estaEnLinea: () => true })
    const segunda = crearDisparadorDeLote({ procesar, estaEnLinea: () => true })

    primera(7)
    segunda(7)
    segunda(7)
    expect(procesar).toHaveBeenCalledTimes(1)

    llamadas[0].resolver()
    await vaciarMicrotareas()
    expect(procesar).toHaveBeenCalledTimes(2)

    llamadas[1].resolver()
    await vaciarMicrotareas()
    expect(procesar).toHaveBeenCalledTimes(2)
  })

  it('puntos de venta distintos son independientes', () => {
    const { procesar } = llamadaControlable()
    const primera = crearDisparadorDeLote({ procesar, estaEnLinea: () => true })
    const segunda = crearDisparadorDeLote({ procesar, estaEnLinea: () => true })

    primera(7)
    segunda(8)

    expect(procesar).toHaveBeenCalledTimes(2)
    expect(procesar.mock.calls.map(([id]) => id)).toEqual([7, 8])
  })

  it('la vuelta pendiente pedida por la instancia nueva respeta SU chequeo de conexión', async () => {
    const { procesar, llamadas } = llamadaControlable()
    const vieja = crearDisparadorDeLote({ procesar, estaEnLinea: () => true })
    let nuevaEnLinea = true
    const nueva = crearDisparadorDeLote({ procesar, estaEnLinea: () => nuevaEnLinea })

    vieja(7)
    nueva(7)
    nuevaEnLinea = false
    llamadas[0].resolver()
    await vaciarMicrotareas()

    expect(procesar).toHaveBeenCalledTimes(1)
  })

  it('un pedido con el servidor ya alcanzado sale aunque la bandera de conexión siga en falso', () => {
    const { procesar } = llamadaControlable()
    const disparar = crearDisparadorDeLote({ procesar, estaEnLinea: () => false })

    disparar(7, { servidorAlcanzable: true })

    expect(procesar).toHaveBeenCalledTimes(1)
  })
})

describe('crearDisparadorDeLote — tiempo límite', () => {
  it('el tope por defecto está por encima del peor caso del servidor (20 s de presupuesto + 30 s de WSAA + 5 s + 5 s de cierres)', () => {
    expect(PEOR_CASO_DEL_LOTE_EN_EL_SERVIDOR_MS).toBe(60_000)
    expect(TIEMPO_LIMITE_DEL_LOTE_DE_FACTURACION_MS).toBeGreaterThan(PEOR_CASO_DEL_LOTE_EN_EL_SERVIDOR_MS)
  })

  it('sin tiempoLimiteMs explícito la llamada se corta recién al vencer el tope por defecto', async () => {
    vi.useFakeTimers()
    try {
      const { procesar, llamadas } = llamadaControlable()
      const disparar = crearDisparadorDeLote({ procesar, estaEnLinea: () => true })

      disparar(7)
      await vi.advanceTimersByTimeAsync(PEOR_CASO_DEL_LOTE_EN_EL_SERVIDOR_MS)
      expect(llamadas[0].senal.aborted).toBe(false)
      await vi.advanceTimersByTimeAsync(TIEMPO_LIMITE_DEL_LOTE_DE_FACTURACION_MS - PEOR_CASO_DEL_LOTE_EN_EL_SERVIDOR_MS - 1)
      expect(llamadas[0].senal.aborted).toBe(false)

      await vi.advanceTimersByTimeAsync(1)
      expect(llamadas[0].senal.aborted).toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })
})
