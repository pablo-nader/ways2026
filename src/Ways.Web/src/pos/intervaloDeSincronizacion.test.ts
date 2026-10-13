import { describe, expect, it } from 'vitest'
import { INTERVALO_DE_SINCRONIZACION_MINUTOS, INTERVALO_DE_SINCRONIZACION_MS } from './intervaloDeSincronizacion'

describe('intervalo de sincronización fijo', () => {
  it('son 10 minutos, expresados también en milisegundos', () => {
    expect(INTERVALO_DE_SINCRONIZACION_MINUTOS).toBe(10)
    expect(INTERVALO_DE_SINCRONIZACION_MS).toBe(600_000)
  })
})
