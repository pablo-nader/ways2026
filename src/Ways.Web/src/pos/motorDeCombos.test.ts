import { describe, expect, it } from 'vitest'
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

function linea(
  indice: number,
  idArticulo: number,
  precio: number,
  opciones: { cantidad?: number; idFamilia?: number | null; descuentoDeOferta?: number } = {},
): LineaParaCombos {
  return {
    indice,
    idArticulo,
    idFamilia: opciones.idFamilia ?? null,
    cantidad: opciones.cantidad ?? 1,
    precioLista: precio,
    descuentoUnitarioDeOferta: opciones.descuentoDeOferta ?? 0,
  }
}

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

  it.each([0.5, 1.5, -1, 0])('las líneas con cantidad no entera o no positiva se ignoran en la detección (%s)', (cantidad) => {
    const r = resolverCombos([linea(0, PANCHO, 3000, { cantidad }), linea(1, COCA, 2500)], [combo(1, 5000, art(PANCHO), art(COCA))])

    expect(r.lineas).toEqual([])
    expect(r.combos).toEqual([])
  })

  it('un combo cuyo componente tiene cantidad fraccionaria no se aplica', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 3000, { cantidad: 2 }), linea(1, COCA, 2500)],
      [combo(1, 5000, art(PANCHO, 1.5), art(COCA))],
    )

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

  it('un importe en notación exponencial (muy chico) se opera exacto: 1e-7 y 3e-7 con el combo a 2e-7', () => {
    const r = resolverCombos([linea(0, PANCHO, 1e-7), linea(1, COCA, 3e-7)], [combo(1, 2e-7, art(PANCHO), art(COCA))])

    expect(deLinea(r, 0).ahorroDeCombo).toBe(1e-7)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(3e-7)
    expect(r.combos[0].ahorro).toBe(4e-7)
  })

  it('un importe en notación exponencial (muy grande) se opera exacto: 1e21 y 1e21 con el combo a 1,5e21', () => {
    const r = resolverCombos([linea(0, PANCHO, 1e21), linea(1, COCA, 1e21)], [combo(1, 1.5e21, art(PANCHO), art(COCA))])

    expect(deLinea(r, 0).ahorroDeCombo).toBe(2.5e20)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(2.5e20)
    expect(r.combos[0].ahorro).toBe(5e20)
  })

  it('con importes de cuatro decimales el cobro se reparte en pesos y el ahorro lleva las fracciones de lista', () => {
    // lista 10,1234 + 20,5678 = 30,6912; combo 25,5 → cobra 26. Proporcional: 8,5757… → 9 y 17,4242… → 17.
    const r = resolverCombos([linea(0, PANCHO, 10.1234), linea(1, COCA, 20.5678)], [combo(1, 25.5, art(PANCHO), art(COCA))])

    expect(deLinea(r, 0).ahorroDeCombo).toBe(1.1234)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(3.5678)
    expect(r.combos[0].ahorro).toBe(4.6912)
  })

  it('una cantidad de línea sobre 2^31−1 queda fuera de la detección y 2^31−1 exacto entra (el límite de int de C#)', () => {
    const combos = [combo(1, 5000, art(PANCHO), art(COCA))]

    expect(resolverCombos([linea(0, PANCHO, 3000, { cantidad: 2_147_483_648 }), linea(1, COCA, 2500)], combos)).toEqual({ lineas: [], combos: [] })
    expect(deLinea(resolverCombos([linea(0, PANCHO, 3000, { cantidad: 2_147_483_647 }), linea(1, COCA, 2500)], combos), 0).unidadesConsumidas).toBe(1)
  })

  it('el motor no corta una parte de fracción de centavo: esa guarda vive en quien llama (resolverDocumentoOffline)', () => {
    const r = resolverCombos([linea(0, PANCHO, 0.005), linea(1, COCA, 2)], [combo(1, 1, art(PANCHO), art(COCA))])

    expect(deLinea(r, 0).ahorroDeCombo).toBe(0.005)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(1)
    expect(r.combos[0].ahorro).toBe(1.005)
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
})
