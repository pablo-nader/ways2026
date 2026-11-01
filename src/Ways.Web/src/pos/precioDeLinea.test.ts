import { describe, expect, it } from 'vitest'
import type { OfertaAplicada, ResultadoDeResolucion } from '../api/tipos'
import { desgloseDeOferta, textoDeDesglose } from './precioDeLinea'

const ESTRICTA: OfertaAplicada = { idOferta: 1, nombre: '2x7500', descuentoUnitario: 250, multiploDe: 2 }

function resultado(sobrescribir: Partial<ResultadoDeResolucion> = {}): ResultadoDeResolucion {
  return {
    idArticulo: 1,
    idListaPrecio: 1,
    precioOriginal: 4000,
    precioFinal: 3750,
    descuentoUnitario: 250,
    aplicadas: [ESTRICTA],
    descuentoDeLinea: 500,
    ...sobrescribir,
  }
}

describe('desgloseDeOferta', () => {
  it('3 unidades con 2x estricta: 2 con oferta y 1 a precio regular', () => {
    const desglose = desgloseDeOferta(3, resultado())
    expect(desglose).toEqual({ unidadesConOferta: 2, precioConOferta: 3750, unidadesSinOferta: 1, precioSinOferta: 4000 })
    expect(textoDeDesglose(desglose!)).toBe('2 × $ 3.750,00 + 1 × $ 4.000,00')
  })

  it('4 unidades con 2x estricta: todas cubiertas, sin desglose', () => {
    expect(desgloseDeOferta(4, resultado({ descuentoDeLinea: 1000 }))).toBeNull()
  })

  it('oferta no estricta: sin desglose', () => {
    const comun: OfertaAplicada = { idOferta: 2, nombre: '10%', descuentoUnitario: 250 }
    expect(desgloseDeOferta(3, resultado({ aplicadas: [comun], descuentoDeLinea: undefined }))).toBeNull()
  })

  it('sin ninguna unidad cubierta: todo a precio regular', () => {
    const desglose = desgloseDeOferta(1, resultado({ descuentoDeLinea: 0 }))
    expect(desglose).toEqual({ unidadesConOferta: 0, precioConOferta: 3750, unidadesSinOferta: 1, precioSinOferta: 4000 })
    expect(textoDeDesglose(desglose!)).toBe('1 × $ 4.000,00')
  })

  it('familia: las unidades cubiertas salen del descuento real de la línea, no de su cantidad sola', () => {
    const familia: OfertaAplicada = { ...ESTRICTA, porFamilia: true }
    const desglose = desgloseDeOferta(3, resultado({ aplicadas: [familia], descuentoDeLinea: 250 }))
    expect(desglose).toMatchObject({ unidadesConOferta: 1, unidadesSinOferta: 2, precioSinOferta: 4000 })
  })

  it('con una oferta común además de la estricta, el descuento común alcanza a todas las unidades', () => {
    const comun: OfertaAplicada = { idOferta: 2, nombre: '100 menos', descuentoUnitario: 100 }
    const desglose = desgloseDeOferta(3, resultado({ aplicadas: [comun, ESTRICTA], precioFinal: 3650, descuentoUnitario: 350, descuentoDeLinea: 800 }))
    expect(desglose).toEqual({ unidadesConOferta: 2, precioConOferta: 3650, unidadesSinOferta: 1, precioSinOferta: 3900 })
  })

  it('descuento que no cierra con un número entero de unidades: sin desglose', () => {
    expect(desgloseDeOferta(3, resultado({ descuentoDeLinea: 400 }))).toBeNull()
  })

  it('sin resolución o sin precio: sin desglose', () => {
    expect(desgloseDeOferta(3, undefined)).toBeNull()
    expect(desgloseDeOferta(3, resultado({ precioFinal: null }))).toBeNull()
  })

  it('las unidades de un combo no cuentan como fuera de oferta', () => {
    const conCombo = resultado({ descuentoDeLinea: 500, combo: { unidadesConsumidas: 1, ahorro: 100, idCombo: 9 } })
    expect(desgloseDeOferta(4, conCombo)).toMatchObject({ unidadesConOferta: 2, unidadesSinOferta: 1 })
  })
})
