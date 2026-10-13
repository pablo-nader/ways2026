/**
 * Motor de combos del POS: puerto a TypeScript, sin React ni I/O, de
 * `Ways.Domain.Combos.ResolvedorDeCombos` más las exclusiones del puente
 * `Ways.Application.Combos.CombosEnVenta` (que las aplica `CombosEnVenta.Detectar`; en TypeScript las
 * aplica `resolverDocumentoOffline`, no este módulo). Duplicado a propósito, como `ParserDeEscaneo`: un cambio en la regla del servidor tiene
 * que replicarse acá a mano, y `motorDeCombos.test.ts` espeja caso por caso `ResolvedorDeCombosTests`.
 *
 * Es voraz: en cada vuelta elige, entre los combos que caben con las cantidades restantes, el de
 * mayor ganancia real por aplicación (costo de esas cantidades con sus ofertas menos lo que el combo
 * cobra; empate: menor id), lo aplica k veces de una vez y reevalúa. El componente artículo se
 * cubre antes que el de familia y las líneas se toman en orden de documento. Lo que cobra una
 * aplicación es el precio del combo redondeado al peso entero, y ese cobro (no el ahorro) se reparte
 * según el importe de lista de cada línea en pesos enteros que suman exacto el cobro, cada parte
 * entre cero y el importe de lista de la cantidad que aporta (hacia abajo al peso); el resto cae
 * hacia atrás desde la última línea. El importe de lista de la cantidad que aporta una línea se redondea a dos
 * decimales antes del reparto, así que el ahorro de cada línea (ese importe menos su parte del cobro) lleva a lo
 * sumo dos decimales. El cobro es exacto por aplicación; como ese redondeo se repite en cada una de las k aplicaciones,
 * el ahorro acumulado de una línea se acota al final por el importe de lista, redondeado a dos decimales, de todo lo que
 * consumió: nunca supera el bruto de sus unidades consumidas (lo que exige el servidor) ni deja la línea en negativo.
 * Un combo cuyo cobro redondeado no es menor al costo con ofertas ni al total de lista,
 * o cuyos topes no alcanzan a contener el cobro, no se aplica.
 *
 * Cantidades: una línea aporta toda su cantidad y el combo consume exactamente la cantidad de cada componente; lo
 * que sobra queda fuera del combo y la línea nunca se parte. Qué cantidades valen lo decide la unidad de venta de
 * la línea, nunca la forma del número: una línea cuya cantidad su unidad no admite no se detecta, una línea solo
 * es candidata de un componente cuya cantidad su unidad admite, y de una línea por unidad solo se toman unidades
 * enteras. Solo entran las líneas por unidad y por peso (`ReglaDeCombos.ParticipaEnCombos`): una línea de un artículo
 * por importe no se detecta. Las cuentas de cantidad se hacen en milésimas enteras, nunca en punto flotante.
 *
 * Aritmética exacta: el servidor trabaja en `decimal` con `MidpointRounding.AwayFromZero`. Acá
 * cada importe y cada cantidad entra por su representación decimal más corta (la que `JSON.stringify` mandó y el
 * servidor leyó) y todo se opera en `bigint` escalado a la mayor cantidad de decimales de los importes de la
 * entrada más los tres de una cantidad; redondean el importe de lista de cada línea (a dos decimales) y el precio
 * del combo y el prorrateo por línea (al peso), siempre "mitad lejos de cero". Los precios de lista no son negativos.
 */
import type { UnidadVenta } from '../api/tipos'

export type ComponenteDeCombo = { idArticulo?: number | null; idFamilia?: number | null; cantidad: number }

/** `precio` es el del conjunto completo (una aplicación). `codigo` solo viaja para quien presenta los
 * combos aplicados: el motor no lo usa. */
export type ComboCandidato = { id: number; nombre: string; precio: number; componentes: readonly ComponenteDeCombo[]; codigo?: number }

/** El orden de la lista es el orden del documento; `indice` solo identifica la línea en la salida. `cantidad` va
 * en la unidad de venta del artículo (unidades enteras o kilos con hasta tres decimales). Una `unidadVenta` ausente
 * se trata como `Peso`, el mismo criterio que `api/cantidadPorUnidad`; una línea `Importe` no se detecta. */
export type LineaParaCombos = {
  indice: number
  idArticulo: number
  idFamilia: number | null
  cantidad: number
  precioLista: number
  descuentoUnitarioDeOferta: number
  unidadVenta: UnidadVenta | null | undefined
}

/** Quien llama calcula la línea con `calcularTotalesDeLinea`, pasando `unidadesConsumidas` y `ahorroDeCombo` (el
 * ahorro es un importe de línea, no por unidad). `unidadesConsumidas` va en la unidad de venta de la línea: unidades
 * enteras o kilos con hasta tres decimales. */
export type ConsumoDeLinea = { indice: number; unidadesConsumidas: number; ahorroDeCombo: number; idsDeCombo: number[] }

export type ComboAplicado = { idCombo: number; nombre: string; veces: number; ahorro: number; codigo?: number }

export type ResolucionDeCombos = { lineas: ConsumoDeLinea[]; combos: ComboAplicado[] }

/** Espejo de `ReglaDeCombos.CantidadMinimaDeUnComponenteUnico` y `ReglaDeCombos.CantidadMaxima`. */
export const CANTIDAD_MINIMA_DE_UN_COMPONENTE_UNICO = 2
export const CANTIDAD_MAXIMA_DE_COMPONENTE = 999_999

const ENTERO_MAXIMO = 2_147_483_647

const DECIMALES_DE_IMPORTE = 2

/** Los decimales de una cantidad por peso (`ReglaDeCantidadDeVenta.DecimalesMaximosDePeso`). */
const DECIMALES_DE_CANTIDAD = 3
const MILESIMAS_POR_UNIDAD = 1000

type Toma = { posicion: number; milesimas: number; ahorro: bigint }

type Aplicacion = { combo: ComboCandidato; ganancia: bigint; ahorro: bigint; reparto: Toma[] }

type ComponentePreparado = { idArticulo: number | null; idFamilia: number | null; milesimas: number }

/** Un combo aplicable, con el precio escalado y los componentes en milésimas, los de artículo antes que los de familia. */
type ComboPreparado = { combo: ComboCandidato; precio: bigint; componentes: ComponentePreparado[] }

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

/** `true` si el importe, tal como lo escribe `String`, tiene más de dos decimales (comparación exacta, sin flotantes). */
export function tieneMasDeDosDecimales(valor: number): boolean {
  return escalaDe(valor) > DECIMALES_DE_IMPORTE
}

/** La cantidad en milésimas enteras, o `null` si no es finita o lleva más de tres decimales. */
function aMilesimas(cantidad: number): number | null {
  if (!Number.isFinite(cantidad) || escalaDe(cantidad) > DECIMALES_DE_CANTIDAD) return null
  return Number(escalar(cantidad, DECIMALES_DE_CANTIDAD))
}

/** Espejo de `ReglaDeCombos.ParticipaEnCombos`: entran a combos los artículos por unidad y por peso (y el de unidad
 * ausente, que cuenta como `Peso`); el que se vende por importe nunca es componente, candidato ni línea detectable. */
export function participaEnCombos(unidadVenta: UnidadVenta | null | undefined): boolean {
  return unidadVenta !== 'Importe'
}

/** Espejo de `ReglaDeCantidadDeVenta.EsCantidadAdmitida` sobre milésimas, para las unidades que entran a combos:
 * por unidad, solo enteros. */
function admite(unidadVenta: UnidadVenta | null | undefined, milesimas: number): boolean {
  return unidadVenta !== 'Unidad' || milesimas % MILESIMAS_POR_UNIDAD === 0
}

/** La cantidad de la línea en milésimas, o cero si la línea no se detecta: de un artículo por importe, cantidad no
 * positiva, que su unidad de venta no admite, o fuera de tope (2^31−1 unidades para una línea por unidad y 2^31−1 milésimas para una pesable,
 * los topes del servidor). */
function milesimasDetectables(linea: LineaParaCombos): number {
  if (!participaEnCombos(linea.unidadVenta) || !(linea.cantidad > 0) || linea.cantidad > ENTERO_MAXIMO) return 0
  const milesimas = aMilesimas(linea.cantidad)
  if (milesimas === null || !admite(linea.unidadVenta, milesimas)) return 0
  return linea.unidadVenta !== 'Unidad' && milesimas > ENTERO_MAXIMO ? 0 : milesimas
}

/** Espejo de `ReglaDeCombos.EsCantidadDeComponenteValida`: las milésimas del componente, o `null` si su cantidad
 * no es mayor a cero, supera el tope o lleva más de tres decimales. */
function milesimasDeComponente(cantidad: number): number | null {
  return cantidad > 0 && cantidad <= CANTIDAD_MAXIMA_DE_COMPONENTE ? aMilesimas(cantidad) : null
}

/** El combo listo para intentarse, o `null` si no es aplicable: sin precio positivo, por debajo del mínimo
 * (`ReglaDeCombos.AlcanzaElMinimo`: dos componentes, o uno solo con cantidad dos o más) o con un componente inválido.
 * Que la cantidad sea la que admite la unidad de venta de cada línea candidata lo decide `intentar`. */
function preparar(combo: ComboCandidato, escala: number): ComboPreparado | null {
  const cantidad = combo.componentes.length
  const alcanzaElMinimo = cantidad >= 2 || (cantidad === 1 && combo.componentes[0].cantidad >= CANTIDAD_MINIMA_DE_UN_COMPONENTE_UNICO)
  if (!(combo.precio > 0) || !alcanzaElMinimo) return null

  const componentes: ComponentePreparado[] = []
  for (const componente of combo.componentes) {
    const milesimas = milesimasDeComponente(componente.cantidad)
    if ((componente.idArticulo != null) === (componente.idFamilia != null) || milesimas === null) return null
    componentes.push({ idArticulo: componente.idArticulo ?? null, idFamilia: componente.idFamilia ?? null, milesimas })
  }
  // Orden estable: los componentes artículo antes que los de familia.
  componentes.sort((a, b) => (a.idArticulo === null ? 1 : 0) - (b.idArticulo === null ? 1 : 0))

  return { combo, precio: escalar(combo.precio, escala), componentes }
}

/** `escala` es la de los importes de la entrada; las cuentas van en `escala + 3` decimales, los que suma una cantidad. */
type Escalados = { escala: number; precioLista: bigint[]; descuento: bigint[] }

function intentar(preparado: ComboPreparado, lineas: readonly LineaParaCombos[], restantes: readonly number[], e: Escalados): Aplicacion | null {
  const disponibles = [...restantes]
  const tomado = new Map<number, number>()

  for (const componente of preparado.componentes) {
    let faltan = componente.milesimas
    for (let i = 0; i < lineas.length && faltan > 0; i++) {
      const coincide = componente.idArticulo !== null ? lineas[i].idArticulo === componente.idArticulo : lineas[i].idFamilia === componente.idFamilia
      if (!coincide || disponibles[i] === 0 || !admite(lineas[i].unidadVenta, componente.milesimas)) continue

      let milesimas = Math.min(faltan, disponibles[i])
      // De una línea por unidad solo se toman unidades enteras. Si le llega menos de una unidad la toma queda en cero y
      // la línea se salta: no consume, no recibe parte del cobro ni figura entre los combos de la línea.
      if (lineas[i].unidadVenta === 'Unidad') milesimas -= milesimas % MILESIMAS_POR_UNIDAD
      if (milesimas === 0) continue

      disponibles[i] -= milesimas
      faltan -= milesimas
      tomado.set(i, (tomado.get(i) ?? 0) + milesimas)
    }
    if (faltan > 0) return null
  }

  const escalaDeCuenta = e.escala + DECIMALES_DE_CANTIDAD
  const unidadDePeso = 10n ** BigInt(escalaDeCuenta)
  const unidadDeCentavo = 10n ** BigInt(escalaDeCuenta - DECIMALES_DE_IMPORTE)

  const posiciones = [...tomado.keys()].sort((a, b) => a - b)
  let totalDeLista = 0n
  let costoConOfertas = 0n
  const importes: bigint[] = []
  for (const p of posiciones) {
    const milesimas = BigInt(tomado.get(p) as number)
    const importe = dividirRedondeando(milesimas * e.precioLista[p], unidadDeCentavo) * unidadDeCentavo
    importes.push(importe)
    totalDeLista += importe
    costoConOfertas += milesimas * (e.precioLista[p] - e.descuento[p])
  }

  const cobro = dividirRedondeando(preparado.precio, 10n ** BigInt(e.escala)) * unidadDePeso
  // Compuerta D1: el combo se aplica solo si lo que cobra, ya al peso, es estrictamente menor que esas cantidades con
  // su oferta (sin redondear) y que su total de lista (ya redondeado, que puede quedar en cero).
  if (cobro >= costoConOfertas || cobro >= totalDeLista) return null

  const topesAlPeso = importes.map((importe) => (importe / unidadDePeso) * unidadDePeso)
  const partes = importes.map((importe, k) => {
    const proporcional = dividirRedondeando(cobro * importe, totalDeLista * unidadDePeso) * unidadDePeso
    return proporcional < 0n ? 0n : proporcional > topesAlPeso[k] ? topesAlPeso[k] : proporcional
  })

  let resto = cobro - partes.reduce((a, b) => a + b, 0n)
  for (let k = partes.length - 1; k >= 0 && resto !== 0n; k--) {
    const ajuste = resto > 0n ? minimo(resto, topesAlPeso[k] - partes[k]) : -minimo(-resto, partes[k])
    partes[k] += ajuste
    resto -= ajuste
  }
  if (resto !== 0n) return null

  const reparto = posiciones.map((posicion, k) => ({ posicion, milesimas: tomado.get(posicion) as number, ahorro: importes[k] - partes[k] }))
  return { combo: preparado.combo, ganancia: costoConOfertas - cobro, ahorro: totalDeLista - cobro, reparto }
}

export function resolverCombos(lineas: readonly LineaParaCombos[], combos: readonly ComboCandidato[]): ResolucionDeCombos {
  const vacia: ResolucionDeCombos = { lineas: [], combos: [] }
  if (lineas.length === 0) return vacia

  const escala = Math.max(
    DECIMALES_DE_IMPORTE,
    ...lineas.flatMap((l) => [escalaDe(l.precioLista), escalaDe(l.descuentoUnitarioDeOferta)]),
    ...combos.map((c) => escalaDe(c.precio)),
  )
  const aplicables = combos.flatMap((c) => preparar(c, escala) ?? [])
  if (aplicables.length === 0) return vacia

  const escalados: Escalados = {
    escala,
    precioLista: lineas.map((l) => escalar(l.precioLista, escala)),
    descuento: lineas.map((l) => escalar(l.descuentoUnitarioDeOferta, escala)),
  }
  const escalaDeCuenta = escala + DECIMALES_DE_CANTIDAD
  const unidadDeCentavo = 10n ** BigInt(escalaDeCuenta - DECIMALES_DE_IMPORTE)

  const restantes = lineas.map(milesimasDetectables)
  const consumidas = lineas.map(() => 0)
  const ahorroPorLinea = lineas.map(() => 0n)
  const combosPorLinea = lineas.map(() => new Set<number>())
  const vecesPorCombo = new Map<number, { veces: number; ahorro: bigint }>()

  for (;;) {
    let mejor: Aplicacion | null = null
    for (const preparado of aplicables) {
      const candidata = intentar(preparado, lineas, restantes, escalados)
      if (candidata === null) continue
      if (mejor === null || candidata.ganancia > mejor.ganancia || (candidata.ganancia === mejor.ganancia && preparado.combo.id < mejor.combo.id)) {
        mejor = candidata
      }
    }
    if (mejor === null) break

    let veces = ENTERO_MAXIMO
    for (const { posicion, milesimas } of mejor.reparto) veces = Math.min(veces, Math.floor(restantes[posicion] / milesimas))

    for (const { posicion, milesimas, ahorro } of mejor.reparto) {
      restantes[posicion] -= milesimas * veces
      consumidas[posicion] += milesimas * veces
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
      const topeDeLinea = dividirRedondeando(BigInt(consumidas[i]) * escalados.precioLista[i], unidadDeCentavo) * unidadDeCentavo
      resultadoLineas.push({
        indice: linea.indice,
        unidadesConsumidas: desescalar(BigInt(consumidas[i]), DECIMALES_DE_CANTIDAD),
        ahorroDeCombo: desescalar(minimo(ahorroPorLinea[i], topeDeLinea), escalaDeCuenta),
        idsDeCombo: [...combosPorLinea[i]].sort((a, b) => a - b),
      })
    }
  })

  const vistos = new Set<number>()
  const resultadoCombos: ComboAplicado[] = []
  for (const { combo } of aplicables) {
    const aplicado = vecesPorCombo.get(combo.id)
    if (aplicado === undefined || vistos.has(combo.id)) continue
    vistos.add(combo.id)
    resultadoCombos.push({
      idCombo: combo.id,
      nombre: combo.nombre,
      veces: aplicado.veces,
      ahorro: desescalar(aplicado.ahorro, escalaDeCuenta),
      ...(combo.codigo !== undefined ? { codigo: combo.codigo } : {}),
    })
  }
  resultadoCombos.sort((a, b) => a.idCombo - b.idCombo)

  return { lineas: resultadoLineas, combos: resultadoCombos }
}
