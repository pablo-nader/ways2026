/**
 * Fin de la sesión local del POS de escritorio: además de la sesión de cajero persistida, borra la
 * instantánea guardada, que trae clientes con sus saldos. Se llama al cerrar sesión, ante un 401,
 * al cerrar el turno y cuando el servidor confirma que el dispositivo ya no está vinculado. Un
 * reinicio normal de la app NO la borra: la venta sin red al arrancar depende de ella. El outbox y
 * el bloque de numeración nunca se tocan: son ventas reales que todavía tienen que sincronizar.
 */
import { limpiarSesionDeCajeroPersistida } from '../api/entornoTauri'
import { crearAlmacenIndexedDb, type AlmacenDeClavesMultiples } from './almacenPos'
import { purgarInstantaneaLocal } from './instantaneaOffline'

export async function terminarSesionLocalDelPos(almacen: AlmacenDeClavesMultiples = crearAlmacenIndexedDb()): Promise<void> {
  await Promise.all([limpiarSesionDeCajeroPersistida(), purgarInstantaneaLocal(almacen)])
}
