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

function guardarOutbox(almacen: AlmacenClaveValor, ventas: VentaEnCola[]): Promise<void> {
  return almacen.escribir(CLAVE_OUTBOX, ventas)
}

/** Encola al FINAL — el drenado siempre recorre desde el principio (regla "drena EN ORDEN"). */
export async function agregarAOutbox(almacen: AlmacenClaveValor, venta: VentaEnCola): Promise<VentaEnCola[]> {
  const actual = await leerOutbox(almacen)
  const siguiente = [...actual, venta]
  await guardarOutbox(almacen, siguiente)
  return siguiente
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

export function guardarBloque(almacen: AlmacenClaveValor, bloque: BloqueDeNumeracionLocal): Promise<void> {
  return almacen.escribir(CLAVE_BLOQUE, bloque)
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

export type MotivoRechazoOffline = 'cliente_no_admitido' | 'medio_no_admitido' | 'sin_instantanea' | 'linea_sin_precio' | 'sin_numeracion'

const MENSAJE_POR_MOTIVO: Record<MotivoRechazoOffline, string> = {
  cliente_no_admitido: 'Sin conexión solo se puede vender al Consumidor Final — la instantánea no tiene los precios de otro cliente.',
  medio_no_admitido: 'Sin conexión solo se admite efectivo — cuenta corriente y otros medios necesitan validarse contra el servidor.',
  sin_instantanea: 'No hay una instantánea local para vender sin conexión — recuperá la señal para descargarla.',
  linea_sin_precio: 'Un artículo del carrito no tiene precio en la última instantánea — no se puede vender sin conexión.',
  sin_numeracion: 'No quedan números reservados para vender sin conexión — recuperá la señal para reponer el bloque.',
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
