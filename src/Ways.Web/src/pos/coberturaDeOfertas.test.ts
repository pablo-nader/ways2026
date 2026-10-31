import { describe, expect, it } from 'vitest'
import { calcularTotalesDeLinea } from '../api/ajusteManual'
import type { OfertaAplicada } from '../api/tipos'
import { descuentosDeLinea, descuentoUnitarioDeAplicadas, tieneSoloMultiplos } from './coberturaDeOfertas'

// Tabla de paridad servidor/web: los mismos casos y números que
// `tests/Ways.Domain.Tests/Ofertas/CoberturaDeOfertasTests.cs` (`CoberturaDeOfertas` más `CalculadorDeTotales`). Las
// ofertas aplicadas son las que el motor del servidor informa para esos casos (acá no se evalúan ofertas). Un cambio de
// un número en una de las dos tablas exige el mismo cambio en la otra. Los casos del motor (`ElResolvedorInforma…`,
// `UnaOfertaComunNoInforma…`) y de la validación del alta no tienen par: el POS no evalúa ofertas.

const PRECIO = 4000

/** "2 x 7500" a 4000: el motor informa 250 por unidad desde 2 unidades. */
const estricta = (descuentoUnitario = 250, multiploDe = 2, idOferta = 1): OfertaAplicada => ({ idOferta, nombre: '2x7500', descuentoUnitario, multiploDe })
const comun = (descuentoUnitario: number, idOferta = 2): OfertaAplicada => ({ idOferta, nombre: 'comun', descuentoUnitario })

function totalDeLinea(cantidad: number, aplicadas: OfertaAplicada[]): number {
  const [descuentoDeLinea] = descuentosDeLinea([{ unidades: cantidad, precioOriginal: PRECIO, aplicadas }])
  return calcularTotalesDeLinea({
    cantidad,
    precioOriginal: PRECIO,
    descuentoUnitario: descuentoUnitarioDeAplicadas(PRECIO, aplicadas),
    porcentaje: null,
    descuentoDeLinea,
  }).total
}

describe('coberturaDeOfertas — paridad con CoberturaDeOfertasTests', () => {
  it.each([
    [1, 4000],
    [2, 7500],
    [3, 11500],
    [4, 15000],
    [5, 19000],
  ])('solo múltiplos cobra el precio de oferta solo en los grupos completos: %d → %d', (cantidad, total) => {
    // Debajo de la cantidad mínima el motor no aplica la oferta.
    expect(totalDeLinea(cantidad, cantidad >= 2 ? [estricta()] : [])).toBe(total)
  })

  it.each([
    [2, 7500],
    [3, 11250],
  ])('sin solo múltiplos todas las unidades llevan la oferta: %d → %d', (cantidad, total) => {
    expect(totalDeLinea(cantidad, [comun(250)])).toBe(total)
  })

  it('vale para un porcentaje: 10 % de 4000 en 2 de las 3 unidades', () => {
    expect(totalDeLinea(3, [estricta(400)])).toBe(11200)
  })

  it('vale para un importe fijo: 300 en 6 de las 7 unidades con cantidad mínima 3', () => {
    expect(totalDeLinea(7, [estricta(300, 3)])).toBe(26200)
  })

  it('una acumulable común se suma a todas las unidades y la solo múltiplos solo a las del grupo', () => {
    // 3 × 4000 − (2 × 250) − (3 × 100).
    expect(totalDeLinea(3, [estricta(), comun(100)])).toBe(11200)
  })

  it('el descuento de cada unidad no supera el precio original', () => {
    // Dos unidades cubiertas por las dos (tope 100 cada una) y una solo por la común (50).
    expect(descuentosDeLinea([{ unidades: 3, precioOriginal: 100, aplicadas: [estricta(80), comun(50)] }])).toEqual([250])
  })

  it('sin solo múltiplos el descuento es unidades por descuento unitario', () => {
    const aplicadas = [comun(150, 1)]
    expect(descuentosDeLinea([{ unidades: 3, precioOriginal: 1000, aplicadas }])).toEqual([450])
    expect(tieneSoloMultiplos(aplicadas)).toBe(false)
    expect(tieneSoloMultiplos([estricta()])).toBe(true)
  })

  it('la familia cuenta los grupos sobre el documento y el resto cae en las últimas líneas', () => {
    const aplicadas = [{ ...estricta(), porFamilia: true }]
    // 2 + 2 + 1 = 5 unidades de la familia: dos grupos (4 unidades), la última línea queda sin descuento.
    expect(descuentosDeLinea([2, 2, 1].map((unidades) => ({ unidades, precioOriginal: PRECIO, aplicadas })))).toEqual([500, 500, 0])
  })

  it('la familia arma grupos con unidades de líneas distintas', () => {
    const aplicadas = [{ ...estricta(), porFamilia: true }]
    // 1 + 1 + 1 = 3: un grupo con las dos primeras líneas, la tercera paga el precio original.
    expect(descuentosDeLinea([1, 1, 1].map((unidades) => ({ unidades, precioOriginal: PRECIO, aplicadas })))).toEqual([250, 250, 0])
  })

  it('una oferta solo múltiplos de artículo cuenta sus grupos por línea', () => {
    const aplicadas = [estricta()]
    expect(descuentosDeLinea([1, 3].map((unidades) => ({ unidades, precioOriginal: PRECIO, aplicadas })))).toEqual([0, 500])
  })

  it('las unidades negativas no cubren nada', () => {
    expect(descuentosDeLinea([{ unidades: -2, precioOriginal: PRECIO, aplicadas: [estricta()] }])).toEqual([0])
  })
})

describe('coberturaDeOfertas — aritmética del POS', () => {
  it('cuenta grupos de una cantidad mínima fraccionaria en decimal exacto: 1,234 kg con grupos de 0,4 cubre 1,2', () => {
    // 0,4 × 3 = 1,2 kg a 12,34 por kilo: 14,808 sin redondear (el neto se redondea al peso después).
    expect(descuentosDeLinea([{ unidades: 1.234, precioOriginal: 1000, aplicadas: [estricta(12.34, 0.4)] }])).toEqual([14.808])
  })

  it('el descuento unitario de la resolución entera es la suma con el tope del precio original', () => {
    expect(descuentoUnitarioDeAplicadas(100, [estricta(60), comun(70)])).toBe(100)
    expect(descuentoUnitarioDeAplicadas(4000, [estricta(0.1), comun(0.2)])).toBe(0.3)
    expect(descuentoUnitarioDeAplicadas(4000, [])).toBe(0)
  })
})
