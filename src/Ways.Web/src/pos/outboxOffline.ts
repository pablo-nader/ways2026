/**
 * Outbox de ventas offline + bloque local de numeración (stage-pos-venta-offline-web, Parte C) —
 * persistido en el mismo almacén durable que la instantánea (`almacenPos.ts`): una venta encolada
 * tiene que sobrevivir un restart de la app exactamente igual que el catálogo, y solo hay un
 * bloque vivo por dispositivo a la vez (el propio backend abandona cualquier bloque anterior al
 * pedir uno nuevo — ver `ServicioDeReservasDeNumeracion`).
 *
 * El drenado es responsabilidad de `useSincronizacionOffline.ts` (necesita orquestar red +
 * generación de React) — este módulo solo da las piezas puras/de persistencia: tomar el próximo
 * número, admitir o rechazar una venta antes de encolarla, leer/escribir la cola.
 */
import type { AlmacenClaveValor } from './almacenPos'
import { clienteAdmitidoOffline, pagosAdmitidosOffline } from './reglasOffline'
import type { ComportamientoMedioPago, SolicitudDeVenta } from '../api/tipos'

const CLAVE_OUTBOX = 'outbox'
const CLAVE_BLOQUE = 'bloqueNumeracion'

/** Una venta ya admitida y con número propio, esperando a que vuelva la señal — `solicitud` es el
 * `SolicitudDeVenta` EXACTO que se va a reenviar (nunca reconstruido al drenar): el servidor
 * compara contenido contra el número pre-asignado (`ExigirMismoContenido`), así que reenviar algo
 * distinto de lo que se decidió al encolar rechazaría con 409. */
export type VentaEnCola = {
  idLocal: string
  numeroPreasignado: number
  idPuntoVenta: number
  creadoEn: string
  solicitud: SolicitudDeVenta
}

/** Bloque de numeración reservado, con `proximo` como puntero local de reparto — nunca se vuelve
 * a leer `desde` una vez reservado, solo `proximo`/`hasta` importan para repartir. */
export type BloqueDeNumeracionLocal = {
  idPuntoVenta: number
  codigoTipoComprobante: string
  desde: number
  hasta: number
  proximo: number
}

export function leerOutbox(almacen: AlmacenClaveValor): Promise<VentaEnCola[]> {
  return almacen.leer<VentaEnCola[]>(CLAVE_OUTBOX).then((v) => v ?? [])
}

function guardarOutbox(almacen: AlmacenClaveValor, ventas: VentaEnCola[]): Promise<boolean> {
  return almacen.escribir(CLAVE_OUTBOX, ventas)
}

/** Se lanza cuando `agregarAOutbox` no puede CONFIRMAR (releyendo) que la venta que acaba de
 * escribir de verdad quedó persistida — un almacén degradado (cuota agotada, modo privado,
 * IndexedDB bloqueado) puede reportar una escritura fallida, o incluso "exitosa" sin haber
 * grabado nada real. La venta ya tiene (o va a tener) un ticket entregado: nunca se puede asumir
 * que quedó guardada solo porque la promesa de escritura no rechazó (judgment-day ronda 1,
 * BLOCKER — antes de este fix, `encolarVentaOffline` devolvía `ok: true` sin verificar nada). */
export class ErrorDePersistenciaOffline extends Error {}

/** Encola al FINAL — el drenado siempre recorre desde el principio (regla "drena EN ORDEN").
 * Nunca resuelve con `ok: true` sin haber releído el almacén y confirmado que la venta nueva está
 * de verdad adentro — tira `ErrorDePersistenciaOffline` en cualquier otro caso (ver el
 * doc-comment de la excepción). */
export async function agregarAOutbox(almacen: AlmacenClaveValor, venta: VentaEnCola): Promise<VentaEnCola[]> {
  const actual = await leerOutbox(almacen)
  const siguiente = [...actual, venta]
  const escrito = await guardarOutbox(almacen, siguiente)
  const relectura = escrito ? await leerOutbox(almacen) : []
  if (!relectura.some((v) => v.idLocal === venta.idLocal)) {
    throw new ErrorDePersistenciaOffline('No se pudo guardar la venta de forma durable en el outbox offline.')
  }
  return relectura
}

export async function quitarDeOutbox(almacen: AlmacenClaveValor, idLocal: string): Promise<VentaEnCola[]> {
  const actual = await leerOutbox(almacen)
  const siguiente = actual.filter((v) => v.idLocal !== idLocal)
  await guardarOutbox(almacen, siguiente)
  return siguiente
}

export function leerBloque(almacen: AlmacenClaveValor): Promise<BloqueDeNumeracionLocal | null> {
  return almacen.leer<BloqueDeNumeracionLocal>(CLAVE_BLOQUE)
}

export async function guardarBloque(almacen: AlmacenClaveValor, bloque: BloqueDeNumeracionLocal): Promise<void> {
  await almacen.escribir(CLAVE_BLOQUE, bloque)
}

const CLAVE_RECHAZADAS = 'ventasRechazadas'

/** Una venta que el servidor rechazó de forma PERMANENTE al drenar (nunca un `ErrorDeRed`
 * transitorio) — se saca del outbox para no bloquear el drenado de las ventas posteriores, pero
 * se conserva acá con su error real: es una venta real, con su ticket ya entregado, que nunca se
 * descarta en silencio (judgment-day ronda 1, CRITICAL — "needs attention", nunca "se perdió"). */
export type VentaRechazada = VentaEnCola & { mensaje: string }

export function leerRechazadas(almacen: AlmacenClaveValor): Promise<VentaRechazada[]> {
  return almacen.leer<VentaRechazada[]>(CLAVE_RECHAZADAS).then((v) => v ?? [])
}

function guardarRechazadas(almacen: AlmacenClaveValor, rechazadas: VentaRechazada[]): Promise<boolean> {
  return almacen.escribir(CLAVE_RECHAZADAS, rechazadas)
}

/** Encola al FINAL, mismo criterio que `agregarAOutbox` (incluida la verificación por relectura:
 * esta venta YA estaba durablemente guardada en el outbox, moverla de acá sin confirmar dónde
 * queda sería perderla). */
export async function agregarARechazada(almacen: AlmacenClaveValor, rechazada: VentaRechazada): Promise<VentaRechazada[]> {
  const actual = await leerRechazadas(almacen)
  const siguiente = [...actual, rechazada]
  const escrito = await guardarRechazadas(almacen, siguiente)
  const relectura = escrito ? await leerRechazadas(almacen) : []
  if (!relectura.some((v) => v.idLocal === rechazada.idLocal)) {
    throw new ErrorDePersistenciaOffline('No se pudo archivar la venta rechazada de forma durable.')
  }
  return relectura
}

/** Cuántos números quedan sin repartir en el bloque — `0` (nunca negativo) sin bloque o agotado. */
export function numerosDisponibles(bloque: BloqueDeNumeracionLocal | null): number {
  if (!bloque) return 0
  return Math.max(0, bloque.hasta - bloque.proximo + 1)
}

/** Umbral de reposición (`useSincronizacionOffline.ts`, "reponer mientras todavía hay señal"):
 * por debajo de esto se pide un bloque nuevo la próxima vez que haya conexión — elegido para
 * sobrar margen frente a una venta ocasional entre ciclos de sincronización, nunca para que el
 * dispositivo llegue exacto a 0 mid-outage. */
export const UMBRAL_DE_REPOSICION = 20

export function necesitaReponerBloque(bloque: BloqueDeNumeracionLocal | null): boolean {
  return numerosDisponibles(bloque) < UMBRAL_DE_REPOSICION
}

/**
 * Toma el próximo número disponible del bloque — pura, nunca persiste (el llamador decide cuándo
 * guardar el bloque actualizado, típicamente junto con el resto de la operación de encolado, para
 * que ambos cambios queden atómicos desde el punto de vista de la UI). `null` sin números
 * disponibles.
 */
export function tomarProximoNumero(bloque: BloqueDeNumeracionLocal | null): { numero: number; bloqueRestante: BloqueDeNumeracionLocal } | null {
  if (numerosDisponibles(bloque) <= 0 || !bloque) return null
  return { numero: bloque.proximo, bloqueRestante: { ...bloque, proximo: bloque.proximo + 1 } }
}

/** `NumeroDeComprobante.Formatear` del backend, replicado — `idPuntoVenta` a 4 dígitos, `numero` a
 * 8, separados por guion. */
export function construirNumeroVisible(idPuntoVenta: number, numero: number): string {
  return `${String(idPuntoVenta).padStart(4, '0')}-${String(numero).padStart(8, '0')}`
}

export type MotivoRechazoOffline =
  | 'cliente_no_admitido'
  | 'medio_no_admitido'
  | 'sin_instantanea'
  | 'linea_sin_precio'
  | 'sin_numeracion'
  | 'error_al_guardar'

const MENSAJE_POR_MOTIVO: Record<MotivoRechazoOffline, string> = {
  cliente_no_admitido: 'Sin conexión solo se puede vender al Consumidor Final — la instantánea no tiene los precios de otro cliente.',
  medio_no_admitido: 'Sin conexión solo se admite efectivo — cuenta corriente y otros medios necesitan validarse contra el servidor.',
  sin_instantanea: 'No hay una instantánea local para vender sin conexión — recuperá la señal para descargarla.',
  linea_sin_precio: 'Un artículo del carrito no tiene precio en la última instantánea — no se puede vender sin conexión.',
  sin_numeracion: 'No quedan números reservados para vender sin conexión — recuperá la señal para reponer el bloque.',
  // judgment-day ronda 1 (BLOCKER): la venta NO se guardó — nunca se le entrega el ticket al
  // cliente con este motivo (a diferencia de los demás, que rechazan ANTES de intentar nada).
  error_al_guardar:
    'No se pudo guardar la venta de forma segura en este dispositivo — no quedó encolada, no le entregues el comprobante al cliente. Probá de nuevo; si persiste, puede ser que el almacenamiento del dispositivo esté lleno o bloqueado.',
}

export function mensajeDeRechazoOffline(motivo: MotivoRechazoOffline): string {
  return MENSAJE_POR_MOTIVO[motivo]
}

/**
 * Gate único antes de encolar (Parte D/E, "nunca solo un aviso — el bloqueo real"): devuelve el
 * PRIMER motivo de rechazo, en un orden estable, o `null` si la venta puede encolarse. Nunca
 * reordena ni acumula — mismo criterio que `validarPagosLocal` (corta en el primer rechazo).
 */
export function admisibilidadDeVentaOffline(params: {
  esConsumidorFinal: boolean
  pagos: readonly { comportamiento: ComportamientoMedioPago }[]
  hayInstantanea: boolean
  todasLasLineasConPrecio: boolean
  hayNumeroDisponible: boolean
}): MotivoRechazoOffline | null {
  if (!clienteAdmitidoOffline({ esConsumidorFinal: params.esConsumidorFinal })) return 'cliente_no_admitido'
  if (!pagosAdmitidosOffline(params.pagos)) return 'medio_no_admitido'
  if (!params.hayInstantanea) return 'sin_instantanea'
  if (!params.todasLasLineasConPrecio) return 'linea_sin_precio'
  if (!params.hayNumeroDisponible) return 'sin_numeracion'
  return null
}

/** `crypto.randomUUID` cuando está disponible (todo navegador/Tauri moderno) — un id puramente
 * local, nunca viaja al servidor ni colisiona con nada del dominio (`idLocal` solo identifica la
 * fila en ESTE outbox, para poder sacarla tras un drenado exitoso). El fallback nunca corre en
 * producción real, solo cubre un entorno de test sin `crypto.randomUUID` poliyenado. */
export function generarIdLocal(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }
  return `local-${Date.now()}-${Math.random().toString(36).slice(2)}`
}
