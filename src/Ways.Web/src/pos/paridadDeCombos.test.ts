import { describe, expect, it } from 'vitest'
import { calcularTotalesDeLinea } from '../api/ajusteManual'
import type { UnidadVenta } from '../api/tipos'
import { resolverCombos, type ComboCandidato, type ComponenteDeCombo, type LineaParaCombos } from './motorDeCombos'

// Tabla de paridad servidor/web: los mismos carritos con los mismos netos esperados por línea que
// `tests/Ways.Domain.Tests/Combos/ParidadDeCombosConElMotorWebTests.cs` (`ResolvedorDeCombos` más
// `CalculadorDeTotales`). Acá la salida del motor de TypeScript pasa por `calcularTotalesDeLinea`.
// Un cambio de un número en una de las dos tablas exige el mismo cambio en la otra.

const linea = (
  indice: number,
  idArticulo: number,
  precioLista: number,
  cantidad = 1,
  descuentoUnitarioDeOferta = 0,
  unidadVenta: UnidadVenta = 'Unidad',
  idFamilia: number | null = null,
): LineaParaCombos => ({
  indice,
  idArticulo,
  idFamilia,
  cantidad,
  precioLista,
  descuentoUnitarioDeOferta,
  unidadVenta,
})

const porPeso = (indice: number, idArticulo: number, precioPorKilo: number, kilos: number, idFamilia: number | null = null): LineaParaCombos =>
  linea(indice, idArticulo, precioPorKilo, kilos, 0, 'Peso', idFamilia)

const art = (idArticulo: number, cantidad: number): ComponenteDeCombo => ({ idArticulo, idFamilia: null, cantidad })
const fam = (idFamilia: number, cantidad: number): ComponenteDeCombo => ({ idArticulo: null, idFamilia, cantidad })
const comboDe = (id: number, precio: number, ...componentes: ComponenteDeCombo[]): ComboCandidato => ({ id, nombre: `combo ${id}`, precio, componentes })

const JAMON = 5
const QUESO = 6
const SALAME = 7
const FAMILIA_FIAMBRES = 30
const doscientosMasDoscientos = comboDe(7, 3000, art(JAMON, 0.2), art(QUESO, 0.2))

const combo = (id: number, precio: number, ...articulos: number[]): ComboCandidato => ({
  id,
  nombre: `combo ${id}`,
  precio,
  componentes: articulos.map((idArticulo) => ({ idArticulo, idFamilia: null, cantidad: 1 })),
})

function verificar(lineas: LineaParaCombos[], candidato: ComboCandidato, netosEsperados: number[], ahorrosEsperados: number[]) {
  const resolucion = resolverCombos(lineas, [candidato])
  const consumos = lineas.map((l) => resolucion.lineas.find((c) => c.indice === l.indice))

  const netos = lineas.map((l, i) =>
    calcularTotalesDeLinea({
      cantidad: l.cantidad,
      precioOriginal: l.precioLista,
      descuentoUnitario: l.descuentoUnitarioDeOferta,
      porcentaje: null,
      unidadesConsumidasPorCombo: consumos[i]?.unidadesConsumidas ?? 0,
      ahorroDeCombo: consumos[i]?.ahorroDeCombo ?? 0,
    }).neto,
  )

  expect(netos).toEqual(netosEsperados)
  expect(consumos.map((c) => c?.ahorroDeCombo ?? 0)).toEqual(ahorrosEsperados)
}

describe('paridad de combos con el servidor', () => {
  it('reparte el cobro en pesos enteros entre dos líneas', () => {
    verificar([linea(0, 1, 3000), linea(1, 2, 2500)], combo(7, 5000, 1, 2), [2727, 2273], [273, 227])
  })

  it('tres líneas iguales dejan el resto en la última', () => {
    verificar([linea(0, 1, 1000), linea(1, 2, 1000), linea(2, 3, 1000)], combo(7, 2000, 1, 2, 3), [667, 667, 666], [333, 333, 334])
  })

  it('importes de lista con centavos llevan centavos en el ahorro y no en el neto', () => {
    verificar([linea(0, 1, 500.5), linea(1, 2, 999.5)], combo(7, 1000, 1, 2), [334, 666], [166.5, 333.5])
  })

  it('el precio del combo se redondea al peso antes de repartir', () => {
    verificar([linea(0, 1, 1000), linea(1, 2, 500)], combo(7, 999.5, 1, 2), [667, 333], [333, 167])
  })

  it('dos aplicaciones con precios con centavos multiplican el reparto', () => {
    verificar([linea(0, 1, 100.5, 2), linea(1, 2, 49.5, 2)], combo(7, 120, 1, 2), [160, 80], [41, 19])
  })

  it('la oferta solo alcanza a las unidades que el combo no consumió', () => {
    verificar([linea(0, 1, 1000, 3, 100), linea(1, 2, 1000)], combo(7, 1500, 1, 2), [2550, 750], [250, 250])
  })

  it('el combo no se aplica si el cobro redondeado no es más barato que las ofertas', () => {
    verificar([linea(0, 1, 1000, 1, 400), linea(1, 2, 1000, 1, 400)], combo(7, 1199.6, 1, 2), [600, 600], [0, 0])
  })

  it('el combo no se aplica si el cobro no entra en los topes de las líneas', () => {
    verificar([linea(0, 1, 100.6), linea(1, 2, 100.6), linea(2, 3, 100.6)], combo(7, 301, 1, 2, 3), [101, 101, 101], [0, 0, 0])
  })

  it('la parte proporcional se recorta al importe de lista hacia abajo al peso', () => {
    verificar([linea(0, 1, 0.9), linea(1, 2, 2.2), linea(2, 3, 6.9)], combo(7, 8, 1, 2, 3), [0, 2, 6], [0.9, 0.2, 0.9])
  })

  it('un empate exacto del reparto sube al peso aunque en punto flotante quede debajo', () => {
    // 3 × 1,2 / 7,2 es 0,5 exacto; en flotante da 0,49999… y redondearía hacia abajo.
    verificar([linea(0, 1, 1.2), linea(1, 2, 6)], combo(7, 3, 1, 2), [1, 2], [0.2, 4])
  })

  it('dos componentes pesables de 200 g reparten el cobro entre las dos líneas', () => {
    verificar([porPeso(0, JAMON, 10000, 0.2), porPeso(1, QUESO, 8000, 0.2)], doscientosMasDoscientos, [1667, 1333], [333, 267])
  })

  it('una línea de 215 g aporta 200 g al combo y los 15 g que sobran se cobran a precio de lista', () => {
    verificar([porPeso(0, JAMON, 10000, 0.215), porPeso(1, QUESO, 8000, 0.2)], doscientosMasDoscientos, [1817, 1333], [333, 267])
    expect(
      resolverCombos([porPeso(0, JAMON, 10000, 0.215), porPeso(1, QUESO, 8000, 0.2)], [doscientosMasDoscientos]).lineas.map((l) => l.unidadesConsumidas),
    ).toEqual([0.2, 0.2])
  })

  it('400 g de cada componente aplican el combo dos veces', () => {
    verificar([porPeso(0, JAMON, 10000, 0.4), porPeso(1, QUESO, 8000, 0.4)], doscientosMasDoscientos, [3334, 2666], [666, 534])
  })

  it('150 g no completan un componente de 200 g y el combo no se aplica', () => {
    verificar([porPeso(0, JAMON, 10000, 0.15), porPeso(1, QUESO, 8000, 0.2)], doscientosMasDoscientos, [1500, 1600], [0, 0])
  })

  it('el importe de lista de la porción consumida redondea el medio centavo hacia arriba (0,250 × 100,10 = 25,03)', () => {
    verificar(
      [porPeso(0, JAMON, 100.1, 0.25), porPeso(1, QUESO, 8000, 0.2)],
      comboDe(7, 1000, art(JAMON, 0.25), art(QUESO, 0.2)),
      [15, 985],
      [10.03, 615],
    )
  })

  it('en una familia mixta el miembro por unidad no es candidato de un componente de 1,5 y lo completa el pesable', () => {
    verificar(
      [linea(0, SALAME, 400, 2, 0, 'Unidad', FAMILIA_FIAMBRES), porPeso(1, JAMON, 1000, 1.5, FAMILIA_FIAMBRES), linea(2, 2, 2500)],
      comboDe(7, 1000, fam(FAMILIA_FIAMBRES, 1.5), art(2, 1)),
      [800, 375, 625],
      [0, 1125, 1875],
    )
  })

  it('en una familia mixta un componente de 200 g lo completa el miembro pesable y no el de unidad', () => {
    verificar(
      [linea(0, SALAME, 400, 1, 0, 'Unidad', FAMILIA_FIAMBRES), porPeso(1, JAMON, 10000, 0.2, FAMILIA_FIAMBRES), porPeso(2, QUESO, 8000, 0.2)],
      comboDe(7, 3000, fam(FAMILIA_FIAMBRES, 0.2), art(QUESO, 0.2)),
      [400, 1667, 1333],
      [0, 333, 267],
    )
  })

  it('una línea por unidad con cantidad fraccionaria se ignora', () => {
    verificar([linea(0, 1, 3000, 1.5), linea(1, 2, 2500)], combo(7, 5000, 1, 2), [4500, 2500], [0, 0])
  })
})
