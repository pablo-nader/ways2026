import { describe, expect, it } from 'vitest'
import type { AlmacenClaveValor } from './almacenPos'
import {
  agregarCobroSinVenta,
  apartadoPendienteDeSincronizar,
  contarApartadosPendientes,
  leerCobrosSinVenta,
  marcarCobroSinVentaSincronizado,
  type CobroSinVenta,
} from './reservaDeCobro'

function almacenFake(opciones: { fallarEscritura?: boolean } = {}): AlmacenClaveValor {
  const datos = new Map<string, unknown>()
  return {
    async leer<T>(clave: string) {
      return (datos.has(clave) ? (datos.get(clave) as T) : null) ?? null
    },
    async escribir<T>(clave: string, valor: T) {
      if (opciones.fallarEscritura) return false
      datos.set(clave, valor)
      return true
    },
  }
}

const cobro = (idCobro: number, sobrescribir: Partial<CobroSinVenta> = {}): CobroSinVenta => ({
  idCobro,
  numero: 500 + idCobro,
  idPuntoVenta: 7,
  importe: 100,
  importePagado: 100,
  apartadoEn: '2026-11-01T10:00:00.000Z',
  ...sobrescribir,
})

describe('apartados pendientes de sincronizar', () => {
  it('una entrada sin sincronizadoEn (también la guardada antes de que existiera el campo) está pendiente', () => {
    expect(apartadoPendienteDeSincronizar(cobro(1))).toBe(true)
    expect(apartadoPendienteDeSincronizar(cobro(1, { sincronizadoEn: '2026-11-01T11:00:00.000Z' }))).toBe(false)
  })

  it('cuenta solo las pendientes', () => {
    const lista = [cobro(1), cobro(2, { sincronizadoEn: 'x' }), cobro(3)]

    expect(contarApartadosPendientes(lista)).toBe(2)
    expect(contarApartadosPendientes([])).toBe(0)
  })
})

describe('marcarCobroSinVentaSincronizado', () => {
  it('marca solo la entrada pedida y la deja releíble', async () => {
    const almacen = almacenFake()
    await agregarCobroSinVenta(almacen, cobro(1))
    await agregarCobroSinVenta(almacen, cobro(2))

    expect(await marcarCobroSinVentaSincronizado(almacen, 2, '2026-11-01T12:00:00.000Z')).toBe(true)

    const [uno, dos] = await leerCobrosSinVenta(almacen)
    expect(uno.sincronizadoEn).toBeUndefined()
    expect(dos.sincronizadoEn).toBe('2026-11-01T12:00:00.000Z')
  })

  it('es idempotente: marcar de nuevo no mueve la fecha', async () => {
    const almacen = almacenFake()
    await agregarCobroSinVenta(almacen, cobro(1))
    await marcarCobroSinVentaSincronizado(almacen, 1, 'primera')

    expect(await marcarCobroSinVentaSincronizado(almacen, 1, 'segunda')).toBe(true)

    expect((await leerCobrosSinVenta(almacen))[0].sincronizadoEn).toBe('primera')
  })

  it('una entrada que ya no está no es un error', async () => {
    expect(await marcarCobroSinVentaSincronizado(almacenFake(), 9, 'x')).toBe(true)
  })

  it('si la escritura no se confirma devuelve false y la entrada sigue pendiente', async () => {
    const almacen = almacenFake()
    await agregarCobroSinVenta(almacen, cobro(1))
    const escribir = almacen.escribir.bind(almacen)
    almacen.escribir = async () => false

    expect(await marcarCobroSinVentaSincronizado(almacen, 1, 'x')).toBe(false)

    almacen.escribir = escribir
    expect((await leerCobrosSinVenta(almacen))[0].sincronizadoEn).toBeUndefined()
  })
})
