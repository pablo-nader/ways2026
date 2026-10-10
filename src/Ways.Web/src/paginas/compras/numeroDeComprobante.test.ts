import { describe, expect, it } from 'vitest'
import {
  dividirNumero,
  rellenarConCeros,
  soloDigitos,
  tieneFormatoEstandar,
  unirNumero,
} from './numeroDeComprobante'

describe('soloDigitos', () => {
  it('descarta lo que no es dígito y corta al máximo', () => {
    expect(soloDigitos('a1b2-3c', 4)).toBe('123')
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
  it('divide el formato estándar', () => {
    expect(dividirNumero('0003-00012345')).toEqual({ puntoVenta: '0003', numero: '00012345' })
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
  it('reconoce el formato y el vacío', () => {
    expect(tieneFormatoEstandar('0003-00012345')).toBe(true)
    expect(tieneFormatoEstandar('')).toBe(true)
  })

  it('rechaza lo que no es PPPP-NNNNNNNN', () => {
    expect(tieneFormatoEstandar('A-0001-00000012')).toBe(false)
    expect(tieneFormatoEstandar('12345')).toBe(false)
    expect(tieneFormatoEstandar('00003-00012345')).toBe(false)
  })
})
