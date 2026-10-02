import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { conTiempoLimite, ErrorDeTiempoAgotado, TIEMPO_LIMITE_DE_RED_MS } from './tiempoLimite'

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('conTiempoLimite', () => {
  it('resuelve con el valor de la operación si llega antes del tope', async () => {
    await expect(conTiempoLimite(() => Promise.resolve(42))).resolves.toBe(42)
  })

  it('propaga el error propio de la operación', async () => {
    const error = new Error('rechazo del servidor')
    await expect(conTiempoLimite(() => Promise.reject(error))).rejects.toBe(error)
  })

  it('con una operación colgada, rechaza con ErrorDeTiempoAgotado justo al vencer el tope y aborta la señal', async () => {
    let senalRecibida: AbortSignal | undefined
    let rechazo: unknown
    conTiempoLimite((senal) => {
      senalRecibida = senal
      return new Promise(() => {})
    }).catch((e: unknown) => {
      rechazo = e
    })

    await vi.advanceTimersByTimeAsync(TIEMPO_LIMITE_DE_RED_MS - 1)
    expect(rechazo).toBeUndefined()
    expect(senalRecibida?.aborted).toBe(false)

    await vi.advanceTimersByTimeAsync(1)
    expect(rechazo).toBeInstanceOf(ErrorDeTiempoAgotado)
    expect(senalRecibida?.aborted).toBe(true)
  })

  it('respeta un tope propio', async () => {
    let rechazo: unknown
    conTiempoLimite(() => new Promise(() => {}), 15_000).catch((e: unknown) => {
      rechazo = e
    })
    await vi.advanceTimersByTimeAsync(TIEMPO_LIMITE_DE_RED_MS)
    expect(rechazo).toBeUndefined()
    await vi.advanceTimersByTimeAsync(15_000 - TIEMPO_LIMITE_DE_RED_MS)
    expect(rechazo).toBeInstanceOf(ErrorDeTiempoAgotado)
  })
})
