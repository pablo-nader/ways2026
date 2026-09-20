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
import { guardarInstantaneaLocal, leerInstantaneaLocal } from './instantaneaOffline'
import {
  admisibilidadDeVentaOffline,
  agregarAOutbox,
  construirNumeroVisible,
  generarIdLocal,
  guardarBloque,
  leerBloque,
  leerOutbox,
  necesitaReponerBloque,
  numerosDisponibles,
  quitarDeOutbox,
  tomarProximoNumero,
  type BloqueDeNumeracionLocal,
  type MotivoRechazoOffline,
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
  /** Motivo del último intento de drenado que NO fue un simple "sin conexión" — un 409/400 real
   * del servidor sobre el ítem más viejo del outbox, que por eso queda sin sacarse (nunca se
   * salta el orden). `''` sin ningún error de ese tipo pendiente. */
  errorDeDrenado: string
  encolarVentaOffline: (params: {
    solicitudBase: SolicitudDeVenta
    esConsumidorFinal: boolean
    pagos: readonly { comportamiento: ComportamientoMedioPago }[]
  }) => Promise<EncoladoOffline>
}

/** Enriquece cada línea con el precio congelado de la instantánea — `null` si CUALQUIER línea no
 * tiene artículo en la instantánea (all-or-nothing, mismo criterio que el servidor exige para
 * `precioUnitario`/`descuentoUnitario`: todas las líneas con precio, o el encolado se rechaza
 * antes de escribir nada). */
function enriquecerLineasConPrecioOffline(lineas: LineaDeVenta[], instantanea: InstantaneaDePos): LineaDeVenta[] | null {
  const porId = new Map(instantanea.articulos.map((a) => [a.idArticulo, a]))
  const enriquecidas: LineaDeVenta[] = []
  for (const linea of lineas) {
    const articulo = porId.get(linea.idArticulo)
    if (!articulo) return null
    enriquecidas.push({ ...linea, precioUnitario: articulo.precioFinal, descuentoUnitario: articulo.descuentoUnitario })
  }
  return enriquecidas
}

export function useSincronizacionOffline(params: ParametrosDeSincronizacionOffline): ResultadoDeSincronizacionOffline {
  const { idPuntoVenta, activo, intervaloMs = INTERVALO_DE_SINCRONIZACION_MS } = params

  const almacenRef = useRef<AlmacenClaveValor>(params.almacen ?? crearAlmacenIndexedDb())

  const [instantanea, setInstantanea] = useState<InstantaneaDePos | null>(null)
  const [outboxCount, setOutboxCount] = useState(0)
  const [errorDeDrenado, setErrorDeDrenado] = useState('')
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
        setErrorDeDrenado('')
      } catch (e) {
        if (e instanceof ErrorDeRed) {
          // Sigue sin señal — se reintenta en el próximo ciclo, nunca se descarta ni se salta.
          return
        }
        // Rechazo REAL del servidor sobre el ítem más viejo (ej. `numero_preasignado_con_otro
        // _contenido`, `turno_no_abierto`) — drenar en orden significa que ningún ítem posterior
        // se envía mientras este siga trabado: saltarlo arriesgaría un número emitido fuera de
        // orden. Se corta el ciclo y se deja visible para que un humano lo resuelva.
        setErrorDeDrenado(
          e instanceof Error ? `La venta ${primera.numeroPreasignado} no se pudo sincronizar: ${e.message}` : 'Una venta encolada no se pudo sincronizar.',
        )
        return
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
      setErrorDeDrenado('')
      setEnLinea(typeof navigator === 'undefined' || navigator.onLine)
      bloqueRef.current = null
      return
    }

    let vigente = true

    Promise.all([leerInstantaneaLocal(almacenRef.current), leerOutbox(almacenRef.current), leerBloque(almacenRef.current)]).then(
      ([instantaneaGuardada, outboxGuardado, bloqueGuardado]) => {
        if (!vigente) return
        setInstantanea(instantaneaGuardada)
        setOutboxCount(outboxGuardado.length)
        bloqueRef.current = bloqueGuardado
      },
    )

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
  }): Promise<EncoladoOffline> {
    return encolarOperacion(async () => {
      const instantaneaActual = instantanea
      const lineasBase = paramsVenta.solicitudBase.lineas ?? []
      const lineasEnriquecidas = instantaneaActual ? enriquecerLineasConPrecioOffline(lineasBase, instantaneaActual) : null

      const motivo = admisibilidadDeVentaOffline({
        esConsumidorFinal: paramsVenta.esConsumidorFinal,
        pagos: paramsVenta.pagos,
        hayInstantanea: instantaneaActual !== null,
        todasLasLineasConPrecio: lineasEnriquecidas !== null && lineasEnriquecidas.length > 0,
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

      const nuevoOutbox = await agregarAOutbox(almacenRef.current, {
        idLocal: generarIdLocal(),
        numeroPreasignado: tomado.numero,
        idPuntoVenta: paramsVenta.solicitudBase.idPuntoVenta,
        creadoEn: new Date().toISOString(),
        solicitud: solicitudFinal,
      })
      setOutboxCount(nuevoOutbox.length)

      return { ok: true, numeroVisible: construirNumeroVisible(paramsVenta.solicitudBase.idPuntoVenta, tomado.numero), numero: tomado.numero }
    })
  }

  return { instantanea, outboxCount, errorDeDrenado, enLinea, encolarVentaOffline }
}
