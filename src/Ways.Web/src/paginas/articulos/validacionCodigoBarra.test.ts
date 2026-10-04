import { describe, expect, it } from 'vitest'
import { advertenciaDeCodigoBarra } from './validacionCodigoBarra'

describe('advertenciaDeCodigoBarra — códigos que parecen GTIN válidos', () => {
  it.each([
    ['EAN-13', '7790001234568'],
    ['EAN-13 con dígito verificador 1', '4006381333931'],
    ['UPC-A (12 dígitos)', '036000291452'],
    ['EAN-8', '96385074'],
    ['GTIN-14', '10012345678902'],
  ])('%s no advierte', (_nombre, codigo) => {
    expect(advertenciaDeCodigoBarra(codigo)).toBeNull()
  })

  it('el dígito verificador 0 se acepta (el resto de la suma es múltiplo de 10)', () => {
    expect(advertenciaDeCodigoBarra('4006381333900')).toBeNull()
  })

  it('el vacío no se evalúa', () => {
    expect(advertenciaDeCodigoBarra('')).toBeNull()
  })
})

describe('advertenciaDeCodigoBarra — caracteres que no son dígitos', () => {
  it.each(['ABC12345', '77900O1234568', '7790001-234568', 'ABC-123'])('"%s" advierte que no es un código estándar', (codigo) => {
    expect(advertenciaDeCodigoBarra(codigo)).toBe(
      'El código contiene caracteres que no son dígitos, por lo que no es un código GTIN estándar (EAN/UPC).',
    )
  })

  it('un espacio interno también cuenta como carácter no numérico', () => {
    expect(advertenciaDeCodigoBarra('7790001 234568')).toContain('no son dígitos')
  })
})

describe('advertenciaDeCodigoBarra — largo', () => {
  it.each([2, 7, 9, 10, 11, 15])('%i dígitos advierte con el largo real (plural) y los esperados', (largo) => {
    expect(advertenciaDeCodigoBarra('1'.repeat(largo))).toBe(
      `El código tiene ${largo} dígitos y los códigos GTIN estándar tienen 8, 12, 13 o 14.`,
    )
  })

  it('un solo dígito usa el singular', () => {
    expect(advertenciaDeCodigoBarra('1')).toBe('El código tiene 1 dígito y los códigos GTIN estándar tienen 8, 12, 13 o 14.')
  })
})

describe('advertenciaDeCodigoBarra — dígito verificador', () => {
  const MENSAJE = 'El dígito verificador no coincide: puede haber un error de tipeo.'

  it('el 7790001234567 de los fixtures tiene el verificador equivocado (el correcto es 8)', () => {
    expect(advertenciaDeCodigoBarra('7790001234567')).toBe(MENSAJE)
  })

  it.each([
    ['EAN-13', '7790001234569'],
    ['UPC-A', '036000291453'],
    ['EAN-8', '96385075'],
    ['GTIN-14', '10012345678903'],
  ])('%s con el último dígito cambiado advierte', (_nombre, codigo) => {
    expect(advertenciaDeCodigoBarra(codigo)).toBe(MENSAJE)
  })

  it('una transposición de dígitos adyacentes de pesos distintos se detecta', () => {
    // 7790001234568 → se intercambian el 3 y el 4 (posiciones de peso 1 y 3)
    expect(advertenciaDeCodigoBarra('7790001243568')).toBe(MENSAJE)
  })

  it('un dígito de datos distinto con el mismo verificador advierte', () => {
    expect(advertenciaDeCodigoBarra('7790001234668')).toBe(MENSAJE)
  })
})
