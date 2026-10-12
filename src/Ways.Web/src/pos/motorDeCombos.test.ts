import { describe, expect, it } from 'vitest'
import { calcularTotalesDeLinea } from '../api/ajusteManual'
import type { UnidadVenta } from '../api/tipos'
import { redondearImporte } from '../formato/importes'
import {
  resolverCombos,
  tieneMasDeDosDecimales,
  type ComboCandidato,
  type ComponenteDeCombo,
  type ConsumoDeLinea,
  type LineaParaCombos,
  type ResolucionDeCombos,
} from './motorDeCombos'

// Espejo caso por caso de `tests/Ways.Domain.Tests/Combos/ResolvedorDeCombosTests.cs`: mismas
// entradas, mismos números esperados.
const PANCHO = 1
const COCA = 2
const SPRITE = 3
const PAPAS = 4
const FAMILIA_BEBIDAS = 10
const FAMILIA_SNACKS = 20
const JAMON = 5
const QUESO = 6
const SALAME = 7
const FAMILIA_FIAMBRES = 30

function linea(
  indice: number,
  idArticulo: number,
  precio: number,
  opciones: { cantidad?: number; idFamilia?: number | null; descuentoDeOferta?: number; unidad?: UnidadVenta | null } = {},
): LineaParaCombos {
  return {
    indice,
    idArticulo,
    idFamilia: opciones.idFamilia ?? null,
    cantidad: opciones.cantidad ?? 1,
    precioLista: precio,
    descuentoUnitarioDeOferta: opciones.descuentoDeOferta ?? 0,
    unidadVenta: opciones.unidad === undefined ? 'Unidad' : opciones.unidad,
  }
}

const porPeso = (indice: number, idArticulo: number, precioPorKilo: number, kilos: number, idFamilia: number | null = null): LineaParaCombos =>
  linea(indice, idArticulo, precioPorKilo, { cantidad: kilos, idFamilia, unidad: 'Peso' })

const art = (idArticulo: number, cantidad = 1): ComponenteDeCombo => ({ idArticulo, idFamilia: null, cantidad })
const fam = (idFamilia: number, cantidad = 1): ComponenteDeCombo => ({ idArticulo: null, idFamilia, cantidad })
const combo = (id: number, precio: number, ...componentes: ComponenteDeCombo[]): ComboCandidato => ({
  id,
  nombre: `combo ${id}`,
  precio,
  componentes,
})

function deLinea(resolucion: ResolucionDeCombos, indice: number): ConsumoDeLinea {
  const encontradas = resolucion.lineas.filter((l) => l.indice === indice)
  expect(encontradas).toHaveLength(1)
  return encontradas[0]
}

const suma = (valores: number[]) => redondearImporte(valores.reduce((a, b) => a + b, 0), 2)
const ahorroTotal = (resolucion: ResolucionDeCombos) => suma(resolucion.lineas.map((l) => l.ahorroDeCombo))

/** Lo que cobra el documento: cada línea por la fórmula de línea, con lo que el motor le consumió. */
function totalCobrado(lineas: LineaParaCombos[], resolucion: ResolucionDeCombos): number {
  return lineas.reduce((total, l) => {
    const consumo = resolucion.lineas.find((c) => c.indice === l.indice)
    return (
      total +
      calcularTotalesDeLinea({
        cantidad: l.cantidad,
        precioOriginal: l.precioLista,
        descuentoUnitario: l.descuentoUnitarioDeOferta,
        porcentaje: null,
        unidadesConsumidasPorCombo: consumo?.unidadesConsumidas ?? 0,
        ahorroDeCombo: consumo?.ahorroDeCombo ?? 0,
      }).neto
    )
  }, 0)
}

describe('motorDeCombos — espejo de ResolvedorDeCombosTests', () => {
  it('el ejemplo del dueño: pancho + coca reparte por proporción y suma el precio del combo', () => {
    const r = resolverCombos([linea(0, PANCHO, 3000), linea(1, COCA, 2500)], [combo(7, 5000, art(PANCHO), art(COCA))])

    const pancho = deLinea(r, 0)
    const coca = deLinea(r, 1)
    expect(pancho.unidadesConsumidas).toBe(1)
    expect(pancho.ahorroDeCombo).toBe(273)
    expect(coca.unidadesConsumidas).toBe(1)
    expect(coca.ahorroDeCombo).toBe(227)
    expect(suma([3000 - pancho.ahorroDeCombo, 2500 - coca.ahorroDeCombo])).toBe(5000)
    expect(pancho.idsDeCombo).toEqual([7])
    expect(r.combos).toEqual([{ idCombo: 7, nombre: 'combo 7', veces: 1, ahorro: 500 }])
  })

  it('un componente artículo más una familia con dos miembros distintos los consume a todos', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 3000), linea(1, COCA, 2500, { idFamilia: FAMILIA_BEBIDAS }), linea(2, SPRITE, 2000, { idFamilia: FAMILIA_BEBIDAS })],
      [combo(1, 6000, art(PANCHO), fam(FAMILIA_BEBIDAS, 2))],
    )

    expect(deLinea(r, 0).ahorroDeCombo).toBe(600)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(500)
    expect(deLinea(r, 2).ahorroDeCombo).toBe(400)
    expect(r.lineas.every((l) => l.unidadesConsumidas === 1)).toBe(true)
  })

  it('dos familias como componentes dejan el resto del redondeo en la última línea', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 2500, { idFamilia: FAMILIA_SNACKS }), linea(1, COCA, 2000, { idFamilia: FAMILIA_BEBIDAS })],
      [combo(1, 4000, fam(FAMILIA_SNACKS), fam(FAMILIA_BEBIDAS))],
    )

    expect(deLinea(r, 0).ahorroDeCombo).toBe(278)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(222)
  })

  it('un combo de tres componentes reparte por proporción del importe de lista', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 1000), linea(1, COCA, 2000), linea(2, SPRITE, 3000)],
      [combo(1, 5000, art(PANCHO), art(COCA), art(SPRITE))],
    )

    expect(deLinea(r, 0).ahorroDeCombo).toBe(167)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(333)
    expect(deLinea(r, 2).ahorroDeCombo).toBe(500)
  })

  it('el resto del redondeo va a la última línea consumida y el conjunto suma exacto el precio', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 100), linea(1, COCA, 100), linea(2, SPRITE, 100)],
      [combo(1, 200, art(PANCHO), art(COCA), art(SPRITE))],
    )

    expect(deLinea(r, 0).ahorroDeCombo).toBe(33)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(33)
    expect(deLinea(r, 2).ahorroDeCombo).toBe(34)
    expect(300 - ahorroTotal(r)).toBe(200)
  })

  it('un componente con cantidad mayor a uno consume esas unidades', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 1000, { cantidad: 2 }), linea(1, COCA, 2000)],
      [combo(1, 3000, art(PANCHO, 2), art(COCA))],
    )

    const pancho = deLinea(r, 0)
    expect(pancho.unidadesConsumidas).toBe(2)
    expect(pancho.ahorroDeCombo).toBe(500)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(500)
  })

  it('dos conjuntos completos aplican el combo dos veces y suman los ahorros', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 3000, { cantidad: 2 }), linea(1, COCA, 2500, { cantidad: 2 })],
      [combo(1, 5000, art(PANCHO), art(COCA))],
    )

    const pancho = deLinea(r, 0)
    const coca = deLinea(r, 1)
    expect(pancho.unidadesConsumidas).toBe(2)
    expect(pancho.ahorroDeCombo).toBe(546)
    expect(coca.unidadesConsumidas).toBe(2)
    expect(coca.ahorroDeCombo).toBe(454)
    expect(r.combos).toHaveLength(1)
    expect(r.combos[0].veces).toBe(2)
    expect(r.combos[0].ahorro).toBe(1000)
  })

  it('un conjunto y medio deja la unidad sobrante a precio normal', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 3000, { cantidad: 2 }), linea(1, COCA, 2500)],
      [combo(1, 5000, art(PANCHO), art(COCA))],
    )

    const pancho = deLinea(r, 0)
    expect(pancho.unidadesConsumidas).toBe(1)
    expect(pancho.ahorroDeCombo).toBe(273)
    expect(r.combos).toHaveLength(1)
    expect(r.combos[0].veces).toBe(1)
  })

  it('el mismo artículo puede cubrir el componente artículo y una unidad de la familia', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 3000, { cantidad: 2, idFamilia: FAMILIA_BEBIDAS })],
      [combo(1, 4500, art(PANCHO), fam(FAMILIA_BEBIDAS))],
    )

    const pancho = deLinea(r, 0)
    expect(pancho.unidadesConsumidas).toBe(2)
    expect(pancho.ahorroDeCombo).toBe(1500)
  })

  it('la unidad tomada como artículo no queda disponible para la familia y se usa otro miembro', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 3000, { idFamilia: FAMILIA_BEBIDAS }), linea(1, COCA, 2000, { idFamilia: FAMILIA_BEBIDAS })],
      [combo(1, 4000, art(PANCHO), fam(FAMILIA_BEBIDAS))],
    )

    expect(deLinea(r, 0).unidadesConsumidas).toBe(1)
    expect(deLinea(r, 0).ahorroDeCombo).toBe(600)
    expect(deLinea(r, 1).unidadesConsumidas).toBe(1)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(400)
  })

  it('el componente artículo se cubre antes que la familia aun si la familia aparece antes en el documento', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 3000, { idFamilia: FAMILIA_BEBIDAS }), linea(1, COCA, 2000, { idFamilia: FAMILIA_BEBIDAS })],
      [combo(1, 4000, fam(FAMILIA_BEBIDAS), art(PANCHO))],
    )

    expect(deLinea(r, 0).ahorroDeCombo).toBe(600)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(400)
  })

  it('con varias líneas candidatas para un componente gana la primera del documento', () => {
    const r = resolverCombos(
      [
        linea(0, COCA, 2000, { idFamilia: FAMILIA_BEBIDAS }),
        linea(1, SPRITE, 2000, { idFamilia: FAMILIA_BEBIDAS }),
        linea(2, PANCHO, 3000),
      ],
      [combo(1, 4000, fam(FAMILIA_BEBIDAS), art(PANCHO))],
    )

    expect(deLinea(r, 0).unidadesConsumidas).toBe(1)
    expect(r.lineas.some((l) => l.indice === 1)).toBe(false)
  })

  it('el combo que no es más barato que la oferta no se aplica y si lo es por un peso sí (compuerta D1)', () => {
    const lineas = [linea(0, PANCHO, 3000, { descuentoDeOferta: 1000 }), linea(1, COCA, 2500)]

    const empate = resolverCombos(lineas, [combo(1, 4500, art(PANCHO), art(COCA))])
    expect(empate.lineas).toEqual([])
    expect(empate.combos).toEqual([])

    const masBarato = resolverCombos(lineas, [combo(1, 4499, art(PANCHO), art(COCA))])
    expect(masBarato.lineas).toHaveLength(2)
    expect(ahorroTotal(masBarato)).toBe(1001)

    const conCentavos = resolverCombos(lineas, [combo(1, 4499.99, art(PANCHO), art(COCA))])
    expect(conCentavos.lineas).toEqual([])
    expect(conCentavos.combos).toEqual([])
  })

  it('las unidades de la línea fuera del combo conservan su oferta porque solo se consume lo necesario', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 3000, { cantidad: 2, descuentoDeOferta: 500 }), linea(1, COCA, 2500)],
      [combo(1, 4800, art(PANCHO), art(COCA))],
    )

    const pancho = deLinea(r, 0)
    const coca = deLinea(r, 1)
    expect(pancho.unidadesConsumidas).toBe(1)
    expect(pancho.ahorroDeCombo).toBe(382)
    expect(coca.ahorroDeCombo).toBe(318)

    const descuentoPancho = suma([500 * (2 - pancho.unidadesConsumidas), pancho.ahorroDeCombo])
    const descuentoCoca = coca.ahorroDeCombo
    expect(descuentoPancho).toBe(882)
    expect(suma([3000 * 2 - descuentoPancho, 2500 - descuentoCoca])).toBe(4800 + 2500)
  })

  it.each([5500, 6000])('un combo cuyo precio no baja el total de lista no se aplica (%s)', (precio) => {
    const r = resolverCombos([linea(0, PANCHO, 3000), linea(1, COCA, 2500)], [combo(1, precio, art(PANCHO), art(COCA))])

    expect(r.lineas).toEqual([])
    expect(r.combos).toEqual([])
  })

  it('con dos combos compitiendo gana el de mayor ahorro y el otro no puede reusar las unidades', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 3000), linea(1, COCA, 2500), linea(2, SPRITE, 2000)],
      [combo(1, 5000, art(PANCHO), art(COCA)), combo(2, 4000, art(PANCHO), art(SPRITE))],
    )

    expect(r.combos).toHaveLength(1)
    expect(r.combos[0].idCombo).toBe(2)
    expect(r.combos[0].ahorro).toBe(1000)
    expect(r.lineas.some((l) => l.indice === 1)).toBe(false)
  })

  it('el empate de ahorro lo gana el menor id sin importar el orden de entrada', () => {
    const lineas = [linea(0, PANCHO, 3000), linea(1, COCA, 2500), linea(2, SPRITE, 2000)]
    const cinco = combo(5, 5000, art(PANCHO), art(COCA))
    const tres = combo(3, 4500, art(PANCHO), art(SPRITE))

    expect(resolverCombos(lineas, [cinco, tres]).combos.map((c) => c.idCombo)).toEqual([3])
    expect(resolverCombos(lineas, [tres, cinco]).combos.map((c) => c.idCombo)).toEqual([3])
  })

  it('el ranking usa la ganancia real frente al costo con ofertas y no el ahorro contra lista', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 60), linea(1, COCA, 40), linea(2, SPRITE, 100, { descuentoDeOferta: 20 })],
      [combo(1, 79, art(PANCHO), art(SPRITE)), combo(2, 30, art(COCA), art(PANCHO))],
    )

    expect(r.combos).toHaveLength(1)
    expect(r.combos[0].idCombo).toBe(2)
  })

  it('una cantidad enorme se aplica en un solo paso y cada conjunto suma el precio', () => {
    const conjuntos = 500_000

    const r = resolverCombos(
      [linea(0, PANCHO, 3000, { cantidad: conjuntos }), linea(1, COCA, 2500, { cantidad: conjuntos })],
      [combo(1, 5000, art(PANCHO), art(COCA))],
    )

    expect(r.combos).toHaveLength(1)
    expect(r.combos[0].veces).toBe(conjuntos)
    expect(r.combos[0].ahorro).toBe(250_000_000)
    expect(deLinea(r, 0).ahorroDeCombo).toBe(136_500_000)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(113_500_000)
    expect(ahorroTotal(r)).toBe(r.combos[0].ahorro)
  })

  it('una línea de precio cero no recibe una parte negativa del redondeo', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 10), linea(1, COCA, 10), linea(2, SPRITE, 0)],
      [combo(1, 19, art(PANCHO), art(COCA), art(SPRITE))],
    )

    expect(r.lineas.every((l) => l.ahorroDeCombo >= 0 && l.ahorroDeCombo <= 10)).toBe(true)
    expect(ahorroTotal(r)).toBe(1)
    expect(r.lineas.find((l) => l.indice === 2)?.ahorroDeCombo ?? 0).toBe(0)
  })

  it('la parte de cada línea nunca supera su importe de lista y el conjunto suma el precio', () => {
    const lineas = [...Array.from({ length: 10 }, (_, i) => linea(i, 100 + i, 1)), linea(10, 200, 0.01)]

    const r = resolverCombos(lineas, [combo(1, 0.06, ...lineas.map((l) => art(l.idArticulo)))])

    expect(r.lineas).toHaveLength(11)
    for (const consumo of r.lineas) {
      expect(consumo.ahorroDeCombo).toBeGreaterThanOrEqual(0)
      expect(consumo.ahorroDeCombo).toBeLessThanOrEqual(lineas[consumo.indice].precioLista)
    }
    // Lista 10,01 y combo 0,06: el conjunto cobra 0 y el ahorro es toda la lista. Igualdad exacta como la de C#
    // (decimal): en centavos enteros, sin tolerancia de punto flotante.
    const centavos = (importe: number) => Math.round(importe * 100)
    expect(r.lineas.reduce((a, l) => a + centavos(l.ahorroDeCombo), 0)).toBe(1001)
    for (const consumo of r.lineas) expect(consumo.ahorroDeCombo).toBe(centavos(consumo.ahorroDeCombo) / 100)
  })

  it('un combo con un componente sobre el tope de cantidad no se aplica y no desborda', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 1, { cantidad: 2_147_483_647 }), linea(1, COCA, 1)],
      [combo(1, 1, art(PANCHO, 3_000_000_000), art(COCA))],
    )

    expect(r.combos).toEqual([])
  })

  it('después del ganador un combo que todavía cabe se aplica con las unidades restantes', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 3000), linea(1, COCA, 2500), linea(2, SPRITE, 2000), linea(3, PAPAS, 1500)],
      [combo(1, 4000, art(PANCHO), art(SPRITE)), combo(2, 3500, art(COCA), art(PAPAS))],
    )

    expect(r.combos.map((c) => c.idCombo).sort()).toEqual([1, 2])
  })

  it.each([0.5, 1.5, -1, 0])('las líneas por unidad con cantidad no entera o no positiva se ignoran en la detección (%s)', (cantidad) => {
    const r = resolverCombos([linea(0, PANCHO, 3000, { cantidad }), linea(1, COCA, 2500)], [combo(1, 5000, art(PANCHO), art(COCA))])

    expect(r.lineas).toEqual([])
    expect(r.combos).toEqual([])
  })

  // Cláusula: una línea solo es candidata de un componente si su unidad de venta admite la cantidad del componente.
  it.each([1.5, 0.5])('una línea por unidad no completa un componente de cantidad fraccionaria (%s)', (cantidad) => {
    const r = resolverCombos(
      [linea(0, PANCHO, 3000, { cantidad: 2 }), linea(1, COCA, 2500)],
      [combo(1, 2000, art(PANCHO, cantidad), art(COCA))],
    )

    expect(r.lineas).toEqual([])
    expect(r.combos).toEqual([])
  })

  it('dos componentes pesables de doscientos gramos aplican el combo una vez', () => {
    const lineas = [porPeso(0, JAMON, 10000, 0.2), porPeso(1, QUESO, 8000, 0.2)]

    const r = resolverCombos(lineas, [combo(1, 3000, art(JAMON, 0.2), art(QUESO, 0.2))])

    expect(deLinea(r, 0)).toEqual({ indice: 0, unidadesConsumidas: 0.2, ahorroDeCombo: 333, idsDeCombo: [1] })
    expect(deLinea(r, 1)).toEqual({ indice: 1, unidadesConsumidas: 0.2, ahorroDeCombo: 267, idsDeCombo: [1] })
    expect(r.combos).toEqual([{ idCombo: 1, nombre: 'combo 1', veces: 1, ahorro: 600 }])
    expect(totalCobrado(lineas, r)).toBe(3000)
  })

  it('el excedente de una línea pesable queda a precio de lista y la línea no se parte', () => {
    const lineas = [porPeso(0, JAMON, 10000, 0.215), porPeso(1, QUESO, 8000, 0.2)]

    const r = resolverCombos(lineas, [combo(1, 3000, art(JAMON, 0.2), art(QUESO, 0.2))])

    expect(deLinea(r, 0).unidadesConsumidas).toBe(0.2)
    expect(deLinea(r, 0).ahorroDeCombo).toBe(333)
    expect(r.combos[0].veces).toBe(1)
    // El combo cobra 3000 y los 15 g que sobran del jamón, 150 a precio de lista.
    expect(totalCobrado(lineas, r)).toBe(3150)
  })

  it('el doble de cada componente pesable aplica el combo dos veces', () => {
    const lineas = [porPeso(0, JAMON, 10000, 0.4), porPeso(1, QUESO, 8000, 0.4)]

    const r = resolverCombos(lineas, [combo(1, 3000, art(JAMON, 0.2), art(QUESO, 0.2))])

    expect(deLinea(r, 0).unidadesConsumidas).toBe(0.4)
    expect(deLinea(r, 0).ahorroDeCombo).toBe(666)
    expect(deLinea(r, 1).unidadesConsumidas).toBe(0.4)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(534)
    expect(r.combos).toEqual([{ idCombo: 1, nombre: 'combo 1', veces: 2, ahorro: 1200 }])
    expect(totalCobrado(lineas, r)).toBe(6000)
  })

  it('las veces son la parte entera de lo disponible sobre lo que pide el componente', () => {
    // 0,590 kg de jamón alcanzan para dos conjuntos de 0,200 (sobran 0,190) aunque el queso alcance para tres.
    const r = resolverCombos(
      [porPeso(0, JAMON, 10000, 0.59), porPeso(1, QUESO, 8000, 0.6)],
      [combo(1, 3000, art(JAMON, 0.2), art(QUESO, 0.2))],
    )

    expect(r.combos[0].veces).toBe(2)
    expect(deLinea(r, 0).unidadesConsumidas).toBe(0.4)
    expect(deLinea(r, 1).unidadesConsumidas).toBe(0.4)
  })

  it('una línea pesable que no llega a la cantidad del componente no aplica el combo', () => {
    const r = resolverCombos(
      [porPeso(0, JAMON, 10000, 0.15), porPeso(1, QUESO, 8000, 0.2)],
      [combo(1, 2000, art(JAMON, 0.2), art(QUESO, 0.2))],
    )

    expect(r.lineas).toEqual([])
    expect(r.combos).toEqual([])
  })

  // Cláusula: el importe de lista de la porción consumida se redondea a dos decimales antes del reparto
  // (0,125 × 9999,50 = 1249,9375 → 1249,94; 0,333 × 7000,10 = 2331,0333 → 2331,03).
  it('el ahorro de una línea pesable nunca lleva más de dos decimales', () => {
    const lineas = [porPeso(0, JAMON, 9999.5, 0.125), porPeso(1, QUESO, 7000.1, 0.333)]

    const r = resolverCombos(lineas, [combo(1, 3000, art(JAMON, 0.125), art(QUESO, 0.333))])

    expect(deLinea(r, 0).ahorroDeCombo).toBe(202.94)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(378.03)
    expect(r.combos[0].ahorro).toBe(580.97)
    expect(totalCobrado(lineas, r)).toBe(3000)
  })

  // Cláusula: un combo cuyo cobro no baja del total de lista ya redondeado no se aplica. Con dos gramos a 1 el kilo
  // el total de lista redondea a cero: no hay importe que repartir.
  it('un combo cuyo total de lista redondea a cero no se aplica', () => {
    const r = resolverCombos(
      [porPeso(0, JAMON, 1, 0.001), porPeso(1, QUESO, 1, 0.001)],
      [combo(1, 0.2, art(JAMON, 0.001), art(QUESO, 0.001))],
    )

    expect(r.lineas).toEqual([])
    expect(r.combos).toEqual([])
  })

  it('un artículo pesable completa un componente de cantidad entera y el resto queda fuera', () => {
    const r = resolverCombos([porPeso(0, JAMON, 10000, 1.5), linea(1, COCA, 2500)], [combo(1, 11000, art(JAMON), art(COCA))])

    expect(deLinea(r, 0).unidadesConsumidas).toBe(1)
    expect(deLinea(r, 1).unidadesConsumidas).toBe(1)
  })

  it('un solo componente pesable de dos kilos o más se aplica y uno menor no', () => {
    const lineas = [porPeso(0, JAMON, 10000, 2.5)]

    expect(deLinea(resolverCombos(lineas, [combo(1, 15000, art(JAMON, 2))]), 0).unidadesConsumidas).toBe(2)
    expect(resolverCombos(lineas, [combo(2, 1000, art(JAMON, 0.5))]).combos).toEqual([])
  })

  // Cláusula: la unidad de venta de la línea decide qué cantidades son detectables. Una línea pesable con más de
  // tres decimales o no positiva no entra.
  it.each([0.2005, 0, -0.2])('una línea pesable con cantidad que su unidad no admite se ignora (%s)', (kilos) => {
    const r = resolverCombos(
      [porPeso(0, JAMON, 10000, kilos), porPeso(1, QUESO, 8000, 0.2)],
      [combo(1, 1000, art(JAMON, 0.2), art(QUESO, 0.2))],
    )

    expect(r.combos).toEqual([])
  })

  // Cláusula: una línea pesable cuya cantidad en milésimas no entra en un entero de 32 bits no se detecta.
  it('una línea pesable cuyas milésimas no entran en un entero se ignora y la del tope no', () => {
    const candidatos = [combo(1, 1000, art(JAMON), art(COCA))]

    const sobreElTope = resolverCombos([porPeso(0, JAMON, 10000, 2_147_483.648), linea(1, COCA, 2500)], candidatos)
    const enElTope = resolverCombos([porPeso(0, JAMON, 10000, 2_147_483.647), linea(1, COCA, 2500)], candidatos)

    expect(sobreElTope.combos).toEqual([])
    expect(enElTope.combos.map((c) => c.veces)).toEqual([1])
  })

  // Cláusula: de una línea por unidad solo se toman unidades enteras. Lo que falta tras un miembro pesable puede ser
  // una fracción; el miembro por unidad aporta la parte entera y la fracción la completa otro pesable.
  it('de una línea por unidad solo se toman unidades enteras aunque al componente le falte una fracción', () => {
    const lineas = [
      porPeso(0, JAMON, 1000, 0.5, FAMILIA_FIAMBRES),
      linea(1, SALAME, 400, { cantidad: 3, idFamilia: FAMILIA_FIAMBRES }),
      porPeso(2, QUESO, 1000, 0.5, FAMILIA_FIAMBRES),
      linea(3, COCA, 2500),
    ]

    const r = resolverCombos(lineas, [combo(1, 3000, fam(FAMILIA_FIAMBRES, 2), art(COCA))])

    expect(deLinea(r, 0).unidadesConsumidas).toBe(0.5)
    expect(deLinea(r, 1).unidadesConsumidas).toBe(1)
    expect(deLinea(r, 2).unidadesConsumidas).toBe(0.5)
    expect(totalCobrado(lineas, r)).toBe(3800)
  })

  // Cláusula: una línea por unidad a la que le llega menos de una unidad por completar no aporta nada y no figura en
  // el reparto; el componente lo termina la línea pesable siguiente.
  it('una línea por unidad a la que le llega menos de una unidad no aporta nada y el combo se completa con la siguiente', () => {
    const r = resolverCombos(
      [
        porPeso(0, JAMON, 1000, 1.5, FAMILIA_FIAMBRES),
        linea(1, SALAME, 400, { cantidad: 3, idFamilia: FAMILIA_FIAMBRES }),
        porPeso(2, QUESO, 1000, 0.5, FAMILIA_FIAMBRES),
        linea(3, COCA, 2500),
      ],
      [combo(1, 3000, fam(FAMILIA_FIAMBRES, 2), art(COCA))],
    )

    expect(r.lineas.map((l) => l.indice)).toEqual([0, 2, 3])
    expect(deLinea(r, 0).unidadesConsumidas).toBe(1.5)
    expect(deLinea(r, 2).unidadesConsumidas).toBe(0.5)
  })

  // Cláusula: un miembro de la familia cuya unidad de venta no admite la cantidad del componente no es candidato, ni
  // siquiera por su parte entera.
  it('un miembro por unidad no es candidato de un componente familia fraccionario ni por su parte entera', () => {
    const candidatos = [combo(1, 1000, fam(FAMILIA_FIAMBRES, 1.5), art(COCA))]

    const mezclado = resolverCombos(
      [linea(0, SALAME, 400, { cantidad: 2, idFamilia: FAMILIA_FIAMBRES }), porPeso(1, JAMON, 1000, 0.5, FAMILIA_FIAMBRES), linea(2, COCA, 2500)],
      candidatos,
    )
    const soloPesable = resolverCombos(
      [linea(0, SALAME, 400, { cantidad: 2, idFamilia: FAMILIA_FIAMBRES }), porPeso(1, JAMON, 1000, 1.5, FAMILIA_FIAMBRES), linea(2, COCA, 2500)],
      candidatos,
    )

    expect(mezclado.combos).toEqual([])
    expect(soloPesable.lineas.map((l) => l.indice)).toEqual([1, 2])
  })

  // Cláusula: el redondeo a dos decimales del importe de lista aleja el punto medio de cero:
  // 0,250 × 100,10 = 25,025 → 25,03 (hacia abajo daría 25,02 y un ahorro de 10,02).
  it('el importe de lista de la porción consumida redondea el medio centavo hacia arriba', () => {
    const r = resolverCombos(
      [porPeso(0, JAMON, 100.1, 0.25), porPeso(1, QUESO, 8000, 0.2)],
      [combo(1, 1000, art(JAMON, 0.25), art(QUESO, 0.2))],
    )

    expect(deLinea(r, 0).ahorroDeCombo).toBe(10.03)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(615)
  })

  it('un componente familia pesable se completa con varias líneas en orden de documento', () => {
    const r = resolverCombos(
      [porPeso(0, JAMON, 10000, 0.15, FAMILIA_FIAMBRES), porPeso(1, QUESO, 8000, 0.3, FAMILIA_FIAMBRES), linea(2, COCA, 2500)],
      [combo(1, 4000, fam(FAMILIA_FIAMBRES, 0.4), art(COCA))],
    )

    expect(deLinea(r, 0).unidadesConsumidas).toBe(0.15)
    expect(deLinea(r, 1).unidadesConsumidas).toBe(0.25)
  })

  // Cláusula: un combo con un componente de cantidad inválida no se aplica. Sin ella, un componente que no llega a
  // una milésima no pediría nada y el combo se completaría solo con los demás.
  it.each([0.0004, 0, -1])('un combo con un componente de cantidad inválida no se aplica aunque los demás se completen (%s)', (cantidad) => {
    const r = resolverCombos(
      [porPeso(0, JAMON, 10000, 1), linea(1, PANCHO, 3000, { cantidad: 2 }), linea(2, COCA, 2500)],
      [combo(1, 1000, art(JAMON, cantidad), art(PANCHO), art(COCA))],
    )

    expect(r.lineas).toEqual([])
    expect(r.combos).toEqual([])
  })

  it('un combo de una sola unidad o sin precio positivo no se aplica', () => {
    const lineas = [linea(0, PANCHO, 3000), linea(1, COCA, 2500)]

    expect(resolverCombos(lineas, [combo(1, 1000, art(PANCHO))]).combos).toEqual([])
    expect(resolverCombos(lineas, [combo(2, 0, art(PANCHO), art(COCA))]).combos).toEqual([])
  })

  it('el mismo artículo en dos líneas se consume en orden de documento', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 3000), linea(1, COCA, 2500), linea(2, PANCHO, 3000)],
      [combo(1, 5000, art(PANCHO), art(COCA))],
    )

    expect(r.lineas.map((l) => l.indice)).toEqual([0, 1])
  })

  it('sin líneas o sin combos la resolución es vacía', () => {
    expect(resolverCombos([], [combo(1, 5000, art(PANCHO), art(COCA))]).combos).toEqual([])
    expect(resolverCombos([linea(0, PANCHO, 3000)], []).lineas).toEqual([])
  })
})

describe('tieneMasDeDosDecimales — comparación exacta sobre la representación decimal', () => {
  it.each([
    [2, false],
    [0.01, false],
    [1234.5, false],
    [1e21, false],
    [1.005, true],
    [0.001, true],
    [1e-7, true],
    [1.5e-7, true],
  ])('%s → %s', (valor, esperado) => {
    expect(tieneMasDeDosDecimales(valor)).toBe(esperado)
  })
})

describe('motorDeCombos — más allá de los casos de C#', () => {
  it('el índice de salida es el de la línea de entrada, no la posición en la lista', () => {
    const r = resolverCombos([linea(5, PANCHO, 3000), linea(9, COCA, 2500)], [combo(7, 5000, art(PANCHO), art(COCA))])

    expect(r.lineas.map((l) => l.indice)).toEqual([5, 9])
  })

  it('el código del combo viaja a los combos aplicados cuando el candidato lo trae', () => {
    const r = resolverCombos([linea(0, PANCHO, 3000), linea(1, COCA, 2500)], [{ ...combo(7, 5000, art(PANCHO), art(COCA)), codigo: 42 }])

    expect(r.combos[0].codigo).toBe(42)
  })

  it('un componente con artículo y familia a la vez, o con ninguno, deja el combo sin aplicar', () => {
    const lineas = [linea(0, PANCHO, 3000, { idFamilia: FAMILIA_BEBIDAS }), linea(1, COCA, 2500)]
    const ambos: ComponenteDeCombo = { idArticulo: PANCHO, idFamilia: FAMILIA_BEBIDAS, cantidad: 1 }
    const ninguno: ComponenteDeCombo = { idArticulo: null, idFamilia: null, cantidad: 1 }

    expect(resolverCombos(lineas, [combo(1, 5000, ambos, art(COCA))]).combos).toEqual([])
    expect(resolverCombos(lineas, [combo(2, 5000, ninguno, art(COCA))]).combos).toEqual([])
  })

  it('el empate exacto del reparto sube (mitad lejos de cero) y el resto vuelve a la última línea', () => {
    // cobro 5 sobre 3 + 3 → 2,5 cada una: mitad lejos de cero da 3 a la primera y el resto (−1) deja 2 en la última;
    // mitad al par daría 2 y 2 con el resto sumado a la última (3).
    const r = resolverCombos([linea(0, PANCHO, 3), linea(1, COCA, 3)], [combo(1, 5, art(PANCHO), art(COCA))])

    expect(deLinea(r, 0).ahorroDeCombo).toBe(0)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(1)
    expect(ahorroTotal(r)).toBe(1)
  })

  it('el precio del combo en medio peso exacto sube al peso (mitad lejos de cero)', () => {
    const r = resolverCombos([linea(0, PANCHO, 3), linea(1, COCA, 3)], [combo(1, 4.5, art(PANCHO), art(COCA))])

    expect(ahorroTotal(r)).toBe(1)
    expect(r.combos[0].ahorro).toBe(1)
  })

  it('la parte proporcional se recorta al importe de lista hacia abajo al peso de la línea', () => {
    // 8 sobre 0,9 + 2,2 + 6,9: la proporcional de la primera (0,72) redondea a 1, pero su tope es 0; la partes cobradas son 0, 2 y 6.
    const r = resolverCombos([linea(0, PANCHO, 0.9), linea(1, COCA, 2.2), linea(2, SPRITE, 6.9)], [combo(1, 8, art(PANCHO), art(COCA), art(SPRITE))])

    expect(deLinea(r, 0).ahorroDeCombo).toBe(0.9)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(0.2)
    expect(deLinea(r, 2).ahorroDeCombo).toBe(0.9)
  })

  it('un importe en notación exponencial (muy chico) se lee exacto: 1e-7 y 3e-7 redondean a cero de lista y el combo no se aplica', () => {
    const r = resolverCombos([linea(0, PANCHO, 1e-7), linea(1, COCA, 3e-7)], [combo(1, 2e-7, art(PANCHO), art(COCA))])

    expect(r).toEqual({ lineas: [], combos: [] })
  })

  it('un precio de lista en notación exponencial (muy chico) convive con uno normal sin romper la escala', () => {
    // 1e-7 de lista redondea a 0,00; el combo cobra 1 sobre 0 + 2 y todo el cobro cae en la segunda línea.
    const r = resolverCombos([linea(0, PANCHO, 1e-7), linea(1, COCA, 2)], [combo(1, 1, art(PANCHO), art(COCA))])

    expect(deLinea(r, 0).ahorroDeCombo).toBe(0)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(1)
  })

  it('un importe en notación exponencial (muy grande) se opera exacto: 1e21 y 1e21 con el combo a 1,5e21', () => {
    const r = resolverCombos([linea(0, PANCHO, 1e21), linea(1, COCA, 1e21)], [combo(1, 1.5e21, art(PANCHO), art(COCA))])

    expect(deLinea(r, 0).ahorroDeCombo).toBe(2.5e20)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(2.5e20)
    expect(r.combos[0].ahorro).toBe(5e20)
  })

  it('con precios de cuatro decimales el importe de lista se redondea a dos antes del reparto y el ahorro no lleva más', () => {
    // lista 10,1234 → 10,12 y 20,5678 → 20,57 (total 30,69); combo 25,5 → cobra 26. Proporcional: 8,57… → 9 y 17,42… → 17.
    const r = resolverCombos([linea(0, PANCHO, 10.1234), linea(1, COCA, 20.5678)], [combo(1, 25.5, art(PANCHO), art(COCA))])

    expect(deLinea(r, 0).ahorroDeCombo).toBe(1.12)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(3.57)
    expect(r.combos[0].ahorro).toBe(4.69)
  })

  it('una cantidad de línea sobre 2^31−1 queda fuera de la detección y 2^31−1 exacto entra (el límite de int de C#)', () => {
    const combos = [combo(1, 5000, art(PANCHO), art(COCA))]

    expect(resolverCombos([linea(0, PANCHO, 3000, { cantidad: 2_147_483_648 }), linea(1, COCA, 2500)], combos)).toEqual({ lineas: [], combos: [] })
    expect(deLinea(resolverCombos([linea(0, PANCHO, 3000, { cantidad: 2_147_483_647 }), linea(1, COCA, 2500)], combos), 0).unidadesConsumidas).toBe(1)
  })

  it('un precio de fracción de centavo no deja un ahorro de fracción de centavo: 0,005 de lista es 0,01', () => {
    const r = resolverCombos([linea(0, PANCHO, 0.005), linea(1, COCA, 2)], [combo(1, 1, art(PANCHO), art(COCA))])

    expect(deLinea(r, 0).ahorroDeCombo).toBe(0.01)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(1)
    expect(r.combos[0].ahorro).toBe(1.01)
  })

  // Cláusula: sin unidad de venta la línea se trata como pesable (el criterio de `cantidadPorUnidad`).
  it.each([null, undefined])('una línea sin unidad de venta (%s) se trata como pesable', (unidad) => {
    const r = resolverCombos(
      [{ ...porPeso(0, JAMON, 10000, 0.215), unidadVenta: unidad }, porPeso(1, QUESO, 8000, 0.2)],
      [combo(1, 3000, art(JAMON, 0.2), art(QUESO, 0.2))],
    )

    expect(deLinea(r, 0).unidadesConsumidas).toBe(0.2)
  })

  // Cláusula: el tope de componente (999999) se compara con la cantidad, no con sus milésimas.
  it('un componente en el tope de cantidad se aplica y uno apenas por encima no', () => {
    const lineas = [porPeso(0, JAMON, 1, 1_000_000), linea(1, COCA, 1)]

    expect(resolverCombos(lineas, [combo(1, 5, art(JAMON, 999_999), art(COCA))]).combos.map((c) => c.veces)).toEqual([1])
    expect(resolverCombos(lineas, [combo(2, 5, art(JAMON, 999_999.001), art(COCA))]).combos).toEqual([])
  })

  // Cláusula: el mínimo no mira la suma de las cantidades. Un solo componente de 1,999 no es un combo; dos de 0,001 sí.
  it('el mínimo es dos componentes o uno solo con cantidad dos o más, sin sumar cantidades', () => {
    const lineas = [porPeso(0, JAMON, 10000, 3), porPeso(1, QUESO, 8000, 3)]

    expect(resolverCombos(lineas, [combo(1, 100, art(JAMON, 1.999))]).combos).toEqual([])
    expect(resolverCombos(lineas, [combo(2, 100, art(JAMON, 2))]).combos.map((c) => c.veces)).toEqual([1])
    expect(resolverCombos(lineas, [combo(3, 5, art(JAMON, 0.001), art(QUESO, 0.001))]).combos.map((c) => c.veces)).toEqual([3000])
  })

  // Cláusula: un combo cuyo cobro no baja del total de lista no se aplica, aunque sí baje del costo con ofertas (un
  // descuento de oferta negativo deja el costo por encima de la lista). Sin ella el combo se aplicaría sin ahorro.
  it('un combo que cobra el total de lista no se aplica aunque el costo con ofertas sea mayor', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 100, { descuentoDeOferta: -50 }), linea(1, COCA, 100, { descuentoDeOferta: -50 })],
      [combo(1, 200, art(PANCHO), art(COCA))],
    )

    expect(r).toEqual({ lineas: [], combos: [] })
  })

  it('una cantidad de millones de conjuntos se resuelve en un solo paso, no uno por conjunto', () => {
    // La salida es la misma aplicando de a uno; lo que cambia es el costo: millones de vueltas del voraz.
    const conjuntos = 3_000_000
    const inicio = performance.now()

    const r = resolverCombos(
      [linea(0, PANCHO, 3000, { cantidad: conjuntos }), linea(1, COCA, 2500, { cantidad: conjuntos })],
      [combo(1, 5000, art(PANCHO), art(COCA))],
    )

    expect(r.combos[0].veces).toBe(conjuntos)
    expect(performance.now() - inicio).toBeLessThan(1000)
  })

  it('un descuento de oferta con más de dos decimales sigue sumando exacto el precio del combo', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 100, { descuentoDeOferta: 33.333 }), linea(1, COCA, 100)],
      [combo(1, 150, art(PANCHO), art(COCA))],
    )

    expect(ahorroTotal(r)).toBe(50)
  })

  // Cláusula: el ahorro acumulado de una línea se acota por el bruto de lo que consumió. Sin el tope, el redondeo hacia
  // arriba del importe de lista de cada aplicación (2,4975 → 2,50) suma 7,50 en tres aplicaciones y supera 7,49.
  it('el ahorro acumulado de una línea no supera el importe de lista de lo que consumió', () => {
    const r = resolverCombos(
      [linea(0, JAMON, 9.99, { cantidad: 0.75, unidad: 'Peso' }), linea(1, COCA, 500, { cantidad: 3 })],
      [combo(1, 100, art(JAMON, 0.25), art(COCA))],
    )

    expect(r.lineas.map((l) => l.ahorroDeCombo)).toEqual([7.49, 1200])
    expect(r.combos.map((c) => c.veces)).toEqual([3])
  })
})
