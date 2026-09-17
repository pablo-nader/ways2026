/**
 * Formateo y parseo de importes (dinero) en formato es-AR: "." separador de miles
 * (SIEMPRE visible, incluso con 4 dígitos: "1.234", nunca "1234"), "," separador decimal.
 *
 * Implementación propia (sin depender de `Intl.NumberFormat`): distintos motores ICU
 * (Node/jsdom en los tests vs. WebView2 en el instalador de escritorio) difieren en si
 * agrupan un número de 4 dígitos (`minimumGroupingDigits` de la locale `es`/`es-AR`), así
 * que el agrupado se arma con un regex determinístico en vez de `useGrouping: 'always'`.
 */

const PATRON_IMPORTE_VALIDO = /^-?(\d+|\d{1,3}(\.\d{3})+)(,\d+)?$/

/** Inserta "." cada 3 dígitos desde la derecha. Determinístico, no depende de ICU. */
function agruparMiles(digitos: string): string {
  return digitos.replace(/\B(?=(\d{3})+(?!\d))/g, '.')
}

/**
 * Redondea "half away from zero" a `decimales` dígitos, corrigiendo el error de
 * representación binaria de punto flotante (ej. `1.005 * 100` da `100.49999999999999`
 * en JS, no `100.5`) para que casos como `1.005 -> 1.01` redondeen como se espera.
 * Devuelve la magnitud ya escalada a entero (unidad = 10^-decimales) y su signo.
 */
function redondearAUnidadEntera(valor: number, decimales: number): { signo: -1 | 1; escalado: number } {
  const factor = 10 ** decimales
  const signo: -1 | 1 = valor < 0 ? -1 : 1
  const absoluto = Math.abs(valor)
  const escalado = Math.round(absoluto * factor * (1 + Number.EPSILON))
  return { signo, escalado }
}

export interface OpcionesFormatearImporte {
  /** Antepone "$ " al resultado. Default `false`. */
  simbolo?: boolean
  /** Cantidad de decimales. Default `2`. */
  decimales?: number
}

/**
 * Formatea un importe. `null`/`undefined`/`NaN` devuelven `—` (convención del resto de la
 * app para "sin dato"). El signo se omite si el valor redondeado da exactamente cero
 * (evita mostrar "-0,00").
 */
export function formatearImporte(valor: number | null | undefined, opciones: OpcionesFormatearImporte = {}): string {
  const { simbolo = false, decimales = 2 } = opciones
  if (valor === null || valor === undefined || Number.isNaN(valor)) {
    return '—'
  }

  const { signo, escalado } = redondearAUnidadEntera(valor, decimales)
  const factor = 10 ** decimales
  const parteEntera = Math.floor(escalado / factor)
  const parteDecimal = escalado - parteEntera * factor

  const enteros = agruparMiles(String(parteEntera))
  const decimalesTexto = decimales > 0 ? ',' + String(parteDecimal).padStart(decimales, '0') : ''
  const signoTexto = signo === -1 && escalado !== 0 ? '-' : ''
  const numero = `${enteros}${decimalesTexto}`

  return `${signoTexto}${simbolo ? '$ ' : ''}${numero}`
}

/**
 * Parsea un texto de importe con la regla: "," es el ÚNICO separador decimal, "." es
 * ÚNICAMENTE separador de miles y solo es válido en grupos completos de 3 dígitos
 * (ej. "1.234", "12.345.678"). Cualquier otro uso de "." (por ejemplo "1234.56", donde
 * "56" no es un grupo de 3) se considera AMBIGUO y se rechaza devolviendo `null` — el
 * usuario debe escribir "1234,56". Tolera un símbolo "$" y espacios, que se descartan
 * antes de validar. Devuelve `null` ante cualquier entrada inválida o vacía.
 */
export function parsearImporte(texto: string | null | undefined): number | null {
  if (texto === null || texto === undefined) {
    return null
  }

  const limpio = texto.trim().replace(/\$/g, '').replace(/\s/g, '')
  if (limpio === '') {
    return null
  }

  if (!PATRON_IMPORTE_VALIDO.test(limpio)) {
    return null
  }

  const sinMiles = limpio.replace(/\./g, '').replace(',', '.')
  const valor = Number.parseFloat(sinMiles)
  if (Number.isNaN(valor)) {
    return null
  }

  return valor === 0 ? 0 : valor
}
