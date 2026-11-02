import { useSyncExternalStore } from 'react'

function suscribir(avisar: () => void) {
  window.addEventListener('online', avisar)
  window.addEventListener('offline', avisar)
  return () => {
    window.removeEventListener('online', avisar)
    window.removeEventListener('offline', avisar)
  }
}

/** Estado de red del navegador, para deshabilitar las acciones que solo existen contra el servidor (no entran en la cola offline).
 * Es la señal optimista del sistema operativo: un fallo real de red sigue mostrando su error al intentar. */
export function useEnLinea(): boolean {
  return useSyncExternalStore(
    suscribir,
    () => navigator.onLine,
    () => true,
  )
}
