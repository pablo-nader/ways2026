import { describe, expect, it } from 'vitest'
import {
  DIGITOS_NUMERO,
  DIGITOS_PUNTO_DE_VENTA,
  dividirNumero,
  estaMedioLleno,
  rellenarConCeros,
  repartirPegado,
  soloDigitos,
  tieneFormatoEstandar,
  unirNumero,
  valorMedioLleno,
} from './numeroDeComprobante'

describe('soloDigitos', () => {
  it('descarta lo que no es dígito y corta al máximo', () => {
    expect(soloDigitos('a1b2-3c', 4)).toBe('123')
    expect(soloDigitos('123456', DIGITOS_PUNTO_DE_VENTA)).toBe('12345')
    expect(soloDigitos('123456789', 8)).toBe('12345678')
    expect(soloDigitos('', 4)).toBe('')
  })
})

describe('rellenarConCeros', () => {
  it('completa a la izquierda hasta el largo pedido', () => {
    expect(rellenarConCeros('10', 4)).toBe('0010')
    expect(rellenarConCeros('9985', 8)).toBe('00009985')
  })

  it('no toca un campo ya completo y deja vacío el vacío', () => {
    expect(rellenarConCeros('0010', 4)).toBe('0010')
    expect(rellenarConCeros('', 4)).toBe('')
  })

  it('el punto de venta se completa a cinco dígitos y el número a ocho', () => {
    expect(DIGITOS_PUNTO_DE_VENTA).toBe(5)
    expect(DIGITOS_NUMERO).toBe(8)
    expect(rellenarConCeros('10', DIGITOS_PUNTO_DE_VENTA)).toBe('00010')
  })
})

describe('unirNumero', () => {
  it('une las partes con guion', () => {
    expect(unirNumero({ puntoVenta: '0010', numero: '00009985' })).toBe('0010-00009985')
  })

  it('con las dos partes vacías no hay número', () => {
    expect(unirNumero({ puntoVenta: '', numero: '' })).toBe('')
  })

  it('con una sola parte conserva la otra vacía', () => {
    expect(unirNumero({ puntoVenta: '0010', numero: '' })).toBe('0010-')
    expect(unirNumero({ puntoVenta: '', numero: '00009985' })).toBe('-00009985')
  })
})

describe('dividirNumero', () => {
  it('divide el formato estándar de cinco dígitos de punto de venta', () => {
    expect(dividirNumero('00003-00012345')).toEqual({ puntoVenta: '00003', numero: '00012345' })
  })

  it('un valor guardado con cuatro dígitos de punto de venta se muestra completo, sin recortar', () => {
    expect(dividirNumero('0003-00012345')).toEqual({ puntoVenta: '0003', numero: '00012345' })
  })

  it('un punto de venta de seis dígitos no es el formato y se muestra recortado a cinco', () => {
    expect(dividirNumero('000003-00012345')).toEqual({ puntoVenta: '00000', numero: '00012345' })
  })

  it('acepta partes sin completar y las devuelve tal cual', () => {
    expect(dividirNumero('3-12345')).toEqual({ puntoVenta: '3', numero: '12345' })
  })

  it('el vacío y el blanco dan partes vacías', () => {
    expect(dividirNumero('')).toEqual({ puntoVenta: '', numero: '' })
    expect(dividirNumero('   ')).toEqual({ puntoVenta: '', numero: '' })
  })

  it('un valor anterior al formato se muestra en lo posible', () => {
    expect(dividirNumero('A-0001-00000012')).toEqual({ puntoVenta: '0001', numero: '00000012' })
    expect(dividirNumero('12345')).toEqual({ puntoVenta: '', numero: '12345' })
    expect(dividirNumero('abc')).toEqual({ puntoVenta: '', numero: '' })
  })

  it('unir lo dividido devuelve el valor estándar original', () => {
    expect(unirNumero(dividirNumero('0003-00012345'))).toBe('0003-00012345')
  })
})

describe('tieneFormatoEstandar', () => {
  it('reconoce el formato, el anterior de cuatro dígitos y el vacío', () => {
    expect(tieneFormatoEstandar('00003-00012345')).toBe(true)
    expect(tieneFormatoEstandar('0003-00012345')).toBe(true)
    expect(tieneFormatoEstandar('')).toBe(true)
  })

  it('rechaza lo que no es PPPPP-NNNNNNNN', () => {
    expect(tieneFormatoEstandar('A-0001-00000012')).toBe(false)
    expect(tieneFormatoEstandar('12345')).toBe(false)
    expect(tieneFormatoEstandar('000003-00012345')).toBe(false)
    expect(tieneFormatoEstandar('00003-000123456')).toBe(false)
  })
})

describe('estaMedioLleno', () => {
  it('es verdadero solo cuando exactamente una parte tiene dígitos', () => {
    expect(estaMedioLleno({ puntoVenta: '0010', numero: '' })).toBe(true)
    expect(estaMedioLleno({ puntoVenta: '', numero: '00009985' })).toBe(true)
  })

  it('con las dos partes llenas o las dos vacías no está a medio llenar', () => {
    expect(estaMedioLleno({ puntoVenta: '0010', numero: '00009985' })).toBe(false)
    expect(estaMedioLleno({ puntoVenta: '', numero: '' })).toBe(false)
  })
})

describe('valorMedioLleno', () => {
  it('reconoce las dos formas que produce unirNumero con una sola parte', () => {
    expect(valorMedioLleno(unirNumero({ puntoVenta: '0010', numero: '' }))).toBe(true)
    expect(valorMedioLleno(unirNumero({ puntoVenta: '', numero: '00009985' }))).toBe(true)
  })

  it('reconoce el punto de venta de cinco dígitos y no el de seis', () => {
    expect(valorMedioLleno('00010-')).toBe(true)
    expect(valorMedioLleno('000010-')).toBe(false)
  })

  it('no confunde el vacío, un número completo ni un valor anterior al formato', () => {
    expect(valorMedioLleno('')).toBe(false)
    expect(valorMedioLleno('0010-00009985')).toBe(false)
    expect(valorMedioLleno('12345')).toBe(false)
    expect(valorMedioLleno('A-0001-00000012')).toBe(false)
  })
})

describe('repartirPegado', () => {
  it('reparte un número con guion y rellena con ceros', () => {
    expect(repartirPegado('0010-00009985')).toEqual({ puntoVenta: '00010', numero: '00009985' })
    expect(repartirPegado(' 10 - 9985 ')).toEqual({ puntoVenta: '00010', numero: '00009985' })
    expect(repartirPegado('00010-00009985')).toEqual({ puntoVenta: '00010', numero: '00009985' })
  })

  it('reparte doce dígitos corridos en cuatro y ocho', () => {
    expect(repartirPegado('001000009985')).toEqual({ puntoVenta: '00010', numero: '00009985' })
  })

  it('reparte trece dígitos corridos en cinco y ocho', () => {
    expect(repartirPegado('0001000009985')).toEqual({ puntoVenta: '00010', numero: '00009985' })
  })

  it('cualquier otro texto no es un número completo', () => {
    expect(repartirPegado('0010')).toBeNull()
    expect(repartirPegado('12345')).toBeNull()
    expect(repartirPegado('000010-00009985')).toBeNull()
    expect(repartirPegado('00010000099851')).toBeNull()
    expect(repartirPegado('00100009985')).toBeNull()
    expect(repartirPegado('A-0001-00000012')).toBeNull()
    expect(repartirPegado('')).toBeNull()
  })
})
