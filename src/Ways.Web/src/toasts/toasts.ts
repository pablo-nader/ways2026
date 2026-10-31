import { useSyncExternalStore } from 'react'

export type TipoDeToast = 'exito' | 'error' | 'advertencia'

export interface Toast {
  id: number
  tipo: TipoDeToast
  texto: string
  /** Se incrementa cuando un duplicado reinicia el temporizador. */
  version: number
}

export const DURACION_MS: Record<TipoDeToast, number> = {
  exito: 4000,
  advertencia: 4000,
  error: 7000,
}

let toasts: readonly Toast[] = []
let siguienteId = 1
const oyentes = new Set<() => void>()

function emitir(nuevos: readonly Toast[]) {
  toasts = nuevos
  oyentes.forEach((oyente) => oyente())
}

function agregar(tipo: TipoDeToast, texto: string): number {
  const existente = toasts.find((t) => t.tipo === tipo && t.texto === texto)
  if (existente !== undefined) {
    emitir(toasts.map((t) => (t === existente ? { ...t, version: t.version + 1 } : t)))
    return existente.id
  }

  const id = siguienteId++
  emitir([...toasts, { id, tipo, texto, version: 0 }])
  return id
}

export const toast = {
  exito: (texto: string) => agregar('exito', texto),
  error: (texto: string) => agregar('error', texto),
  advertencia: (texto: string) => agregar('advertencia', texto),
  cerrar(id: number) {
    if (toasts.some((t) => t.id === id)) emitir(toasts.filter((t) => t.id !== id))
  },
}

export function reiniciarToasts() {
  siguienteId = 1
  if (toasts.length > 0) emitir([])
}

function suscribir(oyente: () => void) {
  oyentes.add(oyente)
  return () => {
    oyentes.delete(oyente)
  }
}

export function useToasts(): readonly Toast[] {
  return useSyncExternalStore(suscribir, () => toasts)
}
