import { describe, expect, it } from 'vitest'
import { redondearImporte } from '../formato/importes'
import {
  resolverCombos,
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
    expect(pancho.ahorroDeCombo).toBe(272.73)
    expect(coca.unidadesConsumidas).toBe(1)
    expect(coca.ahorroDeCombo).toBe(227.27)
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

    expect(deLinea(r, 0).ahorroDeCombo).toBe(277.78)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(222.22)
  })

  it('un combo de tres componentes reparte por proporción del importe de lista', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 1000), linea(1, COCA, 2000), linea(2, SPRITE, 3000)],
      [combo(1, 5000, art(PANCHO), art(COCA), art(SPRITE))],
    )

    expect(deLinea(r, 0).ahorroDeCombo).toBe(166.67)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(333.33)
    expect(deLinea(r, 2).ahorroDeCombo).toBe(500)
  })

  it('el resto del redondeo va a la última línea consumida y el conjunto suma exacto el precio', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 100), linea(1, COCA, 100), linea(2, SPRITE, 100)],
      [combo(1, 200, art(PANCHO), art(COCA), art(SPRITE))],
    )

    expect(deLinea(r, 0).ahorroDeCombo).toBe(33.33)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(33.33)
    expect(deLinea(r, 2).ahorroDeCombo).toBe(33.34)
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
    expect(pancho.ahorroDeCombo).toBe(545.46)
    expect(coca.unidadesConsumidas).toBe(2)
    expect(coca.ahorroDeCombo).toBe(454.54)
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
    expect(pancho.ahorroDeCombo).toBe(272.73)
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

  it('el combo que no es más barato que la oferta no se aplica y si lo es por un centavo sí (compuerta D1)', () => {
    const lineas = [linea(0, PANCHO, 3000, { descuentoDeOferta: 1000 }), linea(1, COCA, 2500)]

    const empate = resolverCombos(lineas, [combo(1, 4500, art(PANCHO), art(COCA))])
    expect(empate.lineas).toEqual([])
    expect(empate.combos).toEqual([])

    const masBarato = resolverCombos(lineas, [combo(1, 4499.99, art(PANCHO), art(COCA))])
    expect(masBarato.lineas).toHaveLength(2)
    expect(ahorroTotal(masBarato)).toBe(1000.01)
  })

  it('las unidades de la línea fuera del combo conservan su oferta porque solo se consume lo necesario', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 3000, { cantidad: 2, descuentoDeOferta: 500 }), linea(1, COCA, 2500)],
      [combo(1, 4800, art(PANCHO), art(COCA))],
    )

    const pancho = deLinea(r, 0)
    const coca = deLinea(r, 1)
    expect(pancho.unidadesConsumidas).toBe(1)
    expect(pancho.ahorroDeCombo).toBe(381.82)
    expect(coca.ahorroDeCombo).toBe(318.18)

    const descuentoPancho = suma([500 * (2 - pancho.unidadesConsumidas), pancho.ahorroDeCombo])
    const descuentoCoca = coca.ahorroDeCombo
    expect(descuentoPancho).toBe(881.82)
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
    expect(deLinea(r, 0).ahorroDeCombo).toBe(136_365_000)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(113_635_000)
    expect(ahorroTotal(r)).toBe(r.combos[0].ahorro)
  })

  it('una línea de precio cero no recibe una parte negativa del redondeo', () => {
    const r = resolverCombos(
      [linea(0, PANCHO, 10), linea(1, COCA, 10), linea(2, SPRITE, 0)],
      [combo(1, 19.99, art(PANCHO), art(COCA), art(SPRITE))],
    )

    expect(r.lineas.every((l) => l.ahorroDeCombo >= 0 && l.ahorroDeCombo <= 10)).toBe(true)
    expect(ahorroTotal(r)).toBe(0.01)
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
    expect(suma(lineas.map((l) => l.precioLista)) - ahorroTotal(r)).toBeCloseTo(0.06, 10)
    expect(redondearImporte(suma(lineas.map((l) => l.precioLista)) - ahorroTotal(r), 2)).toBe(0.06)
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

  it('el redondeo del reparto es exacto en el empate: un medio centavo sube (mitad lejos de cero)', () => {
    // ahorro 0,03 repartido 50/50 → 0,015 cada uno, que en decimal exacto redondea a 0,02 y deja 0,01 negativo
    // de resto; el resto se devuelve a la última línea.
    const r = resolverCombos([linea(0, PANCHO, 1), linea(1, COCA, 1)], [combo(1, 1.97, art(PANCHO), art(COCA))])

    expect(deLinea(r, 0).ahorroDeCombo).toBe(0.02)
    expect(deLinea(r, 1).ahorroDeCombo).toBe(0.01)
    expect(ahorroTotal(r)).toBe(0.03)
  })

  it('con un importe de lista de más de dos decimales la parte redondeada a centavos nunca supera el tope de la línea', () => {
    // Sin el tope, 0,0150985 redondea a 0,02 y la línea de 0,0151 recibiría más que su importe de lista.
    const r = resolverCombos([linea(0, PANCHO, 0.0151), linea(1, COCA, 100)], [combo(1, 0.01, art(PANCHO), art(COCA))])

    expect(deLinea(r, 0).ahorroDeCombo).toBeLessThanOrEqual(0.0151)
    expect(deLinea(r, 1).ahorroDeCombo).toBeLessThanOrEqual(100)
    expect(redondearImporte(deLinea(r, 0).ahorroDeCombo + deLinea(r, 1).ahorroDeCombo, 4)).toBe(100.0051)
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
