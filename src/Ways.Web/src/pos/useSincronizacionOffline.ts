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
  quitarDeRechazada,
  tomarProximoNumero,
  type BloqueDeNumeracionLocal,
  type MotivoRechazoOffline,
  type VentaEnCola,
  type VentaRechazada,
} from './outboxOffline'
import { clienteDePos } from '../api/pos'
import { clienteDeVentas } from '../api/ventas'
import { ErrorApi, ErrorDeRed } from '../api/cliente'
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
  /** Ventas que el servidor rechazó de forma PERMANENTE al drenar (un 4xx real — nunca un 5xx ni
   * un `ErrorDeRed`, ambos transitorios, ver `drenarOutbox`) — se sacaron del outbox para no
   * bloquear el drenado del resto de la cola, pero NUNCA se descartan en silencio: quedan acá,
   * visibles con su error real, hasta que el cajero las resuelva a mano con
   * `reintentarVentaConError` (reenvío idéntico — seguro por `numeroPreasignado` + contenido) o
   * `descartarVentaConError` (judgment-day ronda 2, WARNING — antes de este fix no existía
   * ninguna función para resolver una entrada de esta lista). Bloquea el cierre de turno igual
   * que `outboxCount` (ver `irACerrarCaja` en `Pos.tsx` y el gate de `CierreDeCaja.tsx`). `[]` sin
   * ninguna pendiente. */
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
  /** judgment-day ronda 2 (WARNING): la salida real que `ventasConError` prometía y no tenía —
   * re-encola la venta rechazada AL FINAL del outbox para que el próximo drenado la reintente. Es
   * seguro incluso si el rechazo original fue real y persiste (el servidor la va a rechazar otra
   * vez con el mismo error, sin duplicar nada — `ExigirMismoContenido` dedupea por
   * `numeroPreasignado` + contenido). Idempotente por `idLocal`: si ya no está en `ventasConError`
   * (resuelta por otro reintento/otra pestaña), es un no-op que resuelve `true`. `false` si no se
   * pudo persistir el movimiento de forma durable — la venta queda intacta en `ventasConError`,
   * nunca se pierde. */
  reintentarVentaConError: (idLocal: string) => Promise<boolean>
  /** judgment-day ronda 2 (WARNING): la otra mitad de la salida real — descarta definitivamente
   * una venta que nunca va a sincronizar (un rechazo real que un reintento no va a curar). Nunca
   * se llama sin que el cajero haya confirmado explícitamente qué se descarta (la puerta de
   * confirmación vive en `Pos.tsx`, esta función no pregunta nada por su cuenta). `false` si no se
   * pudo persistir la baja de forma durable — la venta sigue visible en `ventasConError`. */
  descartarVentaConError: (idLocal: string) => Promise<boolean>
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
        // judgment-day ronda 2 (CRITICAL — regresión de la ronda 1): un 5xx (`ManejadorDeErrores.
        // RespuestaDeFalloTransitorio`, típicamente `resultado_incierto`) significa "no se pudo
        // confirmar si la escritura llegó a pasar", NUNCA un rechazo — la venta puede estar YA
        // comprometida en el servidor, y reenviar el MISMO `numeroPreasignado` + contenido es el
        // camino de recuperación seguro (`ServicioDeVentas.BuscarPorNumeroComprometidoAsync` +
        // `ExigirMismoContenido` dedupean por eso). Tratarlo como rechazo permanente (como hacía
        // esta rama antes de este fix) sacaba del outbox una venta real y ya ticketeada sin
        // ninguna vía de recuperación. Mismo criterio que `ErrorDeRed`: sigue sin señal clara, se
        // reintenta TODO en el próximo ciclo, nunca se descarta ni se saca nada del outbox.
        if (e instanceof ErrorDeRed || (e instanceof ErrorApi && e.estado >= 500)) {
          return
        }
        // Rechazo REAL y PERMANENTE del servidor sobre el ítem más viejo — un 4xx real (ej.
        // `numero_preasignado_con_otro_contenido`, `turno_no_abierto`, una validación) — nunca se
        // descarta (la venta es real, con ticket ya entregado): se archiva como "necesita
        // atención" con su error real y se saca del outbox para que el drenado pueda seguir con
        // el resto de la cola, en vez de quedar rehén de un solo ítem trabado para siempre
        // (judgment-day ronda 1, CRITICAL).
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
        try {
          outbox = await quitarDeOutbox(almacenRef.current, primera.idLocal)
        } catch {
          // judgment-day ronda 2 (WARNING): se archivó como rechazada (confirmado arriba), pero
          // la extracción del outbox no se pudo confirmar — el ítem queda temporalmente en AMBOS
          // stores. Se corta esta pasada sin tocar el estado de React todavía: `agregarARechazada`
          // es idempotente por `idLocal` (ver `outboxOffline.ts`), así que el próximo ciclo
          // reintenta `quitarDeOutbox` sin duplicar el archivo, y converge a un solo store.
          return
        }
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

  /** judgment-day ronda 2 (WARNING): ver el doc-comment de `reintentarVentaConError` en
   * `ResultadoDeSincronizacionOffline` — encolada bajo `encolarOperacion` para serializarse con
   * el drenado y con `encolarVentaOffline`, mismo criterio que el resto de las mutaciones del
   * outbox/rechazadas de este hook. */
  async function reintentarVentaConError(idLocal: string): Promise<boolean> {
    return encolarOperacion(async () => {
      const rechazadas = await leerRechazadas(almacenRef.current)
      const rechazada = rechazadas.find((v) => v.idLocal === idLocal)
      if (!rechazada) return true // ya no está — no-op idempotente (resuelta por otra vía).

      const outboxActual = await leerOutbox(almacenRef.current)
      if (!outboxActual.some((v) => v.idLocal === idLocal)) {
        const ventaEnCola: VentaEnCola = {
          idLocal: rechazada.idLocal,
          numeroPreasignado: rechazada.numeroPreasignado,
          idPuntoVenta: rechazada.idPuntoVenta,
          creadoEn: rechazada.creadoEn,
          solicitud: rechazada.solicitud,
        }
        try {
          await agregarAOutbox(almacenRef.current, ventaEnCola)
        } catch {
          // No se pudo re-encolar de forma durable — la venta sigue intacta en `ventasConError`,
          // nunca se pierde; el cajero puede reintentar de nuevo.
          return false
        }
      }

      try {
        await quitarDeRechazada(almacenRef.current, idLocal)
      } catch {
        // Ya está en el outbox (confirmado arriba) pero la salida de `ventasConError` no se pudo
        // confirmar — queda temporalmente en AMBOS stores. La próxima llamada (idempotente arriba,
        // el `idLocal` ya está en el outbox) solo reintenta esta salida, sin duplicar nada.
        setOutboxCount((await leerOutbox(almacenRef.current)).length)
        return false
      }

      setOutboxCount((await leerOutbox(almacenRef.current)).length)
      setVentasConError(await leerRechazadas(almacenRef.current))
      return true
    })
  }

  /** judgment-day ronda 2 (WARNING): ver el doc-comment de `descartarVentaConError` en
   * `ResultadoDeSincronizacionOffline`. */
  async function descartarVentaConError(idLocal: string): Promise<boolean> {
    return encolarOperacion(async () => {
      try {
        setVentasConError(await quitarDeRechazada(almacenRef.current, idLocal))
        return true
      } catch {
        return false
      }
    })
  }

  return { instantanea, outboxCount, ventasConError, enLinea, encolarVentaOffline, reintentarVentaConError, descartarVentaConError }
}
