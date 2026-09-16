/**
 * Builder ESC/POS puro para térmicas de 80mm, Fuente A (48 columnas) — stage-desktop-pos.
 * Sin dependencias de DOM ni de Tauri: produce un `Uint8Array` de comandos crudos que
 * `impresora.ts` manda tal cual al puente nativo. Ningún método hace I/O.
 */

/** Columnas de la Fuente A en una térmica de 80mm (paridad con el ticket legacy). */
export const COLUMNAS_FUENTE_A = 48

/**
 * Tabla CP858 (Latin-1 + Euro, superset de CP850) para los caracteres del español que no son
 * ASCII — el resto de la tabla es idéntica a ASCII de 0x20 a 0x7E. Un carácter fuera de esta
 * tabla y fuera de ASCII cae al fallback `?` (`codificarCp858`) en vez de romper la impresión.
 */
const TABLA_CP858: ReadonlyMap<string, number> = new Map([
  ['ü', 0x81],
  ['é', 0x82],
  ['á', 0xa0],
  ['í', 0xa1],
  ['ó', 0xa2],
  ['ú', 0xa3],
  ['ñ', 0xa4],
  ['Ñ', 0xa5],
  ['¿', 0xa8],
  ['¡', 0xad],
  ['°', 0xf8],
  // Mayúsculas acentuadas: no están en el subconjunto mínimo del contrato, pero aparecen en
  // nombres reales (clientes, artículos) y el fallback '?' sería peor que este agregado.
  ['É', 0x90],
  ['Á', 0xb5],
  ['Í', 0xd6],
  ['Ó', 0xe0],
  ['Ú', 0xe9],
  ['Ü', 0x9a],
])

const CODIGO_INTERROGACION = 0x3f

/**
 * Codifica un string UTF-16 de JS a bytes CP858: ASCII pasa directo, los caracteres de
 * `TABLA_CP858` se traducen, y cualquier otro carácter no representable cae a `?` — nunca lanza,
 * una impresión no fiscal no puede fallar por un carácter raro en un nombre.
 */
export function codificarCp858(texto: string): Uint8Array {
  const bytes = new Uint8Array(texto.length)
  for (let i = 0; i < texto.length; i++) {
    const caracter = texto[i]
    const codigo = caracter.codePointAt(0) ?? CODIGO_INTERROGACION
    if (codigo < 0x80) {
      bytes[i] = codigo
    } else {
      bytes[i] = TABLA_CP858.get(caracter) ?? CODIGO_INTERROGACION
    }
  }
  return bytes
}

/**
 * Línea de dos columnas (descripción / importe) truncando la izquierda cuando no entra —
 * nunca la derecha, que es el importe y tiene que quedar siempre legible.
 */
export function dosColumnas(izquierda: string, derecha: string, ancho = COLUMNAS_FUENTE_A): string {
  if (derecha.length >= ancho) return derecha.slice(0, ancho)

  const disponibleIzquierda = Math.max(ancho - derecha.length - 1, 1)
  const izquierdaTruncada = izquierda.length > disponibleIzquierda ? izquierda.slice(0, disponibleIzquierda) : izquierda
  const relleno = Math.max(ancho - izquierdaTruncada.length - derecha.length, 1)

  return izquierdaTruncada + ' '.repeat(relleno) + derecha
}

type Alineacion = 'izquierda' | 'centro' | 'derecha'

const CODIGO_ALINEACION: Record<Alineacion, number> = { izquierda: 0, centro: 1, derecha: 2 }

/** Builder fluido — cada método apila bytes, `bytes()` concatena todo al final. */
export class ConstructorDeTicket {
  private readonly partes: number[] = []

  private agregar(...valores: number[]): this {
    this.partes.push(...valores)
    return this
  }

  private agregarTexto(texto: string): this {
    this.partes.push(...codificarCp858(texto))
    return this
  }

  /** `ESC @` — resetea el estado del firmware al arrancar el ticket. */
  inicializar(): this {
    return this.agregar(0x1b, 0x40)
  }

  /** `ESC t 19` — tabla de códigos CP858. */
  codificarPagina(): this {
    return this.agregar(0x1b, 0x74, 19)
  }

  /** `ESC a n` */
  alinear(modo: Alineacion): this {
    return this.agregar(0x1b, 0x61, CODIGO_ALINEACION[modo])
  }

  /** `ESC E n` */
  negrita(activa: boolean): this {
    return this.agregar(0x1b, 0x45, activa ? 1 : 0)
  }

  /** `GS ! n` — bits altos = ancho doble, bits bajos = alto doble. */
  tamanioDoble(activo: boolean): this {
    return this.agregar(0x1d, 0x21, activo ? 0x11 : 0x00)
  }

  /** Una línea de texto + salto de línea (`LF`). */
  linea(texto = ''): this {
    return this.agregarTexto(texto).agregar(0x0a)
  }

  /** Línea de guiones a todo el ancho de la Fuente A. */
  lineaDeGuiones(): this {
    return this.linea('-'.repeat(COLUMNAS_FUENTE_A))
  }

  /** Línea de dos columnas — ver `dosColumnas`. */
  lineaDeColumnas(izquierda: string, derecha: string): this {
    return this.linea(dosColumnas(izquierda, derecha))
  }

  /** `LF` × n, o `ESC d n` si se prefiere un único comando — se usa LF simple por simplicidad y
   * compatibilidad amplia de firmwares. */
  avanzar(lineas = 1): this {
    for (let i = 0; i < lineas; i++) this.partes.push(0x0a)
    return this
  }

  /** `GS V 66 0` — corte parcial. */
  cortar(): this {
    return this.agregar(0x1d, 0x56, 0x42, 0x00)
  }

  /** `ESC p 0 25 250` — pulso del cajón de dinero conectado al puerto 0 de la impresora. */
  abrirCajon(): this {
    return this.agregar(0x1b, 0x70, 0x00, 25, 250)
  }

  bytes(): Uint8Array {
    return new Uint8Array(this.partes)
  }
}
