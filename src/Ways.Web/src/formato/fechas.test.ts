import { describe, expect, it } from 'vitest'
import { dentroDeRango, esFechaReal, expandirAnio, formatearTipeo, isoATexto, textoAIso } from './fechas'

describe('esFechaReal', () => {
  it('rechaza días inexistentes y acepta el 29/02 solo en bisiestos', () => {
    expect(esFechaReal(2026, 2, 31)).toBe(false)
    expect(esFechaReal(2026, 4, 31)).toBe(false)
    expect(esFechaReal(2026, 2, 29)).toBe(false)
    expect(esFechaReal(2028, 2, 29)).toBe(true)
    expect(esFechaReal(2026, 13, 1)).toBe(false)
    expect(esFechaReal(2026, 12, 0)).toBe(false)
  })
})

describe('isoATexto', () => {
  it('muestra DD/MM/AAAA', () => {
    expect(isoATexto('2026-08-05')).toBe('05/08/2026')
  })

  it('lo vacío o inválido queda vacío', () => {
    expect(isoATexto('')).toBe('')
    expect(isoATexto('2026-02-31')).toBe('')
    expect(isoATexto('05/08/2026')).toBe('')
  })
})

describe('expandirAnio', () => {
  it('un año de cuatro dígitos queda igual', () => {
    expect(expandirAnio('1985', 2026)).toBe(1985)
  })

  it('uno de dos dígitos cae hasta 20 años hacia adelante y si no en el siglo anterior', () => {
    expect(expandirAnio('26', 2026)).toBe(2026)
    expect(expandirAnio('46', 2026)).toBe(2046)
    expect(expandirAnio('47', 2026)).toBe(1947)
    expect(expandirAnio('85', 2026)).toBe(1985)
  })
})

describe('textoAIso', () => {
  it('acepta día y mes de uno o dos dígitos y año de dos o cuatro', () => {
    expect(textoAIso('05/08/2026', 2026)).toBe('2026-08-05')
    expect(textoAIso('5/8/2026', 2026)).toBe('2026-08-05')
    expect(textoAIso('5/8/26', 2026)).toBe('2026-08-05')
    expect(textoAIso('5-8-2026', 2026)).toBe('2026-08-05')
  })

  it('con admiteAnioCorto en false exige los cuatro dígitos del año', () => {
    expect(textoAIso('5/8/26', 2026, false)).toBeNull()
    expect(textoAIso('5/8/2026', 2026, false)).toBe('2026-08-05')
  })

  it('rechaza fechas que no existen en el calendario', () => {
    expect(textoAIso('31/02/2026', 2026)).toBeNull()
    expect(textoAIso('29/02/2026', 2026)).toBeNull()
    expect(textoAIso('29/02/2028', 2026)).toBe('2028-02-29')
    expect(textoAIso('00/01/2026', 2026)).toBeNull()
    expect(textoAIso('10/13/2026', 2026)).toBeNull()
  })

  it('rechaza texto incompleto o con otro formato', () => {
    expect(textoAIso('', 2026)).toBeNull()
    expect(textoAIso('05/08', 2026)).toBeNull()
    expect(textoAIso('05/08/202', 2026)).toBeNull()
    expect(textoAIso('2026-08-05', 2026)).toBeNull()
    expect(textoAIso('ab/cd/efgh', 2026)).toBeNull()
  })
})

describe('formatearTipeo', () => {
  it('inserta la barra al completar día y mes', () => {
    expect(formatearTipeo('1')).toBe('1')
    expect(formatearTipeo('12')).toBe('12')
    expect(formatearTipeo('123')).toBe('12/3')
    expect(formatearTipeo('1208')).toBe('12/08')
    expect(formatearTipeo('12082')).toBe('12/08/2')
    expect(formatearTipeo('12082026')).toBe('12/08/2026')
  })

  it('respeta las barras tipeadas para días y meses de un dígito', () => {
    expect(formatearTipeo('1/')).toBe('1/')
    expect(formatearTipeo('1/2/')).toBe('1/2/')
    expect(formatearTipeo('1/2/26')).toBe('1/2/26')
  })

  it('descarta letras, separadores sin dígito previo y todo lo que sobra del año', () => {
    expect(formatearTipeo('a1b2')).toBe('12')
    expect(formatearTipeo('/')).toBe('')
    expect(formatearTipeo('//')).toBe('')
    expect(formatearTipeo('1208202699')).toBe('12/08/2026')
  })

  it('cambiar el separador por otro admitido lo normaliza a barra', () => {
    expect(formatearTipeo('1-2-2026')).toBe('1/2/2026')
  })
})

describe('dentroDeRango', () => {
  it('sin límites siempre es válido', () => {
    expect(dentroDeRango('2026-08-05')).toBe(true)
  })

  it('respeta mínimo y máximo inclusivos', () => {
    expect(dentroDeRango('2026-08-05', '2026-08-05', '2026-08-05')).toBe(true)
    expect(dentroDeRango('2026-08-04', '2026-08-05')).toBe(false)
    expect(dentroDeRango('2026-08-06', undefined, '2026-08-05')).toBe(false)
  })
})
