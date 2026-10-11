/** Texto `DD/MM/AAAA` ↔ valor ISO `YYYY-MM-DD` de los campos de fecha. */

const ISO = /^(\d{4})-(\d{2})-(\d{2})$/

/** Los campos de fecha no aceptan años anteriores: casi siempre es un año de dos dígitos mal tipeado. */
export const ANIO_MINIMO = 1900

export function esFechaReal(anio: number, mes: number, dia: number): boolean {
  if (mes < 1 || mes > 12 || dia < 1) return false
  const diasDelMes = new Date(Date.UTC(anio, mes, 0)).getUTCDate()
  return dia <= diasDelMes
}

/** `YYYY-MM-DD` → `DD/MM/AAAA`; cualquier otra cosa (incluido `''`) se muestra vacía. */
export function isoATexto(iso: string): string {
  const m = ISO.exec(iso)
  if (!m || !esFechaReal(Number(m[1]), Number(m[2]), Number(m[3]))) return ''
  return `${m[3]}/${m[2]}/${m[1]}`
}

/** Un año de dos dígitos cae dentro de los próximos 20 años del año actual o, si no, en el siglo anterior. */
export function expandirAnio(anio: string, anioActual: number): number {
  if (anio.length !== 2) return Number(anio)
  const corto = Number(anio)
  const siglo = Math.floor(anioActual / 100) * 100
  return siglo + corto <= anioActual + 20 ? siglo + corto : siglo - 100 + corto
}

/**
 * Texto tipeado → ISO. Acepta día y mes de 1 o 2 dígitos y año de 2 o 4. Devuelve `null` si el
 * texto no es una fecha de calendario real (ej. 31/02/2026) o está incompleto. Con `admiteAnioCorto`
 * en `false` exige el año de cuatro dígitos (lo que se tipea en vivo, antes de salir del campo).
 */
export function textoAIso(
  texto: string,
  anioActual: number = new Date().getFullYear(),
  admiteAnioCorto = true,
): string | null {
  const m = (admiteAnioCorto
    ? /^(\d{1,2})[/\-.](\d{1,2})[/\-.](\d{4}|\d{2})$/
    : /^(\d{1,2})[/\-.](\d{1,2})[/\-.](\d{4})$/
  ).exec(texto.trim())
  if (!m) return null
  const anio = expandirAnio(m[3], anioActual)
  if (anio < ANIO_MINIMO) return null
  const mes = Number(m[2])
  const dia = Number(m[1])
  if (!esFechaReal(anio, mes, dia)) return null
  return `${String(anio).padStart(4, '0')}-${String(mes).padStart(2, '0')}-${String(dia).padStart(2, '0')}`
}

/** Formatea lo tipeado en vivo: solo dígitos y separadores, con la `/` insertada al completar día y mes. */
export function formatearTipeo(crudo: string): string {
  const partes = ['']
  for (const caracter of crudo) {
    const ultima = partes.length - 1
    if (/\d/.test(caracter)) {
      if (ultima < 2 && partes[ultima].length === 2) partes.push(caracter)
      else if (partes[ultima].length < (ultima === 2 ? 4 : 2)) partes[ultima] += caracter
    } else if (/[/\-.]/.test(caracter) && ultima < 2 && partes[ultima] !== '') {
      partes.push('')
    }
  }
  return partes.join('/')
}

/** Como `formatearTipeo`, pero un `YYYY-MM-DD` real (por ejemplo, pegado) se muestra como `DD/MM/AAAA`. */
export function formatearTipeoOPegado(crudo: string): string {
  const texto = isoATexto(crudo.trim())
  return texto !== '' ? texto : formatearTipeo(crudo)
}

/** Un ISO fuera de `[min, max]` (ambos opcionales, también ISO) no es válido para el campo. */
export function dentroDeRango(iso: string, min?: string, max?: string): boolean {
  if (min && iso < min) return false
  if (max && iso > max) return false
  return true
}
