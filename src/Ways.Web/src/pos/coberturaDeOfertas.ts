/**
 * Ofertas "solo múltiplos" en el POS offline: puerto a TypeScript, sin React ni I/O, de
 * `Ways.Domain.Ofertas.CoberturaDeOfertas`. No evalúa ofertas (eso lo hizo el servidor al armar la instantánea): con las
 * ofertas que ya aplicaron a cada línea, calcula su descuento de oferta como IMPORTE DE LÍNEA. Duplicado a propósito,
 * como `motorDeCombos.ts`: un cambio en la regla del servidor tiene que replicarse acá a mano, y
 * `coberturaDeOfertas.test.ts` espeja caso por caso `CoberturaDeOfertasTests`.
 *
 * Una oferta común cubre todas las unidades de la línea. Una "solo múltiplos" (`multiploDe` = N) de artículo, grupo o
 * categoría cubre `N × floor(unidades / N)` de su propia línea. Una de familia (`porFamilia`) cuenta los grupos sobre la
 * suma `U` de las unidades de todas las líneas que la llevan y reparte su descuento (`C × d`, con `C` las unidades de los
 * grupos completos y `d` su descuento unitario en la primera de esas líneas) en proporción a las unidades de cada línea:
 * cada una su parte truncada al peso y la última el resto, sin cubrir más unidades de las que tiene (si no, sigue hacia
 * las anteriores). Cada unidad suma los descuentos unitarios de las ofertas que la cubren, con el tope del precio
 * original; las coberturas quedan anidadas (las unidades cubiertas son las primeras de la línea) y la de una oferta de
 * familia es su parte dividida por `d`, una fracción.
 *
 * Aritmética exacta, como el `decimal` del servidor: cada número entra por su representación decimal más corta y todo
 * se opera en `bigint` a una escala común; el resultado sale con todos sus decimales (sin redondear: el neto de la línea
 * se redondea una sola vez, al peso, en `calcularTotalesDeLinea`), salvo en una línea con una oferta de familia, que sale
 * redondeada a seis decimales lejos de cero, igual que en el servidor.
 */
import type { OfertaAplicada } from '../api/tipos'

/** `unidades` son las que cobra la oferta, sin signo: la cantidad de la línea menos las que consumió un combo. */
export type LineaConOfertas = { unidades: number; precioOriginal: number; aplicadas: readonly OfertaAplicada[] }

/** `true` si alguna oferta aplicada es "solo múltiplos": solo entonces la línea necesita `descuentosDeLinea`. */
export function tieneSoloMultiplos(aplicadas: readonly OfertaAplicada[]): boolean {
  return aplicadas.some((a) => a.multiploDe != null)
}

/** El descuento por unidad de la resolución entera (`PrecioConOfertas.DescuentoUnitario` del servidor): la suma de los
 * descuentos aplicados, entre cero y el precio original. */
export function descuentoUnitarioDeAplicadas(precioOriginal: number, aplicadas: readonly OfertaAplicada[]): number {
  const escala = escalaComun([precioOriginal, ...aplicadas.map((a) => a.descuentoUnitario)])
  const suma = aplicadas.reduce((total, a) => total + escalar(a.descuentoUnitario, escala), 0n)
  return desescalar(acotar(suma, escalar(precioOriginal, escala)), escala)
}

/** El descuento de oferta de cada línea de `lineas`, en el mismo orden, sin signo. */
export function descuentosDeLinea(lineas: readonly LineaConOfertas[]): number[] {
  const escala = escalaComun(
    lineas.flatMap((l) => [l.unidades, l.precioOriginal, ...l.aplicadas.flatMap((a) => [a.descuentoUnitario, a.multiploDe ?? 0])]),
  )
  const unidadDeEscala = 10n ** BigInt(escala)
  const partesDeFamilia = partesDeOfertasDeFamilia(lineas, escala)

  return lineas.map((linea, l) => {
    const unidades = maximo(escalar(linea.unidades, escala), 0n)
    const coberturas = linea.aplicadas.map((aplicada): Cobertura => {
      if (aplicada.multiploDe == null) return { numerador: unidades, denominador: unidadDeEscala }
      if (aplicada.porFamilia) return partesDeFamilia.get(clave(l, aplicada.idOferta)) ?? { numerador: 0n, denominador: 1n }
      return { numerador: enGruposCompletos(unidades, escalar(aplicada.multiploDe, escala)), denominador: unidadDeEscala }
    })
    return descuentoEscalonado(linea, coberturas, escala)
  })
}

/** Unidades cubiertas como fracción exacta `numerador / denominador`, en unidades y sin escala. */
type Cobertura = { numerador: bigint; denominador: bigint }

const DECIMALES_CON_FAMILIA = 6

function clave(linea: number, idOferta: number): string {
  return `${linea}:${idOferta}`
}

/** La cobertura de cada oferta "solo múltiplos" de familia en cada línea que la lleva, por `linea:idOferta`. */
function partesDeOfertasDeFamilia(lineas: readonly LineaConOfertas[], escala: number): Map<string, Cobertura> {
  const porOferta = new Map<number, { multiplo: bigint; unitario: bigint; lineas: { linea: number; unidades: bigint }[] }>()
  lineas.forEach((linea, l) => {
    for (const aplicada of linea.aplicadas) {
      if (aplicada.multiploDe == null || !aplicada.porFamilia) continue
      let oferta = porOferta.get(aplicada.idOferta)
      if (!oferta) {
        oferta = { multiplo: escalar(aplicada.multiploDe, escala), unitario: escalar(aplicada.descuentoUnitario, escala), lineas: [] }
        porOferta.set(aplicada.idOferta, oferta)
      }
      oferta.lineas.push({ linea: l, unidades: maximo(escalar(linea.unidades, escala), 0n) })
    }
  })

  const unidadDeEscala = 10n ** BigInt(escala)
  const partes = new Map<string, Cobertura>()
  for (const [idOferta, { multiplo, unitario, lineas: deLaOferta }] of porOferta) {
    const importes = repartir(
      deLaOferta.map((x) => x.unidades),
      multiplo,
      unitario,
      escala,
    )
    deLaOferta.forEach((x, k) => {
      partes.set(
        clave(x.linea, idOferta),
        unitario > 0n ? { numerador: importes[k], denominador: unitario * unidadDeEscala } : { numerador: 0n, denominador: 1n },
      )
    })
  }
  return partes
}

/** Espejo de `CoberturaDeOfertas.Repartir`: el descuento de los grupos completos repartido por unidades, cada línea su
 * parte truncada al peso y el resto a la última (y hacia atrás, sin pasar de lo que cubren sus unidades). Los importes
 * salen a escala `2 × escala`. */
function repartir(unidades: bigint[], multiplo: bigint, unitario: bigint, escala: number): bigint[] {
  const total = unidades.reduce((a, b) => a + b, 0n)
  const importe = unitario > 0n && total > 0n ? enGruposCompletos(total, multiplo) * unitario : 0n
  const peso = 10n ** BigInt(2 * escala)
  const importes = unidades.map((u) => (importe === 0n ? 0n : ((importe * u) / (total * peso)) * peso))

  let resto = importe - importes.reduce((a, b) => a + b, 0n)
  for (let k = importes.length - 1; k >= 0 && resto > 0n; k--) {
    const agregado = minimo(resto, unidades[k] * unitario - importes[k])
    importes[k] += agregado
    resto -= agregado
  }
  return importes
}

/** `multiplo × floor(unidades / multiplo)`; las dos cantidades no negativas y a la misma escala. */
function enGruposCompletos(unidades: bigint, multiplo: bigint): bigint {
  return multiplo <= 0n ? unidades : multiplo * (unidades / multiplo)
}

/** Recorre las coberturas de menor a mayor: cada tramo de unidades lleva la suma de los descuentos de las ofertas que lo
 * cubren, con el tope del precio original por unidad. Las coberturas se llevan al producto de sus denominadores para
 * ordenarlas y restarlas sin dividir. */
function descuentoEscalonado(linea: LineaConOfertas, coberturas: Cobertura[], escala: number): number {
  const precioOriginal = escalar(linea.precioOriginal, escala)
  const niveles = coberturas.map((c, i) => coberturas.reduce((nivel, otra, j) => (j === i ? nivel : nivel * otra.denominador), c.numerador))
  const distintos = [...new Set(niveles.filter((c) => c > 0n))].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))
  let descuento = 0n
  let anterior = 0n
  for (const nivel of distintos) {
    let unitario = 0n
    niveles.forEach((c, i) => {
      if (c >= nivel) unitario += escalar(linea.aplicadas[i].descuentoUnitario, escala)
    })
    descuento += (nivel - anterior) * acotar(unitario, precioOriginal)
    anterior = nivel
  }

  // El descuento vale `descuento / divisor`; sin oferta de familia el divisor es una potencia de diez.
  const divisor = coberturas.reduce((producto, c) => producto * c.denominador, 1n) * 10n ** BigInt(escala)
  if (linea.aplicadas.some((a) => a.multiploDe != null && a.porFamilia)) {
    const factor = 10n ** BigInt(DECIMALES_CON_FAMILIA)
    return desescalar((2n * descuento * factor + divisor) / (2n * divisor), DECIMALES_CON_FAMILIA)
  }
  return desescalar(descuento, escala * (coberturas.length + 1))
}

const PATRON_DE_NUMERO_DECIMAL = /^(-?)(\d+)(?:\.(\d+))?(?:e([+-]?\d+))?$/i

function aDecimalExacto(valor: number): { mantisa: bigint; escala: number } {
  const [, signo, enteros, fraccion = '', exponente = '0'] = PATRON_DE_NUMERO_DECIMAL.exec(String(valor)) ?? []
  if (enteros === undefined) return { mantisa: 0n, escala: 0 }
  return { mantisa: BigInt(`${signo}${enteros}${fraccion}`), escala: fraccion.length - Number(exponente) }
}

function escalaComun(valores: readonly number[]): number {
  return Math.max(0, ...valores.filter(Number.isFinite).map((v) => aDecimalExacto(v).escala))
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

function acotar(valor: bigint, tope: bigint): bigint {
  return valor < 0n ? 0n : valor > tope ? tope : valor
}

function minimo(a: bigint, b: bigint): bigint {
  return a < b ? a : b
}

function maximo(a: bigint, b: bigint): bigint {
  return a > b ? a : b
}
