const LARGOS_GTIN = [8, 12, 13, 14]

/**
 * Evalúa un código de barras ya recortado (sin espacios en los extremos) y devuelve el motivo por el
 * que no parece un GTIN estándar, o `null` si lo parece. Es solo una advertencia para quien tipea:
 * los códigos no estándar (de proveedor, Code 128 con letras) siguen siendo cargables.
 *
 * Los 8 dígitos se evalúan como EAN-8; UPC-E no está modelado. El vacío no se evalúa (`null`): quien
 * llama lo descarta antes de preguntar.
 */
export function advertenciaDeCodigoBarra(codigo: string): string | null {
  if (codigo === '') return null

  if (!/^[0-9]+$/.test(codigo)) {
    return 'El código contiene caracteres que no son dígitos, por lo que no es un código GTIN estándar (EAN/UPC).'
  }

  if (!LARGOS_GTIN.includes(codigo.length)) {
    return `El código tiene ${codigo.length} dígitos y los códigos GTIN estándar tienen 8, 12, 13 o 14.`
  }

  if (digitoVerificadorEsperado(codigo) !== Number(codigo[codigo.length - 1])) {
    return 'El dígito verificador no coincide: puede haber un error de tipeo.'
  }

  return null
}

/** Módulo 10 de GS1: desde el dígito de datos más a la derecha hacia la izquierda, pesos 3,1,3,1… */
function digitoVerificadorEsperado(codigo: string): number {
  let suma = 0
  for (let i = codigo.length - 2, peso = 3; i >= 0; i--, peso = peso === 3 ? 1 : 3) {
    suma += Number(codigo[i]) * peso
  }
  return (10 - (suma % 10)) % 10
}
