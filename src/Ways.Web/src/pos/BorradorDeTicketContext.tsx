import { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import type { LineaCarrito } from '../api/carrito'
import type { FilaPago } from '../api/pagos'
import type { ClienteListado, ResultadoDeResolucion, UsuarioAutenticado } from '../api/tipos'
import { AuthContext } from '../auth/AuthContext'
import type { AlmacenClaveValor } from './almacenPos'
import { crearAlmacenIndexedDb } from './almacenPos'

/**
 * stage-pos-adjustments / stage-pos-borrador-persistente: ticket en curso de una venta libre,
 * guardado para sobrevivir TANTO a una navegación de ida y vuelta (ej. `/pos` → `/caja` → `/pos`)
 * COMO a un reload/restart entero de la app — persistido en IndexedDB (`almacenPos.ts`), nunca
 * `localStorage` (mismo criterio de tamaño que ya documenta `almacenPos.ts`). Nunca se
 * guarda/restaura bajo `?idPresupuesto=`: esa venta se rehidrata siempre fresca desde el
 * servidor (ver `PantallaPos` en `Pos.tsx`).
 */
export type BorradorDeTicket = {
  lineas: LineaCarrito[]
  precios: Record<number, ResultadoDeResolucion>
  cantidadesEnEdicion: Record<number, string>
  filasPago: FilaPago[]
  /** Próximo valor de `proximaFilaPagoIdRef` — restaurado así en vez de derivarlo de
   * `filasPago` para que una fila agregada después de restaurar nunca reutilice el id de una
   * fila restaurada. */
  proximoIdFilaPago: number
  clienteSeleccionado: ClienteListado | null
}

export type AlmacenDeBorradoresDeTicket = {
  obtener: (clave: string) => BorradorDeTicket | undefined
  guardar: (clave: string, borrador: BorradorDeTicket) => void
  limpiar: (clave: string) => void
}

/**
 * `null` por defecto (ningún `Provider` en el árbol) — todo lo que lee este contexto en
 * `Pos.tsx` cae al comportamiento de siempre (carrito vacío, sin restaurar nada) sin necesitar
 * ninguna rama especial para tests u otros puntos de montaje que no envuelvan este `Provider`.
 */
export const BorradorDeTicketContext = createContext<AlmacenDeBorradoresDeTicket | null>(null)

const CLAVE_BORRADORES = 'borradores-ticket'
const VERSION_BORRADORES = 1

/** Debounce de la escritura a IndexedDB — `almacenPos.ts` documenta que abre una conexión por
 * operación y no está pensado para uso por tecla; cada `guardar`/`limpiar` corre por cada
 * mutación del carrito (escaneo, edición de cantidad, cambio de pago), así que se coalescen en
 * una sola escritura por ráfaga en vez de una por mutación. */
const DEMORA_DE_PERSISTENCIA_MS = 400

/** Lo que persiste por borrador — TODO `BorradorDeTicket` menos `precios`. Un precio persistido
 * sobrevive un restart entero (catálogo/ofertas/listas pudieron cambiar en el medio) y nunca se
 * confía: se restaura vacío y `PantallaPos` vuelve a resolverlo contra el servidor por el mismo
 * efecto que ya dispara cuando el carrito cambia (`Pos.tsx`, el `useEffect` de resolución de
 * precios no depende de `precios` — corre igual con el índice restaurado en `{}`). */
type BorradorPersistido = Omit<BorradorDeTicket, 'precios'>

type PayloadPersistido = {
  version: typeof VERSION_BORRADORES
  /** Scoping por usuario (y tenant, si aplica) — mismo criterio que
   * `almacenDePuntoVenta.leerPuntoVentaDeSesion`: un cajero distinto en el mismo dispositivo
   * nunca hereda el borrador del anterior. `idTenant` cubre además el caso de un mismo `id` de
   * usuario reutilizado entre tenants (no debería pasar, pero el chequeo es gratis). */
  idUsuario: number
  idTenant: number | null
  borradores: Record<string, BorradorPersistido>
}

function esPayloadDelUsuario(valor: unknown, usuario: UsuarioAutenticado): valor is PayloadPersistido {
  if (!valor || typeof valor !== 'object') return false
  const v = valor as Partial<PayloadPersistido>
  return (
    v.version === VERSION_BORRADORES &&
    v.idUsuario === usuario.id &&
    v.idTenant === usuario.idTenant &&
    typeof v.borradores === 'object' &&
    v.borradores !== null
  )
}

type Props = {
  children: ReactNode
  /** Inyectable para tests — default `crearAlmacenIndexedDb()` (IndexedDB real, `fake-indexeddb`
   * en la suite). Mismo criterio que `useSincronizacionOffline`. */
  almacen?: AlmacenClaveValor
}

/**
 * El almacén vive en un `Map` dentro de un `ref` (nunca en `useState`): guardar un borrador no
 * puede forzar un re-render del árbol entero que envuelve — solo el propio efecto de `Pos.tsx`
 * que escribe decide cuándo correr. El valor del contexto se arma una sola vez con `useMemo`
 * (deps vacías) para que sea una referencia estable durante toda la sesión: remontar
 * `PantallaPos` por `key` (cambio de punto de venta o de presupuesto) nunca vacía el almacén,
 * solo una navegación real fuera de este `Provider` (o un reload entero) lo hace — y un reload
 * entero ahora lo repuebla desde IndexedDB antes de que cualquier hijo llegue a montar.
 *
 * Precondición de montaje: este `Provider` SIEMPRE cuelga de un `AuthContext.Provider` ya
 * resuelto (`ShellPos` provee el suyo antes de este `Provider`; `Layout` cuelga de `AuthProvider`
 * detrás de `RutaProtegida`, que no renderiza `Layout` mientras `cargando` es `true`) — lee
 * `usuario` UNA sola vez al montar (el propio `Provider` nunca se remonta durante la sesión, ver
 * el párrafo anterior), nunca lo vuelve a leer en un render posterior. Sin `usuario` (contexto
 * ausente, como en la mayoría de los tests de `Pos.tsx` que no envuelven `AuthProvider`) el
 * almacén sigue funcionando en memoria para esa sesión de render, pero nunca persiste ni
 * restaura nada — no hay ningún usuario contra el cual scopear la clave persistida.
 */
export function ProveedorDeBorradoresDeTicket({ children, almacen: almacenInyectado }: Props) {
  const auth = useContext(AuthContext)
  const usuarioRef = useRef(auth?.usuario ?? null)

  const almacenRef = useRef<AlmacenClaveValor>(almacenInyectado ?? crearAlmacenIndexedDb())
  const mapaRef = useRef<Map<string, BorradorDeTicket>>(new Map())
  const [hidratado, setHidratado] = useState(false)

  const timeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const sucioRef = useRef(false)

  function payloadActual(): PayloadPersistido | null {
    const usuario = usuarioRef.current
    if (!usuario) return null
    const borradores: Record<string, BorradorPersistido> = {}
    for (const [clave, borrador] of mapaRef.current) {
      const { precios: _precios, ...resto } = borrador
      borradores[clave] = resto
    }
    return { version: VERSION_BORRADORES, idUsuario: usuario.id, idTenant: usuario.idTenant, borradores }
  }

  /** Escribe de inmediato si hay algo pendiente (cancela el debounce en curso) — el camino que
   * usan tanto el flush explícito (`pagehide`/`beforeunload`/oculto/desmontaje) como el propio
   * timeout del debounce al vencer. `escribir` nunca lanza (contrato de `almacenPos.ts`); una
   * escritura fallida acá degrada en silencio, mismo criterio que el resto de los usos de este
   * almacén que no necesitan confirmar la persistencia sí o sí (a diferencia del outbox offline). */
  function persistirYa() {
    if (timeoutRef.current !== null) {
      clearTimeout(timeoutRef.current)
      timeoutRef.current = null
    }
    if (!sucioRef.current) return
    sucioRef.current = false
    const payload = payloadActual()
    if (!payload) return
    void almacenRef.current.escribir(CLAVE_BORRADORES, payload)
  }

  function programarPersistencia() {
    sucioRef.current = true
    if (timeoutRef.current !== null) return
    timeoutRef.current = setTimeout(() => {
      timeoutRef.current = null
      persistirYa()
    }, DEMORA_DE_PERSISTENCIA_MS)
  }

  // Hidratación: lee los borradores persistidos ANTES de renderizar los hijos — `PantallaPos`
  // (`Pos.tsx`) lee `almacenBorradores.obtener(clave)` de forma síncrona en el primer render de
  // su propio `useRef`, así que el `Map` tiene que estar poblado antes de que ese componente
  // llegue a montar. `almacenPos.leer` nunca rechaza (degrada a `null` en modo privado, cuota
  // agotada, o sin IndexedDB) — una lectura fallida cae directo al almacén vacío, mismo
  // comportamiento que "nunca hubo nada guardado".
  useEffect(() => {
    let vigente = true
    almacenRef.current
      .leer<unknown>(CLAVE_BORRADORES)
      .then((guardado) => {
        if (!vigente) return
        const usuario = usuarioRef.current
        if (usuario && esPayloadDelUsuario(guardado, usuario)) {
          for (const [clave, borrador] of Object.entries(guardado.borradores)) {
            mapaRef.current.set(clave, { ...borrador, precios: {} })
          }
        }
        setHidratado(true)
      })
      .catch(() => {
        // `almacenPos.leer` ya degrada internamente y no debería rechazar nunca — este catch es
        // un backstop defensivo, nunca deja a la pantalla de venta bloqueada en blanco.
        if (vigente) setHidratado(true)
      })
    return () => {
      vigente = false
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Flush explícito: una escritura debounced que todavía no venció se pierde si la pestaña se
  // cierra o la app se cierra antes de que el timeout corra — estos cuatro disparadores cubren
  // el cierre de pestaña/app (`pagehide`, backstop `beforeunload`), pasar a segundo plano
  // (`visibilitychange` a `hidden`, la señal recomendada para apps que pueden no recibir
  // `pagehide` — ej. cambiar de app en el POS de escritorio) y el desmontaje de este `Provider`.
  useEffect(() => {
    function alCambiarVisibilidad() {
      if (document.visibilityState === 'hidden') persistirYa()
    }
    window.addEventListener('pagehide', persistirYa)
    window.addEventListener('beforeunload', persistirYa)
    document.addEventListener('visibilitychange', alCambiarVisibilidad)
    return () => {
      window.removeEventListener('pagehide', persistirYa)
      window.removeEventListener('beforeunload', persistirYa)
      document.removeEventListener('visibilitychange', alCambiarVisibilidad)
      persistirYa()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const almacen = useMemo<AlmacenDeBorradoresDeTicket>(
    () => ({
      obtener: (clave) => mapaRef.current.get(clave),
      guardar: (clave, borrador) => {
        mapaRef.current.set(clave, borrador)
        programarPersistencia()
      },
      limpiar: (clave) => {
        mapaRef.current.delete(clave)
        programarPersistencia()
      },
    }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  )

  // Nada se renderiza mientras la hidratación está en vuelo (una sola lectura a IndexedDB,
  // rápida) — así ningún hijo (`PantallaPos`) llega a congelar su borrador inicial en `undefined`
  // antes de que el `Map` esté poblado.
  if (!hidratado) return null

  return <BorradorDeTicketContext.Provider value={almacen}>{children}</BorradorDeTicketContext.Provider>
}
