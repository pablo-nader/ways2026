/**
 * Orquestación de la venta offline del POS de escritorio (stage-pos-venta-offline-web, Parte A/C):
 * carga la instantánea persistida apenas monta (sobrevive un restart, goal A), y corre un ciclo
 * — al montar, cada `intervaloMs`, en el evento `online` del navegador y a pedido del cajero
 * (`sincronizarAhora`) — que (1)
 * drena el outbox EN ORDEN, (2) refresca la instantánea si hay señal, y (3) repone el bloque de
 * numeración si está bajo, y (4) rinde el estado de la cola local al servidor para que el cierre
 * de turno pueda verificarlo, todo en un único lugar para que las tareas nunca corran
 * entrelazadas entre sí (ver `encolarOperacion`, más abajo). El módulo de negocio puro
 * (`instantaneaOffline.ts`/`outboxOffline.ts`/`reglasOffline.ts`) no sabe nada de React ni de
 * timers — este hook es la única pieza con estado/efectos.
 */
import { useEffect, useRef, useState } from 'react'
import type { AlmacenClaveValor } from './almacenPos'
import { crearAlmacenIndexedDb } from './almacenPos'
import { guardarInstantaneaLocal, leerInstantaneaLocal, preciosVigentesOffline, todasLasLineasTienenPrecioOffline } from './instantaneaOffline'
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
import { INTERVALO_POR_DEFECTO_MINUTOS, minutosAMilisegundos } from './intervaloDeSincronizacion'
import { clienteDePos } from '../api/pos'
import { clienteDeVentas } from '../api/ventas'
import { ErrorApi, ErrorDeRed } from '../api/cliente'
import type { ComportamientoMedioPago, InstantaneaDePos, LineaDeVenta, SolicitudDeVenta } from '../api/tipos'

/** Tope de espera del ciclo de arranque antes de habilitar la venta: con una red que cuelga, la
 * venta se habilita igual con la instantánea que haya y el ciclo sigue en segundo plano. */
export const LIMITE_DE_SINCRONIZACION_INICIAL_MS = 15_000

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
  /** Período del ciclo automático — default `INTERVALO_POR_DEFECTO_MINUTOS`; `Pos.tsx` pasa el
   * configurado por el cajero (`intervaloDeSincronizacion.ts`). */
  intervaloMs?: number
}

export type EstadoDeColaLocal = { pendientes: number; conError: number }

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
  /** `true` mientras corre un ciclo completo (automático, de arranque o manual). */
  sincronizando: boolean
  /** `true` desde que el hook se activa hasta que termina el primer ciclo (o vence
   * `LIMITE_DE_SINCRONIZACION_INICIAL_MS`) — `Pos.tsx` no habilita "Cobrar" antes, para no vender
   * con una instantánea vieja cuando hay red para refrescarla. Sin red el ciclo termina enseguida
   * y la venta se habilita con la instantánea que haya. */
  sincronizacionInicialPendiente: boolean
  /** Ciclo completo a pedido (drenar + refrescar la instantánea + reponer + rendir). Un pedido
   * mientras otro ciclo corre devuelve ese mismo ciclo: nunca corren dos a la vez. */
  sincronizarAhora: () => Promise<void>
  /** Drena el outbox, repone el bloque si el drenado confirmó señal y rinde la cola — sin
   * descargar la instantánea. Devuelve lo que queda pendiente según el almacén. */
  drenarAhora: () => Promise<EstadoDeColaLocal>
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

/** Enriquece cada línea con el precio congelado de la instantánea, en el tramo que corresponde a
 * SU cantidad (`preciosVigentesOffline` — la misma función que resuelve la vista previa en
 * pantalla, para que lo mostrado y lo cobrado no puedan diferir: este payload es el que el
 * servidor cobra literal). `null` si CUALQUIER línea no tiene artículo en la instantánea. Defensa
 * en profundidad únicamente: el gate real (todas o ninguna) es
 * `todasLasLineasTienenPrecioOffline`, ya evaluado por el llamador ANTES de invocar esto
 * (judgment-day ronda 1, SUGGESTION) — este `if (!articulo) return null` nunca debería disparar en
 * la práctica, mismo criterio que `comprobanteOfflineSintetico.ts`. */
function enriquecerLineasConPrecioOffline(lineas: LineaDeVenta[], instantanea: InstantaneaDePos): LineaDeVenta[] | null {
  const porId = new Map(instantanea.articulos.map((a) => [a.idArticulo, a]))
  const enriquecidas: LineaDeVenta[] = []
  for (const linea of lineas) {
    const articulo = porId.get(linea.idArticulo)
    if (!articulo) return null
    const precios = preciosVigentesOffline(articulo, linea.cantidad)
    // El backend trata `precioUnitario` como precio de LISTA (bruto) y resta `descuentoUnitario`
    // de nuevo (`ServicioDeVentas.MaterializarItems` → `CalculadorDeTotales.Calcular`) — el mismo
    // contrato que el camino online (`PrecioOriginal`/`DescuentoUnitario`). Mandar `precioFinal`
    // (ya neto) restaba el descuento DOS VECES y sub-registraba toda venta offline con oferta.
    // Por eso el tramo solo aporta `descuentoUnitario`: `precioOriginal` es el bruto y no varía
    // con la cantidad.
    enriquecidas.push({ ...linea, precioUnitario: precios.precioOriginal, descuentoUnitario: precios.descuentoUnitario })
  }
  return enriquecidas
}

export function useSincronizacionOffline(params: ParametrosDeSincronizacionOffline): ResultadoDeSincronizacionOffline {
  const { idPuntoVenta, activo, intervaloMs = minutosAMilisegundos(INTERVALO_POR_DEFECTO_MINUTOS) } = params

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

  /** Una pasada de drenado, EN ORDEN. El `emitir` de cada venta corre FUERA de `encolarOperacion`:
   * con una red lenta, retener el candado durante la request bloquearía el encolado de las ventas
   * nuevas del cajero hasta que el servidor responda. Solo la lectura de la cabeza y la salida (o
   * el archivado) de la venta van bajo el candado; nadie más que esta pasada saca ventas del
   * outbox (`drenarOutbox` la mantiene única), así que la cabeza leída no cambia mientras tanto. */
  async function drenarUnaPasada(): Promise<void> {
    for (;;) {
      const primera = await encolarOperacion(async () => (await leerOutbox(almacenRef.current))[0] ?? null)
      if (!primera) return

      try {
        await clienteDeVentas.emitir(primera.solicitud)
      } catch (e) {
        const archivada = await encolarOperacion(() => archivarRechazoSiEsPermanente(primera, e))
        if (!archivada) return
        continue
      }

      // La venta ya llegó al servidor: si su salida del outbox no se puede confirmar, se corta la
      // pasada y la próxima la reenvía con el mismo número y contenido, que el servidor reconoce
      // como la misma venta (`ExigirMismoContenido`) — nunca se archiva como rechazada.
      const quitada = await encolarOperacion(async () => {
        try {
          const outbox = await quitarDeOutbox(almacenRef.current, primera.idLocal)
          setOutboxCount(outbox.length)
          return true
        } catch {
          return false
        }
      })
      if (!quitada) return
    }
  }

  // Drenado único: un pedido que llega con otra pasada en curso no arranca una segunda (enviarían
  // la misma cabeza dos veces), pero deja marcada otra vuelta para que una venta encolada justo
  // cuando la pasada en curso terminaba no espere al próximo ciclo.
  const drenadoEnCursoRef = useRef<Promise<void> | null>(null)
  const otraVueltaDeDrenadoRef = useRef(false)
  function drenarOutbox(): Promise<void> {
    if (drenadoEnCursoRef.current) {
      otraVueltaDeDrenadoRef.current = true
      return drenadoEnCursoRef.current
    }
    const drenado = (async () => {
      try {
        do {
          otraVueltaDeDrenadoRef.current = false
          await drenarUnaPasada()
        } while (otraVueltaDeDrenadoRef.current)
      } finally {
        drenadoEnCursoRef.current = null
      }
    })()
    drenadoEnCursoRef.current = drenado
    return drenado
  }

  /** `true` si la venta se archivó como rechazada y salió del outbox (la pasada puede seguir con
   * la próxima); `false` si el error es transitorio o el archivado no se pudo confirmar (la pasada
   * se corta y el próximo ciclo reintenta desde el mismo punto). */
  async function archivarRechazoSiEsPermanente(primera: VentaEnCola, e: unknown): Promise<boolean> {
    // judgment-day ronda 2 (CRITICAL — regresión de la ronda 1): un 5xx (`ManejadorDeErrores.
    // RespuestaDeFalloTransitorio`, típicamente `resultado_incierto`) significa "no se pudo
    // confirmar si la escritura llegó a pasar", NUNCA un rechazo — la venta puede estar YA
    // comprometida en el servidor, y reenviar el MISMO `numeroPreasignado` + contenido es el
    // camino de recuperación seguro (`ServicioDeVentas.BuscarPorNumeroComprometidoAsync` +
    // `ExigirMismoContenido` dedupean por eso). Mismo criterio que `ErrorDeRed`: sigue sin señal
    // clara, se reintenta TODO en el próximo ciclo, nunca se descarta ni se saca nada del outbox.
    if (e instanceof ErrorDeRed || (e instanceof ErrorApi && e.estado >= 500)) {
      return false
    }
    // Rechazo REAL y PERMANENTE del servidor sobre el ítem más viejo — un 4xx real (ej.
    // `numero_preasignado_con_otro_contenido`, `turno_no_abierto`, una validación) — nunca se
    // descarta (la venta es real, con ticket ya entregado): se archiva como "necesita atención"
    // con su error real y se saca del outbox para que el drenado pueda seguir con el resto de la
    // cola, en vez de quedar rehén de un solo ítem trabado para siempre (judgment-day ronda 1,
    // CRITICAL).
    const mensaje =
      e instanceof Error ? `La venta ${primera.numeroPreasignado} no se pudo sincronizar: ${e.message}` : 'Una venta encolada no se pudo sincronizar.'
    try {
      await agregarARechazada(almacenRef.current, { ...primera, mensaje })
    } catch {
      // No se pudo archivar de forma durable como rechazada — se deja el ítem en el outbox (nunca
      // se saca sin confirmar dónde queda); el próximo ciclo reintenta desde el mismo punto.
      return false
    }
    let outbox: VentaEnCola[]
    try {
      outbox = await quitarDeOutbox(almacenRef.current, primera.idLocal)
    } catch {
      // judgment-day ronda 2 (WARNING): se archivó como rechazada (confirmado arriba), pero la
      // extracción del outbox no se pudo confirmar — el ítem queda temporalmente en AMBOS stores.
      // `agregarARechazada` es idempotente por `idLocal` (ver `outboxOffline.ts`), así que el
      // próximo ciclo reintenta `quitarDeOutbox` sin duplicar el archivo, y converge a un solo
      // store.
      return false
    }
    setOutboxCount(outbox.length)
    setVentasConError(await leerRechazadas(almacenRef.current))
    return true
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

  /**
   * Rinde la cola local al servidor (`POST /api/pos/rendicion-de-cola`) para que el cierre de
   * turno pueda verificarla contra `comprobantes_venta` en vez de creerle al almacén local de UNA
   * máquina (`ReglaDeRendicionDeCola`): `entregadoHasta` es el número más alto que este
   * dispositivo ya le imprimió a un cliente y `pendientes` cuántas de esas ventas todavía no
   * llegaron (outbox + rechazadas — una venta rechazada también tiene su ticket entregado).
   */
  async function rendirColaLocal(): Promise<void> {
    // Los conteos salen del almacén y no del estado de React: este ciclo vive en un closure del
    // efecto del intervalo, que leería el snapshot del render en que se creó.
    const declaracion = await encolarOperacion(async () => {
      const bloque = bloqueRef.current
      // Sin bloque local no hay nada que rendir: un dispositivo que perdió su almacén no puede dar
      // fe de su cola, y el servidor bloquea el cierre por sí mismo ante un reporte ausente.
      if (bloque === null) return null
      const [outbox, rechazadas] = await Promise.all([leerOutbox(almacenRef.current), leerRechazadas(almacenRef.current)])
      return {
        codigoTipoComprobante: bloque.codigoTipoComprobante,
        entregadoHasta: bloque.proximo - 1,
        pendientes: outbox.length + rechazadas.length,
      }
    })

    if (declaracion === null) return

    try {
      await clienteDePos.rendirCola(declaracion)
    } catch {
      // Sin señal (o el servidor rechazó) — se reintenta en el próximo ciclo. Un reporte que no
      // llega deja al cierre bloqueado del lado del servidor, que es el desenlace correcto.
    }
  }

  // Ciclo único: el automático, el de arranque, el del evento `online` y el manual comparten esta
  // misma promesa mientras corre — nunca dos ciclos superpuestos pidiendo la instantánea o
  // reponiendo el bloque a la vez.
  const cicloEnCursoRef = useRef<Promise<void> | null>(null)
  const [sincronizando, setSincronizando] = useState(false)

  function ciclo(idPv: number): Promise<void> {
    if (cicloEnCursoRef.current) return cicloEnCursoRef.current
    setSincronizando(true)
    const enCurso = (async () => {
      try {
        await drenarOutbox()
        const conSenal = await refrescarInstantaneaSiHaySenal()
        if (conSenal) {
          await encolarOperacion(() => reponerBloqueSiNecesario(idPv))
        }
        // Último paso del ciclo: lo que se declara tiene que reflejar el outbox YA drenado y el
        // bloque YA repuesto (el servidor rinde contra el bloque vivo, y reponer abandona el
        // anterior).
        await rendirColaLocal()
      } finally {
        cicloEnCursoRef.current = null
        setSincronizando(false)
      }
    })()
    cicloEnCursoRef.current = enCurso
    return enCurso
  }

  async function sincronizarAhora(): Promise<void> {
    if (!activo || idPuntoVenta === null) return
    await ciclo(idPuntoVenta)
  }

  async function leerEstadoDeColaLocal(): Promise<EstadoDeColaLocal> {
    return encolarOperacion(async () => {
      const [outbox, rechazadas] = await Promise.all([leerOutbox(almacenRef.current), leerRechazadas(almacenRef.current)])
      setOutboxCount(outbox.length)
      setVentasConError(rechazadas)
      return { pendientes: outbox.length, conError: rechazadas.length }
    })
  }

  async function drenarAhora(): Promise<EstadoDeColaLocal> {
    if (!activo || idPuntoVenta === null) return leerEstadoDeColaLocal()
    await drenarOutbox()
    const despuesDelDrenado = await leerEstadoDeColaLocal()
    // Un outbox que quedó vacío después de drenar ventas confirma señal: es el momento de reponer
    // el bloque si está bajo, en vez de esperar al próximo ciclo con la venta local consumiendo un
    // número por cada cobro.
    if (despuesDelDrenado.pendientes === 0) {
      await encolarOperacion(() => reponerBloqueSiNecesario(idPuntoVenta))
    }
    await rendirColaLocal()
    return despuesDelDrenado
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
      // Si el ciclo de arranque ya trajo una instantánea fresca, la persistida (más vieja) no la
      // pisa.
      setInstantanea((actual) => actual ?? instantaneaGuardada)
      setOutboxCount(outboxGuardado.length)
      bloqueRef.current = bloqueRef.current ?? bloqueGuardado
      setVentasConError(rechazadasGuardadas)
    })

    return () => {
      vigente = false
    }
  }, [activo, idPuntoVenta])

  // Ciclo de arranque: corre una vez al activarse y, hasta que termina (o vence el tope), la
  // pantalla no habilita la venta.
  const [sincronizacionInicialPendiente, setSincronizacionInicialPendiente] = useState(activo && idPuntoVenta !== null)
  useEffect(() => {
    if (!activo || idPuntoVenta === null) {
      setSincronizacionInicialPendiente(false)
      return
    }

    let vigente = true
    setSincronizacionInicialPendiente(true)
    const liberar = () => {
      if (vigente) setSincronizacionInicialPendiente(false)
    }
    const idTope = setTimeout(liberar, LIMITE_DE_SINCRONIZACION_INICIAL_MS)
    ciclo(idPuntoVenta)
      .catch(() => undefined)
      .finally(() => {
        clearTimeout(idTope)
        liberar()
      })

    return () => {
      vigente = false
      clearTimeout(idTope)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activo, idPuntoVenta])

  // Ciclo periódico y apenas el navegador reporta que volvió la señal (backstop además del
  // intervalo — el evento `online` no es 100% confiable en todo entorno, así que nunca es el
  // ÚNICO disparador). Cambiar el intervalo solo reprograma el timer, no dispara un ciclo.
  useEffect(() => {
    if (!activo || idPuntoVenta === null) return

    let cancelado = false
    const ejecutar = () => {
      if (cancelado) return
      void ciclo(idPuntoVenta)
    }

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

  return {
    instantanea,
    outboxCount,
    ventasConError,
    enLinea,
    sincronizando,
    sincronizacionInicialPendiente,
    sincronizarAhora,
    drenarAhora,
    encolarVentaOffline,
    reintentarVentaConError,
    descartarVentaConError,
  }
}
