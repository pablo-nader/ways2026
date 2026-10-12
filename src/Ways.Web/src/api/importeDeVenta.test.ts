import { describe, expect, it } from 'vitest'
import { entradaConPuntoDecimal, esImporteDeVentaValido, esTeclaDeAtajo, IMPORTE_DE_VENTA_MAXIMO, leerImporteTipeado } from './importeDeVenta'

describe('esImporteDeVentaValido', () => {
  it('acepta un importe positivo de hasta dos decimales', () => {
    expect(esImporteDeVentaValido(150)).toBe(true)
    expect(esImporteDeVentaValido(150.5)).toBe(true)
    expect(esImporteDeVentaValido(0.01)).toBe(true)
    expect(esImporteDeVentaValido(150.1)).toBe(true)
    expect(esImporteDeVentaValido(123456789012.34)).toBe(true)
  })

  it('rechaza cero, negativos y no finitos', () => {
    expect(esImporteDeVentaValido(0)).toBe(false)
    expect(esImporteDeVentaValido(-1)).toBe(false)
    expect(esImporteDeVentaValido(Number.NaN)).toBe(false)
    expect(esImporteDeVentaValido(Number.POSITIVE_INFINITY)).toBe(false)
  })

  it('rechaza más de dos decimales', () => {
    expect(esImporteDeVentaValido(1.005)).toBe(false)
    expect(esImporteDeVentaValido(0.001)).toBe(false)
  })

  it('acepta el tope del servidor y rechaza lo que lo supera', () => {
    expect(esImporteDeVentaValido(IMPORTE_DE_VENTA_MAXIMO)).toBe(true)
    expect(esImporteDeVentaValido(IMPORTE_DE_VENTA_MAXIMO + 1)).toBe(false)
  })
})

describe('leerImporteTipeado', () => {
  it('lee un entero y un decimal con punto', () => {
    expect(leerImporteTipeado('150')).toBe(150)
    expect(leerImporteTipeado('150.50')).toBe(150.5)
  })

  it('lee la coma del cajero como separador decimal', () => {
    expect(leerImporteTipeado('150,50')).toBe(150.5)
    expect(leerImporteTipeado('1,500')).toBe(1.5)
  })

  it('ignora los espacios de los extremos', () => {
    expect(leerImporteTipeado('  150 ')).toBe(150)
  })

  it('acepta un punto final o inicial como el servidor', () => {
    expect(leerImporteTipeado('150.')).toBe(150)
    expect(leerImporteTipeado('.5')).toBe(0.5)
  })

  it('acepta ceros de más a la derecha del segundo decimal', () => {
    expect(leerImporteTipeado('150.500')).toBe(150.5)
  })

  it('rechaza vacío, cero, negativo, signo y texto', () => {
    for (const texto of ['', '   ', '0', '0.00', '-5', '+5', 'abc', '1e3', '15 0', '$150']) {
      expect(leerImporteTipeado(texto), texto).toBeNull()
    }
  })

  it('rechaza más de un separador y más de dos decimales', () => {
    for (const texto of ['1.234,50', '1,5,0', '1..5', '150.505', '150,505', '.']) {
      expect(leerImporteTipeado(texto), texto).toBeNull()
    }
  })

  it('rechaza lo que supera el tope', () => {
    expect(leerImporteTipeado('1000000000000')).toBeNull()
    expect(leerImporteTipeado('999999999999.99')).toBe(999999999999.99)
  })

  it('un valor ausente no es un importe', () => {
    expect(leerImporteTipeado(null)).toBeNull()
    expect(leerImporteTipeado(undefined)).toBeNull()
  })
})

describe('entradaConPuntoDecimal', () => {
  it('cambia la coma del importe por punto: es la forma que entiende el servidor', () => {
    expect(entradaConPuntoDecimal('150,50*500')).toBe('150.5*500')
    expect(entradaConPuntoDecimal(' 1,5 * 500 ')).toBe('1.5*500')
  })

  it('sin coma en el prefijo no hay nada que corregir', () => {
    expect(entradaConPuntoDecimal('150*500')).toBeNull()
    expect(entradaConPuntoDecimal('500')).toBeNull()
    expect(entradaConPuntoDecimal('150.50*500')).toBeNull()
  })

  it('un prefijo con coma que igual no es un importe válido no se corrige', () => {
    expect(entradaConPuntoDecimal('1.234,50*500')).toBeNull()
    expect(entradaConPuntoDecimal('150,555*500')).toBeNull()
    expect(entradaConPuntoDecimal('0,00*500')).toBeNull()
    expect(entradaConPuntoDecimal('x,5*500')).toBeNull()
  })

  it('una coma después del * es parte del código y no se toca', () => {
    expect(entradaConPuntoDecimal('150*5,00')).toBeNull()
  })
})

describe('esTeclaDeAtajo', () => {
  it('reconoce exactamente F5 a F8', () => {
    for (const tecla of ['F5', 'F6', 'F7', 'F8']) expect(esTeclaDeAtajo(tecla), tecla).toBe(true)
  })

  it('no reconoce las demás teclas de función ni otras teclas', () => {
    for (const tecla of ['F1', 'F2', 'F3', 'F4', 'F9', 'F10', 'F12', 'f5', 'Enter', '5', '']) expect(esTeclaDeAtajo(tecla), tecla).toBe(false)
  })
})
