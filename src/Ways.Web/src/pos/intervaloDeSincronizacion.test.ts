import { describe, expect, it, vi } from 'vitest'
import { INTERVALO_DE_SINCRONIZACION_MINUTOS, INTERVALO_DE_SINCRONIZACION_MS } from './intervaloDeSincronizacion'

describe('intervalo de sincronización fijo', () => {
  it('son 10 minutos, expresados también en milisegundos', () => {
    expect(INTERVALO_DE_SINCRONIZACION_MINUTOS).toBe(10)
    expect(INTERVALO_DE_SINCRONIZACION_MS).toBe(600_000)
  })

  it('cargar el módulo no lee localStorage', async () => {
    const lectura = vi.spyOn(Storage.prototype, 'getItem')
    vi.resetModules()
    await import('./intervaloDeSincronizacion')
    expect(lectura).not.toHaveBeenCalled()
    lectura.mockRestore()
  })
})
