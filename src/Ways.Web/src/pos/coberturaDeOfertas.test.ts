import { describe, expect, it } from 'vitest'
import { calcularTotalesDeLinea } from '../api/ajusteManual'
import type { EscalonDeCantidad, OfertaAplicada, PrecioDeListaDeInstantanea } from '../api/tipos'
import { descuentosDeLinea, descuentoUnitarioDeAplicadas, tieneSoloMultiplos } from './coberturaDeOfertas'
import { preciosVigentesOffline } from './instantaneaOffline'

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

  it('la familia cuenta los grupos sobre el documento y los reparte por unidades', () => {
    const aplicadas = [{ ...estricta(), porFamilia: true }]
    // 2 + 2 + 1 = 5 unidades de la familia: dos grupos (4 unidades, 1000 de descuento), 200 por unidad de línea.
    expect(descuentosDeLinea([2, 2, 1].map((unidades) => ({ unidades, precioOriginal: PRECIO, aplicadas })))).toEqual([400, 400, 200])
  })

  it('la familia reparte pesos enteros y el resto va a la última línea', () => {
    const aplicadas = [{ ...estricta(), porFamilia: true }]
    // 1 + 1 + 1 = 3: un grupo, 500 de descuento; 166 a cada línea y el resto a la última: el documento cobra 11500.
    const descuentos = descuentosDeLinea([1, 1, 1].map((unidades) => ({ unidades, precioOriginal: PRECIO, aplicadas })))
    expect(descuentos).toEqual([166, 166, 168])
    const totales = descuentos.map(
      (descuentoDeLinea) => calcularTotalesDeLinea({ cantidad: 1, precioOriginal: PRECIO, descuentoUnitario: 250, porcentaje: null, descuentoDeLinea }).total,
    )
    expect(totales.reduce((a, b) => a + b, 0)).toBe(11500)
  })

  it('ninguna línea de familia queda debajo de la común a la que le ganó (166,67 por unidad contra 150)', () => {
    const aplicadas = [{ ...estricta(), porFamilia: true }]
    const descuentos = descuentosDeLinea([1, 1, 1].map((unidades) => ({ unidades, precioOriginal: PRECIO, aplicadas })))
    for (const descuento of descuentos) expect(descuento).toBeGreaterThan(150)
    expect(descuentos.reduce((a, b) => a + b, 0)).toBe(500)
  })

  it('el resto no cubre más unidades de las que tiene la línea: 50 centavos en grupos de 2, 1 + 1 + 1', () => {
    const aplicadas = [{ ...estricta(0.5), porFamilia: true }]
    expect(descuentosDeLinea([1, 1, 1].map((unidades) => ({ unidades, precioOriginal: PRECIO, aplicadas })))).toEqual([0, 0.5, 0.5])
  })

  it('con la parte de familia fraccionaria el tope sigue siendo por unidad', () => {
    // 3000 de familia en grupos de 2 más una acumulable de 2000 a 4000: 2/3 de unidad a 4000 (tope) y 1/3 a 2000.
    const aplicadas = [{ ...estricta(3000), porFamilia: true }, comun(2000)]
    expect(descuentosDeLinea([1, 1, 1].map((unidades) => ({ unidades, precioOriginal: PRECIO, aplicadas })))).toEqual([
      3333.333333, 3333.333333, 3333.333333,
    ])
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

// Paridad de la curva con una "solo múltiplos" y una común en competencia: los escalones son los que arma el servidor
// (`EscalonesDeCantidad.Construir`, ver `LaCurvaAlternaEntreLaSoloMultiplosYLaComunHastaQueLaSoloMultiplosGanaSiempre` y
// `LaCurvaConUnaComunEnCompetenciaPasaALaComunDondeEstaDaMasDescuentoDeLinea`) y los totales, los de
// `LosTotalesDelDispositivoConLaCurvaDelEjemploSonLosDelServidor` en `PuntosDeQuiebreSoloMultiplosTests.cs`. Un cambio de
// un número en una de las tablas exige el mismo cambio en la otra.
describe('coberturaDeOfertas — curva con una solo múltiplos y una común en competencia', () => {
  const laComun = comun(200, 2)
  const conLaEstricta = (cantidadDesde: number): EscalonDeCantidad => ({
    cantidadDesde,
    precioFinal: PRECIO,
    descuentoUnitario: 0,
    aplicadas: [],
    aplicadasCompletas: [estricta()],
  })
  const conLaComun = (cantidadDesde: number): EscalonDeCantidad => ({
    cantidadDesde,
    precioFinal: 3800,
    descuentoUnitario: 200,
    aplicadas: [laComun],
  })
  const precio: PrecioDeListaDeInstantanea = {
    idListaPrecio: 1,
    precioOriginal: PRECIO,
    precioFinal: 3800,
    descuentoUnitario: 200,
    aplicadas: [laComun],
    escalones: [conLaEstricta(2), conLaComun(2.501), conLaEstricta(4), conLaComun(5.001), conLaEstricta(6), conLaComun(7.501), conLaEstricta(8)],
  }

  function totalOffline(cantidad: number): number {
    const vigente = preciosVigentesOffline(precio, cantidad)
    const [descuentoDeLinea] = descuentosDeLinea([{ unidades: cantidad, precioOriginal: PRECIO, aplicadas: vigente.aplicadas }])
    return calcularTotalesDeLinea({
      cantidad,
      precioOriginal: PRECIO,
      descuentoUnitario: vigente.descuentoUnitario,
      porcentaje: null,
      descuentoDeLinea,
    }).total
  }

  it.each([
    [1, 3800],
    [2, 7500],
    [2.5, 9500],
    [2.501, 9504],
    [3, 11400],
    [4, 15000],
    [5, 19000],
    [5.001, 19004],
    [7, 26500],
    [7.5, 28500],
    [7.501, 28504],
    [9, 34000],
    [11, 41500],
  ])('a %s unidades el POS cobra lo mismo que el servidor: %s', (cantidad, total) => {
    expect(totalOffline(cantidad)).toBe(total)
  })

  it('un POS anterior a aplicadasCompletas nunca cobra menos que el servidor', () => {
    // Usa los campos de siempre del escalón vigente: cantidad × precioFinal, sin cobertura.
    for (const [cantidad, total] of [[2, 7500], [3, 11400], [4, 15000], [8, 30000], [11, 41500]] as const) {
      const vigente = [...(precio.escalones ?? [])].reverse().find((e) => e.cantidadDesde <= cantidad) ?? precio
      expect(cantidad * vigente.precioFinal).toBeGreaterThanOrEqual(total)
    }
  })
})
