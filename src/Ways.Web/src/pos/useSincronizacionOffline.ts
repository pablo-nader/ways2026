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
import {
  epocaDeSesionLocalActual,
  esInstantaneaValida,
  guardarInstantaneaLocal,
  leerInstantaneaLocal,
  precioEnLista,
  preciosVigentesOffline,
  todasLasLineasTienenPrecioOffline,
  type InstantaneaLocal,
} from './instantaneaOffline'
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
import { conTiempoLimite, ErrorDeTiempoAgotado } from './tiempoLimite'
import { clienteDePos } from '../api/pos'
import { clienteDeVentas } from '../api/ventas'
import { ErrorApi, ErrorDeRed } from '../api/cliente'
import type { ComportamientoMedioPago, InstantaneaDePos, LineaDeVenta, SolicitudDeRendicionDeCola, SolicitudDeVenta } from '../api/tipos'

/** Tope de espera del ciclo de arranque antes de habilitar la venta: con una red que cuelga, la
 * venta se habilita igual con la instantánea que haya y el ciclo sigue en segundo plano. */
export const LIMITE_DE_SINCRONIZACION_INICIAL_MS = 15_000

/** Con una instantánea local utilizable, el arranque no retiene la venta más que esto: con una red
 * lenta se vende con la copia local y el ciclo sigue en segundo plano. */
export const LIMITE_DE_SINCRONIZACION_INICIAL_CON_COPIA_LOCAL_MS = 3_000

/** La guarda de cierre de turno del servidor rechaza un reporte de rendición de más de 5 minutos
 * (`ReglaDeRendicionDeCola.VentanaDeFrescura`) sobre el bloque vivo. El intervalo de sincronización
 * lo configura el cajero hasta 60 minutos, así que la rendición tiene su propio período, fijo y
 * por debajo de esa ventana: sin esto, un cierre desde otra máquina quedaría bloqueado por un
 * reporte vencido de este dispositivo. Es un POST chico, y sin bloque local no sale nada. */
export const INTERVALO_DE_RENDICION_MS = 2 * 60_000

/** Tamaño de bloque a reponer: el tope del servidor (`CantidadMaxima` = 500). Con la venta local
 * cada cobro del Consumidor Final consume un número del bloque, y cada reposición descarta lo que le
 * quedaba al anterior (menos de `UMBRAL_DE_REPOSICION` = 20, ver `reponerBloque`): con 500 ese
 * hueco queda por debajo del 4% de los números reservados. El costo es el hueco de un dispositivo
 * que deja de sincronizar para siempre (hasta 500 números), aceptado igual que el resto de los
 * huecos de numeración del sistema (doc 10 §4). */
const CANTIDAD_A_RESERVAR = 500

const CODIGO_TIPO_COMPROBANTE_OFFLINE = 'TX'

/** Tope de la descarga de la instantánea: el catálogo completo, sin paginar, pesa cientos de KB
 * (`ServicioDeInstantaneaDePos`). Sin tope, una descarga colgada retiene el ciclo único para
 * siempre — sin drenado periódico ni "Sincronizar ahora". La request no se aborta: solo se deja de
 * esperarla. */
const TIEMPO_LIMITE_DE_INSTANTANEA_MS = 30_000

/** Tope de la reserva de numeración. Más largo que el de las demás requests a propósito: corre en
 * segundo plano (el cajero no la espera mientras el bloque en uso tenga números) y una reserva que
 * el servidor comprometió pero cuya respuesta se descartó deja un bloque vivo que este dispositivo
 * nunca conoce ni rinde — que después bloquea el cierre de turno hasta un forzado supervisado. */
const TIEMPO_LIMITE_DE_RESERVA_MS = 15_000

/** Rechazos de la rendición que dicen que el bloque local ya no es el vivo del servidor
 * (`ServicioDeRendicionDeCola`). */
const CODIGOS_DE_BLOQUE_YA_NO_VIVO = new Set(['rendicion_sin_bloque_vivo', 'rendicion_de_bloque_reemplazado'])

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
  /** `true` (default) bloquea la venta hasta que termina el ciclo de arranque
   * (`sincronizacionInicialPendiente`). `Pos.tsx` lo apaga fuera del POS de escritorio: la
   * instantánea es solo de dispositivo (`GET /api/pos/instantanea` exige `RequiereDispositivo`), así
   * que en la app web esperar ese ciclo no traería nada. El ciclo corre igual. */
  sincronizarAntesDeVender?: boolean
}

export type EstadoDeColaLocal = { pendientes: number; conError: number }

export type ResultadoDeSincronizacionOffline = {
  instantanea: InstantaneaDePos | null
  /** Hora local de la última verificación exitosa de `instantanea` contra el servidor (un `200` o un
   * `304`) — la que corresponde mostrar como vejez de los datos locales. `null` sin instantánea. */
  verificadaEn: string | null
  /** Cuántas veces el servidor respondió el pedido de la instantánea (un `200` o un `304`) desde que
   * montó el hook: cada incremento prueba que hay red. Cargar la copia local no lo mueve. */
  verificacionesConElServidor: number
  outboxCount: number
  /** Señal proactiva para la UI (deshabilitar cuenta corriente, avisar que sin conexión solo se
   * vende al Consumidor Final, buscar clientes solo en la copia local) ANTES de un intento, no solo
   * después de que falle — arranca en `navigator.onLine` (optimista:
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
   * `LIMITE_DE_SINCRONIZACION_INICIAL_MS`, o `LIMITE_DE_SINCRONIZACION_INICIAL_CON_COPIA_LOCAL_MS`
   * desde que aparece una instantánea local de este punto de venta) — `Pos.tsx` no habilita
   * "Cobrar" antes, para no vender con una instantánea vieja cuando hay red para refrescarla. Sin
   * red el ciclo termina enseguida y la venta se habilita con la instantánea que haya. */
  sincronizacionInicialPendiente: boolean
  /** Ciclo completo a pedido (drenar + refrescar la instantánea + reponer + rendir). Un pedido
   * mientras otro ciclo corre devuelve ese mismo ciclo: nunca corren dos a la vez. */
  sincronizarAhora: () => Promise<void>
  /** Drena el outbox, repone el bloque si está bajo (`reponerBloque`, solo con la cola vacía) y rinde la cola — sin
   * descargar la instantánea. Devuelve lo que queda pendiente según el almacén. */
  drenarAhora: () => Promise<EstadoDeColaLocal>
  encolarVentaOffline: (params: {
    solicitudBase: SolicitudDeVenta
    esConsumidorFinal: boolean
    /** Lista del cliente de la venta: cada línea se cobra con su precio en esta lista. */
    idListaPrecio: number
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

/** Enriquece cada línea con el precio congelado de la instantánea en la lista de la venta, en el
 * tramo que corresponde a SU cantidad (`preciosVigentesOffline` — la misma función que resuelve la
 * vista previa en pantalla, para que lo mostrado y lo cobrado no puedan diferir: este payload es el
 * que el servidor cobra literal). `null` si CUALQUIER línea no tiene precio en esa lista. Defensa
 * en profundidad únicamente: el gate real (todas o ninguna) es
 * `todasLasLineasTienenPrecioOffline`, ya evaluado por el llamador ANTES de invocar esto. */
function enriquecerLineasConPrecioOffline(lineas: LineaDeVenta[], instantanea: InstantaneaDePos, idListaPrecio: number): LineaDeVenta[] | null {
  const porId = new Map(instantanea.articulos.map((a) => [a.idArticulo, a]))
  const enriquecidas: LineaDeVenta[] = []
  for (const linea of lineas) {
    const articulo = porId.get(linea.idArticulo)
    const precio = articulo ? precioEnLista(articulo, idListaPrecio) : null
    if (!precio) return null
    const precios = preciosVigentesOffline(precio, linea.cantidad)
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
  const { idPuntoVenta, activo, intervaloMs = minutosAMilisegundos(INTERVALO_POR_DEFECTO_MINUTOS), sincronizarAntesDeVender = true } = params

  const almacenRef = useRef<AlmacenClaveValor>(params.almacen ?? crearAlmacenIndexedDb())
  const montadoRef = useRef(true)
  useEffect(() => {
    montadoRef.current = true
    return () => {
      montadoRef.current = false
    }
  }, [])

  const [instantanea, setInstantanea] = useState<InstantaneaDePos | null>(null)
  const [verificadaEn, setVerificadaEn] = useState<string | null>(null)
  const [verificacionesConElServidor, setVerificacionesConElServidor] = useState(0)
  // Última instantánea local conocida (persistida o recién descargada): de acá sale la etiqueta del
  // refresco condicional y lo que se vuelve a persistir con la hora nueva tras un `304`.
  const instantaneaLocalRef = useRef<InstantaneaLocal | null>(null)
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
        await conTiempoLimite((senal) => clienteDeVentas.emitir(primera.solicitud, senal))
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
    // Un tope vencido (`ErrorDeTiempoAgotado`) es el mismo caso: la venta pudo haber llegado.
    if (e instanceof ErrorDeRed || e instanceof ErrorDeTiempoAgotado || (e instanceof ErrorApi && e.estado >= 500)) {
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

  async function adoptarInstantaneaLocal(local: Omit<InstantaneaLocal, 'version'>, epocaAlPedir: number): Promise<void> {
    // Una respuesta que llega después de desmontar o después de terminar la sesión (que borra la
    // instantánea guardada y avanza la época) no vuelve a escribirla.
    if (!montadoRef.current || epocaDeSesionLocalActual() !== epocaAlPedir) return
    if (!montadoRef.current) return
    instantaneaLocalRef.current = { version: 2, ...local }
    setInstantanea(local.instantanea)
    setVerificadaEn(local.verificadaEn)
    await guardarInstantaneaLocal(almacenRef.current, local)
  }

  /** `true` si el servidor respondió (hay señal), haya o no contenido nuevo. Con la etiqueta de la
   * copia local, un contenido sin cambios vuelve como `304` y solo renueva `verificadaEn`. */
  async function refrescarInstantaneaSiHaySenal(): Promise<boolean> {
    try {
      const local = instantaneaLocalRef.current
      const epocaAlPedir = epocaDeSesionLocalActual()
      const respuesta = await conTiempoLimite(() => clienteDePos.obtenerInstantanea(local?.etag ?? null), TIEMPO_LIMITE_DE_INSTANTANEA_MS)
      const ahora = new Date().toISOString()
      setEnLinea(true)
      setVerificacionesConElServidor((n) => n + 1)
      if (!respuesta.modificada) {
        if (local !== null) await adoptarInstantaneaLocal({ instantanea: local.instantanea, etag: local.etag, verificadaEn: ahora }, epocaAlPedir)
        return true
      }
      // Un servidor anterior a los precios por lista ignora `?version=2` y responde la forma vieja:
      // se conserva la copia local en vez de cotizar con algo que no se puede leer.
      if (!esInstantaneaValida(respuesta.cuerpo)) return true
      await adoptarInstantaneaLocal({ instantanea: respuesta.cuerpo, etag: respuesta.etag, verificadaEn: ahora }, epocaAlPedir)
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

  /** Lo que la rendición declararía AHORA sobre el bloque vivo del servidor, leído del almacén
   * bajo el candado (este código vive en closures de efectos, que leerían un estado de React
   * viejo). `null` sin bloque: un dispositivo que perdió su almacén no puede dar fe de su cola, y el
   * servidor bloquea el cierre por sí mismo ante un reporte ausente. */
  function leerDeclaracionDeRendicion(): Promise<SolicitudDeRendicionDeCola | null> {
    return encolarOperacion(async () => {
      const bloque = bloqueRef.current
      if (bloque === null) return null
      const [outbox, rechazadas] = await Promise.all([leerOutbox(almacenRef.current), leerRechazadas(almacenRef.current)])
      return {
        codigoTipoComprobante: bloque.codigoTipoComprobante,
        entregadoHasta: bloque.proximo - 1,
        pendientes: outbox.length + rechazadas.length,
      }
    })
  }

  /**
   * Repone el bloque en segundo plano apenas baja del umbral — mucho antes de agotarse — y FUERA
   * del candado: una reserva lenta nunca demora el encolado de una venta mientras el bloque en uso
   * tenga números.
   *
   * El bloque nuevo reemplaza al que está en uso y los números que le quedaban (menos de
   * `UMBRAL_DE_REPOSICION`) quedan como hueco: el servidor admite un solo bloque vivo por
   * dispositivo (`ux_reservas_numeracion_dispositivo_activo`), reservar abandona el anterior, y doc
   * 10 fija que `abandonada_at` gobierna de qué bloque el dispositivo puede sacar números NUEVOS —
   * la guarda de cierre deja de exigirle frescura a un bloque abandonado porque supone que ya no
   * reparte. Los números que ya salieron de él se siguen aceptando al drenar.
   *
   * Reservar congela la última rendición del bloque abandonado (`ReglaDeRendicionDeCola`, parámetro
   * `bloqueVivo`): si declaraba ventas pendientes, bloquea todo cierre de turno hasta un forzado
   * supervisado. Por eso la reserva solo sigue después de rendir el bloque vivo con
   * `pendientes = 0`; con ventas en cola se reintenta en el próximo drenado.
   *
   * Única: un pedido con otra reposición en curso devuelve esa misma.
   */
  const reposicionEnCursoRef = useRef<Promise<void> | null>(null)
  function reponerBloque(idPv: number): Promise<void> {
    if (reposicionEnCursoRef.current) return reposicionEnCursoRef.current
    const reposicion = (async () => {
      try {
        if (!necesitaReponerBloque(bloqueRef.current)) return

        const declaracion = await leerDeclaracionDeRendicion()
        if (declaracion !== null) {
          if (declaracion.pendientes > 0) return
          try {
            await conTiempoLimite((senal) => clienteDePos.rendirCola(declaracion, senal))
          } catch (e) {
            // El servidor ya no tiene vivo el bloque local (una reserva anterior se comprometió
            // pero su respuesta nunca llegó): no hay nada que rendir sobre él y no reservar
            // dejaría al dispositivo sin números para siempre.
            if (!(e instanceof ErrorApi && CODIGOS_DE_BLOQUE_YA_NO_VIVO.has(e.codigo))) throw e
          }
        }

        const reservado = await conTiempoLimite(
          (senal) =>
            clienteDeVentas.reservarNumeracion(
              { idPuntoVenta: idPv, codigoTipoComprobante: CODIGO_TIPO_COMPROBANTE_OFFLINE, cantidad: CANTIDAD_A_RESERVAR },
              senal,
            ),
          TIEMPO_LIMITE_DE_RESERVA_MS,
        )
        const bloqueNuevo: BloqueDeNumeracionLocal = {
          idPuntoVenta: reservado.idPuntoVenta,
          codigoTipoComprobante: reservado.codigoTipoComprobante,
          desde: reservado.desde,
          hasta: reservado.hasta,
          proximo: reservado.desde,
        }
        // Persistido de inmediato y bajo el candado (serializado con el encolado, que mueve el mismo
        // bloque): si la app se reinicia antes de usarlo, el rango reservado sobrevive.
        await encolarOperacion(async () => {
          bloqueRef.current = bloqueNuevo
          await guardarBloque(almacenRef.current, bloqueNuevo)
        })
      } catch {
        // Sin señal, tope vencido o el servidor rechazó — se reintenta en el próximo drenado o
        // ciclo; el bloque en uso sigue disponible tal cual estaba.
      } finally {
        reposicionEnCursoRef.current = null
      }
    })()
    reposicionEnCursoRef.current = reposicion
    return reposicion
  }

  /**
   * Rinde la cola local al servidor (`POST /api/pos/rendicion-de-cola`) para que el cierre de
   * turno pueda verificarla contra `comprobantes_venta` en vez de creerle al almacén local de UNA
   * máquina (`ReglaDeRendicionDeCola`): `entregadoHasta` es el número más alto que este
   * dispositivo ya le imprimió a un cliente del bloque vivo y `pendientes` cuántas ventas todavía
   * no llegaron (outbox + rechazadas, de cualquier bloque — una venta rechazada también tiene su
   * ticket entregado).
   */
  async function rendirColaLocal(): Promise<void> {
    const declaracion = await leerDeclaracionDeRendicion()
    if (declaracion === null) return

    try {
      await conTiempoLimite((senal) => clienteDePos.rendirCola(declaracion, senal))
    } catch {
      // Sin señal, tope vencido o el servidor rechazó — se reintenta en el próximo ciclo. Un
      // reporte que no llega deja al cierre bloqueado del lado del servidor, que es el desenlace
      // correcto.
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
          await reponerBloque(idPv)
        }
        // Último paso del ciclo: lo que se declara tiene que reflejar el outbox YA drenado y el
        // bloque vivo YA rotado (el servidor rinde contra el bloque vivo).
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
    // Recién drenado es cuando el outbox está vacío y la reposición puede rotar el bloque sin
    // congelar ventas pendientes en el que abandona (ver `reponerBloque`).
    // Sin ningún bloque local (un dispositivo nuevo, o la app web) la primera reserva es del ciclo.
    if (bloqueRef.current !== null) {
      await reponerBloque(idPuntoVenta)
    }
    await rendirColaLocal()
    return despuesDelDrenado
  }

  // Lo instala el efecto del ciclo de arranque (más abajo) y lo llama la carga inicial al encontrar
  // una instantánea local de este punto de venta.
  const liberarArranqueConCopiaLocalRef = useRef<(() => void) | null>(null)

  // Carga inicial: instantánea + outbox + bloque YA persistidos, sin esperar ningún fetch — la
  // pantalla tiene que mostrar datos utilizables inmediatamente después de un restart (goal A),
  // incluso si el primer ciclo de red todavía no corrió (o nunca llega a tener señal).
  useEffect(() => {
    if (!activo || idPuntoVenta === null) {
      setInstantanea(null)
      setVerificadaEn(null)
      instantaneaLocalRef.current = null
      setOutboxCount(0)
      setVentasConError([])
      setEnLinea(typeof navigator === 'undefined' || navigator.onLine)
      bloqueRef.current = null
      return
    }

    let vigente = true
    // El almacén guarda un solo bloque por dispositivo: el de otro punto de venta (un cambio de
    // punto de venta) nunca se usa para numerar acá — queda descartado hasta reponer uno propio.
    bloqueRef.current = null

    Promise.all([
      leerInstantaneaLocal(almacenRef.current),
      leerOutbox(almacenRef.current),
      leerBloque(almacenRef.current),
      leerRechazadas(almacenRef.current),
    ]).then(([instantaneaGuardada, outboxGuardado, bloqueGuardado, rechazadasGuardadas]) => {
      if (!vigente) return
      // Si el ciclo de arranque ya trajo una instantánea fresca, la persistida (más vieja) no la
      // pisa.
      if (instantaneaGuardada !== null && instantaneaLocalRef.current === null) {
        instantaneaLocalRef.current = instantaneaGuardada
        setInstantanea(instantaneaGuardada.instantanea)
        setVerificadaEn(instantaneaGuardada.verificadaEn)
      }
      if (instantaneaGuardada?.instantanea.idPuntoVenta === idPuntoVenta) liberarArranqueConCopiaLocalRef.current?.()
      setOutboxCount(outboxGuardado.length)
      bloqueRef.current = bloqueRef.current ?? (bloqueGuardado?.idPuntoVenta === idPuntoVenta ? bloqueGuardado : null)
      setVentasConError(rechazadasGuardadas)
    })

    return () => {
      vigente = false
    }
  }, [activo, idPuntoVenta])

  // Ciclo de arranque: corre una vez al activarse y, hasta que termina (o vence el tope), la
  // pantalla no habilita la venta. Si aparece una instantánea local de este punto de venta, el
  // tope baja a `LIMITE_DE_SINCRONIZACION_INICIAL_CON_COPIA_LOCAL_MS` contado desde ese momento.
  const [sincronizacionInicialPendiente, setSincronizacionInicialPendiente] = useState(
    activo && idPuntoVenta !== null && sincronizarAntesDeVender,
  )
  useEffect(() => {
    if (!activo || idPuntoVenta === null) {
      setSincronizacionInicialPendiente(false)
      return
    }

    let vigente = true
    setSincronizacionInicialPendiente(sincronizarAntesDeVender)
    const liberar = () => {
      if (vigente) setSincronizacionInicialPendiente(false)
    }
    const idTope = setTimeout(liberar, LIMITE_DE_SINCRONIZACION_INICIAL_MS)
    let idTopeConCopiaLocal: ReturnType<typeof setTimeout> | undefined
    liberarArranqueConCopiaLocalRef.current = () => {
      if (idTopeConCopiaLocal === undefined) idTopeConCopiaLocal = setTimeout(liberar, LIMITE_DE_SINCRONIZACION_INICIAL_CON_COPIA_LOCAL_MS)
    }
    ciclo(idPuntoVenta)
      .catch(() => undefined)
      .finally(() => {
        clearTimeout(idTope)
        clearTimeout(idTopeConCopiaLocal)
        liberar()
      })

    return () => {
      vigente = false
      clearTimeout(idTope)
      clearTimeout(idTopeConCopiaLocal)
      liberarArranqueConCopiaLocalRef.current = null
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
    const idRendicion = setInterval(() => {
      if (!cancelado) void rendirColaLocal()
    }, INTERVALO_DE_RENDICION_MS)
    window.addEventListener('online', ejecutar)

    return () => {
      cancelado = true
      clearInterval(idIntervalo)
      clearInterval(idRendicion)
      window.removeEventListener('online', ejecutar)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activo, idPuntoVenta, intervaloMs])

  async function encolarVentaOffline(paramsVenta: {
    solicitudBase: SolicitudDeVenta
    esConsumidorFinal: boolean
    idListaPrecio: number
    pagos: readonly { comportamiento: ComportamientoMedioPago }[]
    instantaneaCongelada?: InstantaneaDePos | null
  }): Promise<EncoladoOffline> {
    // judgment-day ronda 1 (WARNING): usa la instantánea CONGELADA que el llamador pasa (la
    // misma que ya resolvió la vista previa en pantalla) cuando la pasa — nunca el estado
    // `instantanea` de este hook, que puede haberse refrescado en segundo plano DESPUÉS de esa
    // vista previa. Sin congelada explícita (p. ej. los tests de este hook, sin `Pos.tsx` de
    // por medio), cae al estado interno de siempre.
    const instantaneaActual = paramsVenta.instantaneaCongelada !== undefined ? paramsVenta.instantaneaCongelada : instantanea
    const lineasBase = paramsVenta.solicitudBase.lineas ?? []
    // judgment-day ronda 1 (SUGGESTION): único gate de la precondición "todas las líneas tienen
    // precio" — delegado a `todasLasLineasTienenPrecioOffline` para que no puedan divergir.
    const todasConPrecio = instantaneaActual !== null && todasLasLineasTienenPrecioOffline(lineasBase, instantaneaActual, paramsVenta.idListaPrecio)
    const lineasEnriquecidas = todasConPrecio && instantaneaActual ? enriquecerLineasConPrecioOffline(lineasBase, instantaneaActual, paramsVenta.idListaPrecio) : null
    const admisibilidad = (hayNumeroDisponible: boolean) =>
      admisibilidadDeVentaOffline({
        esConsumidorFinal: paramsVenta.esConsumidorFinal,
        pagos: paramsVenta.pagos,
        hayInstantanea: instantaneaActual !== null,
        todasLasLineasConPrecio: todasConPrecio,
        hayNumeroDisponible,
      })

    // Único caso en que el encolado espera a la red: el bloque agotado en una venta que,
    // salvo por eso, se admitiría. Espera como máximo `TIEMPO_LIMITE_DE_RED_MS` (la reserva sigue
    // en segundo plano); si no llega, la venta se rechaza con `sin_numeracion` y `Pos.tsx` cobra
    // por el camino online.
    // Defensa ante cualquier carrera (una reposición pedida para otro punto de venta que aterriza
    // después del cambio): un número de un bloque de otro punto de venta se rechaza al drenar, con
    // el ticket ya entregado.
    const bloqueDeLaVenta = () =>
      bloqueRef.current?.idPuntoVenta === paramsVenta.solicitudBase.idPuntoVenta ? bloqueRef.current : null
    const sinNumeros = numerosDisponibles(bloqueDeLaVenta()) === 0
    if (sinNumeros && idPuntoVenta !== null && admisibilidad(true) === null) {
      const idPv = idPuntoVenta
      await conTiempoLimite(() => reponerBloque(idPv)).catch(() => undefined)
    }

    return encolarOperacion(async () => {
      const motivo = admisibilidad(numerosDisponibles(bloqueDeLaVenta()) > 0)
      if (motivo) return { ok: false, motivo }

      // Ya validado arriba: lineasEnriquecidas y el bloque no son null/vacíos.
      const tomado = tomarProximoNumero(bloqueDeLaVenta())
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
    verificadaEn,
    verificacionesConElServidor,
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
