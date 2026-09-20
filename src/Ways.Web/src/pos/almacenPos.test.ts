import 'fake-indexeddb/auto'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { crearAlmacenIndexedDb } from './almacenPos'

describe('almacenPos — IndexedDB real (fake-indexeddb)', () => {
  it('escribir y leer devuelve el mismo valor, con round-trip de objetos anidados', async () => {
    const almacen = crearAlmacenIndexedDb()
    const valor = { momento: '2026-09-20T10:00:00Z', articulos: [{ idArticulo: 1, nombre: 'Coca Cola' }] }

    await almacen.escribir('clave-1', valor)
    const leido = await almacen.leer('clave-1')

    expect(leido).toEqual(valor)
  })

  it('leer una clave nunca escrita devuelve null, no lanza', async () => {
    const almacen = crearAlmacenIndexedDb()
    await expect(almacen.leer('nunca-escrita')).resolves.toBeNull()
  })

  it('escribir de nuevo sobre la misma clave reemplaza el valor anterior', async () => {
    const almacen = crearAlmacenIndexedDb()
    await almacen.escribir('clave-2', { version: 1 })
    await almacen.escribir('clave-2', { version: 2 })

    await expect(almacen.leer('clave-2')).resolves.toEqual({ version: 2 })
  })

  it('dos claves distintas no se pisan entre sí', async () => {
    const almacen = crearAlmacenIndexedDb()
    await almacen.escribir('a', 'valor-a')
    await almacen.escribir('b', 'valor-b')

    await expect(almacen.leer('a')).resolves.toBe('valor-a')
    await expect(almacen.leer('b')).resolves.toBe('valor-b')
  })
})

describe('almacenPos — degrada sin romper cuando IndexedDB no está disponible', () => {
  const indexedDbOriginal = globalThis.indexedDB

  beforeEach(() => {
    // Simula un entorno sin IndexedDB (modo privado estricto, navegador viejo) — nunca lanza
    // hacia el llamador, degrada a `null`/no-op (mismo criterio que un `indexedDB.open` que
    // rechaza por política de almacenamiento bloqueada).
    // @ts-expect-error -- se borra a propósito para simular la ausencia del global.
    delete globalThis.indexedDB
  })

  afterEach(() => {
    globalThis.indexedDB = indexedDbOriginal
  })

  it('leer sin IndexedDB resuelve null en vez de rechazar', async () => {
    const almacen = crearAlmacenIndexedDb()
    await expect(almacen.leer('cualquier-clave')).resolves.toBeNull()
  })

  it('escribir sin IndexedDB resuelve (no-op) en vez de rechazar', async () => {
    const almacen = crearAlmacenIndexedDb()
    await expect(almacen.escribir('cualquier-clave', { x: 1 })).resolves.toBeUndefined()
  })
})
