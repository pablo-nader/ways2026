export type Tema = 'oscuro' | 'claro'

export const CLAVE_DE_TEMA = 'ways.tema'

const TEMA_POR_DEFECTO: Tema = 'oscuro'

function esTema(valor: unknown): valor is Tema {
  return valor === 'oscuro' || valor === 'claro'
}

export function leerTemaGuardado(): Tema {
  try {
    const guardado = localStorage.getItem(CLAVE_DE_TEMA)
    return esTema(guardado) ? guardado : TEMA_POR_DEFECTO
  } catch {
    return TEMA_POR_DEFECTO
  }
}

export function aplicarTema(tema: Tema) {
  document.documentElement.dataset.bsTheme = tema === 'oscuro' ? 'dark' : 'light'
}

export function guardarTema(tema: Tema) {
  try {
    localStorage.setItem(CLAVE_DE_TEMA, tema)
  } catch {
    // Sin almacenamiento disponible el tema rige solo durante la sesión.
  }
}
