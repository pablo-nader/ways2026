/**
 * Puente a la impresora térmica del escritorio (stage-desktop-pos) — el shell de Tauri expone
 * `window.__TAURI__.core.invoke('imprimir_raw', { bytes })`, implementado del lado de
 * `src/Ways.Desktop` (otro agente). Fuera de Tauri (la web normal, o Vitest/jsdom) no hay
 * impresora: `imprimir` devuelve un resultado tipado "no disponible" en vez de un fallback con
 * `window.print()` — un ticket ESC/POS crudo no tiene nada que mostrarle a `window.print()`.
 */

type TauriGlobal = {
  core?: {
    invoke: (comando: string, argumentos?: Record<string, unknown>) => Promise<unknown>
  }
}

declare global {
  interface Window {
    __TAURI__?: TauriGlobal
  }
}

/** `true` dentro del shell de escritorio (Tauri inyecta `window.__TAURI__` en su webview). */
export function enEscritorio(): boolean {
  return typeof window !== 'undefined' && window.__TAURI__?.core?.invoke !== undefined
}

export type ResultadoDeImpresion = { ok: true } | { ok: false; motivo: 'no_disponible' | 'error'; mensaje: string }

/**
 * Manda los bytes crudos al comando `imprimir_raw` del lado nativo. Nunca lanza: una venta ya se
 * emitió antes de imprimir el ticket, así que una falla de impresión se reporta como dato, no
 * como excepción — el llamador la muestra como aviso no bloqueante con un botón "Reimprimir".
 */
export async function imprimir(bytes: Uint8Array): Promise<ResultadoDeImpresion> {
  const invoke = window.__TAURI__?.core?.invoke
  if (!invoke) {
    return { ok: false, motivo: 'no_disponible', mensaje: 'La impresora no está disponible fuera del escritorio.' }
  }

  try {
    await invoke('imprimir_raw', { bytes: Array.from(bytes) })
    return { ok: true }
  } catch (error) {
    const mensaje = error instanceof Error ? error.message : 'No se pudo imprimir.'
    return { ok: false, motivo: 'error', mensaje }
  }
}

/** Pide al shell nativo que abra su propia ventana de configuración — botón "Configuración" del
 * shell del POS, visible solo con `enEscritorio()`. Sin Tauri (o si el comando falla) no hay
 * nada más que hacer del lado de esta app: la ventana de configuración la posee el shell. */
export async function abrirConfiguracion(): Promise<void> {
  const invoke = window.__TAURI__?.core?.invoke
  if (!invoke) return

  try {
    await invoke('abrir_configuracion')
  } catch {
    // El shell nativo no pudo abrir su propia ventana — no hay una acción de respaldo acá.
  }
}
