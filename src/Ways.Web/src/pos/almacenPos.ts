/**
 * Almacén clave-valor durable para la venta offline del POS de escritorio
 * (stage-pos-venta-offline-web). `localStorage` (`almacenDePuntoVenta.ts`) alcanza para un par de
 * campos escalares, pero la instantánea completa del catálogo pesa cientos de KB (ver el
 * doc-comment de `ServicioDeInstantaneaDePos`) y el outbox de ventas encoladas crece durante un
 * corte — ambos casos superan lo que `localStorage` debería cargar (cuota típica ~5MB, síncrono,
 * bloquea el hilo principal en cada lectura/escritura grande). IndexedDB es asíncrono, tiene
 * cuota mucho mayor y ya es la elección estándar del navegador para este tamaño de dato — la
 * alternativa (`Cache API`) está pensada para requests HTTP, no para JSON estructurado.
 *
 * Un solo object store (`kv`) con un puñado de claves fijas (instantánea, outbox, bloque de
 * numeración): la escala esperada es "un catálogo, un outbox, un bloque", nunca miles de filas
 * independientes — un esquema multi-store indexado sería complejidad sin un problema medido que
 * la justifique (mismo criterio que el propio backend documenta para no paginar la instantánea).
 *
 * Toda operación está envuelta en `try/catch` — nunca tira una excepción no controlada hacia la
 * pantalla de venta (react-async-state, "wrap every read and write so a failure degrades instead
 * of crashing"): modo privado, almacenamiento bloqueado por política, cuota agotada o `indexedDB`
 * inexistente (un entorno de test sin `fake-indexeddb`, o un navegador viejo) siempre resuelve en
 * vez de rechazar. `leer` degrada a `null` (nunca hay nada mejor que devolver). `escribir` degrada
 * a `false` — a diferencia de `leer`, una escritura fallida SÍ importa para quien la hizo: la
 * instantánea puede tolerar perder una actualización (`instantaneaOffline.guardarInstantaneaLocal`
 * ignora el resultado a propósito, mismo criterio que antes), pero el outbox de ventas NO puede
 * asumir que "no tiró" significa "quedó guardada" (judgment-day ronda 1, BLOCKER — ver
 * `outboxOffline.agregarAOutbox`, el único llamador que de verdad verifica este booleano).
 */

const NOMBRE_DB = 'ways-pos-offline'
const VERSION_DB = 1
const OBJECT_STORE = 'kv'

/** Interfaz mínima que necesita el resto del módulo offline — permite inyectar un fake en tests
 * unitarios de la lógica de negocio (`instantaneaOffline.ts`/`outboxOffline.ts`) sin depender de
 * IndexedDB real ni de `fake-indexeddb`. */
export type AlmacenClaveValor = {
  leer<T>(clave: string): Promise<T | null>
  /** `true` si la escritura de verdad persistió, `false` si se degradó (nunca rechaza) — el
   * llamador decide si una escritura fallida es tolerable o tiene que bloquear la operación. */
  escribir<T>(clave: string, valor: T): Promise<boolean>
}

/** Una entrada leída por `leerPrefijo` — la clave completa (con prefijo incluido) junto a su
 * valor, para que el llamador pueda recuperar la porción de clave que le interesa (ver
 * `BorradorDeTicketContext.ts`, que le saca el prefijo para volver a la clave lógica del mapa en
 * memoria). */
export type EntradaDeAlmacen<T> = { clave: string; valor: T }

/** Superset de `AlmacenClaveValor` — `borrar` y `leerPrefijo` son propios de un uso por
 * MUCHAS claves relacionadas (un registro por borrador de ticket, ver
 * `BorradorDeTicketContext.tsx`) en vez del puñado de claves fijas que usan
 * `outboxOffline.ts`/`instantaneaOffline.ts`. Se declara aparte (en vez de agregarle estos
 * métodos a `AlmacenClaveValor`) para no obligar a los fakes de esos otros módulos a
 * implementarlos. */
export type AlmacenDeClavesMultiples = AlmacenClaveValor & {
  /** `true` si la baja de verdad se aplicó (clave ausente o borrada), `false` si se degradó
   * (nunca rechaza) — mismo contrato que `escribir`. */
  eliminar(clave: string): Promise<boolean>
  /** Todas las entradas cuya clave empieza con `prefijo`, en cualquier orden — nunca rechaza,
   * una lectura fallida degrada a `[]` (mismo criterio que `leer` degradando a `null`). */
  leerPrefijo<T>(prefijo: string): Promise<Array<EntradaDeAlmacen<T>>>
}

function abrirDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const solicitud = indexedDB.open(NOMBRE_DB, VERSION_DB)
    solicitud.onupgradeneeded = () => {
      if (!solicitud.result.objectStoreNames.contains(OBJECT_STORE)) {
        solicitud.result.createObjectStore(OBJECT_STORE)
      }
    }
    solicitud.onsuccess = () => resolve(solicitud.result)
    solicitud.onerror = () => reject(solicitud.error)
    // Otra pestaña con una conexión abierta a una versión anterior nunca la cierra sola —
    // sin este handler, `onsuccess`/`onerror` no llegan a correr nunca y la promesa queda
    // colgada para siempre (JD-2: eso bloquea la hidratación del borrador, y por lo tanto la
    // pantalla entera, si el `Provider` esperara esta promesa sin un timeout propio). Se
    // resuelve con el mismo error que `onerror` para que el llamador degrade igual que
    // cualquier otro fallo de apertura.
    solicitud.onblocked = () => reject(new Error('IndexedDB bloqueada por otra conexión abierta.'))
  })
}

/** Almacén real, respaldado por IndexedDB — el único que usa la app en producción
 * (`useSincronizacionOffline.ts`). Abre la conexión en cada operación (nunca la cachea): el costo
 * de `indexedDB.open` es despreciable frente a la frecuencia de uso de este módulo (un puñado de
 * lecturas/escrituras por minuto, nunca por tecla) y evita tener que manejar una conexión que
 * quedó `onversionchange`/cerrada por otra pestaña.
 */
export function crearAlmacenIndexedDb(): AlmacenDeClavesMultiples {
  async function conTransaccion<T>(modo: IDBTransactionMode, accion: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
    if (typeof indexedDB === 'undefined') {
      throw new Error('IndexedDB no está disponible en este entorno.')
    }
    const db = await abrirDb()
    try {
      return await new Promise<T>((resolve, reject) => {
        const tx = db.transaction(OBJECT_STORE, modo)
        const solicitud = accion(tx.objectStore(OBJECT_STORE))
        solicitud.onsuccess = () => resolve(solicitud.result)
        solicitud.onerror = () => reject(solicitud.error)
        tx.onerror = () => reject(tx.error)
      })
    } finally {
      db.close()
    }
  }

  return {
    async leer<T>(clave: string): Promise<T | null> {
      try {
        const valor = await conTransaccion<T | undefined>('readonly', (store) => store.get(clave))
        return valor ?? null
      } catch {
        // Modo privado, cuota agotada, almacenamiento bloqueado por política, o IndexedDB
        // inexistente — degrada a "no hay nada guardado" en vez de romper la pantalla de venta.
        return null
      }
    },
    async escribir<T>(clave: string, valor: T): Promise<boolean> {
      try {
        await conTransaccion('readwrite', (store) => store.put(valor, clave))
        return true
      } catch {
        // Modo privado, cuota agotada, almacenamiento bloqueado por política, o IndexedDB
        // inexistente — nunca rompe la pantalla de venta, pero SÍ reporta que no persistió: el
        // llamador (`agregarAOutbox`) decide si eso es tolerable.
        return false
      }
    },
    async eliminar(clave: string): Promise<boolean> {
      try {
        await conTransaccion('readwrite', (store) => store.delete(clave))
        return true
      } catch {
        return false
      }
    },
    async leerPrefijo<T>(prefijo: string): Promise<Array<EntradaDeAlmacen<T>>> {
      try {
        if (typeof indexedDB === 'undefined') return []
        const db = await abrirDb()
        try {
          return await new Promise<Array<EntradaDeAlmacen<T>>>((resolve, reject) => {
            const tx = db.transaction(OBJECT_STORE, 'readonly')
            const store = tx.objectStore(OBJECT_STORE)
            // Rango medio abierto `[prefijo, prefijo + '￿']` — cualquier clave que empiece
            // con `prefijo` cae adentro (`￿` es mayor a cualquier carácter que use este
            // módulo para armar claves), sin traer el resto del store entero.
            const rango = IDBKeyRange.bound(prefijo, prefijo + '￿', false, false)
            const resultado: Array<EntradaDeAlmacen<T>> = []
            const solicitud = store.openCursor(rango)
            solicitud.onsuccess = () => {
              const cursor = solicitud.result
              if (!cursor) {
                resolve(resultado)
                return
              }
              resultado.push({ clave: String(cursor.key), valor: cursor.value as T })
              cursor.continue()
            }
            solicitud.onerror = () => reject(solicitud.error)
            tx.onerror = () => reject(tx.error)
          })
        } finally {
          db.close()
        }
      } catch {
        // Mismo criterio que `leer`: modo privado, cuota agotada, almacenamiento bloqueado, o
        // IndexedDB inexistente degradan a "no hay nada guardado" en vez de romper la hidratación.
        return []
      }
    },
  }
}
