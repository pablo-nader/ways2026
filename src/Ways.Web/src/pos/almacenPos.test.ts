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

  it('escribir devuelve true cuando la escritura de verdad persistió', async () => {
    const almacen = crearAlmacenIndexedDb()
    await expect(almacen.escribir('clave-ok', { x: 1 })).resolves.toBe(true)
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

  it('eliminar borra la clave: una lectura posterior vuelve a devolver null', async () => {
    const almacen = crearAlmacenIndexedDb()
    await almacen.escribir('clave-3', { x: 1 })

    await expect(almacen.eliminar('clave-3')).resolves.toBe(true)
    await expect(almacen.leer('clave-3')).resolves.toBeNull()
  })

  it('eliminar una clave nunca escrita resuelve true igual (no-op, nunca lanza)', async () => {
    const almacen = crearAlmacenIndexedDb()
    await expect(almacen.eliminar('nunca-escrita')).resolves.toBe(true)
  })

  it('leerPrefijo devuelve solo las entradas cuya clave empieza con el prefijo dado', async () => {
    const almacen = crearAlmacenIndexedDb()
    await almacen.escribir('borrador:1:a', { n: 1 })
    await almacen.escribir('borrador:1:b', { n: 2 })
    await almacen.escribir('borrador:2:a', { n: 3 })
    await almacen.escribir('otra-cosa', { n: 4 })

    const entradas = await almacen.leerPrefijo('borrador:1:')

    expect(entradas).toHaveLength(2)
    expect(entradas.map((e) => e.clave).sort()).toEqual(['borrador:1:a', 'borrador:1:b'])
  })

  it('leerPrefijo sin coincidencias devuelve un array vacío, no null', async () => {
    const almacen = crearAlmacenIndexedDb()
    await expect(almacen.leerPrefijo('sin-coincidencias:')).resolves.toEqual([])
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

  it('escribir sin IndexedDB resuelve false (no-op, nunca rechaza) — reporta que no persistió', async () => {
    const almacen = crearAlmacenIndexedDb()
    await expect(almacen.escribir('cualquier-clave', { x: 1 })).resolves.toBe(false)
  })

  it('eliminar sin IndexedDB resuelve false (no-op, nunca rechaza)', async () => {
    const almacen = crearAlmacenIndexedDb()
    await expect(almacen.eliminar('cualquier-clave')).resolves.toBe(false)
  })

  it('leerPrefijo sin IndexedDB resuelve un array vacío en vez de rechazar', async () => {
    const almacen = crearAlmacenIndexedDb()
    await expect(almacen.leerPrefijo('cualquier-prefijo:')).resolves.toEqual([])
  })
})
