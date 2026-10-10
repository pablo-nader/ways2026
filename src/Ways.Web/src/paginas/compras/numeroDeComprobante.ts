export const DIGITOS_PUNTO_DE_VENTA = 4
export const DIGITOS_NUMERO = 8

export type PartesDeNumero = { puntoVenta: string; numero: string }

const FORMATO_ESTANDAR = /^(\d{1,4})-(\d{1,8})$/

export function soloDigitos(texto: string, maximo: number): string {
  return texto.replace(/\D/g, '').slice(0, maximo)
}

/** Completa con ceros a la izquierda hasta `largo`; un campo vacío sigue vacío. */
export function rellenarConCeros(digitos: string, largo: number): string {
  return digitos === '' ? '' : digitos.padStart(largo, '0')
}

/** Une las partes en `PPPP-NNNNNNNN`; sin ninguna parte no hay número (cadena vacía). */
export function unirNumero({ puntoVenta, numero }: PartesDeNumero): string {
  if (puntoVenta === '' && numero === '') return ''
  return `${puntoVenta}-${numero}`
}

/** Un valor con el formato `PPPP-NNNNNNNN` se divide tal cual; cualquier otro (datos anteriores
 * al formato) se muestra en lo posible con los dígitos de cada lado del último guion. */
export function dividirNumero(valor: string): PartesDeNumero {
  const texto = valor.trim()
  if (texto === '') return { puntoVenta: '', numero: '' }
  const exacto = FORMATO_ESTANDAR.exec(texto)
  if (exacto) return { puntoVenta: exacto[1], numero: exacto[2] }
  const guion = texto.lastIndexOf('-')
  if (guion === -1) return { puntoVenta: '', numero: soloDigitos(texto, DIGITOS_NUMERO) }
  return {
    puntoVenta: soloDigitos(texto.slice(0, guion), DIGITOS_PUNTO_DE_VENTA),
    numero: soloDigitos(texto.slice(guion + 1), DIGITOS_NUMERO),
  }
}

export function tieneFormatoEstandar(valor: string): boolean {
  const texto = valor.trim()
  return texto === '' || FORMATO_ESTANDAR.test(texto)
}
