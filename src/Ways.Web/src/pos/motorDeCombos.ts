/**
 * Motor de combos del POS: puerto a TypeScript, sin React ni I/O, de
 * `Ways.Domain.Combos.ResolvedorDeCombos` más las exclusiones del puente
 * `Ways.Application.Combos.CombosEnVenta` (que las aplica `resolverCombosDelDocumento`, no este
 * módulo). Duplicado a propósito, como `ParserDeEscaneo`: un cambio en la regla del servidor tiene
 * que replicarse acá a mano, y `motorDeCombos.test.ts` espeja caso por caso `ResolvedorDeCombosTests`.
 *
 * Es voraz: en cada vuelta elige, entre los combos que caben con las unidades restantes, el de
 * mayor ganancia real por aplicación (costo de esas unidades con sus ofertas menos el precio del
 * combo; empate: menor id), lo aplica k veces de una vez y reevalúa. El componente artículo se
 * cubre antes que el de familia y las líneas se toman en orden de documento. El ahorro de una
 * aplicación se reparte según el importe de lista de cada línea, en centavos, con el resto
 * acotado cayendo hacia atrás desde la última línea, de modo que cada conjunto suma exacto el
 * precio del combo.
 *
 * Aritmética exacta: el servidor trabaja en `decimal` con `MidpointRounding.AwayFromZero`. Acá
 * cada importe entra por su representación decimal más corta (la que `JSON.stringify` mandó y el
 * servidor leyó) y todo se opera en `bigint` escalado a la mayor cantidad de decimales de la
 * entrada; solo el prorrateo por línea redondea, a centavos, "mitad lejos de cero".
 */

export type ComponenteDeCombo = { idArticulo?: number | null; idFamilia?: number | null; cantidad: number }

/** `precio` es el del conjunto completo (una aplicación). `codigo` solo viaja para quien presenta los
 * combos aplicados: el motor no lo usa. */
export type ComboCandidato = { id: number; nombre: string; precio: number; componentes: readonly ComponenteDeCombo[]; codigo?: number }

/** El orden de la lista es el orden del documento; `indice` solo identifica la línea en la salida. */
export type LineaParaCombos = {
  indice: number
  idArticulo: number
  idFamilia: number | null
  cantidad: number
  precioLista: number
  descuentoUnitarioDeOferta: number
}

/** Descuento de línea que graba quien llama: `descuentoUnitarioDeOferta × (cantidad − unidadesConsumidas) +
 * ahorroDeCombo` (el ahorro es un importe de línea, no por unidad). */
export type ConsumoDeLinea = { indice: number; unidadesConsumidas: number; ahorroDeCombo: number; idsDeCombo: number[] }

export type ComboAplicado = { idCombo: number; nombre: string; veces: number; ahorro: number; codigo?: number }

export type ResolucionDeCombos = { lineas: ConsumoDeLinea[]; combos: ComboAplicado[] }

/** Espejo de `ReglaDeCombos.UnidadesMinimas` y `ReglaDeCombos.CantidadMaxima`. */
export const UNIDADES_MINIMAS_DE_COMBO = 2
export const CANTIDAD_MAXIMA_DE_COMPONENTE = 999_999

const ENTERO_MAXIMO = 2_147_483_647

const DECIMALES_DE_IMPORTE = 2

type Toma = { posicion: number; unidades: number; ahorro: bigint }

type Aplicacion = { combo: ComboCandidato; ganancia: bigint; ahorro: bigint; reparto: Toma[] }

const PATRON_DE_NUMERO = /^(-?)(\d+)(?:\.(\d+))?(?:e([+-]?\d+))?$/i

/** Mantisa y escala (`mantisa × 10^-escala`) del número tal como lo escribe `String`. */
function aDecimalExacto(valor: number): { mantisa: bigint; escala: number } {
  const coincidencia = PATRON_DE_NUMERO.exec(String(valor))
  if (coincidencia === null) return { mantisa: 0n, escala: 0 }
  const [, signo, enteros, fraccion = '', exponente = '0'] = coincidencia
  return { mantisa: BigInt(`${signo}${enteros}${fraccion}`), escala: fraccion.length - Number(exponente) }
}

function escalaDe(valor: number): number {
  return Number.isFinite(valor) ? Math.max(0, aDecimalExacto(valor).escala) : 0
}

function escalar(valor: number, escala: number): bigint {
  if (!Number.isFinite(valor)) return 0n
  const { mantisa, escala: propia } = aDecimalExacto(valor)
  return propia <= escala ? mantisa * 10n ** BigInt(escala - propia) : mantisa / 10n ** BigInt(propia - escala)
}

/** Número más cercano a `valor / 10^escala`, sin pasar por una división en punto flotante del bigint. */
function desescalar(valor: bigint, escala: number): number {
  const negativo = valor < 0n
  const digitos = (negativo ? -valor : valor).toString().padStart(escala + 1, '0')
  const corte = digitos.length - escala
  const texto = escala === 0 ? digitos : `${digitos.slice(0, corte)}.${digitos.slice(corte)}`
  return Number(`${negativo ? '-' : ''}${texto}`)
}

/** `numerador / denominador` redondeado "mitad lejos de cero"; ambos no negativos, denominador > 0. */
function dividirRedondeando(numerador: bigint, denominador: bigint): bigint {
  return (2n * numerador + denominador) / (2n * denominador)
}

function minimo(a: bigint, b: bigint): bigint {
  return a < b ? a : b
}

function unidadesDetectables(cantidad: number): number {
  return cantidad > 0 && cantidad <= ENTERO_MAXIMO && Number.isInteger(cantidad) ? cantidad : 0
}

function esAplicable(combo: ComboCandidato): boolean {
  if (!(combo.precio > 0) || combo.componentes.length === 0) return false

  let unidades = 0
  for (const componente of combo.componentes) {
    const tieneArticulo = componente.idArticulo != null
    const tieneFamilia = componente.idFamilia != null
    if (
      tieneArticulo === tieneFamilia ||
      !(componente.cantidad > 0) ||
      !Number.isInteger(componente.cantidad) ||
      componente.cantidad > CANTIDAD_MAXIMA_DE_COMPONENTE
    ) {
      return false
    }
    unidades += componente.cantidad
  }
  return unidades >= UNIDADES_MINIMAS_DE_COMBO
}

type Escalados = { escala: number; precioLista: bigint[]; descuento: bigint[] }

function intentar(combo: ComboCandidato, precioDelCombo: bigint, lineas: readonly LineaParaCombos[], restantes: readonly number[], e: Escalados): Aplicacion | null {
  const disponibles = [...restantes]
  const tomado = new Map<number, number>()

  // Orden estable: los componentes artículo antes que los de familia.
  const ordenados = [...combo.componentes].sort((a, b) => (a.idArticulo == null ? 1 : 0) - (b.idArticulo == null ? 1 : 0))

  for (const componente of ordenados) {
    let faltan = componente.cantidad
    for (let i = 0; i < lineas.length && faltan > 0; i++) {
      const coincide = componente.idArticulo != null ? lineas[i].idArticulo === componente.idArticulo : lineas[i].idFamilia === componente.idFamilia
      if (!coincide || disponibles[i] === 0) continue

      const unidades = Math.min(faltan, disponibles[i])
      disponibles[i] -= unidades
      faltan -= unidades
      tomado.set(i, (tomado.get(i) ?? 0) + unidades)
    }
    if (faltan > 0) return null
  }

  const posiciones = [...tomado.keys()].sort((a, b) => a - b)
  let totalDeLista = 0n
  let costoConOfertas = 0n
  const topes: bigint[] = []
  for (const p of posiciones) {
    const unidades = BigInt(tomado.get(p) as number)
    const tope = unidades * e.precioLista[p]
    topes.push(tope)
    totalDeLista += tope
    costoConOfertas += unidades * (e.precioLista[p] - e.descuento[p])
  }

  // Compuerta D1: el combo se aplica solo si es estrictamente más barato que esas unidades con su oferta.
  if (precioDelCombo >= costoConOfertas) return null
  // Sin importe de lista no hay sobre qué repartir; con descuentos de oferta no negativos no puede pasar.
  if (totalDeLista <= 0n) return null

  const ahorro = totalDeLista - precioDelCombo
  const unidadDeCentavo = 10n ** BigInt(Math.max(0, e.escala - DECIMALES_DE_IMPORTE))
  const partes = topes.map((tope) => {
    const proporcional = dividirRedondeando(ahorro * tope, totalDeLista * unidadDeCentavo) * unidadDeCentavo
    return proporcional < 0n ? 0n : proporcional > tope ? tope : proporcional
  })

  let resto = ahorro - partes.reduce((a, b) => a + b, 0n)
  for (let k = partes.length - 1; k >= 0 && resto !== 0n; k--) {
    const ajuste = resto > 0n ? minimo(resto, topes[k] - partes[k]) : -minimo(-resto, partes[k])
    partes[k] += ajuste
    resto -= ajuste
  }

  const reparto = posiciones.map((posicion, k) => ({ posicion, unidades: tomado.get(posicion) as number, ahorro: partes[k] }))
  return { combo, ganancia: costoConOfertas - precioDelCombo, ahorro, reparto }
}

export function resolverCombos(lineas: readonly LineaParaCombos[], combos: readonly ComboCandidato[]): ResolucionDeCombos {
  const vacia: ResolucionDeCombos = { lineas: [], combos: [] }
  const aplicables = combos.filter(esAplicable)
  if (lineas.length === 0 || aplicables.length === 0) return vacia

  const escala = Math.max(
    DECIMALES_DE_IMPORTE,
    ...lineas.flatMap((l) => [escalaDe(l.precioLista), escalaDe(l.descuentoUnitarioDeOferta)]),
    ...aplicables.map((c) => escalaDe(c.precio)),
  )
  const escalados: Escalados = {
    escala,
    precioLista: lineas.map((l) => escalar(l.precioLista, escala)),
    descuento: lineas.map((l) => escalar(l.descuentoUnitarioDeOferta, escala)),
  }
  const preciosDeCombo = new Map(aplicables.map((c) => [c, escalar(c.precio, escala)]))

  const restantes = lineas.map((l) => unidadesDetectables(l.cantidad))
  const consumidas = lineas.map(() => 0)
  const ahorroPorLinea = lineas.map(() => 0n)
  const combosPorLinea = lineas.map(() => new Set<number>())
  const vecesPorCombo = new Map<number, { veces: number; ahorro: bigint }>()

  for (;;) {
    let mejor: Aplicacion | null = null
    for (const combo of aplicables) {
      const candidata = intentar(combo, preciosDeCombo.get(combo) as bigint, lineas, restantes, escalados)
      if (candidata === null) continue
      if (mejor === null || candidata.ganancia > mejor.ganancia || (candidata.ganancia === mejor.ganancia && combo.id < mejor.combo.id)) {
        mejor = candidata
      }
    }
    if (mejor === null) break

    let veces = Number.MAX_SAFE_INTEGER
    for (const { posicion, unidades } of mejor.reparto) veces = Math.min(veces, Math.floor(restantes[posicion] / unidades))

    for (const { posicion, unidades, ahorro } of mejor.reparto) {
      restantes[posicion] -= unidades * veces
      consumidas[posicion] += unidades * veces
      ahorroPorLinea[posicion] += ahorro * BigInt(veces)
      combosPorLinea[posicion].add(mejor.combo.id)
    }

    const acumulado = vecesPorCombo.get(mejor.combo.id) ?? { veces: 0, ahorro: 0n }
    vecesPorCombo.set(mejor.combo.id, { veces: acumulado.veces + veces, ahorro: acumulado.ahorro + mejor.ahorro * BigInt(veces) })
  }

  if (vecesPorCombo.size === 0) return vacia

  const resultadoLineas: ConsumoDeLinea[] = []
  lineas.forEach((linea, i) => {
    if (consumidas[i] > 0) {
      resultadoLineas.push({
        indice: linea.indice,
        unidadesConsumidas: consumidas[i],
        ahorroDeCombo: desescalar(ahorroPorLinea[i], escala),
        idsDeCombo: [...combosPorLinea[i]].sort((a, b) => a - b),
      })
    }
  })

  const vistos = new Set<number>()
  const resultadoCombos: ComboAplicado[] = []
  for (const combo of aplicables) {
    const aplicado = vecesPorCombo.get(combo.id)
    if (aplicado === undefined || vistos.has(combo.id)) continue
    vistos.add(combo.id)
    resultadoCombos.push({
      idCombo: combo.id,
      nombre: combo.nombre,
      veces: aplicado.veces,
      ahorro: desescalar(aplicado.ahorro, escala),
      ...(combo.codigo !== undefined ? { codigo: combo.codigo } : {}),
    })
  }
  resultadoCombos.sort((a, b) => a.idCombo - b.idCombo)

  return { lineas: resultadoLineas, combos: resultadoCombos }
}
