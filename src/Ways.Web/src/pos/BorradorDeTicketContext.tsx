import { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { migrarLineasDeBorrador } from '../api/carrito'
import type { LineaCarrito } from '../api/carrito'
import type { FilaPago } from '../api/pagos'
import type { ClienteListado, UsuarioAutenticado } from '../api/tipos'
import type { PreciosPorLinea } from '../api/ventas'
import { AuthContext } from '../auth/AuthContext'
import type { AlmacenDeClavesMultiples } from './almacenPos'
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
  precios: PreciosPorLinea
  /** Texto en edición del input de cantidad, indexado por `idLinea`. */
  cantidadesEnEdicion: Record<string, string>
  filasPago: FilaPago[]
  /** Próximo valor de `proximaFilaPagoIdRef` — restaurado así en vez de derivarlo de
   * `filasPago` para que una fila agregada después de restaurar nunca reutilice el id de una
   * fila restaurada. */
  proximoIdFilaPago: number
  clienteSeleccionado: ClienteListado | null
}

export type AlmacenDeBorradoresDeTicket = {
  /** `false` mientras la hidratación desde IndexedDB está en vuelo (o hasta que el timeout de
   * hidratación la da por vencida) — `Pos.tsx` espera este flag ANTES de montar `PantallaPos`
   * (que lee el borrador inicial de forma síncrona en su primer render), pero el resto del árbol
   * (`Layout`) nunca espera nada: el `Provider` renderiza `children` desde el primer render,
   * `false` u no. */
  listo: boolean
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

const PREFIJO_BORRADORES = 'borrador-ticket'
const VERSION_BORRADORES = 1

/** Timeout de la hidratación inicial — un `abrirDb()` que nunca resuelve (ver `onblocked` en
 * `almacenPos.ts`) no puede dejar la pantalla de venta bloqueada para siempre (JD-2): vencido
 * este plazo, la hidratación se da por terminada con lo que ya haya (nada, en ese caso) y una
 * lectura que llegue después se ignora — ver `hidratacionResueltaRef` más abajo. */
const TIMEOUT_HIDRATACION_MS = 2000

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

/** JD-1: cada borrador vive bajo SU PROPIA clave de IndexedDB (`claveIndexedDb` más abajo) en vez
 * de un único registro con el `Map` entero — dos `Provider` del mismo usuario (dos pestañas, o
 * una `PantallaPos` de venta libre y otra de punto de venta distinto en la misma sesión) ya no se
 * pisan uno al otro: cada `guardar`/`limpiar` toca solo la clave que mutó, nunca reescribe las
 * claves de las que ni se enteró. */
type PayloadPersistido = {
  version: typeof VERSION_BORRADORES
  borrador: BorradorPersistido
}

function esPayloadValido(valor: unknown): valor is PayloadPersistido {
  if (!valor || typeof valor !== 'object') return false
  const v = valor as Partial<PayloadPersistido>
  return v.version === VERSION_BORRADORES && typeof v.borrador === 'object' && v.borrador !== null
}

/** Borrador persistido → `BorradorDeTicket` en memoria. Un borrador guardado antes de
 * `idLinea`/`acumulaEnVenta` se migra acá (`migrarLineasDeBorrador`) en vez de descartarse, así
 * que `VERSION_BORRADORES` no cambia. */
export function hidratarBorradorPersistido(borrador: BorradorPersistido): BorradorDeTicket {
  const { lineas, cantidadesEnEdicion } = migrarLineasDeBorrador(
    Array.isArray(borrador.lineas) ? borrador.lineas : [],
    borrador.cantidadesEnEdicion ?? {},
  )
  return { ...borrador, lineas, cantidadesEnEdicion, precios: {} }
}

/** Scoping por usuario (y tenant, si aplica) — mismo criterio que
 * `almacenDePuntoVenta.leerPuntoVentaDeSesion`: un cajero distinto en el mismo dispositivo nunca
 * hereda el borrador del anterior. `idTenant` cubre además el caso de un mismo `id` de usuario
 * reutilizado entre tenants (no debería pasar, pero el chequeo es gratis). */
function prefijoDelUsuario(usuario: UsuarioAutenticado): string {
  return `${PREFIJO_BORRADORES}:${usuario.idTenant ?? 'sin-tenant'}:${usuario.id}:`
}

/** Clave de IndexedDB de un borrador puntual — exportada para que los tests puedan escribir
 * directamente en el formato real sin duplicar este armado (ver `BorradorDeTicketContext.test.tsx`
 * y `Pos.test.tsx`). */
export function claveIndexedDbDeBorrador(usuario: UsuarioAutenticado, clave: string): string {
  return `${prefijoDelUsuario(usuario)}${clave}`
}

type Props = {
  children: ReactNode
  /** Inyectable para tests — default `crearAlmacenIndexedDb()` (IndexedDB real, `fake-indexeddb`
   * en la suite). Mismo criterio que `useSincronizacionOffline`. */
  almacen?: AlmacenDeClavesMultiples
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

  const almacenRef = useRef<AlmacenDeClavesMultiples>(almacenInyectado ?? crearAlmacenIndexedDb())
  const mapaRef = useRef<Map<string, BorradorDeTicket>>(new Map())
  const [hidratado, setHidratado] = useState(false)
  // JD-2: distingue "la hidratación real ya terminó (con o sin datos)" de "el timeout la dio por
  // vencida" — una lectura que llega DESPUÉS del timeout ya no tiene forma de saber si el usuario
  // alcanzó a guardar algo nuevo mientras tanto (`mapaRef` ya pudo mutar), así que se descarta
  // entera en vez de arriesgarse a pisar un borrador más nuevo con uno más viejo.
  const hidratacionResueltaRef = useRef(false)

  const timeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  // JD-1: un `Map<clave, accion>` en vez de un solo booleano `sucio` — cada `guardar`/`limpiar`
  // anota SOLO la clave que tocó (la última acción sobre esa clave gana); el flush recorre nada
  // más que esas claves, nunca el almacén entero, así que dos `Provider` de la misma sesión
  // (pestañas distintas) que tocan claves distintas nunca se pisan uno al otro.
  const cambiosPendientesRef = useRef<Map<string, 'guardar' | 'limpiar'>>(new Map())

  /** Escribe de inmediato lo pendiente (cancela el debounce en curso) — el camino que usan tanto
   * el flush explícito (`pagehide`/`beforeunload`/oculto/desmontaje) como el propio timeout del
   * debounce al vencer. `escribir`/`eliminar` nunca lanzan (contrato de `almacenPos.ts`); una
   * escritura fallida acá degrada en silencio, mismo criterio que el resto de los usos de este
   * almacén que no necesitan confirmar la persistencia sí o sí (a diferencia del outbox offline). */
  function persistirYa() {
    if (timeoutRef.current !== null) {
      clearTimeout(timeoutRef.current)
      timeoutRef.current = null
    }
    const usuario = usuarioRef.current
    const pendientes = cambiosPendientesRef.current
    if (pendientes.size === 0) return
    const entradas = Array.from(pendientes.entries())
    pendientes.clear()
    if (!usuario) return
    for (const [clave, accion] of entradas) {
      const claveDb = claveIndexedDbDeBorrador(usuario, clave)
      if (accion === 'limpiar') {
        void almacenRef.current.eliminar(claveDb)
        continue
      }
      const borrador = mapaRef.current.get(clave)
      if (!borrador) continue
      const { precios: _precios, ...resto } = borrador
      const payload: PayloadPersistido = { version: VERSION_BORRADORES, borrador: resto }
      void almacenRef.current.escribir(claveDb, payload)
    }
  }

  function programarPersistencia(clave: string, accion: 'guardar' | 'limpiar') {
    cambiosPendientesRef.current.set(clave, accion)
    if (timeoutRef.current !== null) return
    timeoutRef.current = setTimeout(() => {
      timeoutRef.current = null
      persistirYa()
    }, DEMORA_DE_PERSISTENCIA_MS)
  }

  // Hidratación: lee los borradores persistidos ANTES de que `Pos.tsx` monte `PantallaPos` (ver
  // `listo` en el valor del contexto) — `PantallaPos` lee `almacenBorradores.obtener(clave)` de
  // forma síncrona en el primer render de su propio `useRef`, así que el `Map` tiene que estar
  // poblado antes de que ese componente llegue a montar. `almacenPos.leerPrefijo` nunca rechaza
  // (degrada a `[]`) — una lectura fallida cae directo al almacén vacío, mismo comportamiento que
  // "nunca hubo nada guardado". Sin `usuario` (contexto ausente) no hay ninguna clave contra la
  // cual leer — queda listo de inmediato, en memoria vacía.
  useEffect(() => {
    let vigente = true
    const usuario = usuarioRef.current
    if (!usuario) {
      hidratacionResueltaRef.current = true
      setHidratado(true)
      return
    }

    // JD-2: sin este timeout, una apertura de IndexedDB que nunca resuelve (bloqueada por otra
    // pestaña con una conexión de versión anterior, y sin el `onblocked` de `almacenPos.ts` — o
    // cualquier otro cuelgue) dejaría esta pantalla esperando para siempre. Vencido el plazo, se
    // sigue con el almacén vacío; una lectura que llegue después ya no puede confiar en que
    // `mapaRef` sigue reflejando "nada guardado" (el usuario pudo haber armado un ticket nuevo
    // mientras tanto), así que se descarta.
    const idTimeout = setTimeout(() => {
      if (!vigente || hidratacionResueltaRef.current) return
      hidratacionResueltaRef.current = true
      setHidratado(true)
    }, TIMEOUT_HIDRATACION_MS)

    const prefijo = prefijoDelUsuario(usuario)
    almacenRef.current
      .leerPrefijo<unknown>(prefijo)
      .then((entradas) => {
        if (!vigente || hidratacionResueltaRef.current) return
        clearTimeout(idTimeout)
        for (const { clave, valor } of entradas) {
          if (!esPayloadValido(valor)) continue
          const claveLogica = clave.slice(prefijo.length)
          mapaRef.current.set(claveLogica, hidratarBorradorPersistido(valor.borrador))
        }
        hidratacionResueltaRef.current = true
        setHidratado(true)
      })
      .catch(() => {
        // `almacenPos.leerPrefijo` ya degrada internamente y no debería rechazar nunca — este
        // catch es un backstop defensivo, nunca deja la pantalla de venta esperando.
        if (vigente && !hidratacionResueltaRef.current) {
          clearTimeout(idTimeout)
          hidratacionResueltaRef.current = true
          setHidratado(true)
        }
      })
    return () => {
      vigente = false
      clearTimeout(idTimeout)
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
      listo: hidratado,
      obtener: (clave) => mapaRef.current.get(clave),
      guardar: (clave, borrador) => {
        mapaRef.current.set(clave, borrador)
        programarPersistencia(clave, 'guardar')
      },
      limpiar: (clave) => {
        mapaRef.current.delete(clave)
        programarPersistencia(clave, 'limpiar')
      },
    }),
    // `hidratado` es la única dep real — pasa de `false` a `true` una sola vez por sesión (nunca
    // vuelve atrás), así que esto crea a lo sumo un segundo objeto de contexto, nunca uno por
    // render (ver el doc-comment de este `Provider`, "referencia estable durante toda la sesión").
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [hidratado],
  )

  // JD-2: `children` se renderiza SIEMPRE, hidratado o no — `Layout` envuelve TODAS las páginas
  // autenticadas con este `Provider` (no solo `/pos`), así que bloquear el render acá dejaba la
  // app entera en blanco si la hidratación se colgaba. Solo `Pos.tsx` (la única pantalla que lee
  // un borrador inicial de forma síncrona) espera `almacen.listo` antes de montar `PantallaPos`.
  return <BorradorDeTicketContext.Provider value={almacen}>{children}</BorradorDeTicketContext.Provider>
}
