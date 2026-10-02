import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { AlmacenDeClavesMultiples } from './almacenPos'

const limpiarSesionDeCajeroPersistidaMock = vi.fn()
vi.mock('../api/entornoTauri', () => ({
  limpiarSesionDeCajeroPersistida: (...args: unknown[]) => limpiarSesionDeCajeroPersistidaMock(...args),
}))

const { terminarSesionLocalDelPos } = await import('./finDeSesionLocal')

function almacenFake(datosIniciales: Record<string, unknown>): AlmacenDeClavesMultiples & { datos: Map<string, unknown> } {
  const datos = new Map<string, unknown>(Object.entries(datosIniciales))
  return {
    datos,
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
    async leerPrefijo() {
      return []
    },
  }
}

beforeEach(() => {
  limpiarSesionDeCajeroPersistidaMock.mockReset()
  limpiarSesionDeCajeroPersistidaMock.mockResolvedValue(undefined)
})

describe('terminarSesionLocalDelPos', () => {
  it('limpia la sesión persistida y borra las instantáneas guardadas, sin tocar outbox ni bloque', async () => {
    const almacen = almacenFake({ 'instantanea.v2': { version: 2 }, instantanea: { vieja: true }, outbox: ['venta'], bloqueNumeracion: { desde: 1 } })

    await terminarSesionLocalDelPos(almacen)

    expect(limpiarSesionDeCajeroPersistidaMock).toHaveBeenCalledTimes(1)
    expect([...almacen.datos.keys()].sort()).toEqual(['bloqueNumeracion', 'outbox'])
  })
})
