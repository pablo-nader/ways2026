import { describe, expect, it } from 'vitest'
import { COLUMNAS_FUENTE_A, ConstructorDeTicket, codificarCp858, dosColumnas } from './escpos'

describe('codificarCp858', () => {
  it('deja pasar ASCII tal cual', () => {
    expect(Array.from(codificarCp858('Hola 123'))).toEqual([...'Hola 123'].map((c) => c.charCodeAt(0)))
  })

  it('traduce las vocales acentuadas y la ñ a su byte CP858', () => {
    expect(Array.from(codificarCp858('áéíóú'))).toEqual([0xa0, 0x82, 0xa1, 0xa2, 0xa3])
    expect(Array.from(codificarCp858('ñÑ'))).toEqual([0xa4, 0xa5])
  })

  it('traduce ü, ¿, ¡ y ° a su byte CP858', () => {
    expect(Array.from(codificarCp858('ü'))).toEqual([0x81])
    expect(Array.from(codificarCp858('¿'))).toEqual([0xa8])
    expect(Array.from(codificarCp858('¡'))).toEqual([0xad])
    expect(Array.from(codificarCp858('°'))).toEqual([0xf8])
  })

  it('cae a "?" (0x3F) para un carácter fuera de la tabla, sin lanzar', () => {
    // '😀' es un par subrogado UTF-16 (2 unidades de código) — la codificación es por unidad de
    // código, no por punto de código Unicode, así que produce dos '?' (mismo criterio que
    // "un byte por carácter de entrada" de abajo, `texto.length` cuenta unidades de código).
    expect(Array.from(codificarCp858('€😀中'))).toEqual([0x3f, 0x3f, 0x3f, 0x3f])
  })

  it('produce un byte por carácter de entrada', () => {
    const resultado = codificarCp858('café')
    expect(resultado).toHaveLength(4)
  })
})

describe('dosColumnas', () => {
  it('rellena con espacios hasta el ancho total, importe pegado a la derecha', () => {
    const linea = dosColumnas('2x Coca Cola', '$ 1.234,56')
    expect(linea).toHaveLength(COLUMNAS_FUENTE_A)
    expect(linea.endsWith('$ 1.234,56')).toBe(true)
    expect(linea.startsWith('2x Coca Cola')).toBe(true)
  })

  it('trunca la izquierda cuando no entra, nunca el importe', () => {
    const descripcionLarga = 'x'.repeat(60)
    const linea = dosColumnas(descripcionLarga, '$ 10,00')
    expect(linea).toHaveLength(COLUMNAS_FUENTE_A)
    expect(linea.endsWith('$ 10,00')).toBe(true)
    expect(linea.startsWith('x'.repeat(COLUMNAS_FUENTE_A - '$ 10,00'.length - 1))).toBe(true)
  })

  it('deja al menos un espacio de separación entre las dos columnas', () => {
    const linea = dosColumnas('x'.repeat(50), '$ 1,00')
    const indiceImporte = linea.indexOf('$ 1,00')
    expect(linea[indiceImporte - 1]).toBe(' ')
  })

  describe('límites de ancho (Fix judgment-day W3: nunca más de `ancho` caracteres)', () => {
    it('con derecha de largo ancho-1 e izquierda no vacía, nunca excede ancho (regresión: el piso de relleno forzado a 1 sumaba una columna de más)', () => {
      const derecha = '$'.repeat(COLUMNAS_FUENTE_A - 1)
      const linea = dosColumnas('descripcion', derecha, COLUMNAS_FUENTE_A)
      expect(linea).toHaveLength(COLUMNAS_FUENTE_A)
      expect(linea.endsWith(derecha)).toBe(true)
    })

    it('con derecha de largo ancho-1 e izquierda vacía, nunca excede ancho', () => {
      const derecha = '$'.repeat(COLUMNAS_FUENTE_A - 1)
      const linea = dosColumnas('', derecha, COLUMNAS_FUENTE_A)
      expect(linea).toHaveLength(COLUMNAS_FUENTE_A)
      expect(linea.endsWith(derecha)).toBe(true)
    })

    it('con derecha de largo exactamente ancho, la línea es derecha tal cual (izquierda descartada)', () => {
      const derecha = '$'.repeat(COLUMNAS_FUENTE_A)
      const linea = dosColumnas('descripcion', derecha, COLUMNAS_FUENTE_A)
      expect(linea).toHaveLength(COLUMNAS_FUENTE_A)
      expect(linea).toBe(derecha)
    })

    it('con derecha más largo que ancho (ancho+5), se trunca a ancho — la izquierda nunca se cuela', () => {
      const derecha = '$'.repeat(COLUMNAS_FUENTE_A + 5)
      const linea = dosColumnas('descripcion', derecha, COLUMNAS_FUENTE_A)
      expect(linea).toHaveLength(COLUMNAS_FUENTE_A)
      expect(linea).toBe(derecha.slice(0, COLUMNAS_FUENTE_A))
    })
  })
})

describe('ConstructorDeTicket', () => {
  it('inicializar emite ESC @', () => {
    const bytes = new ConstructorDeTicket().inicializar().bytes()
    expect(Array.from(bytes)).toEqual([0x1b, 0x40])
  })

  it('codificarPagina emite ESC t 19 (CP858)', () => {
    const bytes = new ConstructorDeTicket().codificarPagina().bytes()
    expect(Array.from(bytes)).toEqual([0x1b, 0x74, 19])
  })

  it('alinear centro/derecha/izquierda emite ESC a n con el código correcto', () => {
    expect(Array.from(new ConstructorDeTicket().alinear('izquierda').bytes())).toEqual([0x1b, 0x61, 0])
    expect(Array.from(new ConstructorDeTicket().alinear('centro').bytes())).toEqual([0x1b, 0x61, 1])
    expect(Array.from(new ConstructorDeTicket().alinear('derecha').bytes())).toEqual([0x1b, 0x61, 2])
  })

  it('negrita on/off emite ESC E n', () => {
    expect(Array.from(new ConstructorDeTicket().negrita(true).bytes())).toEqual([0x1b, 0x45, 1])
    expect(Array.from(new ConstructorDeTicket().negrita(false).bytes())).toEqual([0x1b, 0x45, 0])
  })

  it('tamanioDoble on/off emite GS ! n', () => {
    expect(Array.from(new ConstructorDeTicket().tamanioDoble(true).bytes())).toEqual([0x1d, 0x21, 0x11])
    expect(Array.from(new ConstructorDeTicket().tamanioDoble(false).bytes())).toEqual([0x1d, 0x21, 0x00])
  })

  it('linea agrega el texto codificado más LF', () => {
    const bytes = new ConstructorDeTicket().linea('hola').bytes()
    expect(Array.from(bytes)).toEqual([...codificarCp858('hola'), 0x0a])
  })

  it('lineaDeGuiones cubre las 48 columnas', () => {
    const bytes = new ConstructorDeTicket().lineaDeGuiones().bytes()
    expect(bytes).toHaveLength(COLUMNAS_FUENTE_A + 1)
    expect(Array.from(bytes.slice(0, COLUMNAS_FUENTE_A)).every((b) => b === '-'.charCodeAt(0))).toBe(true)
    expect(bytes[COLUMNAS_FUENTE_A]).toBe(0x0a)
  })

  it('avanzar emite n bytes LF', () => {
    const bytes = new ConstructorDeTicket().avanzar(3).bytes()
    expect(Array.from(bytes)).toEqual([0x0a, 0x0a, 0x0a])
  })

  it('cortar emite GS V 66 0 (corte parcial)', () => {
    const bytes = new ConstructorDeTicket().cortar().bytes()
    expect(Array.from(bytes)).toEqual([0x1d, 0x56, 0x42, 0x00])
  })

  it('abrirCajon emite ESC p 0 25 250', () => {
    const bytes = new ConstructorDeTicket().abrirCajon().bytes()
    expect(Array.from(bytes)).toEqual([0x1b, 0x70, 0x00, 25, 250])
  })

  it('encadena varios comandos en orden', () => {
    const bytes = new ConstructorDeTicket().inicializar().alinear('centro').linea('A').cortar().bytes()
    expect(Array.from(bytes)).toEqual([0x1b, 0x40, 0x1b, 0x61, 1, 0x41, 0x0a, 0x1d, 0x56, 0x42, 0x00])
  })
})
