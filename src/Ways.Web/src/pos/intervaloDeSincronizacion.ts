/** Período fijo del ciclo automático de sincronización del POS de escritorio: no se configura ni se
 * persiste, así que ningún valor guardado en el dispositivo puede alterarlo. */
export const INTERVALO_DE_SINCRONIZACION_MINUTOS = 10

export const INTERVALO_DE_SINCRONIZACION_MS = INTERVALO_DE_SINCRONIZACION_MINUTOS * 60_000
