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
 * Importes con magnitud mayor a esto NO están soportados: más allá de este límite el propio
 * `number` de JS ya puede haber perdido precisión en su representación binaria antes de llegar
 * acá (no hay redondeo que reconstruya un dígito que el `double` nunca guardó). El límite es
 * `Number.MAX_SAFE_INTEGER` en la unidad de `decimales` (ej. con 2 decimales, ~9×10^13).
 */
export const LIMITE_IMPORTE_SOPORTADO = Number.MAX_SAFE_INTEGER

/** `true` si `valor` cae dentro del rango en el que `redondearImporte`/`formatearImporte`
 * garantizan precisión exacta (ver `LIMITE_IMPORTE_SOPORTADO`). */
export function estaEnRangoSoportado(valor: number, decimales = 2): boolean {
  return Number.isFinite(valor) && Math.abs(valor) <= LIMITE_IMPORTE_SOPORTADO / 10 ** decimales
}

/**
 * Redondea "half away from zero" a `decimales` dígitos operando sobre la representación
 * decimal EXACTA de `valor` (`Math.abs(valor).toString()` — JS garantiza que es la cadena más
 * corta que hace round-trip al mismo `double`, sin notación exponencial por debajo de 1e21,
 * rango muy por encima de `LIMITE_IMPORTE_SOPORTADO`), en vez de escalar multiplicando por
 * `10**decimales`: un factor multiplicativo (ej. el `(1 + Number.EPSILON)` de una versión
 * anterior) corrige el ruido de `1.005 * 100` pero DESBORDA en magnitudes grandes — con
 * `valor = 1e14`, `1e14 * 100 * (1 + Number.EPSILON)` ya no es `1e16` sino `1e16 + 2`, un error
 * que crece con la magnitud en vez de quedar acotado. Al decidir el redondeo mirando un único
 * dígito decimal (el primero descartado: `>=5` redondea afuera, `<5` trunca — para
 * "half away from zero" los dígitos siguientes nunca cambian esa decisión) el resultado es
 * exacto para cualquier magnitud representable sin notación exponencial.
 * Devuelve la magnitud ya escalada a entero (unidad = 10^-decimales) y su signo.
 */
function redondearAUnidadEntera(valor: number, decimales: number): { signo: -1 | 1; escalado: number } {
  const signo: -1 | 1 = valor < 0 ? -1 : 1
  const absoluto = Math.abs(valor)
  const factor = 10 ** decimales

  if (!Number.isFinite(absoluto)) {
    return { signo, escalado: Number.NaN }
  }

  const texto = absoluto.toString()
  if (texto.includes('e') || texto.includes('E')) {
    // Fuera del rango donde `toString` da notación decimal plana (|valor| >= 1e21): muy por
    // encima de `LIMITE_IMPORTE_SOPORTADO` para cualquier `decimales` razonable — no hay
    // representación exacta posible, se devuelve el mejor esfuerzo sin desbordar.
    return { signo, escalado: Math.round(absoluto * factor) }
  }

  const [enteroTexto, fraccionCruda = ''] = texto.split('.')
  const fraccionTexto = fraccionCruda.padEnd(decimales + 1, '0')
  const digitoDecisor = fraccionTexto.charCodeAt(decimales) - 48 // '0'.charCodeAt(0) === 48

  let entero = BigInt(enteroTexto)
  let decimalesBig = decimales === 0 ? 0n : BigInt(fraccionTexto.slice(0, decimales))

  if (digitoDecisor >= 5) {
    decimalesBig += 1n
    const factorBig = BigInt(factor)
    if (decimalesBig >= factorBig) {
      decimalesBig -= factorBig
      entero += 1n
    }
  }

  const escalado = Number(entero) * factor + Number(decimalesBig)
  return { signo, escalado }
}

/**
 * Redondea un importe "half away from zero" a `decimales` dígitos y devuelve el NÚMERO final
 * (no un texto) — es la única fuente de verdad de qué valor "final" representa un importe: la
 * usan tanto `formatearImporte` (para lo que se muestra) como `CampoImporte` (para lo que emite
 * por `onChange`), así ninguno de los dos puede quedar desincronizado del otro. `NaN`/valores no
 * finitos devuelven `NaN`.
 */
export function redondearImporte(valor: number, decimales = 2): number {
  if (Number.isNaN(valor)) return Number.NaN
  const { signo, escalado } = redondearAUnidadEntera(valor, decimales)
  if (Number.isNaN(escalado)) return Number.NaN
  const factor = 10 ** decimales
  return escalado === 0 ? 0 : (signo * escalado) / factor
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
  if (valor === null || valor === undefined || !Number.isFinite(valor)) {
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
