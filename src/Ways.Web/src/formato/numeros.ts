/**
 * Número decimal tipeado por el usuario (cantidades, porcentajes, pesos): "." y "," son el mismo separador decimal.
 * No admite separador de miles: un texto con más de un separador ("1.234,5", "1,2,3") es inválido. Los campos de dinero
 * tienen su propio parser (`parsearImporte`), donde el punto es de miles.
 */
const FORMA_DECIMAL = /^(\d+[.,]?\d*|[.,]\d+)$/

/** Texto → número, o `null` si está vacío o no es un decimal válido. Acepta el estado intermedio de un tipeo ("1," o "1."
 * valen 1; ",5" vale 0,5). Con `permitirNegativo` admite un "-" inicial. */
export function parsearNumeroDecimal(texto: string, opciones?: { permitirNegativo?: boolean }): number | null {
  let limpio = texto.trim()
  let signo = 1
  if (opciones?.permitirNegativo && limpio.startsWith('-')) {
    signo = -1
    limpio = limpio.slice(1)
  }
  if (!FORMA_DECIMAL.test(limpio)) return null
  const valor = Number(limpio.replace(',', '.'))
  return Number.isFinite(valor) ? signo * valor : null
}

/** Texto → entero no negativo, o `null`: para los campos de códigos, años o páginas, donde "." y "," no valen. */
export function parsearEnteroTipeado(texto: string): number | null {
  const limpio = texto.trim()
  if (!/^\d+$/.test(limpio)) return null
  const valor = Number(limpio)
  return Number.isSafeInteger(valor) ? valor : null
}

/** Atributos de un `<input>` de decimal tipeado: reemplazan `type="number"` (que rechaza la coma según el idioma del navegador).
 * `decimales` acota la cantidad de decimales (lo que antes hacía `step`) y, sin `permitirNegativo`, no admite el signo (`min="0"`). */
export function atributosDeCampoDecimal(opciones?: { decimales?: number; permitirNegativo?: boolean }) {
  const signo = opciones?.permitirNegativo ? '-?' : ''
  const fraccion = opciones?.decimales === undefined ? '+' : `{1,${opciones.decimales}}`
  return {
    type: 'text' as const,
    inputMode: 'decimal' as const,
    pattern: `${signo}([0-9]+([.,][0-9]${fraccion})?|[.,][0-9]${fraccion})`,
    autoComplete: 'off',
  }
}

/** Lo mismo para un entero (códigos, orden, días, páginas): ni "." ni "," valen. */
export function atributosDeCampoEntero(opciones?: { permitirNegativo?: boolean }) {
  return {
    type: 'text' as const,
    inputMode: 'numeric' as const,
    pattern: `${opciones?.permitirNegativo ? '-?' : ''}[0-9]+`,
    autoComplete: 'off',
  }
}

/** Número → texto para mostrar con coma decimal (`1.5` → `1,5`). */
export function formatearNumeroDecimal(valor: number): string {
  return String(valor).replace('.', ',')
}
