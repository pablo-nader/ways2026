import type { AltaRapidaDeCliente } from '../api/clientes'

export const MOTIVO_ALTA_RAPIDA_SIN_CONEXION = 'Sin conexión: el alta de clientes necesita conexión con el servidor.'
const MOTIVO_ALTA_RAPIDA = 'Dar de alta un cliente nuevo'

/** Valores recortados y `null` en los opcionales en blanco; `null` si falta el nombre (el único obligatorio). */
export function armarAltaRapida(nombre: string, telefono: string, domicilio: string): AltaRapidaDeCliente | null {
  const nombreLimpio = nombre.trim()
  if (nombreLimpio === '') return null
  return { nombre: nombreLimpio, telefono: telefono.trim() || null, domicilio: domicilio.trim() || null }
}

/** El alta de clientes solo existe con conexión: no hay cola offline de clientes. `inerte` agrupa los demás bloqueos de la pantalla. */
export function altaRapidaDeClienteDisponible(estado: { enLinea: boolean; inerte: boolean }): { habilitado: boolean; motivo: string } {
  if (!estado.enLinea) return { habilitado: false, motivo: MOTIVO_ALTA_RAPIDA_SIN_CONEXION }
  return { habilitado: !estado.inerte, motivo: MOTIVO_ALTA_RAPIDA }
}
