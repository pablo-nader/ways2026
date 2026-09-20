/**
 * Orquestación de la venta offline del POS de escritorio (stage-pos-venta-offline-web, Parte A/C):
 * carga la instantánea persistida apenas monta (sobrevive un restart, goal A), y corre un ciclo
 * oportunista — al montar, cada `intervaloMs`, y en el evento `online` del navegador — que (1)
 * drena el outbox EN ORDEN, (2) refresca la instantánea si hay señal, y (3) repone el bloque de
 * numeración si está bajo, todo en un único lugar para que las tres tareas nunca corran
 * entrelazadas entre sí (ver `encolarOperacion`, más abajo). El módulo de negocio puro
 * (`instantaneaOffline.ts`/`outboxOffline.ts`/`reglasOffline.ts`) no sabe nada de React ni de
 * timers — este hook es la única pieza con estado/efectos.
 */
import { useEffect, useRef, useState } from 'react'
import type { AlmacenClaveValor } from './almacenPos'
import { crearAlmacenIndexedDb } from './almacenPos'
import { guardarInstantaneaLocal, leerInstantaneaLocal, todasLasLineasTienenPrecioOffline } from './instantaneaOffline'
import {
  admisibilidadDeVentaOffline,
  agregarAOutbox,
  agregarARechazada,
  construirNumeroVisible,
  ErrorDePersistenciaOffline,
  generarIdLocal,
  guardarBloque,
  leerBloque,
  leerOutbox,
  leerRechazadas,
  necesitaReponerBloque,
  numerosDisponibles,
  quitarDeOutbox,
  tomarProximoNumero,
  type BloqueDeNumeracionLocal,
  type MotivoRechazoOffline,
  type VentaEnCola,
  type VentaRechazada,
} from './outboxOffline'
import { clienteDePos } from '../api/pos'
import { clienteDeVentas } from '../api/ventas'
import { ErrorDeRed } from '../api/cliente'
import type { ComportamientoMedioPago, InstantaneaDePos, LineaDeVenta, SolicitudDeVenta } from '../api/tipos'

/** Ciclo de sincronización oportunista — cada 20s alcanza para reponer el bloque y drenar el
 * outbox con margen frente al umbral de reposición (`UMBRAL_DE_REPOSICION` = 20 números), sin
 * generar tráfico apreciable durante una jornada normal. Exportado para que los tests puedan
 * pasar un valor propio (más chico, con fake timers) en vez de esperar el real. */
export const INTERVALO_DE_SINCRONIZACION_MS = 20_000

/** Tamaño de bloque a reponer — bien por debajo del tope del servidor (`CantidadMaxima` = 500),
 * suficiente para una jornada de venta minorista sin inflar el hueco de numeración si el
 * dispositivo nunca vuelve a sincronizar (mismo criterio documentado en el backend). */
const CANTIDAD_A_RESERVAR = 100

const CODIGO_TIPO_COMPROBANTE_OFFLINE = 'TX'

export type EncoladoOffline = { ok: true; numeroVisible: string; numero: number } | { ok: false; motivo: MotivoRechazoOffline }

export type ParametrosDeSincronizacionOffline = {
  /** `null` sin punto de venta de sesión — el hook queda inerte (sin timers, sin fetches). */
  idPuntoVenta: number | null
  /** `false` bajo `?idPresupuesto=` u otro modo donde la venta offline no aplica — mismo criterio
   * que el resto de los efectos de `Pos.tsx` gateados por `modoPresupuesto`. */
  activo: boolean
  /** Inyectable para tests — default `crearAlmacenIndexedDb()` (IndexedDB real). */
  almacen?: AlmacenClaveValor
  intervaloMs?: number
}

export type ResultadoDeSincronizacionOffline = {
  instantanea: InstantaneaDePos | null
  outboxCount: number
  /** Señal proactiva para la UI (deshabilitar cuenta corriente / clientes no-CF ANTES de un
   * intento de venta, no solo después de que falle) — arranca en `navigator.onLine` (optimista:
   * un `false` de entrada bloquearía la primera venta del día sin necesidad) y se corrige con el
   * resultado real del primer ciclo. Nunca es la fuente de verdad del fallback de escaneo/precio/
   * checkout (esa sigue siendo el `ErrorDeRed` real de cada intento) — solo gatea qué opciones
   * mostrar antes de intentar. */
  enLinea: boolean
  /** Ventas que el servidor rechazó de forma PERMANENTE al drenar (un 409/400 real, nunca un
   * simple "sin conexión") — se sacaron del outbox para no bloquear el drenado del resto de la
   * cola, pero NUNCA se descartan: quedan acá, visibles con su error real, hasta que un humano
   * las resuelva (judgment-day ronda 1, CRITICAL — antes de este fix, una sola de estas ventas
   * frenaba el drenado entero para siempre). Bloquea el cierre de turno igual que `outboxCount`
   * (ver `irACerrarCaja` en `Pos.tsx` y el gate de `CierreDeCaja.tsx`). `[]` sin ninguna pendiente. */
  ventasConError: readonly VentaRechazada[]
  encolarVentaOffline: (params: {
    solicitudBase: SolicitudDeVenta
    esConsumidorFinal: boolean
    pagos: readonly { comportamiento: ComportamientoMedioPago }[]
    /** judgment-day ronda 1 (WARNING): la instantánea a usar para resolver precio/descuento de
     * cada línea — inyectable a propósito para que `Pos.tsx` pueda pasar la MISMA instantánea que
     * ya se usó para la vista previa que el cajero tiene en pantalla (congelada contra el refresco
     * en segundo plano, ver `Pos.tsx`), en vez de que este hook lea su propio estado `instantanea`
     * (que puede haberse refrescado DESPUÉS de esa vista previa). `undefined`/ausente cae al
     * estado interno del hook — comportamiento preexistente intacto para quien no tenga una vista
     * previa propia que congelar (p. ej. los tests de este mismo hook). */
    instantaneaCongelada?: InstantaneaDePos | null
  }) => Promise<EncoladoOffline>
}

/** Enriquece cada línea con el precio congelado de la instantánea — `null` si CUALQUIER línea no
 * tiene artículo en la instantánea. Defensa en profundidad únicamente: el gate real (todas o
 * ninguna) es `todasLasLineasTienenPrecioOffline`, ya evaluado por el llamador ANTES de invocar
 * esto (judgment-day ronda 1, SUGGESTION) — este `if (!articulo) return null` nunca debería
 * disparar en la práctica, mismo criterio que `comprobanteOfflineSintetico.ts`. */
function enriquecerLineasConPrecioOffline(lineas: LineaDeVenta[], instantanea: InstantaneaDePos): LineaDeVenta[] | null {
  const porId = new Map(instantanea.articulos.map((a) => [a.idArticulo, a]))
  const enriquecidas: LineaDeVenta[] = []
  for (const linea of lineas) {
    const articulo = porId.get(linea.idArticulo)
    if (!articulo) return null
    // El backend trata `precioUnitario` como precio de LISTA (bruto) y resta `descuentoUnitario`
    // de nuevo (`ServicioDeVentas.MaterializarItems` → `CalculadorDeTotales.Calcular`) — el mismo
    // contrato que el camino online (`PrecioOriginal`/`DescuentoUnitario`). Mandar `precioFinal`
    // (ya neto) restaba el descuento DOS VECES y sub-registraba toda venta offline con oferta.
    enriquecidas.push({ ...linea, precioUnitario: articulo.precioOriginal, descuentoUnitario: articulo.descuentoUnitario })
  }
  return enriquecidas
}

export function useSincronizacionOffline(params: ParametrosDeSincronizacionOffline): ResultadoDeSincronizacionOffline {
  const { idPuntoVenta, activo, intervaloMs = INTERVALO_DE_SINCRONIZACION_MS } = params

  const almacenRef = useRef<AlmacenClaveValor>(params.almacen ?? crearAlmacenIndexedDb())

  const [instantanea, setInstantanea] = useState<InstantaneaDePos | null>(null)
  const [outboxCount, setOutboxCount] = useState(0)
  const [ventasConError, setVentasConError] = useState<VentaRechazada[]>([])
  const [enLinea, setEnLinea] = useState(() => typeof navigator === 'undefined' || navigator.onLine)

  // Fuente de verdad del bloque local — nunca se muestra en pantalla (goal C solo pide mostrar el
  // outbox), así que vive en un ref puro, sin re-render por cada número consumido.
  const bloqueRef = useRef<BloqueDeNumeracionLocal | null>(null)

  // Serializa TODA mutación de outbox/bloque (drenado, reposición, encolado de una venta nueva):
  // sin esto, un drenado en vuelo y un checkout offline concurrente podrían leer-modificar-escribir
  // el mismo array por turnos entrelazados y perderse una actualización del otro.
  const colaRef = useRef<Promise<unknown>>(Promise.resolve())
  function encolarOperacion<T>(operacion: () => Promise<T>): Promise<T> {
    const resultado = colaRef.current.then(operacion, operacion)
    colaRef.current = resultado.then(
      () => undefined,
      () => undefined,
    )
    return resultado
  }

  async function drenarOutbox(): Promise<void> {
    let outbox = await leerOutbox(almacenRef.current)
    while (outbox.length > 0) {
      const [primera] = outbox
      try {
        await clienteDeVentas.emitir(primera.solicitud)
        outbox = await quitarDeOutbox(almacenRef.current, primera.idLocal)
        setOutboxCount(outbox.length)
      } catch (e) {
        if (e instanceof ErrorDeRed) {
          // Transitorio ("reintentar más tarde, mantener el orden") — sigue sin señal, se
          // reintenta TODO en el próximo ciclo: nunca se descarta ni se saca nada del outbox.
          return
        }
        // Rechazo REAL y PERMANENTE del servidor sobre el ítem más viejo (ej.
        // `numero_preasignado_con_otro_contenido`, `turno_no_abierto`) — nunca se descarta (la
        // venta es real, con ticket ya entregado): se archiva como "necesita atención" con su
        // error real y se saca del outbox para que el drenado pueda seguir con el resto de la
        // cola, en vez de quedar rehén de un solo ítem trabado para siempre (judgment-day ronda
        // 1, CRITICAL).
        const mensaje =
          e instanceof Error ? `La venta ${primera.numeroPreasignado} no se pudo sincronizar: ${e.message}` : 'Una venta encolada no se pudo sincronizar.'
        try {
          await agregarARechazada(almacenRef.current, { ...primera, mensaje })
        } catch {
          // No se pudo archivar de forma durable como rechazada — se deja el ítem en el outbox
          // (nunca se saca sin confirmar dónde queda) y se corta esta pasada; el próximo ciclo
          // reintenta desde el mismo punto.
          return
        }
        outbox = await quitarDeOutbox(almacenRef.current, primera.idLocal)
        setOutboxCount(outbox.length)
        setVentasConError(await leerRechazadas(almacenRef.current))
      }
    }
  }

  async function refrescarInstantaneaSiHaySenal(): Promise<boolean> {
    try {
      const fresca = await clienteDePos.obtenerInstantanea()
      await guardarInstantaneaLocal(almacenRef.current, fresca)
      setInstantanea(fresca)
      setEnLinea(true)
      return true
    } catch (e) {
      // Sin conexión, o el servidor rechazó — la instantánea local (potencialmente vieja) se
      // conserva tal cual; refrescar es oportunista, nunca borra lo que ya había. Solo un
      // `ErrorDeRed` real (el fetch nunca llegó a un servidor) corrige `enLinea` a `false` — un
      // `ErrorApi` (el servidor respondió, aunque sea con un rechazo) significa que SÍ hay señal.
      if (e instanceof ErrorDeRed) setEnLinea(false)
      return false
    }
  }

  async function reponerBloqueSiNecesario(idPv: number): Promise<void> {
    if (!necesitaReponerBloque(bloqueRef.current)) return
    try {
      const reservado = await clienteDeVentas.reservarNumeracion({
        idPuntoVenta: idPv,
        codigoTipoComprobante: CODIGO_TIPO_COMPROBANTE_OFFLINE,
        cantidad: CANTIDAD_A_RESERVAR,
      })
      const bloqueNuevo: BloqueDeNumeracionLocal = {
        idPuntoVenta: reservado.idPuntoVenta,
        codigoTipoComprobante: reservado.codigoTipoComprobante,
        desde: reservado.desde,
        hasta: reservado.hasta,
        proximo: reservado.desde,
      }
      bloqueRef.current = bloqueNuevo
      // Persistido de inmediato — mismo criterio que `encolarVentaOffline`: si la app se
      // reinicia entre reponer el bloque y usarlo, el rango reservado tiene que sobrevivir
      // (goal C, "reponer mientras todavía hay señal"), no perderse en un `ref` en memoria.
      await guardarBloque(almacenRef.current, bloqueNuevo)
    } catch {
      // Sin señal (o el servidor rechazó) — se reintenta en el próximo ciclo; el bloque anterior
      // (si queda algo) sigue disponible tal cual estaba.
    }
  }

  async function ciclo(idPv: number): Promise<void> {
    await encolarOperacion(() => drenarOutbox())
    const conSenal = await refrescarInstantaneaSiHaySenal()
    if (conSenal) {
      await encolarOperacion(() => reponerBloqueSiNecesario(idPv))
    }
  }

  // Carga inicial: instantánea + outbox + bloque YA persistidos, sin esperar ningún fetch — la
  // pantalla tiene que mostrar datos utilizables inmediatamente después de un restart (goal A),
  // incluso si el primer ciclo de red todavía no corrió (o nunca llega a tener señal).
  useEffect(() => {
    if (!activo || idPuntoVenta === null) {
      setInstantanea(null)
      setOutboxCount(0)
      setVentasConError([])
      setEnLinea(typeof navigator === 'undefined' || navigator.onLine)
      bloqueRef.current = null
      return
    }

    let vigente = true

    Promise.all([
      leerInstantaneaLocal(almacenRef.current),
      leerOutbox(almacenRef.current),
      leerBloque(almacenRef.current),
      leerRechazadas(almacenRef.current),
    ]).then(([instantaneaGuardada, outboxGuardado, bloqueGuardado, rechazadasGuardadas]) => {
      if (!vigente) return
      setInstantanea(instantaneaGuardada)
      setOutboxCount(outboxGuardado.length)
      bloqueRef.current = bloqueGuardado
      setVentasConError(rechazadasGuardadas)
    })

    return () => {
      vigente = false
    }
  }, [activo, idPuntoVenta])

  // Ciclo oportunista: al montar, periódico, y apenas el navegador reporta que volvió la señal
  // (backstop además del intervalo — el evento `online` no es 100% confiable en todo entorno,
  // así que nunca es el ÚNICO disparador).
  useEffect(() => {
    if (!activo || idPuntoVenta === null) return

    let cancelado = false
    const ejecutar = () => {
      if (cancelado) return
      void ciclo(idPuntoVenta)
    }

    ejecutar()
    const idIntervalo = setInterval(ejecutar, intervaloMs)
    window.addEventListener('online', ejecutar)

    return () => {
      cancelado = true
      clearInterval(idIntervalo)
      window.removeEventListener('online', ejecutar)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activo, idPuntoVenta, intervaloMs])

  async function encolarVentaOffline(paramsVenta: {
    solicitudBase: SolicitudDeVenta
    esConsumidorFinal: boolean
    pagos: readonly { comportamiento: ComportamientoMedioPago }[]
    instantaneaCongelada?: InstantaneaDePos | null
  }): Promise<EncoladoOffline> {
    return encolarOperacion(async () => {
      // judgment-day ronda 1 (WARNING): usa la instantánea CONGELADA que el llamador pasa (la
      // misma que ya resolvió la vista previa en pantalla) cuando la pasa — nunca el estado
      // `instantanea` de este hook, que puede haberse refrescado en segundo plano DESPUÉS de esa
      // vista previa. Sin congelada explícita (p. ej. los tests de este hook, sin `Pos.tsx` de
      // por medio), cae al estado interno de siempre.
      const instantaneaActual = paramsVenta.instantaneaCongelada !== undefined ? paramsVenta.instantaneaCongelada : instantanea
      const lineasBase = paramsVenta.solicitudBase.lineas ?? []
      // judgment-day ronda 1 (SUGGESTION): único gate de la precondición "todas las líneas tienen
      // precio" — antes reimplementado inline acá abajo (`lineasEnriquecidas !== null && ...`),
      // ahora delegado a `todasLasLineasTienenPrecioOffline` para que no puedan divergir.
      const todasConPrecio = instantaneaActual !== null && todasLasLineasTienenPrecioOffline(lineasBase, instantaneaActual)
      const lineasEnriquecidas = todasConPrecio && instantaneaActual ? enriquecerLineasConPrecioOffline(lineasBase, instantaneaActual) : null

      const motivo = admisibilidadDeVentaOffline({
        esConsumidorFinal: paramsVenta.esConsumidorFinal,
        pagos: paramsVenta.pagos,
        hayInstantanea: instantaneaActual !== null,
        todasLasLineasConPrecio: todasConPrecio,
        hayNumeroDisponible: numerosDisponibles(bloqueRef.current) > 0,
      })

      if (motivo) return { ok: false, motivo }

      // Ya validado arriba: lineasEnriquecidas y el bloque no son null/vacíos.
      const tomado = tomarProximoNumero(bloqueRef.current)
      if (!tomado) return { ok: false, motivo: 'sin_numeracion' }

      bloqueRef.current = tomado.bloqueRestante
      await guardarBloque(almacenRef.current, tomado.bloqueRestante)

      const solicitudFinal: SolicitudDeVenta = {
        ...paramsVenta.solicitudBase,
        lineas: lineasEnriquecidas ?? lineasBase,
        numeroPreasignado: tomado.numero,
      }

      // judgment-day ronda 1 (BLOCKER): NUNCA se asume que la venta quedó guardada solo porque
      // `agregarAOutbox` no rechazó — esa función misma verifica (releyendo) que la venta nueva
      // está de verdad en el outbox, y tira `ErrorDePersistenciaOffline` si no puede confirmarlo.
      // Ese fallo tiene que llegar al cajero ANTES de imprimir/mostrar ningún ticket: el llamador
      // (`Pos.tsx`) recién construye el comprobante sintético cuando esta función devuelve
      // `ok: true`, nunca antes.
      let nuevoOutbox: VentaEnCola[]
      try {
        nuevoOutbox = await agregarAOutbox(almacenRef.current, {
          idLocal: generarIdLocal(),
          numeroPreasignado: tomado.numero,
          idPuntoVenta: paramsVenta.solicitudBase.idPuntoVenta,
          creadoEn: new Date().toISOString(),
          solicitud: solicitudFinal,
        })
      } catch (e) {
        if (!(e instanceof ErrorDePersistenciaOffline)) throw e
        return { ok: false, motivo: 'error_al_guardar' }
      }
      setOutboxCount(nuevoOutbox.length)

      return { ok: true, numeroVisible: construirNumeroVisible(paramsVenta.solicitudBase.idPuntoVenta, tomado.numero), numero: tomado.numero }
    })
  }

  return { instantanea, outboxCount, ventasConError, enLinea, encolarVentaOffline }
}
