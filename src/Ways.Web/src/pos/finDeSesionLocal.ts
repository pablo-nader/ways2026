/**
 * Fin de la sesión local del POS de escritorio: además de la sesión de cajero persistida, borra la
 * instantánea guardada, que trae clientes con sus saldos. Se llama al cerrar sesión, ante un 401
 * de una sesión real, al cerrar el turno y cuando el servidor confirma que el dispositivo ya no
 * está vinculado. Un reinicio normal de la app NO la borra: la venta sin red al arrancar depende de
 * ella. El outbox y el bloque de numeración nunca se tocan: son ventas reales que todavía tienen
 * que sincronizar.
 */
import type { PerdidaDeSesion } from '../api/cliente'
import { limpiarSesionDeCajeroPersistida } from '../api/entornoTauri'
import { crearAlmacenIndexedDb, type AlmacenDeClavesMultiples } from './almacenPos'
import { purgarInstantaneaLocal } from './instantaneaOffline'

export async function terminarSesionLocalDelPos(almacen: AlmacenDeClavesMultiples = crearAlmacenIndexedDb()): Promise<void> {
  await Promise.all([limpiarSesionDeCajeroPersistida(), purgarInstantaneaLocal(almacen)])
}

/**
 * Observador de 401 del POS. La sesión persistida se limpia siempre (como antes); la instantánea
 * solo si la solicitud rechazada llevaba una sesión: una contraseña mal tipeada en el login también
 * da 401, y no puede dejar al equipo sin poder arrancar sin red.
 */
export async function alPerderLaSesionDelPos(
  perdida: PerdidaDeSesion,
  almacen: AlmacenDeClavesMultiples = crearAlmacenIndexedDb(),
): Promise<void> {
  if (perdida.conSesionBearer) {
    await terminarSesionLocalDelPos(almacen)
    return
  }
  await limpiarSesionDeCajeroPersistida()
}
