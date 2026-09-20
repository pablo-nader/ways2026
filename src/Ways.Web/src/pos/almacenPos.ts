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
  })
}

/** Almacén real, respaldado por IndexedDB — el único que usa la app en producción
 * (`useSincronizacionOffline.ts`). Abre la conexión en cada operación (nunca la cachea): el costo
 * de `indexedDB.open` es despreciable frente a la frecuencia de uso de este módulo (un puñado de
 * lecturas/escrituras por minuto, nunca por tecla) y evita tener que manejar una conexión que
 * quedó `onversionchange`/cerrada por otra pestaña.
 */
export function crearAlmacenIndexedDb(): AlmacenClaveValor {
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
  }
}
